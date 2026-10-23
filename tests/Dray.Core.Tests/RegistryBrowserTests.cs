using System.Net;
using System.Text;
using Dray.Core.Engine;
using Dray.Core.Model;
using Dray.Core.Tests.Fakes;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The one place Dray talks to a registry itself rather than through the engine.
/// <para>
/// Most of these are about the credential, because that is what docs/CREDENTIALS.md is strict
/// about: asked for only after an anonymous request is refused, sent only over HTTPS, and sent only
/// to the registry or a token service under its own domain. The fake server records every request
/// and its Authorization header, which is how each of those is proved rather than asserted.
/// </para>
/// </summary>
public class RegistryBrowserTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Trimmed from Docker Hub's answer for <c>library/nginx</c>, captured 2026-10-23 — attestations
    /// listed as <c>unknown/unknown</c> beside the real platforms, as Hub serves them.
    /// </summary>
    const string HubTags = """
        {
          "count": 1381,
          "next": "https://hub.docker.com/v2/namespaces/library/repositories/nginx/tags?ordering=last_updated&page=2&page_size=100",
          "results": [
            {
              "name": "stable-trixie-perl",
              "last_updated": "2026-10-23T10:52:15.446682Z",
              "tag_last_pushed": "2026-10-23T10:52:15.446682Z",
              "digest": "sha256:66c5c70a15cd7167344c74b4a43dffb70c52bb0877169e6138c70f7d017af8e8",
              "full_size": 75747071,
              "images": [
                {"architecture":"amd64","os":"linux","variant":null,"size":75747071,"digest":"sha256:aec9"},
                {"architecture":"arm","os":"linux","variant":"v7","size":64655514,"digest":"sha256:b5fa"},
                {"architecture":"arm64","os":"linux","variant":"v8","size":73967447,"digest":"sha256:0d3f"},
                {"architecture":"unknown","os":"unknown","variant":null,"size":4306201,"digest":"sha256:att1"}
              ]
            },
            {
              "name": "alpine",
              "last_updated": "2026-10-22T16:51:50.594217Z",
              "digest": "sha256:a3554c0d0b48177b1413c26e01f695334428020d9df01243fbae3e11cfd17417",
              "images": [
                {"architecture":"amd64","os":"linux","size":20000000}
              ]
            }
          ]
        }
        """;

    static RegistryReader Credentials(ScriptedRunner runner)
        => new(new FakeConfigSource().WithRawConfig("""{"credsStore":"desktop"}"""), runner);

    static ScriptedRunner StoredCredential(string username = "redth", string secret = "ghp_s3cret")
        => new ScriptedRunner()
            .On(["list"], 0, """{"ghcr.io":"redth"}""")
            .On(["get"], 0, $$"""{"ServerURL":"ghcr.io","Username":"{{username}}","Secret":"{{secret}}"}""");

    [Fact]
    public async Task DockerHubTagsComeWithPlatformsNewestFirst()
    {
        var server = new FakeRegistry().On("hub.docker.com", "/v2/namespaces/library/repositories/nginx/tags", _ => FakeRegistry.Json(HubTags));
        using var browser = new RegistryBrowser(handler: server);

        var listing = await browser.ListTagsAsync(ImageReferenceDraft.Parse("nginx:"), Ct);

        Assert.True(listing.ByRecency);
        Assert.Equal(1381, listing.Total);
        Assert.True(listing.Truncated);
        Assert.Equal(["stable-trixie-perl", "alpine"], listing.Tags.Select(t => t.Name));

        // The attestation is not a platform anyone can run.
        Assert.Equal(["amd64", "armv7", "arm64"], listing.Tags[0].Platforms.Select(p => p.Label));
        Assert.Equal(73967447, listing.Tags[0].ImageFor(new ImagePlatform("linux", "arm64"))!.SizeBytes);

        Assert.Contains("ordering=last_updated", server.Requests[0].Uri.Query);
    }

    [Fact]
    public async Task DockerHubFiltersOnTheServer()
    {
        var server = new FakeRegistry().On("hub.docker.com", "/tags", _ => FakeRegistry.Json(HubTags));
        using var browser = new RegistryBrowser(handler: server);

        await browser.ListTagsAsync(ImageReferenceDraft.Parse("nginx:1.27-al"), Ct);

        Assert.Contains("name=1.27-al", server.Requests[0].Uri.Query);
    }

    [Fact]
    public async Task APublicRepositoryNeedsOnlyAnAnonymousToken()
    {
        var runner = StoredCredential();
        var server = Ghcr(registryAccepts: "anonymous-token");
        using var browser = new RegistryBrowser(Credentials(runner), server);

        var listing = await browser.ListTagsAsync(ImageReferenceDraft.Parse("ghcr.io/owner/app:"), Ct);

        Assert.Equal(["latest", "1.10", "1.9"], listing.Tags.Select(t => t.Name));

        // The helper was never asked: a public image is no reason to touch the keychain.
        Assert.Empty(runner.Calls);
        Assert.DoesNotContain(server.Requests, r => r.Authorization?.StartsWith("Basic", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task TheCredentialIsAskedForOnlyAfterARefusal()
    {
        var runner = StoredCredential();
        var server = Ghcr(registryAccepts: "credential-token");
        using var browser = new RegistryBrowser(Credentials(runner), server);

        var listing = await browser.ListTagsAsync(ImageReferenceDraft.Parse("ghcr.io/owner/app:"), Ct);

        Assert.NotEmpty(listing.Tags);
        Assert.Contains(runner.Calls, c => c.Arguments is ["get"] && c.Input == "ghcr.io");

        // Basic auth went to the token service, and only there.
        var basic = Assert.Single(server.Requests, r => r.Authorization?.StartsWith("Basic", StringComparison.Ordinal) == true);
        Assert.Equal("/token", basic.Uri.AbsolutePath);
        Assert.Equal("Basic " + Convert.ToBase64String("redth:ghp_s3cret"u8), basic.Authorization);
    }

    [Fact]
    public async Task ACredentialIsNotSentToATokenServiceOutsideTheRegistry()
    {
        var runner = StoredCredential();
        var server = new FakeRegistry()
            .On("ghcr.io", "/v2/owner/app/tags/list", r => r.Authorization is null
                ? FakeRegistry.Challenge("""Bearer realm="https://collector.example/token",service="ghcr.io" """)
                : FakeRegistry.Status(HttpStatusCode.Unauthorized))
            .On("collector.example", "/token", _ => FakeRegistry.Json("""{"token":"anon"}"""));
        using var browser = new RegistryBrowser(Credentials(runner), server);

        var ex = await Assert.ThrowsAsync<RegistryException>(() => browser.ListTagsAsync(ImageReferenceDraft.Parse("ghcr.io/owner/app:"), Ct));

        Assert.Equal(RegistryFailure.Refused, ex.Failure);
        Assert.Contains("collector.example", ex.Message);
        Assert.DoesNotContain(server.Requests, r => r.Authorization?.StartsWith("Basic", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ACredentialIsNotSentOverPlainHttp()
    {
        // A registry on this machine is allowed over HTTP, as Docker allows it. Its credential is not.
        var runner = new ScriptedRunner()
            .On(["get"], 0, """{"ServerURL":"localhost:5000","Username":"me","Secret":"pw"}""");
        var server = new FakeRegistry { RefuseHttps = true }
            .On("localhost", "/v2/app/tags/list", _ => FakeRegistry.Challenge("""Basic realm="registry" """));
        using var browser = new RegistryBrowser(Credentials(runner), server);

        var ex = await Assert.ThrowsAsync<RegistryException>(() => browser.ListTagsAsync(ImageReferenceDraft.Parse("localhost:5000/app:"), Ct));

        Assert.Equal(RegistryFailure.Refused, ex.Failure);
        Assert.Contains("HTTPS", ex.Message);
        Assert.All(server.Requests, r => Assert.Null(r.Authorization));
    }

    [Fact]
    public async Task ABasicChallengeGetsBasicAuthOverHttps()
    {
        var runner = new ScriptedRunner()
            .On(["get"], 0, """{"ServerURL":"registry.example.com","Username":"me","Secret":"pw"}""");
        var server = new FakeRegistry()
            .On("registry.example.com", "/v2/app/tags/list", r => r.Authorization is null
                ? FakeRegistry.Challenge("""Basic realm="registry" """)
                : FakeRegistry.Json("""{"name":"app","tags":["1.0"]}"""));
        using var browser = new RegistryBrowser(Credentials(runner), server);

        var listing = await browser.ListTagsAsync(ImageReferenceDraft.Parse("registry.example.com/app:"), Ct);

        Assert.Equal("1.0", Assert.Single(listing.Tags).Name);
        Assert.Equal("Basic " + Convert.ToBase64String("me:pw"u8), server.Requests[^1].Authorization);
    }

    [Fact]
    public async Task NothingStoredSaysWhyAndWhatToDo()
    {
        var server = Ghcr(registryAccepts: "credential-token");
        using var browser = new RegistryBrowser(Credentials(new ScriptedRunner()), server);

        var ex = await Assert.ThrowsAsync<RegistryException>(() => browser.ListTagsAsync(ImageReferenceDraft.Parse("ghcr.io/owner/app:"), Ct));

        Assert.Equal(RegistryFailure.SignInNeeded, ex.Failure);
        Assert.Contains("Registries page", ex.Message);
    }

    [Fact]
    public async Task PagesAreFollowedAndAFewThousandTagsAreReadInFull()
    {
        var server = new FakeRegistry()
            .On("quay.io", "/v2/prometheus/prometheus/tags/list", r => r.Uri.Query.Contains("last=")
                ? FakeRegistry.Json("""{"tags":["2.0"]}""")
                : FakeRegistry.Json("""{"tags":["1.0"]}""", link: "</v2/prometheus/prometheus/tags/list?last=1.0&n=1000>; rel=\"next\""));
        using var browser = new RegistryBrowser(handler: server);

        var listing = await browser.ListTagsAsync(ImageReferenceDraft.Parse("quay.io/prometheus/prometheus:"), Ct);

        Assert.Equal(["2.0", "1.0"], listing.Tags.Select(t => t.Name));
        Assert.False(listing.Truncated);
        Assert.Equal(2, listing.Total);
    }

    [Fact]
    public async Task ReadingStopsAtTenPagesAndSaysSo()
    {
        var page = 0;
        var server = new FakeRegistry()
            .On("quay.io", "/tags/list", _ => FakeRegistry.Json($$"""{"tags":["t{{++page}}"]}""", link: $"</v2/a/b/tags/list?last=t{page}>; rel=\"next\""));
        using var browser = new RegistryBrowser(handler: server);

        var listing = await browser.ListTagsAsync(ImageReferenceDraft.Parse("quay.io/a/b:"), Ct);

        Assert.Equal(10, server.Requests.Count);
        Assert.True(listing.Truncated);
        Assert.Equal(-1, listing.Total);
    }

    [Fact]
    public async Task ATagListIsReadOnceAndFilteredHere()
    {
        var server = new FakeRegistry()
            .On("quay.io", "/tags/list", _ => FakeRegistry.Json("""{"tags":["1.0","1.0-alpine","2.0"]}"""));
        using var browser = new RegistryBrowser(handler: server);

        await browser.ListTagsAsync(ImageReferenceDraft.Parse("quay.io/a/b:"), Ct);
        var narrowed = await browser.ListTagsAsync(ImageReferenceDraft.Parse("quay.io/a/b:alp"), Ct);

        Assert.Single(server.Requests);
        Assert.Equal("1.0-alpine", Assert.Single(narrowed.Tags).Name);
    }

    [Fact]
    public async Task APrivateDockerHubRepositoryIsReadThroughTheRegistryApi()
    {
        // Hub's own API cannot see a private repository without a different sign-in; the registry
        // API takes the helper's credential like any other.
        var runner = new ScriptedRunner()
            .On(["get"], 0, """{"ServerURL":"https://index.docker.io/v1/","Username":"redth","Secret":"dckr_pat"}""");
        var server = new FakeRegistry()
            .On("hub.docker.com", "/tags", _ => FakeRegistry.Status(HttpStatusCode.NotFound))
            .On("registry-1.docker.io", "/v2/redth/private/tags/list", r => r.Authorization == "Bearer signed-in"
                ? FakeRegistry.Json("""{"tags":["1.0"]}""")
                : FakeRegistry.Challenge("""Bearer realm="https://auth.docker.io/token",service="registry.docker.io" """))
            .On("auth.docker.io", "/token", r => FakeRegistry.Json(r.Authorization is null ? """{"token":"anon"}""" : """{"token":"signed-in"}"""));
        using var browser = new RegistryBrowser(Credentials(runner), server);

        var listing = await browser.ListTagsAsync(ImageReferenceDraft.Parse("redth/private:"), Ct);

        Assert.Equal("1.0", Assert.Single(listing.Tags).Name);
        Assert.Contains(runner.Calls, c => c.Input == "https://index.docker.io/v1/");
    }

    [Fact]
    public async Task ACatalogIsReadAnonymously()
    {
        var server = new FakeRegistry()
            .On("mcr.microsoft.com", "/v2/_catalog", _ => FakeRegistry.Json("""{"repositories":["dotnet/sdk","dotnet/aspnet","dotnet/sdk"]}"""));
        using var browser = new RegistryBrowser(handler: server);

        Assert.Equal(["dotnet/aspnet", "dotnet/sdk"], await browser.CatalogAsync("mcr.microsoft.com", mayUseCredential: false, Ct));
    }

    [Fact]
    public async Task NoCatalogIsAnAnswerNotAnError()
    {
        var runner = StoredCredential();
        var server = new FakeRegistry()
            .On("ghcr.io", "/v2/_catalog", r => r.Authorization is null
                ? FakeRegistry.Challenge("""Bearer realm="https://ghcr.io/token",service="ghcr.io" """)
                : FakeRegistry.Status(HttpStatusCode.Unauthorized))
            .On("ghcr.io", "/token", _ => FakeRegistry.Json("""{"token":"anon"}"""));
        using var browser = new RegistryBrowser(Credentials(runner), server);

        var ex = await Assert.ThrowsAsync<RegistryException>(() => browser.CatalogAsync("ghcr.io", mayUseCredential: false, Ct));

        Assert.Equal(RegistryFailure.Unsupported, ex.Failure);

        // Asked as someone types: never a reason to ask the keychain.
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task AnIndexSaysItsPlatformsAndTheEnginesDownload()
    {
        var server = new FakeRegistry()
            .On("ghcr.io", "/v2/owner/app/manifests/1.0", _ => FakeRegistry.Json("""
                {
                  "mediaType": "application/vnd.oci.image.index.v1+json",
                  "manifests": [
                    {"digest":"sha256:amd","platform":{"os":"linux","architecture":"amd64"}},
                    {"digest":"sha256:arm","platform":{"os":"linux","architecture":"arm64","variant":"v8"}},
                    {"digest":"sha256:att","platform":{"os":"unknown","architecture":"unknown"},
                     "annotations":{"vnd.docker.reference.type":"attestation-manifest"}}
                  ]
                }
                """, digest: "sha256:index"))
            .On("ghcr.io", "/v2/owner/app/manifests/sha256:arm", _ => FakeRegistry.Json("""
                {"config":{"digest":"sha256:cfg"},"layers":[{"size":1000},{"size":2500}]}
                """));
        using var browser = new RegistryBrowser(handler: server);

        var tag = await browser.InspectAsync(ImageReferenceDraft.Parse("ghcr.io/owner/app:1.0"), new ImagePlatform("linux", "arm64"), Ct);

        Assert.Equal(["amd64", "arm64"], tag.Platforms.Select(p => p.Label));
        Assert.Equal(3500, tag.ImageFor(new ImagePlatform("linux", "arm64"))!.SizeBytes);
        Assert.Equal("sha256:index", tag.Digest);

        // Only the engine's own platform is fetched; the other sizes are not worth a request each.
        Assert.DoesNotContain(server.Requests, r => r.Uri.AbsolutePath.EndsWith("sha256:amd", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASinglePlatformImageIsReadFromItsConfig()
    {
        var server = new FakeRegistry()
            .On("quay.io", "/v2/a/b/manifests/1.0", _ => FakeRegistry.Json("""
                {"config":{"digest":"sha256:cfg"},"layers":[{"size":400},{"size":600}]}
                """))
            .On("quay.io", "/v2/a/b/blobs/sha256:cfg", _ => FakeRegistry.Json("""{"os":"linux","architecture":"amd64"}"""));
        using var browser = new RegistryBrowser(handler: server);

        var tag = await browser.InspectAsync(ImageReferenceDraft.Parse("quay.io/a/b:1.0"), new ImagePlatform("linux", "arm64"), Ct);

        Assert.Equal(new ImagePlatform("linux", "amd64"), Assert.Single(tag.Platforms));
        Assert.False(tag.RunsOn(new ImagePlatform("linux", "arm64")));
    }

    [Fact]
    public async Task AMissingRepositoryIsNamed()
    {
        var server = new FakeRegistry().On("quay.io", "/tags/list", _ => FakeRegistry.Status(HttpStatusCode.NotFound));
        using var browser = new RegistryBrowser(handler: server);

        var ex = await Assert.ThrowsAsync<RegistryException>(() => browser.ListTagsAsync(ImageReferenceDraft.Parse("quay.io/a/nope:"), Ct));

        Assert.Equal(RegistryFailure.NotFound, ex.Failure);
        Assert.Contains("a/nope", ex.Message);
    }

    [Fact]
    public async Task AnUnreachableRegistryIsSaidToBeUnreachableFromHere()
    {
        // From here: an engine on another host may reach it fine, and the pull goes through the engine.
        var server = new FakeRegistry { RefuseHttps = true };
        using var browser = new RegistryBrowser(handler: server);

        var ex = await Assert.ThrowsAsync<RegistryException>(() => browser.ListTagsAsync(ImageReferenceDraft.Parse("registry.lan/app:"), Ct));

        Assert.Equal(RegistryFailure.Unreachable, ex.Failure);
        Assert.Contains("from this machine", ex.Message);
    }

    [Theory]
    [InlineData("ghcr.io", "https://ghcr.io/token", true)]
    [InlineData("registry-1.docker.io", "https://auth.docker.io/token", true)]
    [InlineData("registry.gitlab.com", "https://gitlab.com/jwt/auth", true)]
    [InlineData("localhost:5000", "https://localhost/auth", true)]
    [InlineData("ghcr.io", "https://evil.io/token", false)]
    [InlineData("registry.example.co.uk", "https://evil.co.uk/token", false)]
    [InlineData("ghcr.io", "http://ghcr.io/token", false)]
    [InlineData("registry.example.com", "https://example.com.evil.net/token", false)]
    public void ATokenServiceIsTrustedOnlyUnderTheRegistrysOwnDomain(string registry, string realm, bool trusted)
        => Assert.Equal(trusted, RegistryBrowser.TrustsRealm(registry, new Uri(realm)));

    /// <summary>
    /// GHCR's flow: a bearer challenge, a token service on the same host. Whether the anonymous token
    /// is enough is the test's choice.
    /// </summary>
    static FakeRegistry Ghcr(string registryAccepts) => new FakeRegistry()
        .On("ghcr.io", "/v2/owner/app/tags/list", r => r.Authorization == $"Bearer {registryAccepts}"
            ? FakeRegistry.Json("""{"name":"owner/app","tags":["1.9","1.10","latest"]}""")
            : FakeRegistry.Challenge("""Bearer realm="https://ghcr.io/token",service="ghcr.io",scope="repository:owner/app:pull" """))
        .On("ghcr.io", "/token", r => FakeRegistry.Json(r.Authorization is null ? """{"token":"anonymous-token"}""" : """{"token":"credential-token"}"""));
}

public class ImageFinderTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly RegistryOption Hub = RegistryHosts.WellKnown[0];
    static readonly RegistryOption Quay = new("quay.io", "Quay");
    static readonly RegistryOption Mcr = new("mcr.microsoft.com", "Microsoft");

    sealed class RecordingSearch
    {
        public List<(string Term, ImageSearchFilters? Filters)> Calls { get; } = [];

        public Func<string, IReadOnlyList<ImageSearchResult>> Answer { get; set; } = _ => [];

        public Task<IReadOnlyList<ImageSearchResult>> SearchAsync(string term, int limit, ImageSearchFilters? filters, CancellationToken ct)
        {
            Calls.Add((term, filters));
            return Task.FromResult(Answer(term));
        }
    }

    [Fact]
    public async Task DockerHubIsSearchedByTheEngineAsTyped()
    {
        var search = new RecordingSearch { Answer = _ => [new ImageSearchResult("redis", IsOfficial: true)] };
        var finder = new ImageFinder(search.SearchAsync, new RegistryBrowser(handler: new FakeRegistry()));

        var results = await finder.FindAsync(Hub, ImageReferenceDraft.Parse("redis"), new ImageSearchFilters(true), Ct);

        Assert.Equal(RepositorySource.Search, results.Source);
        Assert.Equal(("redis", new ImageSearchFilters(true)), Assert.Single(search.Calls));
    }

    [Fact]
    public async Task AnotherRegistryIsSearchedWithItsHost()
    {
        var search = new RecordingSearch();
        var finder = new ImageFinder(search.SearchAsync, new RegistryBrowser(handler: new FakeRegistry()));

        await finder.FindAsync(Quay, ImageReferenceDraft.Compose("quay.io", "prom"), null, Ct);

        Assert.Equal("quay.io/prom", Assert.Single(search.Calls).Term);
    }

    [Fact]
    public async Task ARegistryWithoutSearchIsBrowsedByCatalogAndNotSearchedAgain()
    {
        var search = new RecordingSearch { Answer = _ => throw new InvalidOperationException("error decoding registry search results") };
        var server = new FakeRegistry()
            .On("mcr.microsoft.com", "/v2/_catalog", _ => FakeRegistry.Json("""{"repositories":["dotnet/sdk","dotnet/aspnet","azure-functions/dotnet-isolated","dotnet/samples"]}"""));
        var finder = new ImageFinder(search.SearchAsync, new RegistryBrowser(handler: server));

        var first = await finder.FindAsync(Mcr, ImageReferenceDraft.Compose(Mcr.Host, "sdk"), null, Ct);
        var second = await finder.FindAsync(Mcr, ImageReferenceDraft.Compose(Mcr.Host, "dotnet"), null, Ct);

        Assert.Equal(RepositorySource.Catalog, first.Source);
        Assert.Equal("mcr.microsoft.com/dotnet/sdk", Assert.Single(first.Items).Name);
        Assert.Equal(4, first.Total);

        // Measured once. A registry without search is not asked on every keystroke.
        Assert.Single(search.Calls);
        Assert.Equal(RepositorySource.Catalog, second.Source);
    }

    [Fact]
    public async Task NeitherSearchNorCatalogMeansTheUserTypesAName()
    {
        var search = new RecordingSearch { Answer = _ => throw new InvalidOperationException("unexpected status code 404") };
        var server = new FakeRegistry().On("ghcr.io", "/v2/_catalog", _ => FakeRegistry.Status(HttpStatusCode.NotFound));
        var finder = new ImageFinder(search.SearchAsync, new RegistryBrowser(handler: server));
        var ghcr = new RegistryOption("ghcr.io", "GitHub");

        var results = await finder.FindAsync(ghcr, ImageReferenceDraft.Compose("ghcr.io", "home"), null, Ct);

        Assert.Equal(RepositorySource.None, results.Source);
        Assert.True(finder.CannotFind("ghcr.io"));
    }

    [Fact]
    public async Task AFailedDockerHubSearchIsAnError()
    {
        // Docker Hub has search. Failing to reach it is news, not a fact about the registry.
        var search = new RecordingSearch { Answer = _ => throw new InvalidOperationException("dial tcp: no route to host") };
        var finder = new ImageFinder(search.SearchAsync, new RegistryBrowser(handler: new FakeRegistry()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => finder.FindAsync(Hub, ImageReferenceDraft.Parse("redis"), null, Ct));
        Assert.False(finder.CannotFind(RegistryHosts.DockerHub));
    }

    [Fact]
    public async Task OneLetterIsNotWorthSearching()
    {
        var search = new RecordingSearch();
        var finder = new ImageFinder(search.SearchAsync, new RegistryBrowser(handler: new FakeRegistry()));

        await finder.FindAsync(Hub, ImageReferenceDraft.Parse("r"), null, Ct);

        Assert.Empty(search.Calls);
    }

    [Fact]
    public void AnEngineWithoutSearchCannotFindOnDockerHub()
        => Assert.True(new ImageFinder(null, new RegistryBrowser(handler: new FakeRegistry())).CannotFind(RegistryHosts.DockerHub));

    [Fact]
    public void ACatalogRanksTheNameItselfFirst()
    {
        string[] names = ["azure-functions/dotnet-isolated-sdk", "dotnet/sdk", "dotnet/nightly/sdk", "sdk", "dotnet/sdk-tools"];

        Assert.Equal(["sdk", "dotnet/sdk", "dotnet/nightly/sdk", "dotnet/sdk-tools", "azure-functions/dotnet-isolated-sdk"],
            ImageFinder.Rank(names, "sdk"));
    }

    [Fact]
    public void ACatalogIsSubstringMatchedNotSubsequence()
        => Assert.Empty(ImageFinder.Rank(["dotnet/sdk"], "dns"));
}

/// <summary>
/// A registry, its token service and Docker Hub's API in one handler, answering by host and path and
/// recording every request with the Authorization it carried.
/// </summary>
sealed class FakeRegistry : HttpMessageHandler
{
    readonly List<(string Host, string Path, Func<Request, HttpResponseMessage> Answer)> _routes = [];

    public sealed record Request(Uri Uri, string? Authorization);

    public List<Request> Requests { get; } = [];

    /// <summary>Fail every HTTPS connection, as a registry serving only plain HTTP does.</summary>
    public bool RefuseHttps { get; init; }

    public FakeRegistry On(string host, string pathContains, Func<Request, HttpResponseMessage> answer)
    {
        _routes.Add((host, pathContains, answer));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        var uri = message.RequestUri!;

        if (RefuseHttps && uri.Scheme == Uri.UriSchemeHttps)
            throw new HttpRequestException("The SSL connection could not be established.");

        var request = new Request(uri, message.Headers.Authorization?.ToString());
        Requests.Add(request);

        var route = _routes.LastOrDefault(r => r.Host == uri.Host && uri.PathAndQuery.Contains(r.Path, StringComparison.Ordinal));
        return Task.FromResult(route.Answer is { } answer ? answer(request) : Status(HttpStatusCode.NotFound));
    }

    public static HttpResponseMessage Json(string body, string? link = null, string? digest = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (link is not null) response.Headers.TryAddWithoutValidation("Link", link);
        if (digest is not null) response.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
        return response;
    }

    public static HttpResponseMessage Challenge(string header)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.TryAddWithoutValidation("WWW-Authenticate", header.Trim());
        return response;
    }

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status);
}
