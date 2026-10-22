using Dray.Core.Shell;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The channel a form's body uses to drive a native button row. Three heads implement the other
/// end, so its rules are pinned here once rather than rediscovered per head.
/// </summary>
public class DialogControllerTests
{
    [Fact]
    public async Task WithNoBodyListeningEveryButtonCloses()
    {
        var dialog = new DialogController();

        Assert.True(await dialog.PressAsync("cancel"));
        Assert.True(dialog.IsClosed);
        Assert.Equal("cancel", dialog.Result);
    }

    [Fact]
    public async Task TheBodyCanKeepTheDialogOpen()
    {
        // Pull: the button starts the work and the dialog stays to show it.
        var dialog = new DialogController { Pressed = id => Task.FromResult(id != "pull") };

        Assert.False(await dialog.PressAsync("pull"));
        Assert.False(dialog.IsClosed);
        Assert.Null(dialog.Result);
    }

    [Fact]
    public async Task ADisabledButtonDoesNothing()
    {
        // A head may deliver a press for a button it has not redrawn yet — Return on a native dialog
        // whose default button was disabled a frame ago.
        var asked = false;
        var dialog = new DialogController { Pressed = _ => { asked = true; return Task.FromResult(true); } };
        dialog.Set("create", new DialogButtonState("Create", IsEnabled: false));

        Assert.False(await dialog.PressAsync("create"));
        Assert.False(asked);
        Assert.False(dialog.IsClosed);
    }

    [Fact]
    public void SettingTheSameStateTwiceRaisesNothing()
    {
        // Bodies set their buttons on every render; a head redraws on Changed. Raising for a
        // non-change would loop the two.
        var dialog = new DialogController();
        var raised = 0;
        dialog.Changed += () => raised++;

        dialog.Set("pull", new DialogButtonState("Pull", IsEnabled: false));
        dialog.Set("pull", new DialogButtonState("Pull", IsEnabled: false));
        dialog.Set("pull", new DialogButtonState("Pull"));

        Assert.Equal(2, raised);
    }

    [Fact]
    public void TheBodyClosingCarriesItsResultAndValue()
    {
        var dialog = new DialogController();
        var requested = 0;
        dialog.CloseRequested += () => requested++;

        dialog.Close("run", value: "c0ffee");
        dialog.Close("cancel");

        Assert.Equal(1, requested);
        Assert.Equal("run", dialog.Result);
        Assert.Equal("c0ffee", dialog.Value);
    }

    [Fact]
    public void ADismissalHasNoResult()
    {
        var dialog = new DialogController();
        dialog.Dismissed();

        Assert.True(dialog.IsClosed);
        Assert.Null(dialog.Result);
    }

    [Fact]
    public void TheRequestShowsTheBodysStateOverWhatItDeclared()
    {
        var controller = new DialogController();
        var pull = DialogButton.Confirm("pull", "Pull");
        var request = new DialogRequest("Pull an image", typeof(object), Buttons: [DialogButton.Cancel(), pull], Controller: controller);

        Assert.Equal(new DialogButtonState("Pull"), request.Current(pull));

        controller.Set("pull", new DialogButtonState("Pulling…", IsEnabled: false, IsBusy: true));

        Assert.Equal("Pulling…", request.Current(pull).Label);
        Assert.True(request.Current(pull).IsBusy);
    }
}
