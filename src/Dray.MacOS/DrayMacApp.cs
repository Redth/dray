using System.Collections.ObjectModel;
using AppKit;
using CoreGraphics;
using Dray.Core.Engine;
using Dray.Core.Navigation;
using Dray.Core.Shell;
using Dray.Ui.Services;
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
        MacOSFlyoutPage.SetSidebarItems(flyout, BuildSidebarItems());

        MacOSFlyoutPage.SetSidebarSelectionChanged(flyout, item =>
        {
            if (item.Tag as string == PickerTag)
            {
                // The picker is a control, not a place: the selection goes back to the page the
                // user is on, so the sidebar never claims they have navigated to "an engine".
                SelectRoute(flyout, _shell.Route);

                // The sidebar selects its first row by itself as it loads, and that row is this
                // one. Opening the menu then ran a menu's modal loop inside the app's launch, and
                // the window never appeared. Only a click or a key opens it.
                if (IsUserEvent(NSApplication.SharedApplication.CurrentEvent))
                {
                    NSApplication.SharedApplication.BeginInvokeOnMainThread(ShowEnginePicker);
                }

                return;
            }

            // ShellState owns the reentrancy guard, so a head may sync freely: an echo arriving
            // while the router is mid-navigation is swallowed rather than looping.
            if (item.Tag is string route) _shell.RequestNavigation(route);
        });

        _shell.RouteChanged += route =>
            NSApplication.SharedApplication.InvokeOnMainThread(() => SelectRoute(flyout, route));

        // The picker's title is the selected engine, so its row is reloaded when that changes —
        // and only then. Changed also fires for every container event, and reloading the outline
        // view for each would be churn the user can see.
        var engine = _services.GetRequiredService<EngineManager>();
        engine.Changed += () => NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (PickerTitle() == _picker.Title) return;

            // The handler reads its items once, when it connects, and after that only follows a
            // collection that says it changed. Setting a new list does nothing, so the row is
            // replaced in place — with itself, retitled — which is a change the handler hears.
            _picker.Title = PickerTitle();
            _sidebar[0] = _picker;
            SelectRoute(flyout, _shell.Route);
        });

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

    /// <summary>Whether AppKit is handling something the user did, rather than its own housekeeping.</summary>
    static bool IsUserEvent(NSEvent? e) => e?.Type is NSEventType.LeftMouseDown
        or NSEventType.LeftMouseUp
        or NSEventType.RightMouseDown
        or NSEventType.KeyDown;

    /// <summary>Marks the sidebar item that is the host picker rather than a route.</summary>
    const string PickerTag = "dray:engine-picker";

    /// <summary>The sidebar's rows, kept so the picker's row can be replaced in place.</summary>
    readonly ObservableCollection<MacOSSidebarItem> _sidebar = [];

    readonly MacOSSidebarItem _picker = new()
    {
        SystemImage = Icons.SfSymbol(IconRef.Host),
        Tag = PickerTag,
    };

    /// <summary>
    /// The selected engine, or what is happening to it. One line, because a sidebar item has one:
    /// what kind of container it runs is in the menu the item opens.
    /// </summary>
    string PickerTitle()
    {
        var engine = _services.GetRequiredService<EngineManager>();
        return engine.Transition ?? engine.Selected?.Name ?? "No engine";
    }

    ObservableCollection<MacOSSidebarItem> BuildSidebarItems()
    {
        // First, above the navigation: which engine is selected decides what every page below it
        // shows (DESIGN.md section 5).
        _picker.Title = PickerTitle();

        _sidebar.Clear();
        _sidebar.Add(_picker);
        foreach (var node in NavigationManifest.Visible(IsDebug)) _sidebar.Add(ToSidebarItem(node));

        return _sidebar;
    }

    static bool IsDebug =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>
    /// The engines, as a menu dropped from the picker's row the way a pop-up button drops its own.
    /// Each says what it is or what choosing it will do, in the sentence every head's picker uses,
    /// and choosing goes through <see cref="HostSwitcher"/> so a switch that stops something asks
    /// first here too.
    /// </summary>
    void ShowEnginePicker()
    {
        var engine = _services.GetRequiredService<EngineManager>();
        var switcher = _services.GetRequiredService<HostSwitcher>();
        var bridge = _services.GetRequiredService<IShellBridge>();

        var menu = new NSMenu { AutoEnablesItems = false };

        foreach (var host in engine.Hosts)
        {
            var id = host.Id;
            var item = new NSMenuItem($"{host.Name}  ·  {switcher.Describe(host)}")
            {
                State = engine.Selected?.Id == id ? NSCellStateValue.On : NSCellStateValue.Off,
                Enabled = engine.Transition is null,
            };

            item.Activated += (_, _) => _ = ChooseAsync(switcher, bridge, id);
            menu.AddItem(item);
        }

        if (menu.Count > 0) menu.AddItem(NSMenuItem.SeparatorItem);

        var manage = new NSMenuItem("Hosts and setup…");
        manage.Activated += (_, _) => _shell.RequestNavigation("/hosts");
        menu.AddItem(manage);

        // Under the row when it can be found, at the pointer when it cannot. The outline view is
        // flipped, so the row's bottom edge is its larger y.
        if (FindSidebar(NSApplication.SharedApplication.MainWindow?.ContentView) is { } sidebar && sidebar.RowCount > 0)
        {
            var row = sidebar.RectForRow(0);
            menu.PopUpMenu(null, new CGPoint(row.X + 8, row.Y + row.Height), sidebar);
        }
        else
        {
            menu.PopUpMenu(null, NSEvent.CurrentMouseLocation, null);
        }
    }

    static NSOutlineView? FindSidebar(NSView? view)
    {
        if (view is null) return null;
        if (view is NSOutlineView outline) return outline;

        foreach (var child in view.Subviews)
        {
            if (FindSidebar(child) is { } found) return found;
        }

        return null;
    }

    static async Task ChooseAsync(HostSwitcher switcher, IShellBridge bridge, string hostId)
    {
        if (await switcher.ChooseAsync(hostId) is { } problem)
        {
            await bridge.NotifyAsync("Could not switch engine", problem, NoticeKind.Warning);
        }
    }

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

        var settings = new MenuTarget(() => _shell.RequestNavigation("/settings"));
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
