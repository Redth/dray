using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Naming a saved image, and reading back what an engine says it loaded.
/// </summary>
public class ImageArchiveTests
{
    [Theory]
    [InlineData("nginx:alpine", "nginx-alpine.tar")]
    [InlineData("ghcr.io/redth/dray:1.4.2", "redth-dray-1.4.2.tar")]
    [InlineData("docker.io/library/redis:7-alpine", "library-redis-7-alpine.tar")]
    [InlineData("localhost:5000/app:dev", "app-dev.tar")]
    [InlineData("postgres", "postgres.tar")]
    public void ANameSaysWhichImageAndWhichTag(string reference, string expected)
        => Assert.Equal(expected, ImageArchive.SuggestedFileName(reference));

    [Fact]
    public void ARegistryHostIsDroppedAndALibraryPrefixIsNot()
    {
        // "library" is a namespace, not a host, and podman prefixes everything with it — dropping
        // it would name two different images the same file.
        Assert.Equal("library-redis-7.tar", ImageArchive.SuggestedFileName("docker.io/library/redis:7"));
        Assert.Equal("redis-7.tar", ImageArchive.SuggestedFileName("library-redis:7").Replace("library-", ""));
    }

    [Fact]
    public void ADigestGetsSomethingShortRatherThanSixtyFourCharacters()
    {
        var name = ImageArchive.SuggestedFileName("sha256:9a56851f1a97e0586f85f8d7f7652e65cb589b2409d965c5d6e275dfc2551907");

        Assert.Equal("image-9a56851f1a97.tar", name);
    }

    [Fact]
    public void NothingUsableStillProducesAFileName()
    {
        Assert.Equal("image.tar", ImageArchive.SuggestedFileName(null));
        Assert.Equal("image.tar", ImageArchive.SuggestedFileName("   "));
        Assert.Equal("image.tar", ImageArchive.SuggestedFileName("///"));
    }

    [Fact]
    public void TheNameIsAlwaysAUsableFileName()
    {
        var name = ImageArchive.SuggestedFileName("ghcr.io/redth/dray:1.4.2");

        Assert.Equal(-1, name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()));
        Assert.EndsWith(".tar", name, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- load output

    [Fact]
    public void ReadsWhatDockerSaysItLoaded()
    {
        string[] output =
        [
            "Loaded image: nginx:alpine",
            "Loaded image: ghcr.io/redth/dray:1.4.2",
        ];

        Assert.Equal(["nginx:alpine", "ghcr.io/redth/dray:1.4.2"], ImageArchive.LoadedNames(output));
    }

    [Fact]
    public void AnUntaggedArchiveReportsItsId()
    {
        string[] output = ["Loaded image ID: sha256:9a56851f1a97"];

        Assert.Equal(["sha256:9a56851f1a97"], ImageArchive.LoadedNames(output));
    }

    [Fact]
    public void ReadsABareReferenceTheWayAppleContainerPrintsIt()
    {
        string[] output = ["nginx:alpine"];

        Assert.Equal(["nginx:alpine"], ImageArchive.LoadedNames(output));
    }

    [Fact]
    public void ProgressChatterIsNotMistakenForAName()
    {
        // Reporting the wrong name is worse than reporting a count.
        string[] output =
        [
            "Loading layer  1/12",
            "unpacking image",
            "done",
            "Loaded image: nginx:alpine",
        ];

        Assert.Equal(["nginx:alpine"], ImageArchive.LoadedNames(output));
    }

    [Fact]
    public void TheSameImageTwiceIsReportedOnce()
    {
        string[] output = ["Loaded image: nginx:alpine", "nginx:alpine"];

        Assert.Single(ImageArchive.LoadedNames(output));
    }

    [Fact]
    public void NothingAtAllIsNotAFailure()
    {
        Assert.Empty(ImageArchive.LoadedNames(null));
        Assert.Empty(ImageArchive.LoadedNames([]));
        Assert.Empty(ImageArchive.LoadedNames(["", "   "]));
    }
}
