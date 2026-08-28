using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Reading the builders buildx reports.
/// <para>
/// The fixture is captured verbatim from <c>docker-buildx ls --format json</c> on this machine,
/// buildx v0.36.1 — including the broken builder, which is the case worth having: a builder
/// configured against an endpoint that no longer exists is the one that fails a build for a reason
/// nothing on screen explained.
/// </para>
/// </summary>
public class BuildxTests
{
    const string Measured = """
        {"Current":false,"Driver":"docker-container","Dynamic":false,"LastActivity":"2026-08-26T13:28:34Z","Name":"multiarch-builder","Nodes":[{"Endpoint":"orbstack","Err":"unable to parse docker host `orbstack`","Flags":["--allow-insecure-entitlement=network.host"],"Name":"multiarch-builder0","Status":"error"}]}
        {"Current":true,"Driver":"docker-container","Dynamic":false,"LastActivity":"2026-08-09T07:20:36Z","Name":"default","Nodes":[{"Endpoint":"default","Name":"default","Status":"inactive"}]}
        """;

    [Fact]
    public void ReadsEveryBuilderAndWhichOneIsCurrent()
    {
        var builders = Buildx.Parse(Measured);

        Assert.Equal(["multiarch-builder", "default"], builders.Select(b => b.Name));
        Assert.Equal("default", Assert.Single(builders, b => b.IsCurrent).Name);
        Assert.All(builders, b => Assert.Equal("docker-container", b.Driver));
    }

    [Fact]
    public void ABuilderPointingAtSomethingThatIsNotThereSaysSo()
    {
        var broken = Buildx.Parse(Measured).Single(b => b.Name == "multiarch-builder");

        Assert.False(broken.IsUsable);
        Assert.Equal("unable to parse docker host `orbstack`", broken.Problem);
    }

    [Fact]
    public void AnInactiveBuilderIsNotABrokenOne()
    {
        // "inactive" is a builder that has not been bootstrapped yet — the first build starts it.
        // Treating it as broken would hide the default builder on most machines.
        var usable = Buildx.Parse(Measured).Single(b => b.Name == "default");

        Assert.True(usable.IsUsable);
        Assert.Null(usable.Problem);
        Assert.Equal("inactive", Assert.Single(usable.NodeList).Status);
    }

    [Fact]
    public void PlatformsAreCollectedAcrossNodesWithoutRepeating()
    {
        const string json =
            """{"Name":"multi","Driver":"docker-container","Nodes":[{"Name":"a","Platforms":["linux/amd64","linux/arm64"]},{"Name":"b","Platforms":["linux/arm64","linux/arm/v7"]}]}""";

        var builder = Assert.Single(Buildx.Parse(json));

        Assert.Equal(["linux/amd64", "linux/arm64", "linux/arm/v7"], builder.Platforms);
    }

    [Fact]
    public void ANodeWithNoPlatformsReportsNoneRatherThanFailing()
    {
        // The measured fixture is exactly this: buildx omits Platforms for a builder it has not
        // inspected yet.
        Assert.All(Buildx.Parse(Measured), b => Assert.Empty(b.Platforms));
    }

    [Fact]
    public void ALineThatCannotBeReadCostsThatBuilderAndNotTheList()
    {
        var builders = Buildx.Parse("""
            {"Name":"good","Driver":"docker"}
            {"Name":"broken",
            {"Name":"also-good","Driver":"docker"}
            """);

        Assert.Equal(["good", "also-good"], builders.Select(b => b.Name));
    }

    [Fact]
    public void NothingAtAllIsAnEmptyList()
    {
        Assert.Empty(Buildx.Parse(null));
        Assert.Empty(Buildx.Parse(""));
        Assert.Empty(Buildx.Parse("NAME/NODE   DRIVER/ENDPOINT   STATUS"));
    }
}
