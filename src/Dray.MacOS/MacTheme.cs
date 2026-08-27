using AppKit;
using CoreGraphics;
using Dray.Core.Shell;
using Dray.Core.Theme;
using Foundation;

namespace Dray.MacOS;

/// <summary>
/// Resolves the platform's actual window colours so the WebView's token layer can match them.
/// <para>
/// The WebView is transparent over the native window, so <c>--bg</c> must be exactly what AppKit
/// painted rather than our fallback. Everything else stays on the generated palette — this only
/// overrides the handful of roles where "what the OS chose" beats "what we chose"
/// (DESIGN.md section 9.1).
/// </para>
/// </summary>
public sealed class MacTheme : IPlatformTheme, IDisposable
{
    IDisposable? _observer;

    public MacTheme()
    {
        // NSApp.effectiveAppearance is KVO-observable; this is how the app learns the user
        // flipped appearance while it was running. The repaint must land in the same frame as
        // AppKit's, or the user sees the two halves change one after the other.
        _observer = NSApplication.SharedApplication.AddObserver(
            "effectiveAppearance",
            NSKeyValueObservingOptions.New,
            _ => NSApplication.SharedApplication.InvokeOnMainThread(() => Changed?.Invoke()));
    }

    public event Action? Changed;

    public DrayTheme Current => IsDark ? DrayTheme.Dark : DrayTheme.Light;

    public IReadOnlyDictionary<string, string> TokenOverrides() => Overrides();

    public void Dispose()
    {
        _observer?.Dispose();
        _observer = null;
    }

    public static bool IsDark
    {
        get
        {
            var name = NSApplication.SharedApplication.EffectiveAppearance.FindBestMatch(
                [NSAppearance.NameAqua, NSAppearance.NameDarkAqua]);
            return name == NSAppearance.NameDarkAqua;
        }
    }

    /// <summary>
    /// Token overrides to push into CSS, as <c>name -&gt; css colour</c>.
    /// <para>
    /// Resolution happens inside the effective appearance's drawing context. AppKit's semantic
    /// colours are dynamic: outside a drawing context they resolve against the DEFAULT (light)
    /// appearance regardless of what the app is actually showing. Without this, switching to dark
    /// repaints the pills and text but leaves every surface on its light value, and the result is
    /// light-grey text on a white panel inside a dark window.
    /// </para>
    /// </summary>
    public static Dictionary<string, string> Overrides()
    {
        var overrides = new Dictionary<string, string>();

        NSApplication.SharedApplication.EffectiveAppearance.PerformAsCurrentDrawingAppearance(() =>
        {
            // The ground the WebView composites onto. Getting this even slightly wrong is the
            // single most visible way the seam shows.
            Add(overrides, "bg", NSColor.WindowBackground);
            Add(overrides, "surface", NSColor.ControlBackground);
            Add(overrides, "surface-2", NSColor.UnderPageBackground);
            Add(overrides, "line", NSColor.Separator);

            // Selection follows the user's system accent, because that is what every native list
            // on their machine does. --brand stays Dray's own identity: icon, splash, primary
            // buttons.
            Add(overrides, "focus", NSColor.KeyboardFocusIndicator);
        });

        return overrides;
    }

    static void Add(Dictionary<string, string> map, string token, NSColor color)
    {
        if (ToCss(color) is { } css) map[token] = css;
    }

    /// <summary>
    /// AppKit colours are often catalog or pattern colours with no direct RGB components, so they
    /// have to be rendered through a colour space first. Returns null when that is not possible,
    /// and the generated fallback stands.
    /// </summary>
    static string? ToCss(NSColor color)
    {
        try
        {
            var rgb = color.UsingColorSpace(NSColorSpace.SRGBColorSpace);
            if (rgb is null) return null;

            rgb.GetRgba(out var r, out var g, out var b, out var a);
            return Css(To255(r), To255(g), To255(b), (double)a);
        }
        catch (Exception)
        {
            // A colour we cannot resolve is not worth failing startup over.
            return null;
        }
    }

    /// <summary>The platform's own colour, formatted for CSS.</summary>
    static string Css(int r, int g, int b, double a)
    {
        // design-lint-ok: AppKit's colour handed to the token layer is the point of the theme
        // handoff, not a palette value authored by us.
        return a >= 0.999 ? $"rgb({r} {g} {b})" : $"rgb({r} {g} {b} / {a:0.###})";
    }

    static int To255(nfloat v) => (int)Math.Round(Math.Clamp((double)v, 0, 1) * 255);
}
