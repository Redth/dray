using Dray.Core.Shell;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using Xaml = Microsoft.UI.Xaml;

namespace Dray.WinUI;

/// <summary>
/// The window's whole content: a placeholder in MAUI's tree that <see cref="NativeShellHandler"/>
/// turns into WinUI.
/// <para>
/// The Blazor WebView is created here, as a MAUI element, because that is what gives it the Blazor
/// hosting — the service provider, the dispatcher, the static assets. Everything around it is WinUI
/// built directly, because MAUI has no element for a <c>NavigationView</c> sidebar.
/// </para>
/// </summary>
public sealed class NativeShell : View
{
    public NativeShell()
    {
        Body = new BlazorWebView { HostPage = "wwwroot/index.html" };
        Body.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = typeof(Dray.Ui.App),
        });

        WebViewSetup.Apply(Body);
        WebViewSetup.MakeTransparent(Body);

        // Lays out natively inside the shell's grid, but belongs to this element in MAUI's logical
        // tree, so it resolves its window and dispatcher the way any page content would.
        Body.Parent = this;
    }

    public BlazorWebView Body { get; }
}

/// <summary>
/// Builds the WinUI shell around the WebView, and does the things that make the seam invisible on
/// Windows (docs/NATIVE-SHELL.md section 1.3 is the macOS list; section 5 is this one).
/// </summary>
public sealed class NativeShellHandler() : ViewHandler<NativeShell, Xaml.Controls.NavigationView>(Mapper)
{
    public static readonly IPropertyMapper<NativeShell, NativeShellHandler> Mapper =
        new PropertyMapper<NativeShell, NativeShellHandler>(ViewMapper);

    /// <summary>
    /// The margin content keeps from the edges of its pane. Pushed into CSS as
    /// <c>--chrome-inset</c>, so the toolbar row above the WebView and the content inside it start
    /// at the same x — one number, owned here, rather than two that happen to agree.
    /// </summary>
    public const double ContentInset = 16;

    WinSidebar? _sidebar;
    WinToolbarProjector? _toolbar;
    Xaml.FrameworkElement? _webView;
    Xaml.Controls.Grid? _loadingOverlay;
    bool _revealed;

    protected override Xaml.Controls.NavigationView CreatePlatformView()
    {
        var nav = new Xaml.Controls.NavigationView
        {
            PaneDisplayMode = Xaml.Controls.NavigationViewPaneDisplayMode.Auto,
            IsSettingsVisible = false,
            IsBackButtonVisible = Xaml.Controls.NavigationViewBackButtonVisible.Collapsed,
            IsTitleBarAutoPaddingEnabled = false,
            OpenPaneLength = WinPaneResizer.SavedWidth,
        };

        // MAUI sets this to 0 in the app's resources, which left each item as tall as its label: a
        // 19-pixel selection highlight with no room around the icon and text. 32 gives them some,
        // a little under WinUI's own 36. Scoped to this pane.
        nav.Resources["NavigationViewItemOnLeftMinHeight"] = 32.0;
        return nav;
    }

    protected override void ConnectHandler(Xaml.Controls.NavigationView nav)
    {
        base.ConnectHandler(nav);

        var services = MauiContext!.Services;
        var shell = services.GetRequiredService<IShellState>();
        var theme = services.GetRequiredService<IPlatformTheme>();

        var toolbarRow = new Xaml.Controls.Grid
        {
            Padding = new Xaml.Thickness(ContentInset, 8, ContentInset - 8, 8),
            MinHeight = 56,
        };

        // Before ToPlatform, which is what starts the WebView initialising.
        WebViewSetup.SeedTheme(VirtualView.Body, theme);
        _webView = (Xaml.FrameworkElement)VirtualView.Body.ToPlatform(MauiContext);

        // Hidden until Blazor has painted, then faded in, so the first thing on screen is the
        // finished page rather than an empty WebView filling in.
        _webView.Opacity = 0;
        _webView.OpacityTransition = new Xaml.ScalarTransition { Duration = TimeSpan.FromMilliseconds(220) };

        _loadingOverlay = BuildLoadingOverlay();

        // Clear of the pane's resize grip, which has to be beside the WebView to own the cursor.
        // The page's inset is the same amount smaller (WinTheme), so its content still starts
        // where the toolbar row's does.
        var body = new Xaml.Controls.Grid { Margin = new Xaml.Thickness(WinPaneResizer.GripWidth, 0, 0, 0) };
        body.Children.Add(_webView);
        body.Children.Add(_loadingOverlay);

        var content = new Xaml.Controls.Grid();
        content.RowDefinitions.Add(new Xaml.Controls.RowDefinition { Height = Xaml.GridLength.Auto });
        content.RowDefinitions.Add(new Xaml.Controls.RowDefinition { Height = new Xaml.GridLength(1, Xaml.GridUnitType.Star) });
        Xaml.Controls.Grid.SetRow(toolbarRow, 0);
        Xaml.Controls.Grid.SetRow(body, 1);
        content.Children.Add(toolbarRow);
        content.Children.Add(body);

        // Last, so it is above the WebView: the pane's edge is the content area's leading edge.
        var resizer = new WinPaneResizer(nav);
        Xaml.Controls.Grid.SetRowSpan(resizer, 2);
        content.Children.Add(resizer);

        nav.Content = content;
        nav.Loaded += (_, _) =>
        {
            ClearHostLayer(nav);
            KeepContentUnderTitleBar(content);
        };


        // Before the sidebar and toolbar draw their first icons, so they draw them in the theme
        // the NavigationView is actually in.
        (theme as WinTheme)?.Follow(nav);

        // Back and the pane toggle live in the title bar, so nothing above the sidebar items comes
        // and goes with the page.
        var titleBar = services.GetRequiredService<WinTitleBar>();
        titleBar.Attach(nav);

        _sidebar = new WinSidebar(nav, shell, theme, debug: services.GetRequiredService<ShellCapabilities>().IsDebugBuild);
        _toolbar = new WinToolbarProjector(toolbarRow, titleBar, _webView, shell, theme);

        services.GetRequiredService<WinShellReadySignal>().Attach(this);

        // Without this, a Blazor startup exception leaves a spinner on screen forever. Reveal
        // regardless after a bounded wait.
        var timer = nav.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(15);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Reveal();
        timer.Start();
    }

