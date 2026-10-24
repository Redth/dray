using System.Collections.ObjectModel;
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
            // ShellState owns the reentrancy guard, so a head may sync freely: an echo arriving
            // while the router is mid-navigation is swallowed rather than looping.
            if (item.Tag is string route) _shell.RequestNavigation(route);
        });

        _shell.RouteChanged += route =>
            NSApplication.SharedApplication.InvokeOnMainThread(() => SelectRoute(flyout, route));

        // The host picker, in the toolbar over the sidebar: a control rather than a place, so it
        // is not a row among the places, and it sits where macOS puts what scopes a window's
        // content — above the sidebar, beside the window's own buttons.
        SyncHostPicker();
        MacOSToolbar.SetSidebarLayout(content, [MacOSToolbarLayoutItem.Menu(_hostPicker)]);

        // Changed fires for every container event; the picker is only touched when what it shows
        // has changed.
        var engine = _services.GetRequiredService<EngineManager>();
        engine.Changed += () => NSApplication.SharedApplication.BeginInvokeOnMainThread(SyncHostPicker);

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

    readonly MacOSMenuToolbarItem _hostPicker = new()
    {
        Icon = Icons.SfSymbol(IconRef.Host),
        ShowsTitle = true,
        ShowsIndicator = true,
    };

    /// <summary>What the picker last showed, so a container event that changes none of it is ignored.</summary>
    string? _hostPickerState;

    /// <summary>
    /// The selected engine, or what is happening to it. One line: what kind of container each host
    /// runs is in the menu the picker opens.
    /// </summary>
    string PickerTitle()
    {
        var engine = _services.GetRequiredService<EngineManager>();
        return engine.Transition ?? engine.Selected?.Name ?? "No engine";
    }

    /// <summary>The picker's rows, as data: a host and its sentence, whether it is chosen, whether it can be.</summary>
    IReadOnlyList<(string Id, string Text, bool Chosen, bool Enabled)> PickerRows()
    {
        var engine = _services.GetRequiredService<EngineManager>();
        var switcher = _services.GetRequiredService<HostSwitcher>();

        return
        [
            .. engine.Hosts.Select(host => (host.Id, $"{host.Name}  ·  {switcher.Describe(host)}", engine.Selected?.Id == host.Id, engine.Transition is null)),
        ];
    }

    /// <summary>
    /// Bring the picker up to date, in both the places it lives.
    /// <para>
    /// The managed item, because the platform builds the native one from it whenever the toolbar is
    /// rebuilt — every page whose chrome has a different shape. The native one too, because the
    /// platform builds it once and does not follow the managed item after that: without this the
    /// title and the check stayed on the host the app opened with.
    /// </para>
    /// </summary>
    void SyncHostPicker()
    {
        var title = PickerTitle();
        var rows = PickerRows();
        var state = title + "\n" + string.Join("\n", rows.Select(r => $"{r.Id}|{r.Text}|{r.Chosen}|{r.Enabled}"));

        if (state == _hostPickerState) return;
        _hostPickerState = state;

        _hostPicker.Text = title;
        _hostPicker.Items.Clear();

        foreach (var row in rows)
        {
            var id = row.Id;
            _hostPicker.Items.Add(new MacOSMenuItem
            {
                Text = row.Text,
                IsChecked = row.Chosen,
                IsEnabled = row.Enabled,
                Command = new Microsoft.Maui.Controls.Command(() => Choose(id)),
            });
        }

        if (rows.Count > 0) _hostPicker.Items.Add(new MacOSMenuItem { IsSeparator = true });
        _hostPicker.Items.Add(new MacOSMenuItem { Text = "Hosts and setup…", Command = new Microsoft.Maui.Controls.Command(() => _shell.RequestNavigation("/hosts")) });

        if (NativeHostPicker() is { } native)
        {
            native.Label = title;
            native.ToolTip = title;
            native.Title = title;
            native.Menu = BuildPickerMenu(rows);
        }
    }

    /// <summary>
    /// The picker's native item: the one menu in the toolbar's sidebar section, which is everything
    /// before the separator that tracks the sidebar's edge.
    /// </summary>
    NSMenuToolbarItem? NativeHostPicker()
        => (_flyout?.Window?.Handler?.PlatformView as NSWindow ?? NSApplication.SharedApplication.MainWindow)?.Toolbar?.Items
            .TakeWhile(i => i is not NSTrackingSeparatorToolbarItem)
            .OfType<NSMenuToolbarItem>()
            .FirstOrDefault();

    /// <summary>The same rows the managed item holds, as the native menu shows them.</summary>
    NSMenu BuildPickerMenu(IReadOnlyList<(string Id, string Text, bool Chosen, bool Enabled)> rows)
    {
        var menu = new NSMenu { AutoEnablesItems = false };

        foreach (var row in rows)
        {
            var id = row.Id;
            var item = new NSMenuItem(row.Text)
            {
                State = row.Chosen ? NSCellStateValue.On : NSCellStateValue.Off,
                Enabled = row.Enabled,
            };

            item.Activated += (_, _) => Choose(id);
            menu.AddItem(item);
        }

        if (menu.Count > 0) menu.AddItem(NSMenuItem.SeparatorItem);

        var manage = new NSMenuItem("Hosts and setup…");
        manage.Activated += (_, _) => _shell.RequestNavigation("/hosts");
        menu.AddItem(manage);

        return menu;
    }

    /// <summary>Choosing goes through <see cref="HostSwitcher"/>, so a switch that stops something asks first here too.</summary>
    void Choose(string hostId)
        => _ = ChooseAsync(_services.GetRequiredService<HostSwitcher>(), _services.GetRequiredService<IShellBridge>(), hostId);

    static ObservableCollection<MacOSSidebarItem> BuildSidebarItems()
        => [.. NavigationManifest.Visible(IsDebug).Select(ToSidebarItem)];

    static bool IsDebug =>
#if DEBUG
        true;
#else
        false;
#endif

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
