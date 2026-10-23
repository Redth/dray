using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The standard registry API hands tags over in lexical order, which puts 10.0 between 1.9 and 2.0
/// and the newest release of a date-versioned project at the bottom of a thousand rows.
/// </summary>
public class TagOrderTests
{
    static IReadOnlyList<string> Order(params string[] tags)
        => [.. TagOrder.Newest(tags.Select(t => new RemoteTag(t))).Select(t => t.Name)];

    [Fact]
    public void LatestComesFirst()
        => Assert.Equal("latest", Order("1.0", "alpine", "latest")[0]);

    [Fact]
    public void VersionsAreNumbersNotText()
        => Assert.Equal(["10.0", "9.0", "8.0", "1.9"], Order("1.9", "10.0", "8.0", "9.0"));

    [Fact]
    public void TheFloatingTagSitsAboveItsVariants()
    {
        // "18" is the one most people want; "18-alpine" and "18.0" are refinements of it.
        Assert.Equal(["18", "18-alpine", "18-bookworm", "18.0", "18.0-alpine", "17"],
            Order("17", "18.0-alpine", "18-bookworm", "18.0", "18", "18-alpine"));
    }

    [Fact]
    public void AReleaseSitsAboveItsPreReleases()
        => Assert.Equal(["2023.4.0", "2023.4.0b1", "2023.4.0b2"], Order("2023.4.0b2", "2023.4.0", "2023.4.0b1"));

    [Fact]
    public void ALeadingVIsStillAVersion()
        => Assert.Equal(["v2.0.0", "v1.10.0", "v1.9.0"], Order("v1.9.0", "v2.0.0", "v1.10.0"));

    [Fact]
    public void DateStampsTooBigForAnIntStillOrder()
        => Assert.Equal(["20240101", "20210427"], Order("20210427", "20240101"));

    [Fact]
    public void NamesWithoutAVersionComeAfterVersionsByName()
        => Assert.Equal(["1.0", "alpine", "edge", "sha-a1b2"], Order("sha-a1b2", "edge", "1.0", "alpine"));

    [Fact]
    public void DigitsInsideNamesCompareAsNumbers()
        => Assert.True(TagOrder.Natural("alpine3.9", "alpine3.21") < 0);

    [Fact]
    public void AFilterPutsTagsThatStartWithItFirst()
    {
        var tags = new[] { "9.6-alpine3.16", "16", "16-alpine" }.Select(t => new RemoteTag(t)).ToList();

        Assert.Equal(["16", "16-alpine", "9.6-alpine3.16"], TagOrder.Filter(tags, "16").Select(t => t.Name));
    }

    [Fact]
    public void TheTagTypedInFullComesFirst()
    {
        var tags = TagOrder.Newest(new[] { "2025.1", "2025.12", "2025.12.1", "2025.10" }.Select(t => new RemoteTag(t)));

        Assert.Equal("2025.1", TagOrder.Filter(tags, "2025.1")[0].Name);
    }

    [Fact]
    public void AFilterIgnoresCase()
        => Assert.Single(TagOrder.Filter([new RemoteTag("alpine")], "Alp"));

    [Fact]
    public void NoFilterIsEveryTagInOrder()
    {
        IReadOnlyList<RemoteTag> tags = [new("b"), new("a")];
        Assert.Same(tags, TagOrder.Filter(tags, ""));
    }
}
