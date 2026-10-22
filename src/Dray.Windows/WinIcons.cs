using System.Text;
using Dray.Core.Shell;
using Dray.Core.Theme;
using Microsoft.UI.Xaml.Media.Imaging;
using Xaml = Microsoft.UI.Xaml;

namespace Dray.WinUI;

/// <summary>
/// Native icons for WinUI chrome, drawn from the same geometry as the WebView's sprite.
/// <para>
/// Not Segoe Fluent glyphs. Mapping each <see cref="IconRef"/> to a codepoint would be a fourth
/// vocabulary written by hand and checked by nobody — the Sherpa failure design/icons.json exists to
/// prevent. The sprite is already the canonical drawing, and <c>SvgImageSource</c> renders it.
/// </para>
/// <para>
/// A rendered SVG has no <c>currentColor</c> to inherit, so each icon is stroked in the palette's
/// ink for the theme being drawn, and redrawn when the theme changes.
/// </para>
/// </summary>
public static class WinIcons
{
    /// <summary>The size WinUI's own sidebar and command bar glyphs are drawn at.</summary>
    public const double Size = 16;

    public static Xaml.Controls.ImageIcon Create(IconRef icon, DrayTheme theme)
        => FromSvg(Icons.Svg(icon, DrayTokens.Get(theme, DrayColor.Ink).ToHex()), icon.ToString());

    /// <summary>
    /// The app tile, at the size of every other icon in the chrome, for the title bar. In full
    /// colour, as the taskbar shows it, so it is the same in both themes. Drawn by
    /// <c>build/gen-app-icon.mjs</c> and embedded rather than shipped as a file, so there is no path
    /// for an unpackaged app to get wrong.
    /// </summary>
    public static Xaml.Controls.ImageIcon AppTile()
    {
        using var stream = typeof(WinIcons).Assembly.GetManifestResourceStream("TitleBarIcon.svg")
            ?? throw new InvalidOperationException("TitleBarIcon.svg is not embedded. Run: node build/gen-app-icon.mjs");
        using var reader = new StreamReader(stream);
        return FromSvg(reader.ReadToEnd(), "app tile");
    }

    static Xaml.Controls.ImageIcon FromSvg(string svg, string name)
    {
        var source = new SvgImageSource();
        var image = new Xaml.Controls.ImageIcon
        {
            Source = source,
            Width = Size,
            Height = Size,
        };

        // The rasterize size is in physical pixels, and the icon is laid out in effective ones. At
        // a fixed 16 the SVG was drawn at 16 pixels and stretched to 32 on a 200% display, so every
        // icon was soft beside the text next to it. It follows the scale of the display the icon
        // is actually on, including after the window moves to another monitor.
        void Rasterize()
        {
            var pixels = Math.Ceiling(Size * (image.XamlRoot?.RasterizationScale ?? 1));
            if (source.RasterizePixelWidth == pixels) return;
            source.RasterizePixelWidth = pixels;
            source.RasterizePixelHeight = pixels;
        }

        void OnRootChanged(Xaml.XamlRoot sender, Xaml.XamlRootChangedEventArgs e) => Rasterize();

        image.Loaded += (_, _) =>
        {
            Rasterize();
            if (image.XamlRoot is { } root) root.Changed += OnRootChanged;
        };
        image.Unloaded += (_, _) =>
        {
            if (image.XamlRoot is { } root) root.Changed -= OnRootChanged;
        };

        _ = LoadAsync(source, svg, name);
        return image;
    }

    static async Task LoadAsync(SvgImageSource source, string svg, string name)
    {
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg));
            var status = await source.SetSourceAsync(stream.AsRandomAccessStream());

            // SvgImageSource reports a document it cannot draw as a status, not an exception — and
            // draws nothing. Logged so a blank icon has a cause somewhere.
            if (status != SvgImageSourceLoadStatus.Success)
                CrashLog.Write($"[dray:icon] {name} did not render: {status}");
        }
        catch (Exception ex)
        {
            CrashLog.Write($"[dray:icon] {name} failed: {ex.Message}");
        }
    }
}
