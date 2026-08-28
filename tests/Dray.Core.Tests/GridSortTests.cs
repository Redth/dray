using Dray.Core.Shell;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// How a column sorts when its cells are not plain strings.
/// </summary>
public class GridSortTests
{
    [Fact]
    public void StateSortsBySeverityNotByItsWords()
    {
        // Alphabetically, Dead falls between Created and Exited. The reason anyone sorts by state
        // is to bring the broken ones up.
        var dead = new GridState("danger", "✕", "Dead", null);
        var created = new GridState("neutral", "○", "Created", null);
        var running = new GridState("ok", "●", "Running", null);

        Assert.True(GridSort.Compare(dead, created) < 0);
        Assert.True(GridSort.Compare(created, running) < 0);
    }

    [Fact]
    public void ALinkSortsByWhatItSaysNotWhereItGoes()
    {
        var a = new GridLink("alpha", "/containers/zzz");
        var b = new GridLink("beta", "/containers/aaa");

        Assert.True(GridSort.Compare(a, b) < 0);
    }

    [Fact]
    public void AChipSortsByTheWholeValueNotTheShortening()
    {
        // Two digests can share their first twelve characters; the full value is what distinguishes
        // them, and it is what the chip is for.
        var a = new GridChip("9a56851f1a97", "sha256:9a56851f1a97aaa");
        var b = new GridChip("9a56851f1a97", "sha256:9a56851f1a97bbb");

        Assert.True(GridSort.Compare(a, b) < 0);
    }

    [Fact]
    public void EmptySortsLastInBothDirections()
    {
        // A row with no value has nothing to say about the question being asked. Burying it is more
        // useful than having it alternate between the top and the bottom as the sort flips.
        Assert.True(GridSort.Compare(null, "anything") > 0);
        Assert.True(GridSort.Compare("anything", null) < 0);
        Assert.Equal(0, GridSort.Compare(null, null));
    }

    [Fact]
    public void NumbersCompareAsNumbers()
        => Assert.True(GridSort.Compare(9, 10) < 0);

    [Fact]
    public void MixedTypesFallBackToTextRatherThanThrowing()
    {
        // A column whose values are not all the same shape is a bug somewhere else; sorting is not
        // the place to surface it by crashing the page.
        Assert.NotEqual(0, GridSort.Compare(1, "one"));
    }
}
