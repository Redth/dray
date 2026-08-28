using AppKit;
using CoreGraphics;
using Dray.Core.Shell;
using Dray.Ui.Services;
using Microsoft.Maui.Platform;
using Microsoft.Maui.Platforms.MacOS.Controls;

namespace Dray.MacOS;

/// <summary>
/// The web half of a native dialog: a <c>BlazorWebView</c> sized to fit inside an alert.
/// <para>
/// A second WebView rather than a second app. It is built from the same service provider as the
/// window behind it, so the component inside talks to the same <c>EngineManager</c> and the same
/// store — another view of the running app, not a copy of it.
/// </para>
/// </summary>
public sealed class MacDialogBody : IDisposable
{
    readonly IServiceProvider _services;
    readonly MacOSBlazorWebView _webView;

    public NSView View { get; }

    public MacDialogBody(IServiceProvider services, DialogRequest request, CGSize size)
    {
        _services = services;

        // Set before the view is created: the surface reads it as it mounts, and a request arriving
        // afterwards would render an empty dialog for a frame.
        services.GetRequiredService<NativeDialogState>().Set(request);

        _webView = new MacOSBlazorWebView
        {
            HostPage = "wwwroot/dialog.html",
            WidthRequest = size.Width,
            HeightRequest = size.Height,
        };

        _webView.RootComponents.Add(new BlazorRootComponent
        {
            Selector = "#dialog",
            ComponentType = typeof(Dray.Ui.Components.DialogSurface),
        });

        // From the window rather than from DI. IMauiContext is created per window and is not in
        // the app's service collection at all — asking for it there throws, which is what the
        // first run of this did.
        var context = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.MauiContext
            ?? throw new InvalidOperationException("No window to present a dialog from.");

        View = (NSView)_webView.ToPlatform(context);

        // An alert lays its accessory view out from the frame it already has, so the size has to be
        // on the NSView and not only on the MAUI element.
        View.SetFrameSize(size);
    }

    public void Dispose()
    {
        _services.GetRequiredService<NativeDialogState>().Set(null);

        View.RemoveFromSuperview();
        _webView.Handler?.DisconnectHandler();
    }
}
