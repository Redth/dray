using Dray.Core.Shell;

namespace Dray.Core.Navigation;

/// <summary>
/// One entry in the sidebar. A group has <see cref="Children"/> and no <see cref="Route"/>;
/// a leaf has a route and no children.
/// </summary>
public sealed record NavNode
{
    public required string Title { get; init; }

    /// <summary>Blazor route. Null for a group header.</summary>
    public string? Route { get; init; }

    public IconRef? Icon { get; init; }

    public IReadOnlyList<NavNode> Children { get; init; } = [];

    /// <summary>Hidden unless the app is running a Debug build.</summary>
    public bool DebugOnly { get; init; }

    public bool IsGroup => Children.Count > 0;

    public static NavNode Leaf(string title, string route, IconRef icon, bool debugOnly = false)
        => new() { Title = title, Route = route, Icon = icon, DebugOnly = debugOnly };

    public static NavNode Group(string title, params NavNode[] children)
        => new() { Title = title, Children = children };
}

/// <summary>
/// The one place Dray's navigation is declared.
/// <para>
/// Native sidebars (<c>NSOutlineView</c>, <c>NavigationView</c>, <c>AdwNavigationSplitView</c>) and
/// the web fallback nav all render from this list. MAUI.Sherpa declared its sidebar twice — once as
/// <c>MacOSSidebarItem</c> records in the macOS head, once as <c>NavLink</c> markup in
/// <c>MainLayout.razor</c> — two hand-maintained trees with nothing enforcing that they match.
/// Adding an entry here is one line, in one file, and every platform picks it up.
/// </para>
/// </summary>
public static class NavigationManifest
{
    /// <summary>Where the app opens.</summary>
    public const string HomeRoute = "/";

    public static IReadOnlyList<NavNode> Nodes { get; } =
    [
        NavNode.Leaf("Dashboard", "/", IconRef.Dashboard),

        NavNode.Group(
            "Workloads",
            NavNode.Leaf("Containers", "/containers", IconRef.Container),
            NavNode.Leaf("Stacks", "/stacks", IconRef.Stack)),

        NavNode.Group(
            "Resources",
            NavNode.Leaf("Images", "/images", IconRef.Image),
            NavNode.Leaf("Volumes", "/volumes", IconRef.Volume),
            NavNode.Leaf("Networks", "/networks", IconRef.Network)),

        NavNode.Group(
            "Configuration",
            NavNode.Leaf("Registries", "/registries", IconRef.Registry),
            NavNode.Leaf("Hosts", "/hosts", IconRef.Host),
            NavNode.Leaf("Component gallery", "/gallery", IconRef.Build, debugOnly: true)),
    ];

    /// <summary>The manifest with debug-only entries filtered out for release builds.</summary>
    public static IReadOnlyList<NavNode> Visible(bool includeDebug)
        => includeDebug ? Nodes : Nodes.Select(Prune).Where(n => n is not null).Select(n => n!).ToList();

    static NavNode? Prune(NavNode node)
    {
        if (node.DebugOnly) return null;
        if (!node.IsGroup) return node;

        var kept = node.Children.Select(Prune).Where(c => c is not null).Select(c => c!).ToList();
        return kept.Count == 0 ? null : node with { Children = kept };
    }

    /// <summary>Every routable leaf, flattened. Used by the command palette and route validation.</summary>
    public static IEnumerable<NavNode> Leaves(bool includeDebug = true)
        => Flatten(Visible(includeDebug)).Where(n => n.Route is not null);

    static IEnumerable<NavNode> Flatten(IEnumerable<NavNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    /// <summary>The leaf matching a route, or null. Route matching is case-insensitive.</summary>
    public static NavNode? Find(string route)
        => Leaves().FirstOrDefault(n => string.Equals(n.Route, route, StringComparison.OrdinalIgnoreCase));
}
