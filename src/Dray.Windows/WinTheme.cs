using Dray.Core.Shell;
using Dray.Core.Theme;
using Windows.UI.ViewManagement;
using WinColor = Windows.UI.Color;
using Xaml = Microsoft.UI.Xaml;

namespace Dray.WinUI;

/// <summary>
/// Resolves what Windows actually painted, so the WebView's token layer can match it.
/// <para>
/// The same rule as the macOS head: the GROUND comes from the OS and the surfaces above it are
/// Dray's unless the OS has a colour that means the same thing (DESIGN.md section 9.1). Windows 11
/// does — its card and layer fills are exactly "a panel above the ground" — so where macOS needed a
/// stepped ramp, this takes the system's own values and composites them to opaque.
/// </para>
/// <para>
/// Every colour is read from WinUI's own theme dictionaries by name, never written down here. A
/// resource that cannot be found leaves the generated palette in place rather than being filled
/// with a plausible value.
/// </para>
/// </summary>
public sealed class WinTheme : IPlatformTheme
{
    readonly UISettings _settings = new();

    // The theme the shell actually painted in, once Follow has an element to read it from. Null
    // until then, and the system's app theme answers instead.
    volatile object? _effectiveDark;

    public WinTheme()
    {
        // Raised on a background thread, for accent changes as well as light/dark. Consumers
        // repaint through Blazor's own dispatcher, and the native chrome marshals for itself.
        _settings.ColorValuesChanged += (_, _) => Changed?.Invoke();
    }

    public event Action? Changed;

    public DrayTheme Current => IsDark ? DrayTheme.Dark : DrayTheme.Light;

    /// <summary>
    /// Follow the theme <paramref name="root"/> is drawn in, rather than the system's.
    /// <para>
    /// The two differ whenever the app's theme is set apart from the system's: MAUI's
    /// <c>UserAppTheme</c> sets <c>RequestedTheme</c> on the window's root, which turns every WinUI
    /// control dark without raising <see cref="UISettings.ColorValuesChanged"/>. Reading the system
    /// would then leave the icons and the WebView's tokens in the other theme from the controls
    /// around them. <c>ActualTheme</c> is WinUI's counterpart of macOS's <c>EffectiveAppearance</c>,
    /// which is what <c>MacTheme</c> reads. Call on the UI thread.
    /// </para>
    /// </summary>
    public void Follow(Xaml.FrameworkElement root)
    {
        _effectiveDark = root.ActualTheme == Xaml.ElementTheme.Dark;
        root.ActualThemeChanged += (sender, _) =>
        {
            _effectiveDark = sender.ActualTheme == Xaml.ElementTheme.Dark;
            Changed?.Invoke();
        };
        Changed?.Invoke();
    }

    /// <summary>
    /// Whether the shell is dark. Before <see cref="Follow"/>, the system's own test: the foreground
    /// the shell hands apps is light exactly when the app theme is dark. There is no direct "is
    /// dark" API for a desktop app.
    /// </summary>
    bool IsDark
    {
        get
        {
            if (_effectiveDark is bool dark) return dark;

            var foreground = _settings.GetColorValue(UIColorType.Foreground);
            return (5 * foreground.G) + (2 * foreground.R) + foreground.B > 8 * 128;
        }
    }

