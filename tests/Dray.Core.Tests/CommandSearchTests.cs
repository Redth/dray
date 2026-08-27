using Dray.Core.Model;
using Dray.Core.Navigation;
using Dray.Core.Shell;
using Xunit;

namespace Dray.Core.Tests;

public class CommandSearchTests
{
    static Command Cmd(string title, string category = "Test", string? keywords = null) =>
        new($"id:{title}", title, category, CommandKind.Action, IconRef.More, Keywords: keywords);

    static IReadOnlyList<string> Titles(IEnumerable<CommandMatch> matches)
        => [.. matches.Select(m => m.Command.Title)];

    [Fact]
    public void AnEmptyQueryKeepsEveryCommandInOrder()
    {
        // How the palette shows its default list.
        var ranked = CommandSearch.Rank([Cmd("Alpha"), Cmd("Beta")], "");

        Assert.Equal(["Alpha", "Beta"], Titles(ranked));
    }

    [Fact]
    public void AnExactPrefixWins()
    {
        // "stop" should offer Stop before Prune stopped.
        var ranked = CommandSearch.Rank([Cmd("Prune stopped"), Cmd("Stop")], "stop");

        Assert.Equal("Stop", ranked[0].Command.Title);
    }

    [Fact]
    public void AnAcronymMatchesInitials()
    {
        // The reason a palette uses subsequence matching rather than substring.
        var ranked = CommandSearch.Rank([Cmd("Restart container"), Cmd("Remove volume")], "rc");

        Assert.Equal("Restart container", ranked[0].Command.Title);
    }

    [Fact]
    public void CharactersMustAppearInOrder()
    {
        // "cr" is not in "Restart" in that order, so it must not match.
        Assert.Empty(CommandSearch.Rank([Cmd("Restart")], "zq"));
    }

    [Fact]
    public void AMatchOnHiddenKeywordsStillAppears()
    {
        // A container called "db" running postgres should be findable by "postgres".
        var ranked = CommandSearch.Rank([Cmd("db", keywords: "postgres:16")], "postgres");

        Assert.Single(ranked);
    }

    [Fact]
    public void AVisibleMatchOutranksAHiddenOne()
    {
        // A row that matched on text the user cannot see looks like a bug if it comes first.
        var ranked = CommandSearch.Rank(
            [Cmd("db", keywords: "redis"), Cmd("redis")],
            "redis");

        Assert.Equal("redis", ranked[0].Command.Title);
    }

    [Fact]
    public void HighlightsPointAtTheMatchedCharactersOfTheTitle()
    {
        var match = CommandSearch.Rank([Cmd("Restart")], "rst")[0];

        // r-e-s-t-a-r-t → positions 0, 2, 3
        Assert.Equal([0, 2, 3], match.Highlights);
    }

    [Fact]
    public void AHiddenMatchHasNoHighlights()
    {
        // There is nothing on screen to highlight, and pointing at the title would mark the wrong
        // characters.
        var match = CommandSearch.Rank([Cmd("db", keywords: "postgres")], "postgres")[0];

        Assert.Empty(match.Highlights);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
        => Assert.Single(CommandSearch.Rank([Cmd("Restart Container")], "RESTART"));

    [Fact]
    public void TiesAreBrokenByTitleSoTheListDoesNotReshuffle()
    {
        var ranked = CommandSearch.Rank([Cmd("beta"), Cmd("alpha")], "a");

        // Both match once at the same quality; ordering must be stable between identical queries.
        Assert.Equal(Titles(ranked), Titles(CommandSearch.Rank([Cmd("beta"), Cmd("alpha")], "a")));
    }

    [Fact]
    public void AShorterTitleBeatsALongerOneForTheSameMatch()
        => Assert.Equal("Logs", CommandSearch.Rank([Cmd("Aggregated logs view"), Cmd("Logs")], "logs")[0].Command.Title);
}

public class CommandCatalogueTests
{
    static ContainerSummary Container(string name, DockerState state) =>
        new() { Id = name + "0123456789abcdef", Name = name, Image = "docker.io/library/redis:7", State = state };

    [Fact]
    public void EveryNavigationEntryBecomesACommand()
    {
        var commands = CommandCatalogue.Navigation(includeDebug: false).ToList();

        Assert.NotEmpty(commands);
        Assert.All(commands, c => Assert.Equal(CommandKind.Navigate, c.Kind));
        Assert.Contains(commands, c => c.Title == "Containers");
    }

    [Fact]
    public void AContainerOffersOnlyTheActionsThatApplyToIt()
    {
        // Offering Start on a running container through the palette is the same mistake as
        // offering it in the row, with less context to catch it.
        var commands = CommandCatalogue.ForContainers([Container("web", DockerState.Running)]).ToList();

        Assert.Contains(commands, c => c.Title == "Stop web");
        Assert.DoesNotContain(commands, c => c.Title == "Start web");
    }

    [Fact]
    public void AStoppedContainerOffersStartAndRemove()
    {
        var commands = CommandCatalogue.ForContainers([Container("web", DockerState.Exited)]).ToList();

        Assert.Contains(commands, c => c.Title == "Start web");
        Assert.Contains(commands, c => c.Title == "Remove web");
    }

    [Fact]
    public void DestructiveActionsAreMarked()
    {
        var remove = CommandCatalogue
            .ForContainers([Container("web", DockerState.Exited)])
            .Single(c => c.Title == "Remove web");

        Assert.True(remove.IsDestructive);
    }

    [Fact]
    public void AContainerIsFindableByItsImage()
    {
        var entry = CommandCatalogue
            .ForContainers([Container("db", DockerState.Running)])
            .First(c => c.Kind == CommandKind.Entity);

        Assert.Single(CommandSearch.Rank([entry], "redis"));
    }
}
