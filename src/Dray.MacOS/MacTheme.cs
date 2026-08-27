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

    /// <summary>
    /// Height of the titlebar plus toolbar, in points. Falls back to the standard unified height
    /// when no window exists yet — which is the case on the very first paint.
    /// </summary>
    public static double TitlebarHeight()
    {
        var window = NSApplication.SharedApplication.KeyWindow ?? NSApplication.SharedApplication.MainWindow;
        if (window is null) return 52;

        var height = (double)(window.Frame.Height - window.ContentLayoutRect.Height);
        return height is > 20 and < 200 ? height : 52;
    }

    /// <summary>
    /// The inset macOS uses around its own sidebar card, in points.
    /// <para>
    /// On macOS 26 the sidebar is a rounded card floating on the window, not a full-height pane, and
    /// content laid out beside it has to sit on the same margin or the window looks like two designs
    /// bolted together. Measured from the real view rather than assumed: the value is Apple's, it is
    /// not published, and it is exactly the kind of number that changes in a point release.
    /// </para>
    /// <para>
    /// Falls back to 8 — what macOS 26.6 measures — when there is no card to measure, which is the
    /// case before the window exists and on any earlier macOS where the sidebar is full-bleed.
    /// </para>
    /// </summary>
    public static double SidebarInset()
    {
        var window = NSApplication.SharedApplication.KeyWindow ?? NSApplication.SharedApplication.MainWindow;
        if (window?.ContentView is not { } root) return DefaultSidebarInset;

        var inset = FindGlassInset(root, 0);
        return inset is > 0 and < 40 ? inset.Value : DefaultSidebarInset;
    }

    const double DefaultSidebarInset = 8;

    /// <summary>
    /// Find the inset of the first glass-backed view in the tree.
    /// <para>
    /// Matched on the type name because the class is what draws the card, and its origin within its
    /// container <i>is</i> the inset. Walking by name is unlovely, but the alternative — assuming a
    /// position in the split view's subview order — breaks the first time MAUI changes how it builds
    /// a flyout page.
    /// </para>
    /// </summary>
    static double? FindGlassInset(NSView view, int depth)
    {
        if (depth > 8) return null;

        if (view.GetType().Name.Contains("Glass", StringComparison.Ordinal) && view.Frame.X > 0)
            return view.Frame.X;

        foreach (var child in view.Subviews)
        {
            if (FindGlassInset(child, depth + 1) is { } found) return found;
        }

        return null;
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
            // The rule: the GROUND comes from the OS, the surfaces above it are ours.
            //
            // `--bg` is what the WebView composites onto, so it must be exactly what AppKit
            // painted — getting it even slightly wrong is the most visible way the seam shows.
            // `--surface` and `--surface-2` are Dray's own hierarchy of panels and chrome, and
            // AppKit has no colours that mean the same thing: controlBackgroundColor is identical
            // to the window colour in a full-size-content window (so panels lose all separation),
            // and underPageBackgroundColor is the mid-grey shown BEHIND a document, which renders
            // table headers as a dark band. Both stay on the generated palette.
            Add(overrides, "bg", NSColor.WindowBackground);
            Add(overrides, "line", NSColor.Separator);

            // Selection follows the user's system accent, because that is what every native list
            // on their machine does. --brand stays Dray's own identity: icon, splash, primary
            // buttons.
            Add(overrides, "focus", NSColor.KeyboardFocusIndicator);
        });

        // The content view runs the full height of the window under a unified titlebar, so the
        // web layer has to reserve the toolbar's height itself. Measured rather than hardcoded:
        // the height changes with the toolbar style and whether a window is in full screen.
        overrides["chrome-top"] = $"{TitlebarHeight():0}px";

        // Content lines up with the system's own sidebar card rather than with a margin Dray chose.
        overrides["chrome-inset"] = $"{SidebarInset():0}px";

#if DEBUG
        Console.Error.WriteLine($"[dray:theme] {(IsDark ? "dark" : "light")} " +
            string.Join("  ", overrides.Select(kv => $"{kv.Key}={kv.Value}")));
#endif

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
