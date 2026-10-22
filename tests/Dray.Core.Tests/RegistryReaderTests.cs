using Dray.Core.Engine;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// docs/CREDENTIALS.md is emphatic that Dray stores no secrets. These check the parts that decide
/// where a credential lives and how it is described — getting those wrong is how a user ends up
/// told their token is safe when it is base64 in a file.
/// </summary>
public class RegistryReaderTests
{
    [Fact]
    public void AHelperUrlAndABareHostAreTheSameRegistry()
    {
        // The helper keys by "https://ghcr.io" while config.json keys by "ghcr.io". Matching
        // exactly finds nothing, which reads as "no username stored" for every registry.
        Assert.True(RegistryReader.SameServer("https://ghcr.io", "ghcr.io"));
        Assert.True(RegistryReader.SameServer("ghcr.io", "https://ghcr.io/"));
        Assert.True(RegistryReader.SameServer("http://localhost:5000", "localhost:5000"));
    }

    [Fact]
    public void DifferentRegistriesAreNotConfused()
    {
        Assert.False(RegistryReader.SameServer("https://ghcr.io", "gcr.io"));
        Assert.False(RegistryReader.SameServer("registry.a.com", "registry.b.com"));
    }

    [Fact]
    public void AUsernameIsReadFromABase64AuthWithoutTheSecret()
    {
        // "redth:hunter2" — only the half before the colon is returned, and the other half is not
        // returned anywhere.
        var auth = Convert.ToBase64String("redth:hunter2"u8);

        Assert.Equal("redth", RegistryReader.UsernameFromAuth(auth));
    }

    [Fact]
    public void ASecretContainingAColonDoesNotSplitTheUsername()
    {
        // Tokens routinely contain colons. Splitting on the last one would return most of the
        // secret as the username.
        var auth = Convert.ToBase64String("redth:ghp_a:b:c"u8);

        Assert.Equal("redth", RegistryReader.UsernameFromAuth(auth));
    }

    [Fact]
    public void MalformedBase64IsIgnoredRatherThanThrowing()
        => Assert.Null(RegistryReader.UsernameFromAuth("not base64 at all !!"));

    [Fact]
    public void AnAuthWithNoColonHasNoUsername()
        => Assert.Null(RegistryReader.UsernameFromAuth(Convert.ToBase64String("justastring"u8)));

    [Fact]
    public void TheHelperExecutableFollowsTheProtocolNaming()
        => Assert.Equal("docker-credential-osxkeychain", RegistryReader.Executable("osxkeychain"));

    [Fact]
    public async Task ARegistrySignedIntoWithoutAnAuthsEntryIsListedFromTheDefaultHelper()
    {
        // Dray writes nothing to config.json, so a registry it signed in to exists only in the
        // helper's store. The CLI finds it there through credsStore, and so must the list.
        var source = new FakeConfigSource().WithRawConfig("""{ "credsStore": "desktop" }""");
        var runner = new HelperListRunner("""{ "https://ghcr.io": "redth" }""");

        var entry = Assert.Single(await new RegistryReader(source, runner).ListAsync(TestContext.Current.CancellationToken));

        Assert.Equal("https://ghcr.io", entry.Server);
        Assert.Equal("ghcr.io", entry.DisplayName);
        Assert.Equal("redth", entry.Username);
        Assert.Equal(CredentialStorage.Helper, entry.Storage);
    }

    [Fact]
    public async Task ARegistryInBothTheConfigAndTheHelperIsListedOnce()
    {
        var source = new FakeConfigSource().WithRawConfig("""{ "credsStore": "desktop", "auths": { "ghcr.io": {} } }""");
        var runner = new HelperListRunner("""{ "https://ghcr.io": "redth" }""");

        var entry = Assert.Single(await new RegistryReader(source, runner).ListAsync(TestContext.Current.CancellationToken));

        Assert.Equal("ghcr.io", entry.Server);
        Assert.Equal("redth", entry.Username);
    }

