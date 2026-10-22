using AppKit;
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
                ShowEnginePicker();

                // The picker is a control, not a place: the selection goes back to the page the
                // user is on, so the sidebar never claims they have navigated to "an engine".
                MacOSFlyoutPage.SelectSidebarItem(flyout, i => i.Tag as string == _shell.Route);
                return;
            }

            // ShellState owns the reentrancy guard, so a head may sync freely: an echo arriving
            // while the router is mid-navigation is swallowed rather than looping.
            if (item.Tag is string route) _shell.RequestNavigation(route);
        });

        _shell.RouteChanged += route =>
            NSApplication.SharedApplication.InvokeOnMainThread(() =>
                MacOSFlyoutPage.SelectSidebarItem(flyout, i => i.Tag as string == route));

        // The picker's title is the selected engine, so the sidebar is rebuilt when that changes —
        // and only then. Changed also fires for every container event, and rebuilding the outline
        // view for each would be churn the user can see.
        var engine = _services.GetRequiredService<EngineManager>();
        engine.Changed += () => NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (PickerTitle() == _pickerTitle) return;

            MacOSFlyoutPage.SetSidebarItems(flyout, BuildSidebarItems());
            MacOSFlyoutPage.SelectSidebarItem(flyout, i => i.Tag as string == _shell.Route);
        });

        return flyout;
    }

    /// <summary>Marks the sidebar item that is the host picker rather than a route.</summary>
    const string PickerTag = "dray:engine-picker";

    /// <summary>The title the picker was last built with, so a rebuild happens only when it would differ.</summary>
    string? _pickerTitle;

    /// <summary>
    /// The selected engine, or what is happening to it. One line, because a sidebar item has one:
    /// what kind of container it runs is in the menu the item opens.
    /// </summary>
    string PickerTitle()
    {
        var engine = _services.GetRequiredService<EngineManager>();
        return engine.Transition ?? engine.Selected?.Name ?? "No engine";
    }

    List<MacOSSidebarItem> BuildSidebarItems()
    {
        var debug =
#if DEBUG
            true;
#else
            false;
#endif

        // First, above the navigation: which engine is selected decides what every page below it
        // shows (DESIGN.md section 5).
        _pickerTitle = PickerTitle();
        var picker = new MacOSSidebarItem
        {
            Title = _pickerTitle,
            SystemImage = Icons.SfSymbol(IconRef.Host),
            Tag = PickerTag,
        };

        return [picker, .. NavigationManifest.Visible(debug).Select(ToSidebarItem)];
    }

    /// <summary>
    /// The engines, as a menu at the pointer. Each says what it is or what choosing it will do, in
    /// the sentence every head's picker uses, and choosing goes through <see cref="HostSwitcher"/>
    /// so a switch that stops something asks first here too.
    /// </summary>
    void ShowEnginePicker()
    {
        var engine = _services.GetRequiredService<EngineManager>();
        var switcher = _services.GetRequiredService<HostSwitcher>();
        var bridge = _services.GetRequiredService<IShellBridge>();

        var menu = new NSMenu();
        _pickerTargets.Clear();

        foreach (var host in engine.Hosts)
        {
            var id = host.Id;
            var item = new NSMenuItem($"{host.Name}  ·  {switcher.Describe(host)}")
            {
                State = engine.Selected?.Id == id ? NSCellStateValue.On : NSCellStateValue.Off,
                Enabled = engine.Transition is null,
            };

            var choose = new Action(() => _ = ChooseAsync(switcher, bridge, id));
            item.Activated += (_, _) => choose();
            _pickerTargets.Add(new Retained(choose));
            menu.AddItem(item);
        }

        if (menu.Count > 0) menu.AddItem(NSMenuItem.SeparatorItem);

        var manage = new NSMenuItem("Hosts and setup…");
        manage.Activated += (_, _) => _shell.RequestNavigation("/hosts");
        menu.AddItem(manage);

        menu.PopUpMenu(null, NSEvent.CurrentMouseLocation, null);
    }

    static async Task ChooseAsync(HostSwitcher switcher, IShellBridge bridge, string hostId)
    {
        if (await switcher.ChooseAsync(hostId) is { } problem)
        {
            await bridge.NotifyAsync("Could not switch engine", problem, NoticeKind.Warning);
        }
    }

    // Menu callbacks, kept alive for as long as the menu that calls them (docs/NATIVE-SHELL.md 1.5b).
    readonly List<NSObject> _pickerTargets = [];

    sealed class Retained(Action action) : NSObject
    {
        public Action Action { get; } = action;
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
