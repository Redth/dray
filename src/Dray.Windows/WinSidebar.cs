using Dray.Core.Kubernetes;
using Dray.Core.Navigation;
using Dray.Core.Shell;
using Xaml = Microsoft.UI.Xaml;

namespace Dray.WinUI;

/// <summary>
/// Fills a <c>NavigationView</c> from <see cref="NavigationManifest"/> and keeps its selection in
/// step with the router.
/// <para>
/// The same list the macOS <c>NSOutlineView</c> and the web fallback nav render, so the three cannot
/// drift (docs/NATIVE-SHELL.md section 2.2). A top-level group becomes a section header, which is
/// how Windows' own apps divide a navigation pane; WinUI hides headers by itself when the pane
/// collapses to icons.
/// </para>
/// </summary>
public sealed class WinSidebar : IDisposable
{
    readonly Xaml.Controls.NavigationView _nav;
    readonly IShellState _shell;
    readonly IPlatformTheme _theme;
    readonly ClusterManager _clusters;
    readonly List<Entry> _items = [];
    readonly bool _debug;
    bool _hasCluster;

    public WinSidebar(
        Xaml.Controls.NavigationView nav,
        IShellState shell,
        IPlatformTheme theme,
        ClusterManager clusters,
        bool debug)
    {
        _nav = nav;
        _shell = shell;
        _theme = theme;

        _debug = debug;
        _clusters = clusters;
        _hasCluster = clusters.LocalContexts.Count > 0;

        // The engine picker is not in here: it is in the title bar (WinEnginePicker), so every item
        // in the pane is a place to go.
        Fill();
        _clusters.Changed += OnClustersChanged;

        // ItemInvoked rather than SelectionChanged: it fires for the user and not for the
        // programmatic selection below, so following the router never asks the router to navigate.
        // ShellState's guard would swallow the echo anyway; this just never sends one.
        nav.ItemInvoked += OnItemInvoked;

        _shell.RouteChanged += OnRouteChanged;
        _theme.Changed += OnThemeChanged;

        Select(_shell.Route);
    }

    /// <summary>The pane's items from the manifest — with the Kubernetes group only while there is a local cluster.</summary>
    void Fill()
    {
        _nav.MenuItems.Clear();
        _items.Clear();

        foreach (var node in NavigationManifest.Visible(_debug, _hasCluster))
        {
            if (node.IsGroup)
            {
                _nav.MenuItems.Add(new Xaml.Controls.NavigationViewItemHeader { Content = node.Title });
                foreach (var child in node.Children) _nav.MenuItems.Add(Build(child));
            }
            else
            {
                _nav.MenuItems.Add(Build(node));
            }
        }
    }

    /// <summary>ClusterManager raises for every watch event; the pane is rebuilt only when the group comes or goes.</summary>
    void OnClustersChanged()
    {
        var has = _clusters.LocalContexts.Count > 0;
        if (has == _hasCluster) return;
        _hasCluster = has;

        _nav.DispatcherQueue.TryEnqueue(() =>
        {
            Fill();
            Select(_shell.Route);
        });
    }

    Xaml.Controls.NavigationViewItem Build(NavNode node)
    {
        if (node.IsGroup)
        {
            // Only reachable for a group nested inside a group. The manifest has none today, but a
            // second level is an expandable item on Windows rather than a second header.
            var group = new Xaml.Controls.NavigationViewItem { Content = node.Title, SelectsOnInvoked = false };
            foreach (var child in node.Children) group.MenuItems.Add(Build(child));
            return group;
        }

        var item = new Xaml.Controls.NavigationViewItem
        {
            Content = node.Title,
            Tag = node.Route,
        };

        if (node.Icon is { } icon) item.Icon = WinIcons.Create(icon, _theme.Current);

        _items.Add(new Entry(item, node.Route!, node.Icon));
        return item;
    }

    void OnItemInvoked(Xaml.Controls.NavigationView sender, Xaml.Controls.NavigationViewItemInvokedEventArgs e)
    {
        if (e.InvokedItemContainer?.Tag is string route) _shell.RequestNavigation(route);
    }

    void OnRouteChanged(string route) => _nav.DispatcherQueue.TryEnqueue(() => Select(route));

    /// <summary>
    /// The entry for a route, or for the list a detail page belongs to.
    /// <para>
    /// <c>/containers/abc</c> keeps Containers selected rather than clearing the pane: the user is
    /// still in Containers, and an empty selection reads as having left the app's structure. The
    /// root is matched exactly, or Dashboard would claim every route.
    /// </para>
    /// </summary>
    void Select(string route)
    {
        var match = _items.FirstOrDefault(i => string.Equals(i.Route, route, StringComparison.OrdinalIgnoreCase))
            ?? _items
                .Where(i => i.Route != NavigationManifest.HomeRoute
                    && route.StartsWith(i.Route + "/", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => i.Route.Length)
                .FirstOrDefault();

        _nav.SelectedItem = match?.Item;
    }

    void OnThemeChanged() => _nav.DispatcherQueue.TryEnqueue(() =>
    {
        foreach (var entry in _items)
        {
            if (entry.Icon is { } icon) entry.Item.Icon = WinIcons.Create(icon, _theme.Current);
        }
    });

    sealed record Entry(Xaml.Controls.NavigationViewItem Item, string Route, IconRef? Icon);

    public void Dispose()
    {
        _nav.ItemInvoked -= OnItemInvoked;
        _shell.RouteChanged -= OnRouteChanged;
        _theme.Changed -= OnThemeChanged;
        _clusters.Changed -= OnClustersChanged;
    }
}
