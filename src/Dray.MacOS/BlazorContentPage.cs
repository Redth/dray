using AppKit;
using CoreGraphics;
using Dray.Core.Shell;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Platforms.MacOS.Controls;
using WebKit;

namespace Dray.MacOS;

/// <summary>
/// Hosts the Blazor WebView as the detail pane, and does the four non-obvious things that make the
/// seam between AppKit and the WebView invisible (docs/NATIVE-SHELL.md section 1.3).
/// </summary>
public sealed class BlazorContentPage : ContentPage
{
    /// <summary>Clears the unified titlebar so content does not slide under the toolbar.</summary>
    const double TitlebarInset = 52;

    readonly MacOSBlazorWebView _webView;
    readonly IShellState _shell;
    readonly MacToolbarProjector _toolbar;

    NSView? _loadingOverlay;
    TitlebarDragView? _dragOverlay;
    bool _revealed;

    public BlazorContentPage(IServiceProvider services)
    {
        Title = string.Empty;
        _shell = services.GetRequiredService<IShellState>();

        // Whatever the current page declared, rendered as real NSToolbar items.
        _toolbar = new MacToolbarProjector(this, _shell);

        _webView = new MacOSBlazorWebView
        {
            HostPage = "wwwroot/index.html",
            ContentInsets = new Thickness(0, TitlebarInset, 0, 0),
            HideScrollPocketOverlay = true,
            Opacity = 0,
        };

        _webView.RootComponents.Add(new BlazorRootComponent
        {
            Selector = "#app",
            ComponentType = typeof(Dray.Ui.App),
        });

        Content = _webView;

        _webView.HandlerChanged += (_, _) =>
        {
            if (_webView.Handler is null) return;

            MakeWebViewTransparent();
            AddLoadingOverlay();
            Dispatcher.Dispatch(AddTitlebarDragOverlay);
        };

        // Without this, a Blazor startup exception leaves a permanently blank window with a
        // spinner on it. Reveal regardless after a bounded wait.
        Dispatcher.StartTimer(TimeSpan.FromSeconds(15), () =>
        {
            Reveal();
            return false;
        });
    }

    /// <summary>Called once Blazor has rendered its first frame.</summary>
    public void Reveal()
    {
        if (_revealed) return;
        _revealed = true;

        if (_webView.Handler?.PlatformView is WKWebView native) native.Hidden = false;

        _loadingOverlay?.RemoveFromSuperview();
        _loadingOverlay = null;

        _ = _webView.FadeToAsync(1, 220, Easing.CubicOut);
    }

    /// <summary>
    /// WKWebView paints an opaque background by default, which would hide the native window colour
    /// the whole theming approach depends on. There is no public API for this; KVC is the way.
    /// </summary>
    void MakeWebViewTransparent()
    {
        if (_webView.Handler?.PlatformView is not WKWebView webView) return;

        webView.SetValueForKey(NSObject.FromObject(false), new NSString("drawsBackground"));

        // Stay hidden until Blazor has painted, or the user sees a flash of empty WebView.
        webView.Hidden = true;
    }

    /// <summary>
    /// A native overlay rather than a web one: it has to be on screen before the WebView exists.
    /// </summary>
    void AddLoadingOverlay()
    {
        if (_webView.Handler?.PlatformView is not NSView native) return;

        var superview = native.Superview ?? native;

        _loadingOverlay = new NSView(superview.Bounds)
        {
            AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable,
            WantsLayer = true,
        };
        _loadingOverlay.Layer!.BackgroundColor = NSColor.WindowBackground.CGColor;

        var spinner = new NSProgressIndicator(new CGRect(0, 0, 24, 24))
        {
            Style = NSProgressIndicatorStyle.Spinning,
            IsDisplayedWhenStopped = false,
            ControlSize = NSControlSize.Small,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        spinner.StartAnimation(null);

        _loadingOverlay.AddSubview(spinner);
        spinner.CenterXAnchor.ConstraintEqualTo(_loadingOverlay.CenterXAnchor).Active = true;
        spinner.CenterYAnchor.ConstraintEqualTo(_loadingOverlay.CenterYAnchor).Active = true;

        superview.AddSubview(_loadingOverlay, NSWindowOrderingMode.Above, native);
    }

    /// <summary>
    /// The WKWebView swallows mouse events across the titlebar strip, so the window cannot be
    /// dragged by its own title bar until a transparent view above it hands them back to AppKit.
    /// </summary>
    void AddTitlebarDragOverlay()
    {
        if (_dragOverlay is not null) return;
        if (_webView.Handler?.PlatformView is not NSView native) return;
        if (Window?.Handler?.PlatformView is not NSWindow window) return;

        var titlebarHeight = window.Frame.Height - window.ContentLayoutRect.GetMaxY();
        if (titlebarHeight < 20) titlebarHeight = (System.Runtime.InteropServices.NFloat)TitlebarInset;

        var container = native.Superview ?? native;

        _dragOverlay = new TitlebarDragView(new CGRect(0, container.Bounds.Height - titlebarHeight, container.Bounds.Width, titlebarHeight))
        {
            AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.MinYMargin,
        };

        container.AddSubview(_dragOverlay, NSWindowOrderingMode.Above, native);
    }
}

/// <summary>A transparent view that forwards its drags to the window.</summary>
sealed class TitlebarDragView(CGRect frame) : NSView(frame)
{
    public override void MouseDown(NSEvent theEvent) => Window?.PerformWindowDrag(theEvent);

    // Double-click on a title bar zooms the window; keep that.
    public override void MouseUp(NSEvent theEvent)
    {
        if (theEvent.ClickCount == 2) Window?.Zoom(this);
        else base.MouseUp(theEvent);
    }
}
