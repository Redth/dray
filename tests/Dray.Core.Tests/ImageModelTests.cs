using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

public class ImageTagTests
{
    [Theory]
    [InlineData("nginx:alpine", "nginx", "alpine")]
    [InlineData("docker.io/library/redis:7-alpine", "docker.io/library/redis", "7-alpine")]
    [InlineData("ghcr.io/owner/app:1.2.3", "ghcr.io/owner/app", "1.2.3")]
    public void ATagSplitsFromItsRepository(string reference, string repository, string tag)
    {
        var parsed = ImageTag.Parse(reference);

        Assert.Equal(repository, parsed.Repository);
        Assert.Equal(tag, parsed.Tag);
    }

    [Fact]
    public void ANameWithNoTagMeansLatest()
        => Assert.Equal("latest", ImageTag.Parse("nginx").Tag);

    [Fact]
    public void ARegistryPortIsNotATag()
    {
        // "registry.local:5000/app" has a colon before the last slash. Cutting there would leave a
        // repository of "registry.local" and a tag of "5000/app".
        var parsed = ImageTag.Parse("registry.local:5000/app");

        Assert.Equal("registry.local:5000/app", parsed.Repository);
        Assert.Equal("latest", parsed.Tag);
    }

    [Fact]
    public void ARegistryPortAndATagBothSurvive()
    {
        var parsed = ImageTag.Parse("registry.local:5000/team/app:2.0");

        Assert.Equal("registry.local:5000/team/app", parsed.Repository);
        Assert.Equal("2.0", parsed.Tag);
    }

    [Theory]
    [InlineData("docker.io/library/nginx", "nginx")]
    [InlineData("docker.io/redth/dray", "redth/dray")]
    [InlineData("library/alpine", "alpine")]
    [InlineData("ghcr.io/owner/app", "ghcr.io/owner/app")]
    public void TheRegistryPrefixIsDroppedForDisplay(string repository, string expected)
        => Assert.Equal(expected, new ImageTag(repository, "latest").ShortRepository);
}

public class ImageSummaryTests
{
    static ImageSummary Image(params string[] tags) => new()
    {
        Id = "sha256:" + new string('a', 64),
        Tags = [.. tags.Select(ImageTag.Parse)],
    };

    [Fact]
    public void AnImageWithNoTagsIsDangling()
        => Assert.True(Image().IsDangling);

    [Fact]
    public void ATaggedImageIsNot()
        => Assert.False(Image("nginx:alpine").IsDangling);

    [Fact]
    public void TheAlgorithmPrefixIsNotPartOfTheShortId()
    {
        // "sha256:abc…" shown as an id would waste seven of twelve characters on something every
        // id has.
        Assert.Equal(new string('a', 12), Image().ShortId);
    }

    [Fact]
    public void UniqueBytesAreWhatDeletingWouldActuallyFree()
    {
        // The number a prune preview has to show: an image sharing most of its layers frees far
        // less than its listed size.
        var image = new ImageSummary { Id = "x", SizeBytes = 900, SharedBytes = 850 };

        Assert.Equal(50, image.UniqueBytes);
    }

    [Fact]
    public void SharedBytesLargerThanTheImageDoNotProduceANegativeSaving()
        => Assert.Equal(0, new ImageSummary { Id = "x", SizeBytes = 100, SharedBytes = 500 }.UniqueBytes);

    [Fact]
    public void AnUncountedImageIsNotReportedAsUnused()
    {
        // The engine sends -1 when it has not counted. "Nothing is using this" would be a claim it
        // never made, and the button next to it deletes things.
        Assert.Null(new ImageSummary { Id = "x", ContainerCount = -1 }.IsInUse);
        Assert.False(new ImageSummary { Id = "x", ContainerCount = 0 }.IsInUse);
        Assert.True(new ImageSummary { Id = "x", ContainerCount = 2 }.IsInUse);
    }
}

public class ImageLayerTests
{
    static ImageLayer Layer(string createdBy) => new("id", null, 0, createdBy, null);

