using Dray.Core.Settings;
using Dray.Core.Kubernetes;
using System.Collections.ObjectModel;
using AppKit;
using Dray.Core.Engine;
using Dray.Core.Navigation;
using Dray.Core.Shell;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Platforms.MacOS.Platform;

namespace Dray.MacOS;

/// <summary>
/// The AppKit shell: a real <c>NSSplitViewController</c> sidebar over an <c>NSOutlineView</c>,
/// with a Blazor WebView as the detail.
/// <para>
/// The sidebar is built from <see cref="NavigationManifest"/> — the same list the web fallback nav
/// renders — so the two cannot drift (docs/NATIVE-SHELL.md section 2.2).
/// </para>
/// </summary>
public sealed class DrayMacApp : Application
{
    readonly IServiceProvider _services;
    readonly IShellState _shell;

    FlyoutPage? _flyout;
    BlazorContentPage? _content;
    MacStatusItem? _status;

    // Native menu targets are plain NSObjects; without a strong reference the runtime collects
    // them and the menu items silently stop working (docs/NATIVE-SHELL.md section 1.5).
    readonly List<NSObject> _menuTargets = [];

    public DrayMacApp(IServiceProvider services)
    {
        _services = services;
        _shell = services.GetRequiredService<IShellState>();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        _content = new BlazorContentPage(_services);
        _services.GetRequiredService<MacShellReadySignal>().Attach(_content);
        _flyout = BuildFlyout(_content);

        var window = new Window(_flyout)
        {
            Title = "Dray",
            Width = 1180,
            Height = 760,
            MinimumWidth = 720,
            MinimumHeight = 480,
        };

        NSApplication.SharedApplication.BeginInvokeOnMainThread(AddAppMenuItems);

        // The menu bar item answers "is anything running?" without the window, which is the
        // question this app gets asked most often and the one it should not need a window for.
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            _status = new MacStatusItem(_services.GetRequiredService<EngineManager>(), _shell));

        // Discover and connect in the background; the UI shows its connecting state until this
        // lands rather than blocking the window from appearing.
        _ = Task.Run(async () =>
        {
            try
            {
                await _services.GetRequiredService<EngineManager>().InitializeAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[dray:engine] initialise failed: {ex}");
            }
        });

        // The cluster beside it, independently: a cluster that does not answer must not hold the
        // engine back, nor the other way round.
        _ = Task.Run(async () =>
        {
            try
            {
                await _services.GetRequiredService<ClusterManager>().InitializeAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[dray:cluster] initialise failed: {ex}");
            }
        });

        return window;
    }

    FlyoutPage BuildFlyout(BlazorContentPage content)
    {
        var flyout = new FlyoutPage
        {
            Detail = new NavigationPage(content),
            Flyout = new ContentPage { Title = "Dray" },
            FlyoutLayoutBehavior = FlyoutLayoutBehavior.Split,
        };

        MacOSFlyoutPage.SetUseNativeSidebar(flyout, true);

        // The Kubernetes group is in the list only while the kubeconfig has a local cluster. The
        // handler follows this collection, not a new one, so the rows are replaced in place.
        var clusters = _services.GetRequiredService<ClusterManager>();
        var settings = _services.GetRequiredService<DraySettings>();
        _hasCluster = clusters.LocalContexts.Count > 0;
        Fill(_sidebar, _hasCluster, settings.HiddenRoutes);
        MacOSFlyoutPage.SetSidebarItems(flyout, _sidebar);

        clusters.Changed += () =>
        {
            // Changed fires for every watch event; the sidebar is touched only when the group comes or goes.
            var has = clusters.LocalContexts.Count > 0;
            if (has == _hasCluster) return;
            _hasCluster = has;

            NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            {
                Fill(_sidebar, has, settings.HiddenRoutes);
                SelectRoute(flyout, _shell.Route);
            });
        };

        // A page turned off in Settings leaves the sidebar at once.
        settings.Changed += () => NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            Fill(_sidebar, _hasCluster, settings.HiddenRoutes);
            SelectRoute(flyout, _shell.Route);
        });

        // The engine picker is not in here: it is the toolbar's leading item, above this list
        // (MacToolbarProjector), so every row of the sidebar is a place to go.
        MacOSFlyoutPage.SetSidebarSelectionChanged(flyout, item =>
        {
            // ShellState owns the reentrancy guard, so a head may sync freely: an echo arriving
            // while the router is mid-navigation is swallowed rather than looping.
            if (item.Tag is string route) _shell.RequestNavigation(route);
        });

        _shell.RouteChanged += route =>
            NSApplication.SharedApplication.InvokeOnMainThread(() => SelectRoute(flyout, route));

        return flyout;
    }

    /// <summary>
    /// Select the sidebar row for a route, or for the list a detail page belongs to. Selection is
    /// cleared first, because setting the row that is already the selected value is not a change
    /// and would leave a reloaded outline view showing nothing selected.
    /// </summary>
    static void SelectRoute(FlyoutPage flyout, string route)
    {
        var owner = NavigationManifest.Owner(route, IsDebug)?.Route;

        MacOSFlyoutPage.SetSelectedItem(flyout, null);
        if (owner is not null) MacOSFlyoutPage.SelectSidebarItem(flyout, i => i.Tag as string == owner);
    }

    readonly ObservableCollection<MacOSSidebarItem> _sidebar = [];

    bool _hasCluster;

    static void Fill(ObservableCollection<MacOSSidebarItem> rows, bool hasCluster, IReadOnlyCollection<string> hidden)
    {
        rows.Clear();
        foreach (var node in NavigationManifest.Visible(IsDebug, hasCluster, hidden)) rows.Add(ToSidebarItem(node));
    }

    static bool IsDebug =>
#if DEBUG
        true;
#else
        false;
#endif

    static MacOSSidebarItem ToSidebarItem(NavNode node) => node.IsGroup
        ? new MacOSSidebarItem
        {
            Title = node.Title,
            Children = [.. node.Children.Select(ToSidebarItem)],
        }
        : new MacOSSidebarItem
        {
            Title = node.Title,
            SystemImage = node.Icon is { } icon ? Icons.SfSymbol(icon) : null,
            Tag = node.Route,
        };

    void AddAppMenuItems()
    {
        var appMenu = NSApplication.SharedApplication.MainMenu?.ItemAt(0)?.Submenu;
        if (appMenu is null || _content is null) return;

        // After "About" and its separator.
        var index = Math.Min(2, (int)appMenu.Count);

        appMenu.InsertItem(NSMenuItem.SeparatorItem, index++);

        var settings = new MenuTarget(() => _shell.RequestNavigation(NavigationManifest.SettingsRoute));
        _menuTargets.Add(settings);

        var item = new NSMenuItem("Settings…", new ObjCRuntime.Selector("invoke:"), ",") { Target = settings };
        appMenu.InsertItem(item, index);
    }
}

/// <summary>An <c>NSMenuItem</c> target that runs a callback. Must be retained by the caller.</summary>
sealed class MenuTarget(Action action) : NSObject
{
    [Export("invoke:")]
    public void Invoke(NSObject sender) => action();
}
