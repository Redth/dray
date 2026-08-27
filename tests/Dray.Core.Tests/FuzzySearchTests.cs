using Dray.Core.Navigation;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The ranking the command palette and every combobox share. These pin the behaviour that made the
/// palette feel right, so generalising it off <c>Command</c> could not quietly change it.
/// </summary>
public class FuzzySearchTests
{
    static IReadOnlyList<string> Rank(IEnumerable<string> items, string query)
        => [.. FuzzySearch.Rank(items, query, s => s, s => s.ToLowerInvariant()).Select(m => m.Value)];

    [Fact]
    public void AnAcronymFindsTheWordsItAbbreviates()
    {
        // The whole reason for subsequence rather than substring matching.
        Assert.Equal("Restart container", Rank(["Prune stopped", "Restart container", "Remove image"], "rsc")[0]);
    }

    [Fact]
    public void AnExactPrefixOutranksAScatteredMatch()
        => Assert.Equal("Stop", Rank(["Prune stopped", "Stop"], "stop")[0]);

    [Fact]
    public void AShorterMatchBeatsALongerOneOfEqualShape()
        => Assert.Equal("Stop", Rank(["Stop the whole stack", "Stop"], "stop")[0]);

    [Fact]
    public void SomethingThatDoesNotMatchIsAbsentRatherThanLast()
        => Assert.DoesNotContain("Networks", Rank(["Containers", "Networks"], "cont"));

    [Fact]
    public void AnEmptyQueryKeepsTheGivenOrder()
    {
        // The palette's default list is "everything, in the order the catalogue built it" — a
        // ranking applied to nothing would reorder it into alphabetical noise.
        Assert.Equal(["b", "a", "c"], Rank(["b", "a", "c"], ""));
    }

    [Fact]
    public void TiesAreBrokenStablySoTheListDoesNotReshuffle()
    {
        // Two identical scores must come back in the same order every time, or the list jitters
        // between keystrokes that changed nothing.
        var once = Rank(["web-2", "web-1"], "web");
        var twice = Rank(["web-2", "web-1"], "web");

        Assert.Equal(once, twice);
        Assert.Equal(["web-1", "web-2"], once);
    }

    // ---------------------------------------------------------------- highlights

    [Fact]
    public void HighlightsPointAtTheLabelTheUserCanSee()
    {
        var match = Assert.Single(FuzzySearch.Rank(["Restart"], "rst", s => s, s => s.ToLowerInvariant()));

        // r-e-s-t-a-r-t: r at 0, s at 2, t at 3.
        Assert.Equal([0, 2, 3], match.Highlights);
    }

    [Fact]
    public void AMatchOnHiddenTextHighlightsNothing()
    {
        // The row appears because "postgres" is in its haystack, but nothing in the visible label
        // matched — marking characters there would be inventing a reason.
        var match = Assert.Single(FuzzySearch.Rank(["db"], "postgres", s => s, _ => "db postgres:16"));

        Assert.Empty(match.Highlights);
    }

    [Fact]
    public void AVisibleMatchOutranksAHiddenOne()
    {
        var ranked = FuzzySearch.Rank(
            ["db", "postgres-primary"],
            "postgres",
            s => s,
            s => s == "db" ? "db postgres:16" : s.ToLowerInvariant());

        // A row that matched on text the user cannot see looks like a bug if it comes first.
        Assert.Equal("postgres-primary", ranked[0].Value);
    }

    // ---------------------------------------------------------------- segmentation

    [Fact]
    public void SegmentSplitsIntoAlternatingRuns()
    {
        var runs = FuzzySearch.Segment("Restart", [0, 2, 3]);

        Assert.Equal(
            [("R", true), ("e", false), ("st", true), ("art", false)],
            runs.Select(r => (r.Text, r.Matched)));
    }

    [Fact]
    public void SegmentWithNoHighlightsIsOneUnmarkedRun()
    {
        var run = Assert.Single(FuzzySearch.Segment("Restart", []));

        Assert.Equal(("Restart", false), (run.Text, run.Matched));
    }

    [Fact]
    public void SegmentPreservesTheWholeLabel()
    {
        // Whatever the runs, concatenating them must give back exactly what was passed in —
        // a highlight that dropped or duplicated a character would corrupt the name on screen.
        const string label = "draydemo-worker-1";
        var runs = FuzzySearch.Segment(label, [0, 1, 9, 10, 16]);

        Assert.Equal(label, string.Concat(runs.Select(r => r.Text)));
    }

    [Fact]
    public void SegmentToleratesAnOutOfRangeHighlight()
        => Assert.Equal("ab", string.Concat(FuzzySearch.Segment("ab", [0, 99]).Select(r => r.Text)));
}