    [Fact]
    public void TheBuildkitNopPrefixIsStripped()
    {
        // The engine records the machinery; the Dockerfile said the rest.
        Assert.Equal("CMD [\"nginx\"]", Layer("/bin/sh -c #(nop)  CMD [\"nginx\"]").Instruction);
    }

    [Fact]
    public void AShellCommandIsShownAsRun()
        => Assert.Equal("RUN apk add curl", Layer("/bin/sh -c apk add curl").Instruction);

    [Fact]
    public void ABuildkitInstructionIsLeftAlone()
        => Assert.Equal("ADD rootfs.tar.gz /", Layer("ADD rootfs.tar.gz /").Instruction);

    [Fact]
    public void AZeroSizeLayerIsMetadata()
        => Assert.True(new ImageLayer("id", null, 0, "ENV A=b", null).IsEmpty);
}

public class PullProgressTests
{
    [Fact]
    public void AFractionNeedsATotal()
    {
        // The engine reports "Extracting" with no total for some layers; a bar computed from zero
        // would sit at 100% for the whole extraction.
        Assert.Null(new PullProgress("l", "Extracting", 50, 0).Fraction);
        Assert.Equal(0.5, new PullProgress("l", "Downloading", 50, 100).Fraction);
    }

    [Theory]
    [InlineData("Pull complete")]
    [InlineData("Already exists")]
    [InlineData("Image is up to date")]
    public void ALayerNeedingNothingFurtherIsComplete(string status)
        => Assert.True(new PullProgress("l", status).IsComplete);

    [Theory]
    [InlineData("Downloading")]
    [InlineData("Extracting")]
    [InlineData("Waiting")]
    public void ALayerStillWorkingIsNot(string status)
        => Assert.False(new PullProgress("l", status).IsComplete);

    [Fact]
    public void AnErrorInTheStreamIsAnError()
    {
        // The engine reports pull failures inside the stream rather than by failing the request.
        Assert.True(new PullProgress(null, "", Error: "denied").IsError);
    }
}

public class NetworkSummaryTests
{
    static NetworkSummary Network(string name, params NetworkMember[] members) =>
        new() { Id = "n", Name = name, Driver = "bridge", Members = members };

    [Theory]
    [InlineData("bridge")]
    [InlineData("host")]
    [InlineData("none")]
    [InlineData("podman")]
    public void TheEnginesOwnNetworksAreMarked(string name)
    {
        // Offering Remove on one is offering a button that always fails.
        Assert.True(Network(name).IsPredefined);
    }

    [Fact]
    public void AUserNetworkIsNot()
        => Assert.False(Network("dray-backend").IsPredefined);

    [Fact]
    public void ANetworkWithMembersIsInUse()
        => Assert.True(Network("n", new NetworkMember("abc", "web", "10.0.0.2/16", null)).IsInUse);

    [Fact]
    public void TheCidrSuffixIsNotPartOfAContainersAddress()
    {
        // "/16" describes the subnet, not this container.
        var member = new NetworkMember("abc", "web", "10.88.0.7/16", null);

        Assert.Equal("10.88.0.7", member.Address);
    }

    [Fact]
    public void AMemberWithNoAddressHasNone()
        => Assert.Null(new NetworkMember("abc", "web", null, null).Address);
}

public class PrunePreviewTests
{
    [Fact]
    public void AnEmptyPreviewIsEmpty()
        => Assert.True(PrunePreview.Empty(PruneKind.Images).IsEmpty);

    [Fact]
    public void ThePhraseNamesWhatIsBeingPruned()
    {
        // Not "DELETE": the phrase should describe the action, so typing it is reading it.
        Assert.Equal("prune volumes", PrunePreview.Empty(PruneKind.Volumes).ConfirmationPhrase);
    }

    [Fact]
    public void TheNounAgreesWithTheCount()
    {
        Assert.Equal("image", new PrunePreview(PruneKind.Images, ["a"], 0).Noun);
        Assert.Equal("images", new PrunePreview(PruneKind.Images, ["a", "b"], 0).Noun);
    }
}
