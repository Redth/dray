using Docker.DotNet.Models;
using Xunit;

namespace Dray.Docker.Tests;

/// <summary>
/// The volume browser mounts a volume into a container the user never asked for. Everything here
/// is about that container staying invisible — and not staying at all.
/// </summary>
public class VolumeHelperTests
{
    static ContainerListResponse Container(string name, params (string Volume, string Destination)[] mounts)
        => new()
        {
            ID = new string('a', 64),
            Names = [$"/{name}"],
            Mounts =
            [
                .. mounts.Select(m => new MountPoint
                {
                    Type = "volume",
                    Name = m.Volume,
                    Destination = m.Destination,
                    RW = true,
                }),
            ],
        };

    [Fact]
    public void AHelperDoesNotCountAsUsingTheVolumeItMounts()
    {
        // Opening a volume would otherwise make it report itself as in use, which is both wrong
        // and exactly backwards: browsing a volume is how you decide whether to delete it.
        var helper = Container("dray-browse-data-a1b2c3", ("data", "/dray-volume"));
        helper.Labels = new Dictionary<string, string> { [DockerVolumeSession.HelperLabel] = "volume-browser" };

        Assert.True(helper.Labels.ContainsKey(DockerVolumeSession.HelperLabel));
    }

    [Fact]
    public void AHelperIsNotOneOfTheUsersContainers()
    {
        var helper = new ContainerListResponse
        {
            ID = new string('b', 64),
            Names = ["/dray-browse-data-a1b2c3"],
            Labels = new Dictionary<string, string> { [DockerVolumeSession.HelperLabel] = "volume-browser" },
        };

        var mine = new ContainerListResponse
        {
            ID = new string('c', 64),
            Names = ["/dray-web"],
            Labels = new Dictionary<string, string> { ["com.docker.compose.project"] = "dray" },
        };

        Assert.False(DockerRuntime.IsUsersOwn(helper));
        Assert.True(DockerRuntime.IsUsersOwn(mine));
    }

    [Fact]
    public void AContainerWithNoLabelsAtAllIsTheUsers()
    {
        // Most containers have no labels; a null check missed here would empty the whole table.
        Assert.True(DockerRuntime.IsUsersOwn(new ContainerListResponse { ID = "x", Names = ["/plain"] }));
    }

    // ---------------------------------------------------------------- image choice

    [Theory]
    [InlineData("alpine", true)]
    [InlineData("alpine:3.20", true)]
    [InlineData("docker.io/library/busybox:latest", true)]
    [InlineData("busybox", true)]
    public void AThrowawayImageIsPreferred(string reference, bool expected)
        => Assert.Equal(expected, DockerVolumeSession.IsThrowawayRepositoryFor(reference));

    [Theory]
    [InlineData("library/nginx:alpine")]
    [InlineData("nginx:1.27-alpine")]
    [InlineData("my-registry.example.com:5000/app:alpine")]
    [InlineData("postgres:16-alpine")]
    public void AnImageThatMerelyHasAnAlpineTagIsNotAThrowaway(string reference)
    {
        // The earlier version matched "alpine" anywhere in the reference and cheerfully picked the
        // user's nginx. Functionally harmless, but a stray helper named after a production image
        // is exactly the thing that makes someone distrust the app.
        Assert.False(DockerVolumeSession.IsThrowawayRepositoryFor(reference));
    }

    [Fact]
    public void ARegistryPortIsNotMistakenForATag()
    {
        // "host:5000/busybox" has a colon before the last slash; cutting at it would leave "host".
        Assert.True(DockerVolumeSession.IsThrowawayRepositoryFor("registry.local:5000/busybox"));
    }

    // ---------------------------------------------------------------- naming

    [Fact]
    public void TheHelperIsNamedAfterWhatItIsAndWhatItWasFor()
    {
        var name = DockerVolumeSession.HelperNameFor("dray-demo", "a1b2c3");

        Assert.Equal("dray-browse-dray-demo-a1b2c3", name);
    }

    [Fact]
    public void AnAnonymousVolumesNameIsTruncatedRatherThanUsedWhole()
    {
        // 64 hex characters would make an unreadable and needlessly long container name.
        var name = DockerVolumeSession.HelperNameFor(new string('f', 64), "a1b2c3");

        Assert.Equal($"dray-browse-{new string('f', 24)}-a1b2c3", name);
    }

    [Theory]
    [InlineData("my volume", "my-volume")]
    [InlineData("weird/name", "weird-name")]
    [InlineData("dots.and.things", "dots-and-things")]
    public void CharactersAnEngineWouldRejectAreReplaced(string volume, string expected)
        => Assert.Equal($"dray-browse-{expected}-a1b2c3", DockerVolumeSession.HelperNameFor(volume, "a1b2c3"));
}