    [Fact]
    public async Task DockerDesktopsOwnTokensAreNotRegistries()
    {
        // Docker Desktop keeps its sign-in in the same store, under the hub URL plus a path.
        var source = new FakeConfigSource().WithRawConfig("""{ "credsStore": "desktop" }""");
        var runner = new HelperListRunner("""
            {
              "https://index.docker.io/v1/": "redth",
              "https://index.docker.io/v1/access-token": "redth",
              "https://index.docker.io/v1/refresh-token": "redth"
            }
            """);

        var entry = Assert.Single(await new RegistryReader(source, runner).ListAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Docker Hub", entry.DisplayName);
    }

    [Fact]
    public void HelperForPrefersTheRegistrysOwnHelper()
    {
        var source = new FakeConfigSource().WithRawConfig(
            """{ "credsStore": "desktop", "credHelpers": { "123.dkr.ecr.us-east-1.amazonaws.com": "ecr-login" } }""");
        var reader = new RegistryReader(source, new HelperListRunner("{}"));

        Assert.Equal("ecr-login", reader.HelperFor("123.dkr.ecr.us-east-1.amazonaws.com"));
        Assert.Equal("desktop", reader.HelperFor("ghcr.io"));
    }

    [Fact]
    public void NoConfigMeansNoHelperRatherThanPlaintext()
        => Assert.Null(new RegistryReader(new FakeConfigSource(), new HelperListRunner("{}")).HelperFor("ghcr.io"));

    [Theory]
    [InlineData("", RegistryEntry.DockerHub)]
    [InlineData("  docker.io ", RegistryEntry.DockerHub)]
    [InlineData("https://index.docker.io/v1/", RegistryEntry.DockerHub)]
    [InlineData("hub.docker.com", RegistryEntry.DockerHub)]
    [InlineData("https://ghcr.io/", "ghcr.io")]
    [InlineData("localhost:5000", "localhost:5000")]
    public void ATypedRegistryIsNamedAsTheCliNamesIt(string typed, string expected)
        => Assert.Equal(expected, RegistryReader.NormalizeServer(typed));
}

/// <summary>A credential helper whose <c>list</c> answers with the given JSON, and that does nothing else.</summary>
sealed class HelperListRunner(string list) : IProcessRunner
{
    public Task<ProcessResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct)
        => Task.FromResult(arguments is ["list"]
            ? new ProcessResult(0, list, "")
            : new ProcessResult(1, "", "unexpected"));

    public Task<ProcessResult> RunWithInputAsync(
        string executable, IReadOnlyList<string> arguments, string input, CancellationToken ct)
        => Task.FromResult(new ProcessResult(1, "", "unexpected"));

    public Task<ProcessResult> RunWithBytesAsync(
        string executable, IReadOnlyList<string> arguments, byte[] input, CancellationToken ct)
        => Task.FromResult(new ProcessResult(1, "", "unexpected"));

    public async IAsyncEnumerable<ComposeOutput> StreamAsync(
        string executable, IReadOnlyList<string> arguments, string? workingDirectory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }
}

public class RegistryEntryTests
{
    static RegistryEntry Entry(string server, string? helper = null, bool missing = false) =>
        new(server, null, CredentialStorage.Helper, helper, missing);

    [Fact]
    public void DockerHubIsShownByNameNotByItsLegacyUrl()
        => Assert.Equal("Docker Hub", Entry(RegistryEntry.DockerHub).DisplayName);

    [Fact]
    public void EveryOtherRegistryIsShownAsItself()
        => Assert.Equal("ghcr.io", Entry("ghcr.io").DisplayName);

    [Theory]
    [InlineData("ecr-login")]
    [InlineData("gcloud")]
    [InlineData("acr-env")]
    public void ACloudHelperMintsItsOwnTokensSoDrayOffersNoSignIn(string helper)
    {
        // These derive short-lived tokens from an ambient identity; a username and password field
        // would be the wrong question.
        var entry = Entry("registry", helper);

        Assert.True(entry.IsAmbient);
        Assert.False(entry.AcceptsSignIn);
    }

    [Fact]
    public void AnOrdinaryHelperAcceptsASignIn()
        => Assert.True(Entry("ghcr.io", "osxkeychain").AcceptsSignIn);

    [Fact]
    public void AMissingHelperAcceptsNothing()
    {
        // Offering sign-in would be offering a button that cannot work.
        Assert.False(Entry("ghcr.io", "osxkeychain", missing: true).AcceptsSignIn);
    }
}
