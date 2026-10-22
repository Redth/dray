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
    {
        var source = new SvgImageSource
        {
            RasterizePixelWidth = Size,
            RasterizePixelHeight = Size,
        };

        _ = LoadAsync(source, icon, theme);

        return new Xaml.Controls.ImageIcon
        {
            Source = source,
            Width = Size,
            Height = Size,
        };
    }

    static async Task LoadAsync(SvgImageSource source, IconRef icon, DrayTheme theme)
    {
        var svg = Icons.Svg(icon, DrayTokens.Get(theme, DrayColor.Ink).ToHex());

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg));
            var status = await source.SetSourceAsync(stream.AsRandomAccessStream());

            // SvgImageSource reports a document it cannot draw as a status, not an exception — and
            // draws nothing. Logged so a blank icon has a cause somewhere.
            if (status != SvgImageSourceLoadStatus.Success)
                CrashLog.Write($"[dray:icon] {icon} did not render: {status}");
        }
        catch (Exception ex)
        {
            CrashLog.Write($"[dray:icon] {icon} failed: {ex.Message}");
        }
    }
}
