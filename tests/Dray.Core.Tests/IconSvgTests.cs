using System.Xml.Linq;
using Dray.Core.Shell;
using Dray.Core.Theme;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Guards the standalone SVG Windows chrome renders. <c>SvgImageSource</c> fails on a malformed
/// document by drawing nothing — a blank sidebar row rather than an error — so the only place a
/// bad icon can be caught before it reaches a screen is here.
/// </summary>
public class IconSvgTests
{
    public static TheoryData<IconRef> AllIcons()
    {
        var data = new TheoryData<IconRef>();
        foreach (var icon in Enum.GetValues<IconRef>()) data.Add(icon);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllIcons))]
    public void EveryIconIsAWellFormedSvgDocument(IconRef icon)
    {
        var svg = XElement.Parse(Icons.Svg(icon, DrayTokens.Light[DrayColor.Ink].ToHex()));

        Assert.Equal("svg", svg.Name.LocalName);
        Assert.Equal("http://www.w3.org/2000/svg", svg.Name.NamespaceName);
        Assert.NotEmpty(svg.Elements());
    }

    [Fact]
    public void TheStrokeIsTheColourTheHeadPassed()
    {
        var stroke = DrayTokens.Dark[DrayColor.Ink].ToHex();
        var svg = XElement.Parse(Icons.Svg(IconRef.Container, stroke));

        Assert.Equal(stroke, (string?)svg.Attribute("stroke"));
        Assert.Equal("none", (string?)svg.Attribute("fill"));
    }
}
