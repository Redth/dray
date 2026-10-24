using Dray.Core.Kubernetes;
using Dray.Core.Engine;
using Dray.Core.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using Microsoft.UI.Composition.SystemBackdrops;
using Xaml = Microsoft.UI.Xaml;

namespace Dray.WinUI;

/// <summary>
/// The WinUI shell: a <c>NavigationView</c> sidebar built from <c>NavigationManifest</c>, a native
/// toolbar row projected from <c>PageChrome</c>, and the Blazor WebView as the content — on a Mica
/// window, which is the Windows 11 counterpart of the material macOS paints behind its sidebar.
/// </summary>
public sealed class DrayWinApp(IServiceProvider services) : Application
{
    WinTray? _tray;

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // Before any control exists: a template resolves its colours as it is applied, and WinUI's
        // own resources are in place by now, so Dray's are searched ahead of them.
        if (services.GetRequiredService<IPlatformTheme>() is WinTheme theme) WinPalette.Install(theme);

        var shell = new NativeShell();
        var page = new ContentPage
        {
            Title = string.Empty,
            Content = shell,

            // A page paints an opaque ground by default, which would cover the window's Mica.
            BackgroundColor = Colors.Transparent,
        };

        var window = new Window(page)
        {
            Title = "Dray",
            Width = 1180,
            Height = 760,
            MinimumWidth = 720,
            MinimumHeight = 480,

            // MAUI's title bar, which already owns the drag region and the caption buttons, with
            // Back and the pane toggle at its leading edge and the page's toolbar after them
            // (WinTitleBarToolbar). 48 is the height WinUI gives a title bar that carries a back
            // button. No title text: the page's own title is in the bar, as it is in a macOS
            // toolbar, and the window is still "Dray" to the taskbar and Alt+Tab.
            TitleBar = new TitleBar
            {
                LeadingContent = new TitleBarNavigation(),
                Content = new TitleBarPageLead(),
                TrailingContent = new TitleBarPageActions(),
                HeightRequest = 48,
            },
        };

        window.HandlerChanged += (_, _) =>
        {
            if (window.Handler?.PlatformView is not Xaml.Window native) return;

            WinFirstFrame.HoldUntilDrawn(native, () => shell.Handler?.PlatformView as Xaml.FrameworkElement);
            ApplyBackdrop(native);
            WinWindow.TrackActivation(native);

            // Once per process: the window's handler can be replaced, the notification area icon
            // cannot be added twice.
            // Dray without a tray icon is still Dray; a failure here is logged, not fatal.
            try
            {
                _tray ??= new WinTray(
                    native,
                    services.GetRequiredService<EngineManager>(),
                    services.GetRequiredService<IShellState>(),
                    services.GetRequiredService<IShellBridge>(),
                    services.GetRequiredService<IPlatformTheme>());
            }
            catch (Exception ex)
            {
                CrashLog.Write($"[dray:tray] {ex}");
            }
        };

        // Discover and connect in the background; the UI shows its connecting state until this
        // lands rather than blocking the window from appearing.
        _ = Task.Run(async () =>
        {
            try
            {
                await services.GetRequiredService<EngineManager>().InitializeAsync();
            }
            catch (Exception ex)
            {
                CrashLog.Write($"[dray:engine] initialise failed: {ex}");
            }
        });

        // The cluster beside it, independently: a cluster that does not answer must not hold the
        // engine back, nor the other way round.
        _ = Task.Run(async () =>
        {
            try
            {
                await services.GetRequiredService<ClusterManager>().InitializeAsync();
            }
            catch (Exception ex)
            {
                CrashLog.Write($"[dray:cluster] initialise failed: {ex}");
            }
        });

        return window;
    }

    /// <summary>
    /// Mica where the OS has it, and nothing where it does not.
    /// <para>
    /// Mica is Windows 11. On Windows 10 a backdrop that cannot be shown leaves the window with no
    /// ground at all, so <see cref="WinBackdrop.IsMica"/> records which happened and the theme
    /// paints an opaque ground in that case rather than a transparent one over nothing.
    /// </para>
    /// </summary>
    static void ApplyBackdrop(Xaml.Window native)
    {
        if (!MicaController.IsSupported())
        {
            WinBackdrop.IsMica = false;
            return;
        }

        native.SystemBackdrop = new DrayMica();
        WinBackdrop.IsMica = true;

        // MAUI's root view paints the page background over whatever the window has behind it.
        // Clearing it is what lets the backdrop reach the sidebar pane and, through the transparent
        // WebView, the content.
        if (native.Content is Xaml.Controls.Panel panel) panel.Background = null;
        else if (native.Content is Xaml.Controls.Control control) control.Background = null;
    }
}

/// <summary>Whether the window got a Mica backdrop. Read by <see cref="WinTheme"/>.</summary>
public static class WinBackdrop
{
    public static bool IsMica { get; set; }
}