    public IReadOnlyDictionary<string, string> TokenOverrides()
    {
        var theme = Current;
        var dictionary = theme == DrayTheme.Dark ? "Default" : "Light";
        var overrides = new Dictionary<string, string>();

        // The ground. Natively, the content area is the NavigationView's content layer: a
        // translucent fill over the window's base. --bg is that composite, because it is what
        // color-mix() blends against.
        //
        // With Mica the WebView is transparent, but not onto that layer: a transparent pixel in
        // WinUI's WebView2 shows the window's backdrop directly, never the XAML beneath the control
        // (docs/NATIVE-SHELL.md section 5.3). So the page paints the layer itself, as the same
        // translucent colour, and it composites over Mica exactly as the native layer does under
        // the toolbar row: one surface, no seam where the toolbar row meets the page.
        var baseFill = ThemeColor(dictionary, "SolidBackgroundFillColorBase");
        var layer = ThemeColor(dictionary, "LayerFillColorDefault");

        if (baseFill is { } ground)
        {
            var bg = layer is { } l ? Over(l, ground) : ground;
            overrides["bg"] = Css(bg);
            overrides["ground"] = !WinBackdrop.IsMica ? Css(bg) : layer is { } fill ? Css(fill) : "transparent";

            // Cards above the layer. Composited to opaque: a translucent --surface would let
            // scrolled rows show through every sticky header drawn with it.
            var card = ThemeColor(dictionary, "CardBackgroundFillColorDefault");
            var cardHigh = ThemeColor(dictionary, "CardBackgroundFillColorSecondary");
            var muted = ThemeColor(dictionary, "TextFillColorSecondary");

            if (card is { } c && cardHigh is { } c2 && muted is { } m)
            {
                var surface = Over(c, bg);
                var surface2 = Over(c2, bg);
                var quiet = Over(m, surface2);
                var ink = Rgb(DrayTokens.Get(theme, DrayColor.Ink));

                // Verified, not assumed. The contrast gate checks the generated palette and cannot
                // see a value resolved at runtime, so the check runs here: if Windows' surfaces
                // would put Dray's text below AA, all three stay on the generated palette together
                // rather than shipping a pairing nobody measured.
                if (Contrast(ink, surface) >= 4.5 && Contrast(ink, surface2) >= 4.5
                    && Contrast(quiet, surface2) >= 4.5 && Contrast(Over(m, surface), surface) >= 4.5)
                {
                    overrides["surface"] = Css(surface);
                    overrides["surface-2"] = Css(surface2);
                    overrides["muted"] = Css(quiet);
                }
            }
        }

        if (ThemeColor(dictionary, "DividerStrokeColorDefault") is { } divider)
            overrides["line"] = Css(divider);

        if (ThemeColor(dictionary, "ControlStrongStrokeColorDefault") is { } strong)
            overrides["line-strong"] = Css(strong);

        // Focus follows the user's accent, at the shade WinUI's own accent fill uses for the theme:
        // the darker step on light, the lighter on dark, which is what keeps it legible on both.
        var accent = _settings.GetColorValue(theme == DrayTheme.Dark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
        overrides["focus"] = Css(accent);

        // The toolbar row is a native sibling above the WebView, not an overlay on it, so there is
        // nothing to reserve. The inset is the shell's own, shared with that row.
        overrides["chrome-top"] = "0px";
        // The WebView itself starts a grip's width in, beside the pane's resize grip.
        overrides["chrome-inset"] = $"{NativeShellHandler.ContentInset - WinPaneResizer.GripWidth:0}px";

        // Windows' type ramp: Body is 14 and Caption is 12, in effective pixels. Dray's own scale is
        // a point smaller, sized for macOS, and text one size off the platform's reads as subtly
        // wrong beside a real control. The smallest step has no Windows counterpart and keeps
        // Dray's value.
        overrides["text-base"] = "14px";
        overrides["text-sm"] = "12px";

#if DEBUG
        CrashLog.Write($"[dray:theme] {(theme == DrayTheme.Dark ? "dark" : "light")} mica={WinBackdrop.IsMica} " +
            string.Join("  ", overrides.Select(kv => $"{kv.Key}={kv.Value}")));
#endif

        return overrides;
    }

    /// <summary>
    /// The colour of a <c>ContentDialog</c>'s content section in the current theme:
    /// <c>ContentDialogTopOverlay</c> (<c>LayerFillColorAlt</c>) over <c>ContentDialogBackground</c>
    /// (<c>SolidBackgroundFillColorBase</c>), composited to opaque. What a dialog body's WebView has
    /// to paint to be indistinguishable from the dialog around it. Null if either is missing.
    /// </summary>
    public WinColor? DialogBodyGround()
    {
        var dictionary = Current == DrayTheme.Dark ? "Default" : "Light";
        if (ThemeColor(dictionary, "SolidBackgroundFillColorBase") is not { } ground) return null;
        return ThemeColor(dictionary, "LayerFillColorAlt") is { } top ? Over(top, ground) : ground;
    }

    /// <summary>
    /// A colour from WinUI's theme dictionaries, for a theme named explicitly.
    /// <para>
    /// Looked up by theme rather than through <c>Application.Resources[key]</c>, which answers for
    /// the theme the app STARTED in — so after the user switches, every lookup would describe the
    /// theme they just left. "Default" is WinUI's name for dark.
    /// </para>
    /// </summary>
    static WinColor? ThemeColor(string theme, string key)
        => Xaml.Application.Current?.Resources is { } resources ? Find(resources, theme, key, 0) : null;

    static WinColor? Find(Xaml.ResourceDictionary dictionary, string theme, string key, int depth)
    {
        if (depth > 6) return null;

        if (dictionary.ThemeDictionaries.TryGetValue(theme, out var themed)
            && themed is Xaml.ResourceDictionary themeDictionary
            && themeDictionary.TryGetValue(key, out var value)
            && value is WinColor color)
        {
            return color;
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (Find(merged, theme, key, depth + 1) is { } found) return found;
        }

        return null;
    }

    readonly record struct Rgba(double R, double G, double B, double A);

    static Rgba Rgb(WinColor c) => new(c.R, c.G, c.B, c.A / 255.0);

    static Rgba Rgb(DrayRgb c) => new(c.R, c.G, c.B, 1);

    static WinColor Opaque(Rgba c) => WinColor.FromArgb(
        255,
        (byte)Math.Round(Math.Clamp(c.R, 0, 255)),
        (byte)Math.Round(Math.Clamp(c.G, 0, 255)),
        (byte)Math.Round(Math.Clamp(c.B, 0, 255)));

    /// <summary>Lay a translucent colour over an opaque one.</summary>
    static WinColor Over(WinColor top, WinColor under)
    {
        var t = Rgb(top);
        var u = Rgb(under);
        return Opaque(new Rgba(
            u.R + (t.A * (t.R - u.R)),
            u.G + (t.A * (t.G - u.G)),
            u.B + (t.A * (t.B - u.B)),
            1));
    }

    static double Contrast(WinColor a, WinColor b) => Contrast(Rgb(a), Rgb(b));

    /// <summary>WCAG 2 contrast ratio between two opaque colours.</summary>
    static double Contrast(Rgba a, Rgba b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    static double Contrast(Rgba a, WinColor b) => Contrast(a, Rgb(b));

    static double Luminance(Rgba c)
    {
        static double Channel(double v)
        {
            v /= 255;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    /// <summary>The platform's own colour, formatted for CSS.</summary>
    static string Css(WinColor c)
    {
        // design-lint-ok: a colour WinUI resolved, handed to the token layer, is the point of the theme handoff rather than a palette value authored here.
        return c.A >= 255 ? $"rgb({c.R} {c.G} {c.B})" : $"rgb({c.R} {c.G} {c.B} / {c.A / 255.0:0.###})";
    }
}
