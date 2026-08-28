using Dray.Core.Shell;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The arithmetic behind a row of controls that collapses into a menu as the window narrows.
/// <para>
/// The browser measures; this decides. Which matters because the decision is the part that can be
/// wrong in a way nobody notices — a row that keeps one control too many overflows its container,
/// and one that keeps one too few has a menu holding a single item beside empty space.
/// </para>
/// </summary>
public class OverflowFitTests
{
    // A 28px control with a 8px gap — the size of Dray's icon buttons.
    const double Item = 28;
    const double Gap = 8;

    [Fact]
    public void EverythingFitsWhenThereIsRoom()
    {
        // Four buttons need 4×28 + 3×8 = 136.
        Assert.Equal(4, OverflowFit.Visible(available: 136, Item, Gap, total: 4));
        Assert.Equal(4, OverflowFit.Visible(available: 400, Item, Gap, total: 4));
    }

    [Fact]
    public void OneShortMeansTwoGoInTheMenu()
    {
        // 135 is one pixel short of four. Three would fit — but the menu's trigger needs a slot of
        // its own, so what actually fits is two buttons and the trigger (3×28 + 2×8 = 100).
        Assert.Equal(2, OverflowFit.Visible(available: 135, Item, Gap, total: 4));
    }

    [Fact]
    public void TheTriggerIsPaidForBeforeAnyButtonIsKept()
    {
        // Room for exactly two controls. One of them is the trigger.
        Assert.Equal(1, OverflowFit.Visible(available: 64, Item, Gap, total: 4));
    }

    [Fact]
    public void AtItsNarrowestTheRowIsNothingButTheMenu()
    {
        Assert.Equal(0, OverflowFit.Visible(available: 30, Item, Gap, total: 4));
        Assert.Equal(0, OverflowFit.Visible(available: 1, Item, Gap, total: 4));
    }

    [Fact]
    public void ARowThatFitsExactlyIsNotCollapsed()
    {
        // The boundary in the direction that matters: collapsing here would put a menu on a row
        // that had no problem, and the menu costs more width than it saves.
        Assert.Equal(3, OverflowFit.Visible(available: 100, Item, Gap, total: 3));
    }

    [Fact]
    public void NothingMeasuredYetShowsEverything()
    {
        // The first render, before the resize observer has reported. Starting full and collapsing
        // is right because full is where most rows settle; starting empty would flash a menu onto
        // every row on the page.
        Assert.Equal(4, OverflowFit.Visible(available: 0, item: 0, Gap, total: 4));
        Assert.Equal(4, OverflowFit.Visible(available: 500, item: 0, Gap, total: 4));
        Assert.Equal(4, OverflowFit.Visible(available: 0, Item, Gap, total: 4));
    }

    [Fact]
    public void ARowWithNothingInItAsksForNothing()
        => Assert.Equal(0, OverflowFit.Visible(available: 500, Item, Gap, total: 0));

    [Fact]
    public void ASingleControlNeverBecomesAMenuOfOne()
    {
        // Hiding one button behind a trigger of the same size buys nothing at all.
        Assert.Equal(1, OverflowFit.Visible(available: 28, Item, Gap, total: 1));
    }

    [Fact]
    public void GrowingTheWindowBringsButtonsBackInOrder()
    {
        // The property that matters over a resize: the count only ever goes up with width, so a
        // control never disappears while the row is getting wider.
        var last = -1;

        for (var width = 1; width <= 300; width++)
        {
            var visible = OverflowFit.Visible(width, Item, Gap, total: 5);

            Assert.True(visible >= last, $"{width}px showed {visible} after {last}");
            last = visible;
        }

        Assert.Equal(5, last);
    }
}
