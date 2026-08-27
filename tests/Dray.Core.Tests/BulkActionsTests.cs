using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

public class BulkActionsTests
{
    static ContainerSummary Container(string name, DockerState state) =>
        new() { Id = name, Name = name, Image = "alpine", State = state };

    static readonly ContainerSummary Running = Container("web", DockerState.Running);
    static readonly ContainerSummary AlsoRunning = Container("api", DockerState.Running);
    static readonly ContainerSummary Stopped = Container("worker", DockerState.Exited);

    [Fact]
    public void AnEmptySelectionOffersNothing()
        => Assert.Empty(BulkActions.For([]));

    [Fact]
    public void AnActionThatFitsEveryContainerCarriesNoCount()
    {
        // "Stop" reads better than "Stop 2" when two are selected and both will stop.
        var stop = BulkActions.For([Running, AlsoRunning]).Single(b => b.Action == ContainerAction.Stop);

        Assert.False(stop.IsPartial);
        Assert.Equal("Stop", stop.Label);
    }

    [Fact]
    public void AMixedSelectionSaysHowManyTheActionWouldReach()
    {
        // Selecting two running and one stopped and pressing Stop stops two. Saying so is the
        // difference between a clear instruction and a surprise.
        var stop = BulkActions.For([Running, AlsoRunning, Stopped]).Single(b => b.Action == ContainerAction.Stop);

        Assert.True(stop.IsPartial);
        Assert.Equal(2, stop.Applicable);
        Assert.Equal("Stop 2", stop.Label);
    }

    [Fact]
    public void AnActionThatFitsNothingSelectedIsNotOffered()
    {
        // Rather than offered and disabled: a row of greyed buttons is noise, and the set changes
        // every time the selection does.
        Assert.DoesNotContain(BulkActions.For([Running]), b => b.Action == ContainerAction.Start);
    }

    [Fact]
    public void AMixedSelectionStillOffersBothDirections()
    {
        // Select-all then Stop must work even though one is already stopped — that is the most
        // common selection there is.
        var actions = BulkActions.For([Running, Stopped]).Select(b => b.Action).ToList();

        Assert.Contains(ContainerAction.Stop, actions);
        Assert.Contains(ContainerAction.Start, actions);
    }

    [Fact]
    public void TargetsAreOnlyTheContainersTheActionApplind()
    {
        var targets = BulkActions.Targets(ContainerAction.Stop, [Running, AlsoRunning, Stopped]);

        Assert.Equal(["web", "api"], targets.Select(t => t.Name));
    }

    // ---------------------------------------------------------------- confirmation

    [Fact]
    public void ASingleTargetUsesTheSingularConfirmation()
    {
        var (title, _) = BulkActions.Confirmation(ContainerAction.Remove, [Stopped]);

        Assert.Equal("Remove worker?", title);
    }

    [Fact]
    public void AShortSelectionIsNamedRatherThanCounted()
    {
        // "Remove 3 containers?" is a number; naming them is a decision.
        var (title, _) = BulkActions.Confirmation(
            ContainerAction.Remove, [Stopped, Container("db", DockerState.Exited)]);

        Assert.Equal("Remove worker and db?", title);
    }

    [Fact]
    public void ALongSelectionIsCountedBecauseNamingItIsUnreadable()
    {
        var many = Enumerable.Range(0, 7).Select(i => Container($"c{i}", DockerState.Exited)).ToList();

        var (title, _) = BulkActions.Confirmation(ContainerAction.Remove, many);

        Assert.Equal("Remove 7 containers?", title);
    }

    [Theory]
    [InlineData(new[] { "a" }, "a")]
    [InlineData(new[] { "a", "b" }, "a and b")]
    [InlineData(new[] { "a", "b", "c" }, "a, b and c")]
    public void NamesReadAsASentence(string[] names, string expected)
        => Assert.Equal(expected, Humanize.Names(names));

    [Fact]
    public void AnEmptyNameListIsEmpty()
        => Assert.Equal("", Humanize.Names([]));

    [Fact]
    public void ADestructiveBulkConfirmationSaysWhatIsLost()
    {
        var (_, body) = BulkActions.Confirmation(
            ContainerAction.Remove, [Stopped, Container("db", DockerState.Exited)]);

        Assert.Contains("not on a volume is lost", body, StringComparison.Ordinal);
    }
}

public class ContainerNameTests
{
    [Theory]
    [InlineData("web")]
    [InlineData("dray-web")]
    [InlineData("my_app.1")]
    [InlineData("2fast")]
    public void ValidNamesPass(string name)
        => Assert.True(ContainerName.IsValid(name));

    [Fact]
    public void AnEmptyNameIsRejected()
        => Assert.Equal("A container needs a name.", ContainerName.Validate("  "));

    [Fact]
    public void AnAwkwardFirstCharacterIsRejectedSpecifically()
    {
        // The engine requires the first character to be alphanumeric. Saying which rule was broken
        // beats "invalid name".
        Assert.Equal(
            "The first character has to be a letter or a number.",
            ContainerName.Validate("-leading-dash"));
    }

    [Fact]
    public void AnIllegalCharacterIsNamedInTheMessage()
        => Assert.Contains("'/' is not allowed", ContainerName.Validate("web/api")!, StringComparison.Ordinal);

    [Fact]
    public void ASpaceIsRejected()
        => Assert.NotNull(ContainerName.Validate("my container"));

    [Fact]
    public void AnOverlongNameIsRejected()
        => Assert.Contains("at most 255", ContainerName.Validate(new string('a', 256))!, StringComparison.Ordinal);
}
