using Dray.Core.Shell;
using Xunit;

namespace Dray.Core.Tests;

public class PageChromeTests
{
    [Fact]
    public void AChromeWithNothingButTitlesHasNoControls()
    {
        // The web head draws no toolbar in this case: a bar containing only the page's own heading
        // is a second copy of it, which is what made detail pages look like they had two titles.
        var chrome = new PageChrome("dray-web", "Local engine");

        Assert.False(chrome.HasControls);
    }

    [Fact]
    public void BackAloneIsNotAControl()
    {
        // Back renders beside the heading in the content, so it must not on its own bring back the
        // toolbar it was moved out of.
        var chrome = new PageChrome("dray-web", Back: new ChromeBack("back", "All containers"));

        Assert.False(chrome.HasControls);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void SearchFiltersAndActionsEachCountAsControls(bool search, bool action, bool filter)
    {
        var chrome = new PageChrome(
            "Containers",
            Search: search ? new ChromeSearch("Filter") : null,
            Actions: action ? [ChromeAction.Secondary("refresh", "Refresh", IconRef.Refresh)] : null,
            Filters: filter ? [new ChromeFilter("state", "State", [new ChromeFilterOption("All")])] : null);

        Assert.True(chrome.HasControls);
    }

    // ---------------------------------------------------------------- signature

    [Fact]
    public void GainingBackChangesTheShapeSoTheToolbarRebuilds()
    {
        // NSToolbar only rebuilds when the signature changes. A back item that appeared without
        // one would never be inserted.
        var without = new PageChrome("Containers");
        var with = new PageChrome("Containers", Back: new ChromeBack("back", "All containers"));

        Assert.NotEqual(without.Signature, with.Signature);
    }

    [Fact]
    public void ChangingOnlyTheDestinationLabelDoesNotRebuild()
    {
        // A label is updated in place; rebuilding for it would drop the search field's native
        // subscription for no reason.
        var a = new PageChrome("X", Back: new ChromeBack("back", "All containers"));
        var b = new PageChrome("X", Back: new ChromeBack("back", "Everything"));

        Assert.Equal(a.Signature, b.Signature);
    }

    [Fact]
    public void TwoChromesBuiltFromTheSameLiteralsAreEqual()
    {
        // Value equality including Back — otherwise every re-render looks like a change and
        // thrashes the native toolbar.
        var a = new PageChrome("X", Back: new ChromeBack("back", "All containers"));
        var b = new PageChrome("X", Back: new ChromeBack("back", "All containers"));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void AChromeThatGainsBackIsNotEqualToOneWithout()
        => Assert.NotEqual(new PageChrome("X"), new PageChrome("X", Back: new ChromeBack("back", "Out")));
}
