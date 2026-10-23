using Docker.DotNet;
using Dray.Docker;
using Xunit;

namespace Dray.Docker.Tests;

/// <summary>What <c>/images/create</c> is asked for, which decides whether the right image arrives.</summary>
public class DockerPullTests
{
    [Fact]
    public void ADigestGoesInTheTagWhereTheEngineReadsItAsOne()
    {
        // Split on the last colon instead, this was a repository called "nginx@sha256".
        var parameters = DockerImages.PullParameters("nginx@sha256:a3554c0d0b48177b1413c26e01f695334428020d9df01243fbae3e11cfd17417");

        Assert.Equal("nginx", parameters.FromImage);
        Assert.Equal("sha256:a3554c0d0b48177b1413c26e01f695334428020d9df01243fbae3e11cfd17417", parameters.Tag);
    }

    [Fact]
    public void ARegistryPortIsNotATag()
    {
        var parameters = DockerImages.PullParameters("localhost:5000/app");

        Assert.Equal("localhost:5000/app", parameters.FromImage);
        Assert.Equal("latest", parameters.Tag);
    }

    [Fact]
    public void ATagIsNeverLeftEmpty()
    {
        // An empty tag on a bare name asks the engine for every tag the repository has.
        Assert.Equal("latest", DockerImages.PullParameters("nginx").Tag);
        Assert.Equal("alpine", DockerImages.PullParameters("nginx:alpine").Tag);
    }

    [Fact]
    public void ARefusalIsTheEnginesSentenceNotTheClientsWrapper()
    {
        // Captured from Docker 29 pulling a Docker Hub repository that does not exist.
        var ex = new DockerApiException(System.Net.HttpStatusCode.NotFound,
            """{"message":"pull access denied for ho-world, repository does not exist or may require 'docker login'"}""");

        Assert.Equal("pull access denied for ho-world, repository does not exist or may require 'docker login'", DockerImages.EngineMessage(ex));
    }
}
