using Dray.Core.Theme;
using WinColor = Windows.UI.Color;
using Xaml = Microsoft.UI.Xaml;

namespace Dray.WinUI;

/// <summary>
/// WinUI's controls, drawn in Dray's colours: the same values the page beside them is drawn in.
/// <para>
/// Left alone, every native control takes the user's Windows accent, which is blue unless they
/// changed it. The sidebar's selection, the toolbar's main action and a dialog's committing button
/// came up blue inside a terracotta app, and the seam showed at exactly the moments the user
/// interacted (DESIGN.md section 9.1). Windows has no counterpart of macOS's Multicolor setting, the
/// state in which an app's own accent applies, so there is no way to tell a chosen accent from the
/// default one, and Dray's applies.
/// </para>
/// <para>
/// Two layers, both installed once into the application's resources, after WinUI's own:
/// </para>
/// <list type="bullet">
/// <item>The accent ramp, <c>SystemAccentColor</c> and its six steps, which every accent-coloured
/// part of every control derives from: a checkbox, a progress ring, a text selection. Light themes
/// read the dark steps and dark themes the light ones, so one ramp carries both brands.</item>
/// <item>Lightweight-styling keys for the controls Dray shows, per theme, where WinUI draws
/// something the page draws differently: a button's fill and border, a selected sidebar item. These
/// are looked up by the control templates through the application's resources, so they apply even
/// where a control's colour was not derived from the ramp.</item>
/// </list>
/// <para>
/// High contrast is left to the system. Its dictionary is present and empty, which keeps WinUI from
/// falling back to the dark one and painting Dray's colours over the user's.
/// </para>
/// </summary>
public static class WinPalette
{
    static bool _installed;

    public static void Install(WinTheme theme)
    {
        if (_installed || Xaml.Application.Current?.Resources is not { } resources) return;
        _installed = true;

        var palette = new Xaml.ResourceDictionary();

        var light = Generated(DrayTheme.Light, DrayColor.Brand);
        var lightHi = Generated(DrayTheme.Light, DrayColor.BrandHi);
        var dark = Generated(DrayTheme.Dark, DrayColor.Brand);
        var darkHi = Generated(DrayTheme.Dark, DrayColor.BrandHi);

        // Light reads Dark1 for fills and Dark2 for accent text; dark reads Light2 and Light3.
        // --brand-hi is the step that holds text contrast in each, which is what it is for on the
        // page too. The base is a text selection's background in both themes, under on-brand text
        // in light, which is the pairing the contrast gate checks.
        palette["SystemAccentColor"] = light;
        palette["SystemAccentColorDark1"] = light;
        palette["SystemAccentColorDark2"] = lightHi;
        palette["SystemAccentColorDark3"] = lightHi;
        palette["SystemAccentColorLight1"] = dark;
        palette["SystemAccentColorLight2"] = dark;
        palette["SystemAccentColorLight3"] = darkHi;

        palette.ThemeDictionaries["Light"] = Controls(theme, DrayTheme.Light);
        palette.ThemeDictionaries[WinTheme.DictionaryName(DrayTheme.Dark)] = Controls(theme, DrayTheme.Dark);
        palette.ThemeDictionaries["HighContrast"] = new Xaml.ResourceDictionary();

        // Last, so it is searched first.
        resources.MergedDictionaries.Add(palette);
    }

    /// <summary>
    /// The controls Dray puts on screen, in the treatments DESIGN.md section 6 gives the page's.
    /// </summary>
    static Xaml.ResourceDictionary Controls(WinTheme theme, DrayTheme t)
    {
        var d = new Xaml.ResourceDictionary();

        var brand = Generated(t, DrayColor.Brand);
        var brandHi = Generated(t, DrayColor.BrandHi);
        var onBrand = Generated(t, DrayColor.OnBrand);
        var bg = theme.Token(t, DrayColor.Bg);
        var surface = theme.Token(t, DrayColor.Surface);
        var surface2 = theme.Token(t, DrayColor.Surface2);
        var lineStrong = theme.Token(t, DrayColor.LineStrong);

        // The primary button: --brand, --brand-hi under the pointer, and on-brand text. The page
        // has no pressed state; WinUI's press lightens towards the ground, and so does this one.
        var pressed = Mix(brandHi, bg, 0.8);
        States(d, "AccentButtonBackground", brand, brandHi, pressed);
        States(d, "AccentButtonForeground", onBrand, onBrand, onBrand);

        // Flat, as the page's is. WinUI's border is a gradient of the system accent, which would
        // ring the brand in blue.
        States(d, "AccentButtonBorderBrush", brand, brandHi, pressed);

        // Every other button is the page's secondary: the raised surface, the base one under the
        // pointer, and the strong line around it. A dialog's Cancel, a filter's dropdown.
        States(d, "ButtonBackground", surface2, surface, surface);
        States(d, "ButtonBorderBrush", lineStrong, lineStrong, lineStrong);

        // The toolbar's commands are the page's ghost buttons: nothing at rest, the raised surface
        // under the pointer.
        d["AppBarButtonBackgroundPointerOver"] = Brush(surface2);
        d["AppBarButtonBackgroundPressed"] = Brush(surface);

        // The sidebar's selection is the page's: a table's selected row is --brand at 14% into the
        // ground, and so is this, translucent because the pane's ground is Mica rather than a
        // colour. The indicator pill is the brand itself.
        d["NavigationViewSelectionIndicatorForeground"] = Brush(brand);
        d["NavigationViewItemBackgroundSelected"] = Brush(brand, 0.14);
        d["NavigationViewItemBackgroundSelectedPointerOver"] = Brush(brand, 0.18);
        d["NavigationViewItemBackgroundSelectedPressed"] = Brush(brand, 0.10);

        return d;
    }

    static void States(Xaml.ResourceDictionary d, string key, WinColor rest, WinColor pointerOver, WinColor pressed)
    {
        d[key] = Brush(rest);
        d[key + "PointerOver"] = Brush(pointerOver);
        d[key + "Pressed"] = Brush(pressed);
    }

    static Xaml.Media.SolidColorBrush Brush(WinColor color, double opacity = 1)
        => new(color) { Opacity = opacity };

    static WinColor Generated(DrayTheme theme, DrayColor role)
    {
        var rgb = DrayTokens.Get(theme, role);
        return WinColor.FromArgb(255, rgb.R, rgb.G, rgb.B);
    }

    /// <summary><paramref name="weight"/> of <paramref name="a"/> over <paramref name="b"/>, in sRGB.</summary>
    static WinColor Mix(WinColor a, WinColor b, double weight) => WinColor.FromArgb(
        255,
        (byte)Math.Round((weight * a.R) + ((1 - weight) * b.R)),
        (byte)Math.Round((weight * a.G) + ((1 - weight) * b.G)),
        (byte)Math.Round((weight * a.B) + ((1 - weight) * b.B)));
}
