using Dray.Core.Theme;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using WinColor = Windows.UI.Color;
using Xaml = Microsoft.UI.Xaml;

namespace Dray.WinUI;

/// <summary>
/// Mica, tinted with Dray's ground rather than the wallpaper's.
/// <para>
/// Stock Mica in light is a half-opaque grey over a blur of the desktop, so the wallpaper's hue
/// comes through: on a blue wallpaper the sidebar, the title bar and, through the translucent page
/// ground, the content all read cool beside a warm palette. In light the tint is
/// <c>--surface</c>, opaque enough that the wallpaper only moves its brightness. Dark keeps
/// WinUI's own values, where the wallpaper barely shows.
/// </para>
/// <para>
/// The fallback, what an inactive window shows instead of Mica, is the same colour, so focus
/// moving away does not turn the window grey.
/// </para>
/// </summary>
public sealed class DrayMica : Xaml.Media.SystemBackdrop
{
    MicaController? _controller;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, Xaml.XamlRoot root)
    {
        base.OnTargetConnected(target, root);

        _controller = new MicaController();
        _controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, root));
        _controller.AddSystemBackdropTarget(target);
        Tint(root);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);

        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
    }

    /// <summary>
    /// Raised when the window's theme or activation changes. The base implementation is not called:
    /// it has nothing to do, and throws E_INVALIDARG for the target on a theme change, which took
    /// the app down on the switch from dark to light.
    /// </summary>
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, Xaml.XamlRoot root)
        => Tint(root);

    void Tint(Xaml.XamlRoot root)
    {
        if (_controller is null) return;

        // Setting any property stops the controller following the theme, so dark is handed back
        // to it explicitly.
        if ((root.Content as Xaml.FrameworkElement)?.ActualTheme == Xaml.ElementTheme.Dark)
        {
            _controller.ResetProperties();
            return;
        }

        var rgb = DrayTokens.Get(DrayTheme.Light, DrayColor.Surface);
        var surface = WinColor.FromArgb(255, rgb.R, rgb.G, rgb.B);

        _controller.TintColor = surface;
        _controller.TintOpacity = 0.9f;
        _controller.LuminosityOpacity = 0.9f;
        _controller.FallbackColor = surface;
    }
}
