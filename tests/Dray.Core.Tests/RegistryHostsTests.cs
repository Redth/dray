using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

public class RegistryHostsTests
{
    [Theory]
    [InlineData("docker.io")]
    [InlineData("index.docker.io")]
    [InlineData("registry-1.docker.io")]
    [InlineData("https://index.docker.io/v1/")]
    [InlineData("")]
    public void EverySpellingOfDockerHubIsDockerHub(string host)
        => Assert.True(RegistryHosts.IsDockerHub(host));

    [Fact]
    public void DockerHubsApiIsNotWhereItsReferencesPoint()
    {
        Assert.Equal("registry-1.docker.io", RegistryHosts.ApiHost("docker.io"));
        Assert.Equal("ghcr.io", RegistryHosts.ApiHost("ghcr.io"));
    }

    [Fact]
    public void DockerHubsCredentialIsUnderItsLegacyUrl()
    {
        // The only key a helper matches for a Hub credential, as for the CLI.
        Assert.Equal("https://index.docker.io/v1/", RegistryHosts.CredentialServer("docker.io"));
        Assert.Equal("ghcr.io", RegistryHosts.CredentialServer("ghcr.io"));
    }

    [Theory]
    [InlineData("https://ghcr.io", "ghcr.io")]
    [InlineData("ghcr.io/", "ghcr.io")]
    [InlineData("https://index.docker.io/v1/", "docker.io")]
    [InlineData("http://localhost:5000", "localhost:5000")]
    public void AServerKeyBecomesAHost(string server, string host)
        => Assert.Equal(host, RegistryHosts.FromServer(server));

    [Fact]
    public void ASignedInWellKnownRegistryIsListedOnceWithItsUser()
    {
        var options = RegistryHosts.Offer([("https://ghcr.io", "redth")]);

        var github = Assert.Single(options, o => o.Host == "ghcr.io");
        Assert.Equal("redth", github.SignedInAs);
        Assert.True(github.IsConfigured);
        Assert.Equal("GitHub · redth", github.Label);
    }

    [Fact]
    public void AConfiguredRegistryNobodyElseKnowsIsAdded()
    {
        var options = RegistryHosts.Offer([("registry.example.com:5000", null)]);

        Assert.Equal("registry.example.com:5000", options[^1].Host);
        Assert.Equal(RegistryHosts.WellKnown.Count + 1, options.Count);
    }

    [Fact]
    public void ARegistryTypedIntoTheFieldIsAlwaysInThePicker()
    {
        // A picker that cannot show what the field says has two answers to "which registry".
        var options = RegistryHosts.Offer([], "nas.local:5000");

        Assert.Contains(options, o => o.Host == "nas.local:5000");
    }

    [Fact]
    public void DockerHubComesFirst()
        => Assert.Equal("docker.io", RegistryHosts.Offer([]).First().Host);
}

public class LocalImageIndexTests
{
    static ImageSummary Image(string tag, params string[] digests) => new()
    {
        Id = "sha256:" + tag.GetHashCode().ToString("x"),
        Tags = [ImageTag.Parse(tag)],
        Digests = digests,
        Created = DateTimeOffset.UnixEpoch,
    };

    const string Pulled = "sha256:a3554c0d0b48177b1413c26e01f695334428020d9df01243fbae3e11cfd17417";

    [Fact]
    public void ATagWhoseDigestMatchesIsPresent()
    {
        // Measured: Docker Hub's tag digest for nginx:alpine equals the RepoDigests entry Docker
        // Desktop recorded when it pulled it.
        var index = new LocalImageIndex([Image("nginx:alpine", "nginx@" + Pulled)]);

        Assert.Equal(LocalTagState.Present,
            index.StateOf(ImageReferenceDraft.Parse("nginx"), new RemoteTag("alpine", Digest: Pulled)));
    }

    [Fact]
    public void ATagThatPointsElsewhereNowHasMoved()
    {
        var index = new LocalImageIndex([Image("nginx:alpine", "nginx@" + Pulled)]);

        Assert.Equal(LocalTagState.Moved,
            index.StateOf(ImageReferenceDraft.Parse("nginx"), new RemoteTag("alpine", Digest: "sha256:other")));
    }

    [Fact]
    public void PodmansLongSpellingIsTheSameImage()
    {
        var index = new LocalImageIndex([Image("docker.io/library/nginx:alpine", "docker.io/library/nginx@" + Pulled)]);

        Assert.Equal(LocalTagState.Present,
            index.StateOf(ImageReferenceDraft.Parse("nginx"), new RemoteTag("alpine", Digest: Pulled)));
    }

    [Fact]
    public void AnImageBuiltHereHasNothingToCompareSoIsOnlyPresent()
    {
        // Not "moved": there is no registry copy it came from.
        var index = new LocalImageIndex([Image("nginx:alpine")]);

        Assert.Equal(LocalTagState.Present,
            index.StateOf(ImageReferenceDraft.Parse("nginx"), new RemoteTag("alpine", Digest: "sha256:other")));
    }

    [Fact]
    public void AnotherTagIsAbsent()
    {
        var index = new LocalImageIndex([Image("nginx:alpine", "nginx@" + Pulled)]);

        Assert.Equal(LocalTagState.Absent, index.StateOf(ImageReferenceDraft.Parse("nginx"), new RemoteTag("latest")));
    }

    [Fact]
    public void AnotherRegistrysDigestIsNotCompared()
    {
        // Pulled from Docker Hub and tagged for GHCR here: its one digest is Hub's. Compared against
        // GHCR's tag it differs, and "moved" would be a claim about a copy this engine never pulled.
        var index = new LocalImageIndex([Image("ghcr.io/owner/app:1", "owner/app@sha256:hub")]);

        Assert.Equal(LocalTagState.Present,
            index.StateOf(ImageReferenceDraft.Parse("ghcr.io/owner/app"), new RemoteTag("1", Digest: "sha256:ghcr")));
    }
}

public class PullRefusalTests
{
    [Theory]
    [InlineData("pull access denied for nope, repository does not exist or may require 'docker login': denied: requested access to the resource is denied")]
    [InlineData("Head \"https://ghcr.io/v2/owner/app/manifests/latest\": unauthorized")]
    [InlineData("no basic auth credentials")]
    [InlineData("denied: requested access to the resource is denied")]
    public void ARegistryAskingWhoIsAskingIsACredentialQuestion(string error)
        => Assert.True(PullRefusal.NeedsCredential(error));

    [Theory]
    [InlineData("manifest for nginx:nope not found: manifest unknown")]
    [InlineData("no matching manifest for linux/arm64/v8 in the manifest list entries")]
    [InlineData("failed to register layer: open /var/lib/docker/overlay2/x: permission denied")]
    [InlineData(null)]
    public void OtherFailuresAreNot(string? error)
        => Assert.False(PullRefusal.NeedsCredential(error));
}
