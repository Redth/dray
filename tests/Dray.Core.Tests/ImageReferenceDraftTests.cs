using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The pull field is its own state — a colon moves it from repositories to tags, backspacing over
/// it moves it back — so the parse has to be the CLI's. A rule that is merely plausible here sends
/// <c>team/app</c> to a registry called "team".
/// </summary>
public class ImageReferenceDraftTests
{
    [Theory]
    [InlineData("", DraftStage.Empty)]
    [InlineData("ngi", DraftStage.Repository)]
    [InlineData("bitnami/pos", DraftStage.Repository)]
    [InlineData("nginx:", DraftStage.Tag)]
    [InlineData("nginx:1.27-al", DraftStage.Tag)]
    [InlineData("nginx@sha256:abc", DraftStage.Digest)]
    [InlineData("ghcr.io/", DraftStage.Empty)]
    public void TheStageFollowsTheText(string text, DraftStage stage)
        => Assert.Equal(stage, ImageReferenceDraft.Parse(text).Stage);

    [Fact]
    public void ASegmentWithoutADotOrPortIsNotARegistry()
    {
        var draft = ImageReferenceDraft.Parse("team/app:1.0");

        Assert.Null(draft.Registry);
        Assert.Equal("team/app", draft.Path);
        Assert.True(draft.IsDockerHub);
    }

    [Fact]
    public void ARegistryPortIsNotATag()
    {
        var draft = ImageReferenceDraft.Parse("registry.lan:5000/team/app");

        Assert.Equal("registry.lan:5000", draft.Registry);
        Assert.Equal("team/app", draft.Path);
        Assert.Null(draft.Tag);
    }

    [Fact]
    public void LocalhostIsARegistryEvenWithoutADot()
        => Assert.Equal("localhost", ImageReferenceDraft.Parse("localhost/app").Registry);

    [Fact]
    public void AHostWithNoSlashAfterItIsStillBeingTyped()
    {
        // Typing "ghcr.io" on the way to "ghcr.io/owner/app": until the slash, it is a search term.
        var draft = ImageReferenceDraft.Parse("ghcr.io");

        Assert.Null(draft.Registry);
        Assert.Equal("ghcr.io", draft.Path);
    }

    [Fact]
    public void AnOfficialImageLivesUnderLibrary()
    {
        Assert.Equal("library/nginx", ImageReferenceDraft.Parse("nginx").RemotePath);
        Assert.Equal("bitnami/redis", ImageReferenceDraft.Parse("bitnami/redis").RemotePath);

        // Only on Docker Hub. A one-segment name elsewhere is exactly what it says.
        Assert.Equal("app", ImageReferenceDraft.Parse("localhost:5000/app").RemotePath);
    }

    [Theory]
    [InlineData("nginx", "docker.io/library/nginx:latest")]
    [InlineData("nginx:alpine", "docker.io/library/nginx:alpine")]
    [InlineData("docker.io/library/nginx:1.27", "docker.io/library/nginx:1.27")]
    [InlineData("index.docker.io/bitnami/redis", "docker.io/bitnami/redis:latest")]
    [InlineData("ghcr.io/owner/app:1.2", "ghcr.io/owner/app:1.2")]
    [InlineData("nginx@sha256:abc", "docker.io/library/nginx@sha256:abc")]
    public void TheCanonicalNameSpellsOutWhatIsImplied(string text, string canonical)
        => Assert.Equal(canonical, ImageReferenceDraft.Parse(text).Canonical);

    [Fact]
    public void AFieldIsReadAgainstTheChosenRegistry()
    {
        var draft = ImageReferenceDraft.Compose("ghcr.io", "owner/app:1.0");

        Assert.Equal("ghcr.io/owner/app:1.0", draft.Reference);
        Assert.Equal("owner/app:1.0", draft.Field);
    }

    [Fact]
    public void AHostTypedIntoTheFieldWinsOverThePicker()
        => Assert.Equal("quay.io", ImageReferenceDraft.Compose("ghcr.io", "quay.io/prometheus/prometheus").Registry);

    [Fact]
    public void DockerHubAddsNoPrefix()
        => Assert.Equal("nginx:alpine", ImageReferenceDraft.Compose("docker.io", "nginx:alpine").Reference);

    [Theory]
    [InlineData("nginx", true)]
    [InlineData("nginx:alpine", true)]
    [InlineData("nginx:", false)]
    [InlineData("nginx@", false)]
    [InlineData("", false)]
    [InlineData("Nginx", false)]
    [InlineData("my app", false)]
    [InlineData("bitnami/", false)]
    public void OnlyACompleteReferenceCanBePulled(string text, bool pullable)
        => Assert.Equal(pullable, ImageReferenceDraft.Parse(text).IsPullable);

    [Fact]
    public void ANamespaceBeingTypedIsNotAMistake()
        => Assert.Null(ImageReferenceDraft.Parse("bitnami/").Problem);

    [Fact]
    public void AnUppercaseNameSaysWhatToTypeInstead()
    {
        var problem = ImageReferenceDraft.Parse("Postgres:16").Problem;

        Assert.NotNull(problem);
        Assert.Contains("postgres", problem);
    }

    [Theory]
    [InlineData("nginx:-bad")]
    [InlineData("nginx:tag!")]
    [InlineData("app//double")]
    [InlineData("/app")]
    public void TheGrammarIsCheckedBeforeTheEngineIsAsked(string text)
        => Assert.NotNull(ImageReferenceDraft.Parse(text).Problem);
}
