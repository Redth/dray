using Dray.Core.Shell;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The session is the line between a native frame and a web body. These hold the promises both
/// heads rely on: a press goes to the body, the body decides whether to close, Cancel always gets
/// out, and nothing is pressed twice.
/// </summary>
public class DialogSessionTests
{
    sealed class Body;

    static DialogSession Session(params DialogButton[] buttons)
        => new(new DialogRequest("Title", typeof(Body), Buttons: buttons.Length == 0
            ? [DialogButton.Cancel(), DialogButton.Confirm("ok", "OK")]
            : buttons));

    [Fact]
    public async Task APressWithNoHandlerCloses()
    {
        var session = Session();

        await session.PressAsync("ok");

        Assert.Equal("ok", await session.Completion);
    }

    [Fact]
    public async Task TheBodyDecidesWhetherAPressCloses()
    {
        var session = Session();
        session.Pressed = _ => Task.FromResult(false);

        await session.PressAsync("ok");
        Assert.False(session.IsClosed);

        session.Pressed = _ => Task.FromResult(true);
        await session.PressAsync("ok");
        Assert.Equal("ok", await session.Completion);
    }

    [Fact]
    public async Task ADisabledButtonIsNotPressed()
    {
        var session = Session(DialogButton.Cancel(), DialogButton.Confirm("ok", "OK") with { Enabled = false });
        var pressed = false;
        session.Pressed = _ => { pressed = true; return Task.FromResult(true); };

        await session.PressAsync("ok");

        Assert.False(pressed);
        Assert.False(session.IsClosed);
    }

    [Fact]
    public async Task ASecondPressWhileTheFirstIsWorkingIsIgnored()
    {
        var session = Session();
        var gate = new TaskCompletionSource<bool>();
        var presses = 0;
        session.Pressed = _ => { presses++; return gate.Task; };

        var first = session.PressAsync("ok");
        await session.PressAsync("ok");

        Assert.Equal(1, presses);

        gate.SetResult(false);
        await first;
    }

    [Fact]
    public async Task CancelClosesEvenWhileAPressIsWorking()
    {
        // How a pull or a build is stopped: the commit press is still awaiting the stream.
        var session = Session();
        var gate = new TaskCompletionSource<bool>();
        session.Pressed = _ => gate.Task;

        var pull = session.PressAsync("ok");
        await session.PressAsync("cancel");

        Assert.Equal("cancel", await session.Completion);
        Assert.True(session.Closing.IsCancellationRequested);

        gate.SetResult(true);
        await pull;
        Assert.Equal("cancel", await session.Completion);
    }

    [Fact]
    public async Task TheBodyCannotRefuseCancel()
    {
        var session = Session();
        session.Pressed = _ => Task.FromResult(false);

        await session.PressAsync("cancel");

        Assert.Equal("cancel", await session.Completion);
    }

    [Fact]
    public async Task ReturnPressesTheDefaultOnlyWhenItCanBePressed()
    {
        var session = Session(DialogButton.Cancel(), DialogButton.Confirm("ok", "OK") with { Busy = true });

        await session.PressDefaultAsync();
        Assert.False(session.IsClosed);

        session.Update("ok", b => b with { Busy = false });
        await session.PressDefaultAsync();
        Assert.Equal("ok", await session.Completion);
    }

    [Fact]
    public async Task ADestructiveButtonIsNeverTheDefault()
    {
        var session = Session(DialogButton.Cancel(), DialogButton.Destructive("rm", "Remove"));

        Assert.Null(session.DefaultButton);

        await session.PressDefaultAsync();
        Assert.False(session.IsClosed);
    }

    [Fact]
    public async Task DismissalClosesWithNoButtonAndOnlyOnce()
    {
        var session = Session();
        var closes = 0;
        session.Closed += _ => closes++;

        session.Dismiss();
        session.Close("ok");

        Assert.Null(await session.Completion);
        Assert.Equal(1, closes);
    }

    [Fact]
    public void AnUnchangedRowRaisesNothing()
    {
        // A body pushes its row after every render; the head must only redraw for a real change.
        var session = Session();
        var changes = 0;
        session.Changed += () => changes++;

        session.SetButtons([DialogButton.Cancel(), DialogButton.Confirm("ok", "OK")]);
        session.SetTitle("Title");
        Assert.Equal(0, changes);

        session.SetButtons([DialogButton.Cancel(), DialogButton.Confirm("ok", "OK") with { Enabled = false }]);
        session.SetTitle("Another");
        Assert.Equal(2, changes);
    }

    [Fact]
    public void LeadingButtonsAreLeftOutOfTheTrailingOrder()
    {
        IReadOnlyList<DialogButton> row =
            [DialogButton.Back(), DialogButton.Cancel(), new("test", "Test"), DialogButton.Confirm("add", "Add")];

        Assert.Equal(["cancel", "test", "add"], DialogRequest.Order(row, DialogButtonOrder.CommitLast).Select(b => b.Id));
        Assert.Equal(["add", "test", "cancel"], DialogRequest.Order(row, DialogButtonOrder.CommitFirst).Select(b => b.Id));
    }

    [Fact]
    public void ContentHeightIgnoresSubPixelJitter()
    {
        var session = Session();
        var changes = 0;
        session.Changed += () => changes++;

        session.ReportContentHeight(300);
        session.ReportContentHeight(300.4);
        session.ReportContentHeight(320);

        Assert.Equal(2, changes);
        Assert.Equal(320, session.ContentHeight);
    }
}