    protected override void DisconnectHandler(Xaml.Controls.NavigationView nav)
    {
        _sidebar?.Dispose();
        _toolbar?.Dispose();
        VirtualView.Body.Handler?.DisconnectHandler();
        base.DisconnectHandler(nav);
    }

    /// <summary>Called once Blazor has rendered its first frame. Safe from any thread.</summary>
    public void Reveal()
    {
        if (PlatformView is not { } nav) return;

        nav.DispatcherQueue.TryEnqueue(() =>
        {
            if (_revealed) return;
            _revealed = true;

            if (_loadingOverlay is not null) _loadingOverlay.Visibility = Xaml.Visibility.Collapsed;
            if (_webView is not null) _webView.Opacity = 1;
        });
    }

    /// <summary>
    /// MAUI's window root is a NavigationView of its own, and its content grid paints a layer fill
    /// with a border under everything this shell draws. That puts the sidebar on a layer rather
    /// than on Mica, and the content area on two. The page cannot match two: the WebView's
    /// transparent pixels show the window's backdrop, not the XAML beneath it, so it paints the
    /// one layer the content area should have (<see cref="WinTheme"/>). Clearing MAUI's leaves
    /// exactly that one, here and in the page alike.
    /// <para>
    /// A local value, so it outlasts the theme resource it replaces when the theme changes.
    /// </para>
    /// </summary>
    static void ClearHostLayer(Xaml.Controls.NavigationView nav)
    {
        for (var o = Xaml.Media.VisualTreeHelper.GetParent(nav); o is not null; o = Xaml.Media.VisualTreeHelper.GetParent(o))
        {
            if (o is Xaml.Controls.Grid { Name: "ContentGrid" } grid)
            {
                grid.Background = null;
                grid.BorderBrush = null;
            }
        }
    }

    /// <summary>
    /// The NavigationView pads its content grid by the title bar's height, on the assumption that
    /// it is drawn under the title bar, and re-pads it whenever that height changes, even with
    /// <c>IsTitleBarAutoPaddingEnabled</c> off. MAUI already lays the page out below its title bar,
    /// so the padding left the content area a title bar's height lower than the sidebar beside it.
    /// Held at zero, so the toolbar row starts where the sidebar does.
    /// </summary>
    static void KeepContentUnderTitleBar(Xaml.FrameworkElement content)
    {
        if (Ancestor(content, "ContentGrid") is not { } grid) return;

        grid.Margin = new Xaml.Thickness(0);
        grid.RegisterPropertyChangedCallback(Xaml.FrameworkElement.MarginProperty, (_, _) =>
        {
            if (grid.Margin != new Xaml.Thickness(0)) grid.Margin = new Xaml.Thickness(0);
        });
    }

    /// <summary>
    /// The nearest element above <paramref name="element"/> with this name: the NavigationView
    /// template part that holds its content. Searched upward because the pane's items have parts
    /// of the same name.
    /// </summary>
    static Xaml.FrameworkElement? Ancestor(Xaml.DependencyObject element, string name)
    {
        for (var o = Xaml.Media.VisualTreeHelper.GetParent(element); o is not null; o = Xaml.Media.VisualTreeHelper.GetParent(o))
        {
            if (o is Xaml.FrameworkElement { Name: var found } match && found == name) return match;
        }

        return null;
    }

    /// <summary>
    /// A native overlay rather than a web one: it has to be on screen before the WebView has
    /// anything to show. No background of its own — the window's material is the ground it sits on.
    /// </summary>
    static Xaml.Controls.Grid BuildLoadingOverlay()
    {
        var overlay = new Xaml.Controls.Grid();
        overlay.Children.Add(new Xaml.Controls.ProgressRing
        {
            IsActive = true,
            Width = 24,
            Height = 24,
            HorizontalAlignment = Xaml.HorizontalAlignment.Center,
            VerticalAlignment = Xaml.VerticalAlignment.Center,
        });
        return overlay;
    }
}
