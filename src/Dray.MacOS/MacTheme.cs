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

            // A stronger rule than --line, for the few places that need one. tertiaryLabel is the
            // system's own next step up the same ramp separatorColor sits on, so the two stay in
            // proportion the way they do in a native list.
            Add(overrides, "line-strong", NSColor.TertiaryLabel);

            // The panels above the ground. Measured rather than named: in dark,
            // windowBackground, controlBackground and textBackground all resolve to the SAME
            // value — a card built from any of them would vanish into the ground — and
            // underPageBackground is a mid grey in light that would render every panel as a dark
            // band. None of them is a card.
            //
            // What is adopted instead is the ground's HUE. The system's is perfectly neutral;
            // Dray's generated surfaces carry a warm tint (chroma 0.004–0.005 at hue 41), and next
            // to neutral system chrome that tint is exactly what reads as slightly-off. Dray's own
            // spacing is kept — these are the steps its palette already takes from its own ground —
            // so the ramp is Dray's and the hue is the platform's.
            foreach (var (token, step) in SurfaceSteps)
            {
                if (Shift(NSColor.WindowBackground, step) is { } surface) overrides[token] = surface;
            }

            // Selection follows the user's system accent, because that is what every native list
            // on their machine does. --brand stays Dray's own identity: icon, splash, primary
            // buttons.
            Add(overrides, "focus", NSColor.KeyboardFocusIndicator);

            // The background a native list paints behind a selected row when its own view is not
            // the first responder — which is what the sidebar's selected item is showing whenever
            // the user is looking at the content beside it. Dray's tables and its table headers
            // use the same colour, so a selected row here and a selected row in Finder are the
            // same shade rather than two different opinions about selection.
            Add(overrides, "selected", NSColor.UnemphasizedSelectedContentBackground);

            // Quiet text, neutralised to sit beside the system's own.
            //
            // NOT NSColor.SecondaryLabel, though that is the colour macOS puts on a sidebar's
            // section headings and the obvious answer. Measured against these backgrounds it lands
            // at 4.16:1 in dark and 3.72:1 in light — Apple targets a lower bar for secondary text
            // than WCAG AA, and DESIGN.md's contrast gate is not negotiable.
            //
            // So the same trade as the surfaces: keep Dray's own value, take the platform's hue.
            // Its muted greys carry a warm tint about ten channels wide, which is what read as a
            // different kind of quiet next to neutral system text.
            //
            // Then one step further from Dray's luminance, which is the part worth explaining. A
            // column heading sits on --selected here, and that band is lighter than any surface
            // Dray has — quiet text at Dray's own luminance lands on it at 4.16:1. Rather than
            // move the heading off the platform's colour, the text is strengthened until it clears
            // AA on the lightest thing it has to sit on: 4.60:1 on the selection, and 6–7:1 on the
            // surfaces, where it was 4.9–5.5:1 before. Quiet, and never below the bar.
            overrides["muted"] = IsDark ? Css(181, 181, 181, 1) : Css(95, 95, 95, 1);
        });

        // The content view runs the full height of the window under a unified titlebar, so the
        // web layer has to reserve the toolbar's height itself. Measured rather than hardcoded:
        // the height changes with the toolbar style and whether a window is in full screen.
        overrides["chrome-top"] = $"{TitlebarHeight():0}px";

        // Content lines up with the system's own sidebar card rather than with a margin Dray chose.
        overrides["chrome-inset"] = $"{SidebarInset():0}px";

        // The system's own text sizes rather than Dray's approximation of them. The family was
        // already right — `-apple-system` resolves to the same face AppKit uses — but the sizes
        // were a scale Dray chose, and a control that is one point off the platform's reads as
        // subtly wrong beside a real one.
        //
        // Points, not pixels: AppKit reports these in points and CSS px are the same unit here.
        overrides["text-base"] = $"{NSFont.SystemFontSize:0.##}px";
        overrides["text-sm"] = $"{NSFont.SmallSystemFontSize:0.##}px";
        overrides["text-xs"] = $"{NSFont.LabelFontSize:0.##}px";

        // Nothing paints the ground. The WebView already has drawsBackground=false, so leaving it
        // unpainted lets the window's own material show through — and on macOS 26 that material is
        // not a flat colour, so painting windowBackgroundColor over it produced a visible seam
        // where the web content met the window. Matching a material with a colour cannot be done;
        // not covering it can.
        //
        // --bg keeps its resolved colour because color-mix() needs a real one to blend against.
        overrides["ground"] = "transparent";

#if DEBUG
        Console.Error.WriteLine($"[dray:theme] {(IsDark ? "dark" : "light")} " +
            string.Join("  ", overrides.Select(kv => $"{kv.Key}={kv.Value}")));

#endif

        return overrides;
    }

    /// <summary>
    /// How far each surface sits from the ground, in 0–255 sRGB.
    /// <para>
    /// Sized from Dray's own generated palette — the steps its ground takes to its two panel
    /// surfaces — then tightened in dark, because the system's dark ground is lighter than Dray's
    /// and the same steps put muted text at 4.56:1 on the upper surface. That passes AA by six
    /// hundredths, which is not a margin. These give 5.5:1 and 4.9:1.
    /// </para>
    /// <para>
    /// Worth stating plainly: <c>verify-contrast.mjs</c> checks the generated palette and cannot
    /// see values resolved from the OS at runtime, so these ratios were computed against the
    /// measured system grounds by hand. Anyone changing a step here has to redo that arithmetic —
    /// the gate will not catch it.
    /// </para>
    /// <para>
    /// The dark surface step happens to land on exactly the value AppKit's own
    /// <c>underPageBackgroundColor</c> reports, which is reassuring about the ratio rather than
    /// the source of it.
    /// </para>
    /// </summary>
    static (string Token, int Step)[] SurfaceSteps => IsDark
        ? [("surface", 10), ("surface-2", 18)]
        : [("surface", -7), ("surface-2", -15)];

    /// <summary>
    /// A colour moved along the neutral axis by <paramref name="step"/>, as CSS.
    /// <para>
    /// Deliberately does not preserve the source's hue: the system ground is neutral, and keeping
    /// it neutral is the reason for doing this at all.
    /// </para>
    /// </summary>
    static string? Shift(NSColor color, int step)
    {
        try
        {
            var rgb = color.UsingColorSpace(NSColorSpace.SRGBColorSpace);
            if (rgb is null) return null;

            rgb.GetRgba(out var r, out var g, out var b, out _);

            return Css(
                Clamp(To255(r) + step),
                Clamp(To255(g) + step),
                Clamp(To255(b) + step),
                1);
        }
        catch (Exception)
        {
            return null;
        }
    }

    static int Clamp(int value) => Math.Clamp(value, 0, 255);

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
