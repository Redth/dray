using Xunit;
using Dray.Core.Theme;

namespace Dray.Core.Tests;

/// <summary>
/// Guards the generated palette. The JS pipeline checks contrast; these check that the C# side
/// the native heads consume is complete and consistent with it. A generator bug that dropped a
/// role would otherwise surface as a black NSColor at runtime.
/// </summary>
public class TokensTests
{
    public static TheoryData<DrayColor> AllColors()
    {
        var data = new TheoryData<DrayColor>();
        foreach (var c in Enum.GetValues<DrayColor>()) data.Add(c);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllColors))]
    public void EveryRoleIsDefinedInBothThemes(DrayColor color)
    {
        Assert.True(DrayTokens.Light.ContainsKey(color), $"Light theme is missing {color}");
        Assert.True(DrayTokens.Dark.ContainsKey(color), $"Dark theme is missing {color}");
    }

    [Fact]
    public void ThemesHaveNoExtraRoles()
    {
        var known = Enum.GetValues<DrayColor>().ToHashSet();
        Assert.DoesNotContain(DrayTokens.Light.Keys, k => !known.Contains(k));
        Assert.DoesNotContain(DrayTokens.Dark.Keys, k => !known.Contains(k));
    }

    [Fact]
    public void GetResolvesPerTheme()
    {
        Assert.Equal(DrayTokens.Light[DrayColor.Bg], DrayTokens.Get(DrayTheme.Light, DrayColor.Bg));
        Assert.Equal(DrayTokens.Dark[DrayColor.Bg], DrayTokens.Get(DrayTheme.Dark, DrayColor.Bg));
    }

    [Fact]
    public void LightAndDarkGroundsAreOpposites()
    {
        // A generator that emitted the same block twice would pass every other test here.
        var light = DrayTokens.Light[DrayColor.Bg];
        var dark = DrayTokens.Dark[DrayColor.Bg];
        Assert.True(Luminance(light) > 0.8, $"Light bg should be near-white, got {light.ToHex()}");
        Assert.True(Luminance(dark) < 0.2, $"Dark bg should be near-black, got {dark.ToHex()}");
    }

    [Theory]
    [MemberData(nameof(AllColors))]
    public void InkIsReadableOnItsGround(DrayColor _)
    {
        // Body text must clear WCAG AA on the window ground in both themes. This duplicates the
        // JS verifier deliberately: it is the one invariant worth failing the C# build over.
        foreach (var theme in new[] { DrayTheme.Light, DrayTheme.Dark })
        {
            var ratio = Contrast(DrayTokens.Get(theme, DrayColor.Ink), DrayTokens.Get(theme, DrayColor.Bg));
            Assert.True(ratio >= 4.5, $"{theme}: ink on bg is {ratio:F2}:1, needs 4.5:1");
        }
    }

    [Fact]
    public void ToHexRoundTripsToSixDigits()
    {
        var hex = DrayTokens.Light[DrayColor.Brand].ToHex();
        Assert.Matches("^#[0-9a-f]{6}$", hex);
    }

    static double Luminance(DrayRgb c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    static double Contrast(DrayRgb a, DrayRgb b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }
}
