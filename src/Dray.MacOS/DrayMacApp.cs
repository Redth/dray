using AppKit;
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
            NSApplication.SharedApplication.InvokeOnMainThread(() =>
                MacOSFlyoutPage.SelectSidebarItem(flyout, i => i.Tag as string == route));

        return flyout;
    }

    static List<MacOSSidebarItem> BuildSidebarItems()
    {
        var debug =
#if DEBUG
            true;
#else
            false;
#endif

        return [.. NavigationManifest.Visible(debug).Select(ToSidebarItem)];
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
