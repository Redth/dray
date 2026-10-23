using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// "Does this tag run here" is the question an ARM laptop asks of every image. The engine and the
/// registries spell the answer in different vocabularies, so the conversion is where it goes wrong.
/// </summary>
public class ImagePlatformTests
{
    [Theory]
    [InlineData("aarch64", "arm64", null)]
    [InlineData("x86_64", "amd64", null)]
    [InlineData("armv7l", "arm", "v7")]
    [InlineData("armv6l", "arm", "v6")]
    [InlineData("i686", "386", null)]
    [InlineData("s390x", "s390x", null)]
    public void TheKernelsWordBecomesTheRegistrys(string reported, string architecture, string? variant)
    {
        // Measured: Docker Desktop on an M-series Mac reports "aarch64" in /info, and every manifest
        // calls the same machine "arm64". Unconverted, nothing on any registry would run there.
        var platform = ImagePlatform.OfEngine(ContainerOs.Linux, reported);

        Assert.NotNull(platform);
        Assert.Equal(architecture, platform.Architecture);
        Assert.Equal(variant, platform.Variant);
    }

    [Fact]
    public void AnUnknownContainerOsIsNoPlatformAtAll()
    {
        // Not a Linux guess: a Windows engine would then be told Linux images run on it.
        Assert.Null(ImagePlatform.OfEngine(ContainerOs.Unknown, "x86_64"));
        Assert.Null(ImagePlatform.OfEngine(ContainerOs.Linux, null));
    }

    [Fact]
    public void Arm64RunsWhetherOrNotTheRegistryWroteV8()
    {
        var engine = ImagePlatform.OfEngine(ContainerOs.Linux, "aarch64")!;

        Assert.True(new ImagePlatform("linux", "arm64", "v8").RunsOn(engine));
        Assert.True(new ImagePlatform("linux", "arm64").RunsOn(engine));
    }

    [Fact]
    public void ThirtyTwoBitArmVariantsAreDifferentMachines()
    {
        var v7 = new ImagePlatform("linux", "arm", "v7");

        Assert.False(new ImagePlatform("linux", "arm", "v6").RunsOn(v7));
        Assert.True(new ImagePlatform("linux", "arm", "v7").RunsOn(v7));
    }

    [Fact]
    public void AnotherOsOrArchitectureDoesNotRun()
    {
        var engine = new ImagePlatform("linux", "arm64");

        Assert.False(new ImagePlatform("linux", "amd64").RunsOn(engine));
        Assert.False(new ImagePlatform("windows", "arm64").RunsOn(engine));
    }

    [Fact]
    public void AttestationsAreNotPlatforms()
        => Assert.True(new ImagePlatform("unknown", "unknown").IsAttestation);

    [Theory]
    [InlineData("arm64", null, "arm64")]
    [InlineData("arm64", "v8", "arm64")]
    [InlineData("arm", "v7", "armv7")]
    [InlineData("amd64", null, "amd64")]
    public void TheLabelIsWhatPeopleCallIt(string architecture, string? variant, string label)
        => Assert.Equal(label, new ImagePlatform("linux", architecture, variant).Label);

    [Fact]
    public void ATagWithNoPlatformsListedIsUnknownNotNo()
    {
        var engine = new ImagePlatform("linux", "arm64");

        Assert.Null(new RemoteTag("1.0").RunsOn(engine));
        Assert.Null(new RemoteTag("1.0", Images: []).RunsOn(engine));
        Assert.False(new RemoteTag("1.0", Images: [new TagImage(new ImagePlatform("linux", "amd64"))]).RunsOn(engine));
    }

    [Fact]
    public void TheDownloadIsTheEnginesOwnPlatforms()
    {
        var tag = new RemoteTag("1.0", Images:
        [
            new TagImage(new ImagePlatform("linux", "amd64"), 70_000_000),
            new TagImage(new ImagePlatform("linux", "arm64", "v8"), 65_000_000),
        ]);

        Assert.Equal(65_000_000, tag.ImageFor(new ImagePlatform("linux", "arm64"))!.SizeBytes);
    }
}
