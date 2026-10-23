using Dray.Core.Theme;
using WinColor = Windows.UI.Color;
using Xaml = Microsoft.UI.Xaml;

namespace Dray.WinUI;

/// <summary>
/// The committing button of a destructive dialog, drawn in Dray's danger colours.
/// <para>
/// WinUI has no destructive button style, so a ContentDialog's Remove looks exactly like its Save.
/// DESIGN.md section 2.5 says the committing button of a destructive confirmation is the one place
/// a danger fill belongs, and the web head draws it from the same tokens: a filled
/// <c>--danger</c> in light, and in dark a <c>--danger-tint</c> fill with danger text and border,
/// because a saturated fill on a dark ground is the exception <c>tokens.json</c> declares.
/// </para>
/// <para>
/// Applied through WinUI's lightweight styling — the button's own resource keys for each visual
/// state — so hover and press keep working rather than being frozen at one colour.
/// </para>
/// </summary>
public static class WinDanger
{
    /// <summary>Style the dialog's primary button once its template exists.</summary>
    public static void Style(Xaml.Controls.ContentDialog dialog)
    {
        void Apply()
        {
            if (Find(dialog, "PrimaryButton") is not Xaml.Controls.Button button) return;

            var dark = dialog.ActualTheme == Xaml.ElementTheme.Dark;
            var theme = dark ? DrayTheme.Dark : DrayTheme.Light;

            var danger = Color(theme, DrayColor.Danger);
            var rest = dark ? Color(theme, DrayColor.DangerTint) : danger;
            var text = dark ? danger : Color(theme, DrayColor.OnDanger);
            var border = dark ? danger : rest;

            // The web head's hover: towards ink in light, towards the ground in dark.
            var hover = dark
                ? Mix(danger, Color(theme, DrayColor.Bg), 0.28)
                : Mix(danger, Color(theme, DrayColor.Ink), 0.88);

            Set(button, "Background", rest, hover, hover);
            Set(button, "Foreground", text, text, text);
            Set(button, "BorderBrush", border, border, border);

            // The resources answer for states entered from now on; the state the button is already
            // in has resolved its brushes, so it is given them directly too.
            button.Background = new Xaml.Media.SolidColorBrush(rest);
            button.Foreground = new Xaml.Media.SolidColorBrush(text);
            button.BorderBrush = new Xaml.Media.SolidColorBrush(border);
        }

        dialog.Opened += (_, _) => Apply();
        dialog.ActualThemeChanged += (_, _) => Apply();

        // The safe button is the default one, and must not be drawn as if it were the commit.
        WinPalette.SecondaryDefault(dialog.Resources);
    }

    static void Set(Xaml.Controls.Button button, string part, WinColor rest, WinColor hover, WinColor pressed)
    {
        // Both families: a ContentDialog's primary button takes the accent style when it is the
        // default and the plain one when it is not, and a destructive button is never the default
        // only by convention.
        foreach (var family in new[] { "Button", "AccentButton" })
        {
            button.Resources[$"{family}{part}"] = new Xaml.Media.SolidColorBrush(rest);
            button.Resources[$"{family}{part}PointerOver"] = new Xaml.Media.SolidColorBrush(hover);
            button.Resources[$"{family}{part}Pressed"] = new Xaml.Media.SolidColorBrush(pressed);
        }
    }

    static WinColor Color(DrayTheme theme, DrayColor role)
    {
        var rgb = DrayTokens.Get(theme, role);
        return WinColor.FromArgb(255, rgb.R, rgb.G, rgb.B);
    }

    /// <summary>
    /// <paramref name="weight"/> of <paramref name="a"/> over <paramref name="b"/>, in sRGB. The web
    /// mixes in OKLab; at these weights the two differ by less than a channel step that matters for
    /// a hover state, and the contrast of the text is unaffected because hover never changes it.
    /// </summary>
    static WinColor Mix(WinColor a, WinColor b, double weight) => WinColor.FromArgb(
        255,
        (byte)Math.Round((weight * a.R) + ((1 - weight) * b.R)),
        (byte)Math.Round((weight * a.G) + ((1 - weight) * b.G)),
        (byte)Math.Round((weight * a.B) + ((1 - weight) * b.B)));

    static Xaml.DependencyObject? Find(Xaml.DependencyObject root, string name)
    {
        for (var i = 0; i < Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Xaml.FrameworkElement { Name: var n } && n == name) return child;
            if (Find(child, name) is { } found) return found;
        }

        return null;
    }
}
