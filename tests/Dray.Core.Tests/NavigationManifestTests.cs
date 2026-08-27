using Dray.Core.Navigation;
using Dray.Core.Shell;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The manifest is the single source both native sidebars and the web nav render from
/// (docs/NATIVE-SHELL.md section 2.2). These guard the invariants a second hand-maintained list
/// would have broken silently.
/// </summary>
public class NavigationManifestTests
{
    [Fact]
    public void RoutesAreUnique()
    {
        var duplicates = NavigationManifest.Leaves()
            .GroupBy(n => n.Route!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, "Duplicate routes: " + string.Join(", ", duplicates));
    }

    [Fact]
    public void EveryLeafHasARouteAnIconAndATitle()
    {
        foreach (var leaf in NavigationManifest.Leaves())
        {
            Assert.False(string.IsNullOrWhiteSpace(leaf.Title));
            Assert.False(string.IsNullOrWhiteSpace(leaf.Route));
            Assert.True(leaf.Icon.HasValue, $"{leaf.Title} has no icon — it would render blank in a sidebar");
        }
    }

    [Fact]
    public void EveryRouteStartsWithASlash()
    {
        foreach (var leaf in NavigationManifest.Leaves())
            Assert.StartsWith("/", leaf.Route!, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupsAreNotRoutableAndLeavesAreNotGroups()
    {
        foreach (var node in NavigationManifest.Nodes)
        {
            if (node.IsGroup) Assert.Null(node.Route);
            else Assert.NotNull(node.Route);
        }
    }

    [Fact]
    public void IconsAreDistinctSoTheSidebarIsScannable()
    {
        // Two entries with the same icon are a coin-flip for the user. Caught here rather than in
        // a screenshot review.
        var shared = NavigationManifest.Leaves()
            .GroupBy(n => n.Icon!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(n => n.Title))}")
            .ToList();

        Assert.True(shared.Count == 0, "Icons reused across nav entries: " + string.Join(" | ", shared));
    }

    [Fact]
    public void HomeRouteResolves()
    {
        var home = NavigationManifest.Find(NavigationManifest.HomeRoute);
        Assert.NotNull(home);
        Assert.Equal("Dashboard", home.Title);
    }

    [Fact]
    public void FindIsCaseInsensitive()
    {
        Assert.NotNull(NavigationManifest.Find("/CONTAINERS"));
        Assert.Null(NavigationManifest.Find("/nope"));
    }

    [Fact]
    public void ReleaseBuildsHideDebugOnlyEntries()
    {
        var release = NavigationManifest.Visible(includeDebug: false);
        var titles = Flatten(release).Select(n => n.Title).ToList();

        Assert.DoesNotContain("Component gallery", titles);
        Assert.Contains("Containers", titles);
    }

    [Fact]
    public void PruningNeverLeavesAnEmptyGroup()
    {
        // A group whose children are all debug-only must disappear too, not render as a dead header.
        foreach (var node in NavigationManifest.Visible(includeDebug: false))
            if (node.Children.Count == 0)
                Assert.NotNull(node.Route);
    }

    [Fact]
    public void EveryNavIconExistsInTheGeneratedIconSet()
    {
        // Redundant with the compiler today, but pins the contract: a nav entry can only name an
        // icon the sprite and both native tables actually carry.
        foreach (var leaf in NavigationManifest.Leaves())
        {
            var icon = leaf.Icon!.Value;
            Assert.True(Icons.SfSymbols.ContainsKey(icon), $"{icon} has no SF Symbol mapping");
            Assert.True(Icons.GtkNames.ContainsKey(icon), $"{icon} has no GTK mapping");
            Assert.StartsWith("i-", Icons.SpriteId(icon), StringComparison.Ordinal);
        }
    }

    static IEnumerable<NavNode> Flatten(IEnumerable<NavNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children)) yield return c;
        }
    }
}
