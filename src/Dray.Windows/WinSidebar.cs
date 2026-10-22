using Dray.Core.Engine;
using Dray.Core.Model;
using Dray.Core.Navigation;
using Dray.Core.Shell;
using Dray.Ui.Services;
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
    readonly EngineManager _engine;
    readonly HostSwitcher _switcher;
    readonly IShellBridge _bridge;
    readonly List<Entry> _items = [];

    /// <summary>
    /// The host picker: the first item in the pane, above the navigation, because which engine
    /// is selected decides what every page below it shows (DESIGN.md section 5).
    /// <para>
    /// An item that opens a menu rather than a combo box, so it survives the pane collapsing to
    /// icons — the icon stays and still opens the list — and so the list can carry a sentence per
    /// engine, which a combo box's items cannot.
    /// </para>
    /// </summary>
    readonly Xaml.Controls.NavigationViewItem _picker;
    readonly Xaml.Controls.TextBlock _pickerName = new() { TextTrimming = Xaml.TextTrimming.CharacterEllipsis };
    readonly Xaml.Controls.TextBlock _pickerDetail = new() { TextTrimming = Xaml.TextTrimming.CharacterEllipsis };

    public WinSidebar(
        Xaml.Controls.NavigationView nav,
        IShellState shell,
        IPlatformTheme theme,
        EngineManager engine,
        HostSwitcher switcher,
        IShellBridge bridge,
        bool debug)
    {
        _nav = nav;
        _shell = shell;
        _theme = theme;
        _engine = engine;
        _switcher = switcher;
        _bridge = bridge;

        _pickerDetail.Style = (Xaml.Style)Xaml.Application.Current.Resources["CaptionTextBlockStyle"];
        _pickerDetail.Foreground = (Xaml.Media.Brush)Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"];

        var label = new Xaml.Controls.StackPanel();
        label.Children.Add(_pickerName);
        label.Children.Add(_pickerDetail);

        _picker = new Xaml.Controls.NavigationViewItem
        {
            Content = label,
            SelectsOnInvoked = false,
            Icon = WinIcons.Create(IconRef.Host, _theme.Current),

            // Two lines, so taller than the 32 the pane's other items get.
            MinHeight = 48,
        };

        Xaml.Automation.AutomationProperties.SetName(_picker, "Engine");
        nav.MenuItems.Add(_picker);
        nav.MenuItems.Add(new Xaml.Controls.NavigationViewItemSeparator());
        UpdatePicker();

        foreach (var node in NavigationManifest.Visible(debug))
        {
            if (node.IsGroup)
            {
                nav.MenuItems.Add(new Xaml.Controls.NavigationViewItemHeader { Content = node.Title });
                foreach (var child in node.Children) nav.MenuItems.Add(Build(child));
            }
            else
            {
                nav.MenuItems.Add(Build(node));
            }
        }

        // ItemInvoked rather than SelectionChanged: it fires for the user and not for the
        // programmatic selection below, so following the router never asks the router to navigate.
        // ShellState's guard would swallow the echo anyway; this just never sends one.
        nav.ItemInvoked += OnItemInvoked;

        _shell.RouteChanged += OnRouteChanged;
        _theme.Changed += OnThemeChanged;
        _engine.Changed += OnEngineChanged;

        Select(_shell.Route);
    }

    void OnEngineChanged() => _nav.DispatcherQueue.TryEnqueue(UpdatePicker);

    /// <summary>The selected engine and what it is, or what is happening to it.</summary>
    void UpdatePicker()
    {
        var selected = _engine.Selected;

        _pickerName.Text = selected?.Name ?? "No engine";
        _pickerDetail.Text = _engine.Transition ?? (selected is null ? "Choose or set one up" : Describe(selected));

        Xaml.Controls.ToolTipService.SetToolTip(_picker, selected is null ? "Choose an engine" : $"{selected.Name} — {Describe(selected)}");
    }

    /// <summary>
    /// What an engine is, in the words that decide whether it is the one wanted: the kind of
    /// container it runs, then its state when that is anything but connected. Words throughout,
    /// never a colour, so the picker reads the same in high contrast (DESIGN.md 2.4).
    /// </summary>
    string Describe(DockerHost host)
    {
        if (_engine.SwitchNeededFor(host) is { } mode) return $"Switch Docker Desktop to {DockerDesktop.Label(mode)}";
        if (_engine.StartNeededFor(host)) return "Docker Desktop is not running";

        var kind = host.Capabilities.ContainerOs switch
        {
            ContainerOs.Linux => "Linux containers",
            ContainerOs.Windows => "Windows containers",
            _ => host.DesktopMode is { } desktop ? DockerDesktop.Label(desktop) : host.Endpoint.Display,
        };

        return host.State switch
        {
            HostConnectionState.Connected => kind,
            HostConnectionState.Connecting => $"{kind} · connecting",
            HostConnectionState.Degraded => $"{kind} · degraded",
            HostConnectionState.Unreachable => $"{kind} · unreachable",
            _ when host.StartsOnConnect => $"{kind} · starts when chosen",
            _ => kind,
        };
    }

    /// <summary>
    /// The list of engines, built when it opens so it is never stale. Each is a radio item: one
    /// engine is selected at a time, and the menu says which.
    /// </summary>
    void ShowPicker()
    {
        var menu = new Xaml.Controls.MenuFlyout { Placement = Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };

        foreach (var host in _engine.Hosts)
        {
            var id = host.Id;
            var item = new Xaml.Controls.RadioMenuFlyoutItem
            {
                Text = $"{host.Name}  ·  {Describe(host)}",
                GroupName = "engines",
                IsChecked = _engine.Selected?.Id == id,
                IsEnabled = _engine.Transition is null,
            };

            item.Click += (_, _) => _ = ChooseAsync(id);
            menu.Items.Add(item);
        }

        if (menu.Items.Count > 0) menu.Items.Add(new Xaml.Controls.MenuFlyoutSeparator());

        var manage = new Xaml.Controls.MenuFlyoutItem
        {
            Text = "Hosts and setup…",
            Icon = WinIcons.Create(IconRef.Host, _theme.Current),
        };
        manage.Click += (_, _) => _shell.RequestNavigation("/hosts");
        menu.Items.Add(manage);

        menu.ShowAt(_picker);
    }

    async Task ChooseAsync(string hostId)
    {
        if (await _switcher.ChooseAsync(hostId) is { } problem)
        {
            // Said where the user is likely to look next, and on the Hosts page if they go there.
            await _bridge.NotifyAsync("Could not switch engine", problem, NoticeKind.Warning);
        }
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
        if (e.InvokedItemContainer == _picker)
        {
            ShowPicker();
            return;
        }

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
        _picker.Icon = WinIcons.Create(IconRef.Host, _theme.Current);

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
        _engine.Changed -= OnEngineChanged;
    }
}
