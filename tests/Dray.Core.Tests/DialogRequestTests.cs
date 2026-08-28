using Dray.Core.Shell;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The shape a dialog declares, which every head then draws with its own widgets.
/// <para>
/// These are small rules and each one is a decision the platform would otherwise make differently
/// on each head — which is the thing docs/NATIVE-SHELL.md section 4 exists to stop.
/// </para>
/// </summary>
public class DialogRequestTests
{
    sealed class Body { }

    static DialogRequest Request(IReadOnlyList<DialogButton>? buttons = null)
        => new("Raw JSON", typeof(Body), Buttons: buttons);

    [Fact]
    public void ADialogWithNoButtonsStillHasAWayOut()
    {
        // Null buttons is the common case — a dialog that only shows something — and a dialog with
        // no button row at all would be one the user has to guess how to leave.
        var single = Assert.Single(Request().ButtonList);

        Assert.Equal("done", single.Id);
        Assert.Equal(DialogButtonRole.Confirm, single.Role);
    }

    [Fact]
    public void AnEmptyButtonListIsTreatedAsNoneGiven()
        => Assert.Single(Request([]).ButtonList);

    [Fact]
    public void EscapeAndReturnFindTheirButtons()
    {
        var request = Request([DialogButton.Cancel(), DialogButton.Confirm("save", "Save")]);

        Assert.Equal("cancel", request.CancelButton?.Id);
        Assert.Equal("save", request.DefaultButton?.Id);
    }

    [Fact]
    public void ADestructiveButtonIsNeverTheOneReturnCommits()
    {
        // Return is pressed by people who have stopped reading. Marking something destructive and
        // then making it the default undoes the marking.
        var request = Request([DialogButton.Cancel(), DialogButton.Destructive("remove", "Remove")]);

        Assert.Null(request.DefaultButton);
        Assert.Equal("cancel", request.CancelButton?.Id);
    }

    [Fact]
    public void ADialogWithNoCancelSaysSoRatherThanGuessing()
    {
        // A head that needs one can add it; inventing a cancel here would put a button in the row
        // that the caller never asked for and has no id for.
        Assert.Null(Request(DialogButton.Done()).CancelButton);
    }

    [Fact]
    public void ParametersAreNeverNullForTheHeadToWalk()
    {
        Assert.Empty(Request().ParameterMap);

        var withOne = new DialogRequest("t", typeof(Body), new Dictionary<string, object?> { ["Json"] = "{}" });

        Assert.Equal("{}", Assert.Single(withOne.ParameterMap).Value);
    }

    [Fact]
    public void TheDefaultsAreTheOnesAPageWouldWant()
    {
        // Medium, because the common dialog is a form. A viewer asks for Large explicitly.
        Assert.Equal(DialogSize.Medium, Request().Size);
        Assert.Null(Request().Subtitle);
    }

    // ---------------------------------------------------------------- button order

    [Fact]
    public void AppKitGetsTheCommittingButtonFirst()
    {
        // NSAlert adds buttons right to left, and the first added is the default. Getting this
        // backwards puts Cancel where Save should be — on the platform where muscle memory is
        // strongest about it.
        var request = Request([DialogButton.Cancel(), new DialogButton("later", "Later"), DialogButton.Confirm("save", "Save")]);

        Assert.Equal(["save", "later", "cancel"],
            request.Ordered(DialogButtonOrder.CommitFirst).Select(b => b.Id));
    }

    [Fact]
    public void TheWebGetsItLast()
    {
        var request = Request([DialogButton.Cancel(), new DialogButton("later", "Later"), DialogButton.Confirm("save", "Save")]);

        Assert.Equal(["cancel", "later", "save"],
            request.Ordered(DialogButtonOrder.CommitLast).Select(b => b.Id));
    }

    [Fact]
    public void ADestructiveButtonTakesThePositionButNotTheReturnKey()
    {
        // It is the decision the dialog exists to take, so it sits where the committing button
        // sits — and it is still not the one Return presses.
        var request = Request([DialogButton.Cancel(), DialogButton.Destructive("remove", "Remove")]);

        Assert.Equal(["remove", "cancel"], request.Ordered(DialogButtonOrder.CommitFirst).Select(b => b.Id));
        Assert.Equal(["cancel", "remove"], request.Ordered(DialogButtonOrder.CommitLast).Select(b => b.Id));

        Assert.Null(request.DefaultButton);
    }

    [Fact]
    public void OrderingNeverLosesOrDuplicatesAButton()
    {
        // The head maps the platform's answer back to an id by position, so a lost button is a
        // dialog that returns the wrong one.
        var request = Request(
        [
            DialogButton.Cancel(),
            new DialogButton("a", "A"),
            new DialogButton("b", "B"),
            DialogButton.Confirm("go", "Go"),
        ]);

        foreach (var order in Enum.GetValues<DialogButtonOrder>())
        {
            var ordered = request.Ordered(order);

            Assert.Equal(request.ButtonList.Count, ordered.Count);
            Assert.Equal([.. request.ButtonList.Select(b => b.Id).Order()], [.. ordered.Select(b => b.Id).Order()]);
        }
    }

    [Fact]
    public void ADialogWithOneButtonIsTheSameInEitherOrder()
    {
        Assert.Equal(["done"], Request().Ordered(DialogButtonOrder.CommitFirst).Select(b => b.Id));
        Assert.Equal(["done"], Request().Ordered(DialogButtonOrder.CommitLast).Select(b => b.Id));
    }
}
