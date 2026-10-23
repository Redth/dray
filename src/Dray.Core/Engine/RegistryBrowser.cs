using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dray.Core.Model;

namespace Dray.Core.Engine;

/// <summary>Why a registry would not answer, as far as Dray can tell.</summary>
public enum RegistryFailure
{
    /// <summary>No such repository, tag or endpoint.</summary>
    NotFound,

    /// <summary>The registry wants to know who is asking, and there is no credential to offer it.</summary>
    SignInNeeded,

    /// <summary>A credential was offered and refused, or Dray declined to send one where it was asked to.</summary>
    Refused,

    /// <summary>The registry could not be reached from this machine.</summary>
    Unreachable,

    /// <summary>The registry is limiting requests from this address.</summary>
    RateLimited,

    /// <summary>The registry does not offer this at all — a catalog, usually.</summary>
    Unsupported,

    /// <summary>It answered with something Dray could not read.</summary>
    Unreadable,
}

/// <summary>A registry's refusal, with a sentence fit to show as it is.</summary>
public sealed class RegistryException(RegistryFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public RegistryFailure Failure { get; } = failure;
}

/// <summary>
/// Reading a registry's own API for the things the engine cannot answer: which tags a repository
/// has, which platforms a tag was built for, and which repositories a registry without search holds.
/// <para>
/// <b>Why this is not through the engine, when search is.</b> The Engine API has no endpoint that
/// lists tags, and a pull dialog that cannot list tags makes the user guess them or go to a
/// browser — which is the round trip the dialog exists to save. So this reads the registry directly,
/// and only for that: searching stays with the engine, and pulling always goes through it. The
/// cost is the one <see cref="IContainerRuntime.SearchImagesAsync"/> warns about — a second network
/// path — and it is kept honest by what it is allowed to do: GET, read-only, nothing written, and
/// a registry this machine cannot reach is reported as exactly that, since an engine on another
/// host may reach it fine.
/// </para>
/// <para>
/// <b>Credentials</b> follow docs/CREDENTIALS.md. Every request is anonymous first, because a public
/// repository needs nothing and asking the helper can raise a prompt. Only when the registry
/// refuses is the helper asked, and then the credential lives for one operation and is sent only
/// over HTTPS, only to the registry or to a token service under the registry's own domain.
/// </para>
/// </summary>
public sealed class RegistryBrowser : IDisposable
{
    /// <summary>Tags per page from the standard API. The largest page GHCR will serve.</summary>
    const int PageSize = 1000;

    /// <summary>
    /// Pages read before stopping. The standard API has no filter and no order, so the only way to
    /// show the newest tag is to read all of them; ten thousand is more than any repository someone
    /// is choosing a tag from by eye, and the listing says when it stopped short.
    /// </summary>
    const int MaxPages = 10;

    /// <summary>Docker Hub tags per request. Hub filters and orders server-side, so one page is enough.</summary>
    const int HubPageSize = 100;

    const string HubApi = "https://hub.docker.com";

    static readonly TimeSpan ListingLifetime = TimeSpan.FromMinutes(2);
    static readonly TimeSpan CatalogLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Every manifest shape a tag can resolve to, index types first.</summary>
    static readonly string[] ManifestTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    ];

    static readonly string[] Json = ["application/json"];

    readonly HttpClient _http;
    readonly RegistryReader? _credentials;
    readonly TimeProvider _time;
    readonly ConcurrentDictionary<string, (DateTimeOffset At, object Value)> _cache = new(StringComparer.Ordinal);

    /// <param name="credentials">The credential store. Null browses anonymously and only that.</param>
    /// <param name="handler">For tests. Otherwise a pooled handler that honours the system proxy.</param>
    public RegistryBrowser(RegistryReader? credentials = null, HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        _credentials = credentials;
        _time = time ?? TimeProvider.System;

        _http = new HttpClient(
            handler ?? new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = DecompressionMethods.All,
            },
            disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Dray");
    }

    // ---------------------------------------------------------------- tags

    /// <summary>
    /// The tags of the repository a draft names, narrowed by whatever follows its colon.
    /// <para>
    /// Docker Hub is asked through its own API, which orders by when each tag moved, filters on the
    /// server, and says which platforms each tag holds — the difference between a useful list and a
    /// thousand names in alphabetical order. A private Hub repository is not visible to that API
    /// without a different sign-in, so a refusal there falls through to the standard registry API,
    /// which takes the helper's credential like any other registry.
    /// </para>
    /// </summary>
    public async Task<TagListing> ListTagsAsync(ImageReferenceDraft draft, CancellationToken ct = default)
    {
        if (draft.Path.Trim('/').Length == 0) return new TagListing([]);

        if (draft.IsDockerHub)
        {
            try
            {
                return await HubTagsAsync(draft.RemotePath, draft.Tag, ct).ConfigureAwait(false);
            }
            catch (RegistryException ex) when (ex.Failure is RegistryFailure.NotFound or RegistryFailure.SignInNeeded)
            {
                // Missing, or private. The registry API can tell those apart with a credential.
            }
        }

        var listing = await AllTagsAsync(draft.Host, draft.RemotePath, ct).ConfigureAwait(false);
        return listing with { Tags = TagOrder.Filter(listing.Tags, draft.Tag) };
    }

    async Task<TagListing> HubTagsAsync(string path, string? filter, CancellationToken ct)
    {
        var key = $"hub-tags|{path}|{filter}";
        if (Cached<TagListing>(key, ListingLifetime) is { } cached) return cached;

        var (ns, name) = HubName(path);
        var url = $"{HubApi}/v2/namespaces/{Escape(ns)}/repositories/{Escape(name)}/tags"
            + $"?page_size={HubPageSize}&ordering=last_updated"
            + (filter is { Length: > 0 } ? "&name=" + Uri.EscapeDataString(filter) : "");

        using var response = await SendAsync(new Uri(url), Json, null, "Docker Hub", ct).ConfigureAwait(false);
        Ensure(response, "Docker Hub", $"No repository called {Display(path)} on Docker Hub.");

        var listing = ParseHubTags(await ReadAsync(response, "Docker Hub", ct).ConfigureAwait(false));
        return Remember(key, listing);
    }

    async Task<TagListing> AllTagsAsync(string host, string path, CancellationToken ct)
    {
        var key = $"tags|{host}|{path}";
        if (Cached<TagListing>(key, ListingLifetime) is { } cached) return cached;

        var name = RegistryHosts.NameOf(host);
        var session = new Session(host, $"repository:{path}:pull", MayUseCredential: true);

        var names = new List<string>();
        Uri? next = Api(session, $"/v2/{EscapePath(path)}/tags/list?n={PageSize}");
        var pages = 0;
        var truncated = false;

        while (next is not null)
        {
            if (pages == MaxPages)
            {
                truncated = true;
                break;
            }

            using var response = await GetAsync(session, next, Json, ct).ConfigureAwait(false);
            Ensure(response, name, $"No repository called {path} on {name}.");

            var body = await ReadAsync(response, name, ct).ConfigureAwait(false);
            names.AddRange(ParseTagNames(body));

            next = NextLink(response, next);
            pages++;
        }

        var tags = TagOrder.Newest(names.Distinct(StringComparer.Ordinal).Select(t => new RemoteTag(t)));
        return Remember(key, new TagListing(tags, truncated ? -1 : tags.Count, truncated));
    }

    // ---------------------------------------------------------------- one tag

    /// <summary>
    /// What one tag holds: its platforms, its digest, and — for the platform the engine runs — how
    /// much a pull downloads.
    /// <para>
    /// For registries whose listing says nothing but names. Docker Hub's listing already carries all
    /// of this for every tag. Up to three requests — the manifest, then either the matching
    /// platform's manifest or the image's config — so it is asked for one tag at a time, the one
    /// the user has settled on.
    /// </para>
    /// </summary>
    public async Task<RemoteTag> InspectAsync(ImageReferenceDraft draft, ImagePlatform? engine, CancellationToken ct = default)
    {
        var name = RegistryHosts.NameOf(draft.Host);
        var tag = draft.Tag is { Length: > 0 } t ? t : "latest";
        var reference = draft.Digest is { Length: > 0 } digest ? digest : tag;

        var session = new Session(draft.Host, $"repository:{draft.RemotePath}:pull", MayUseCredential: true);
        var path = EscapePath(draft.RemotePath);

        using var response = await GetAsync(session, Api(session, $"/v2/{path}/manifests/{Uri.EscapeDataString(reference)}"), ManifestTypes, ct)
            .ConfigureAwait(false);
        Ensure(response, name, $"{name} has no {draft.Path}:{tag}.");

        var pointsAt = response.Headers.TryGetValues("Docker-Content-Digest", out var values) ? values.FirstOrDefault() : null;
        var manifest = ParseManifest(await ReadAsync(response, name, ct).ConfigureAwait(false));

        if (manifest.Platforms is { } index)
        {
            var images = new List<TagImage>();

            foreach (var (platform, child) in index)
            {
                long size = -1;

                // The engine's own platform is the only one whose download size anyone is weighing.
                if (engine is not null && platform.RunsOn(engine) && images.All(i => !i.Platform.RunsOn(engine)))
                    size = await LayerBytesAsync(session, path, child, ct).ConfigureAwait(false);

                images.Add(new TagImage(platform, size));
            }

            return new RemoteTag(tag, Images: images, Digest: pointsAt);
        }

        // A single-platform image: the platform is in its config, not its manifest.
        var single = manifest.ConfigDigest is { } config
            ? await ConfigPlatformAsync(session, path, config, ct).ConfigureAwait(false)
            : null;

        return new RemoteTag(
            tag,
            Images: single is null ? null : [new TagImage(single, manifest.LayerBytes ?? -1)],
            Digest: pointsAt);
    }

    async Task<long> LayerBytesAsync(Session session, string path, string digest, CancellationToken ct)
    {
        try
        {
            using var response = await GetAsync(session, Api(session, $"/v2/{path}/manifests/{digest}"), ManifestTypes, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return -1;

            return ParseManifest(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).LayerBytes ?? -1;
        }
        catch (Exception ex) when (ex is RegistryException or JsonException)
        {
            // The size is a detail. Losing it is not worth losing the platforms that were already read.
            return -1;
        }
    }

    async Task<ImagePlatform?> ConfigPlatformAsync(Session session, string path, string digest, CancellationToken ct)
    {
        try
        {
            using var response = await GetAsync(session, Api(session, $"/v2/{path}/blobs/{digest}"), Json, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = document.RootElement;

            return ImagePlatform.From(Text(root, "os"), Text(root, "architecture"), Text(root, "variant"));
        }
        catch (Exception ex) when (ex is RegistryException or JsonException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- repositories

    /// <summary>
    /// What Docker Hub says about a repository — its description, stars and pulls — or null for any
    /// other registry, none of which has an API for it.
    /// </summary>
    public async Task<ImageSearchResult?> DescribeAsync(ImageReferenceDraft draft, CancellationToken ct = default)
    {
        if (!draft.IsDockerHub || draft.Path.Trim('/').Length == 0) return null;

        var path = draft.RemotePath;
        var key = $"hub-repo|{path}";
        if (Cached<ImageSearchResult>(key, CatalogLifetime) is { } cached) return cached;

        var (ns, name) = HubName(path);
        using var response = await SendAsync(
            new Uri($"{HubApi}/v2/namespaces/{Escape(ns)}/repositories/{Escape(name)}"), Json, null, "Docker Hub", ct)
            .ConfigureAwait(false);

        // Missing or private: the tag listing will say which, with more to go on.
        if (!response.IsSuccessStatusCode) return null;

        var repository = ParseHubRepository(await ReadAsync(response, "Docker Hub", ct).ConfigureAwait(false), Display(path));
        return Remember(key, repository);
    }

    /// <summary>
    /// Every repository a registry lists in its catalog, for a registry that has one and no search —
    /// Microsoft's, notably, which publishes all of its several thousand.
    /// <para>
    /// Most registries do not serve a catalog to anyone but an administrator, and Docker Hub serves
    /// none. That is reported as <see cref="RegistryFailure.Unsupported"/>, an answer about the
    /// registry rather than an error.
    /// </para>
    /// </summary>
    /// <param name="mayUseCredential">
    /// Whether to ask the helper if the registry refuses. Only for a registry the user has signed in
    /// to: this runs as they type, and a keychain prompt in the middle of a word for a registry they
    /// never signed in to would be a prompt for nothing.
    /// </param>
    public async Task<IReadOnlyList<string>> CatalogAsync(string host, bool mayUseCredential, CancellationToken ct = default)
    {
        var name = RegistryHosts.NameOf(host);
        if (RegistryHosts.IsDockerHub(host)) throw new RegistryException(RegistryFailure.Unsupported, "Docker Hub publishes no catalog.");

        var key = $"catalog|{host}";
        if (Cached<IReadOnlyList<string>>(key, CatalogLifetime) is { } cached) return cached;

        var session = new Session(host, "registry:catalog:*", mayUseCredential);
        var names = new List<string>();
        Uri? next = Api(session, $"/v2/_catalog?n={PageSize}");
        var pages = 0;

        while (next is not null && pages < MaxPages)
        {
            HttpResponseMessage response;

            try
            {
                response = await GetAsync(session, next, Json, ct).ConfigureAwait(false);
            }
            catch (RegistryException ex) when (ex.Failure is RegistryFailure.SignInNeeded or RegistryFailure.Refused)
            {
                throw new RegistryException(RegistryFailure.Unsupported, $"{name} does not list its repositories.", ex);
            }

            using (response)
            {
                if ((int)response.StatusCode is 401 or 403 or 404 or 405)
                    throw new RegistryException(RegistryFailure.Unsupported, $"{name} does not list its repositories.");

                Ensure(response, name, $"{name} does not list its repositories.");

                names.AddRange(ParseCatalog(await ReadAsync(response, name, ct).ConfigureAwait(false)));
                next = NextLink(response, next);
                pages++;
            }
        }

        IReadOnlyList<string> catalog = [.. names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        return Remember(key, catalog);
    }

    // ---------------------------------------------------------------- the conversation

    /// <summary>
    /// One operation's worth of state: where the registry answers, and the authorization it has
    /// granted so far. Thrown away when the operation ends, which is how a token minted from the
    /// user's credential lives for one operation and no longer.
    /// </summary>
    sealed record Session(string Host, string Scope, bool MayUseCredential)
    {
        public AuthenticationHeaderValue? Authorization { get; set; }

        public bool TriedAnonymous { get; set; }

        public bool TriedCredential { get; set; }

        /// <summary>
        /// HTTPS, except for a registry on this machine, which Docker itself allows over plain HTTP
        /// and which is commonly run that way. Nothing secret is ever sent over it.
        /// </summary>
        public string Scheme { get; set; } = "https";
    }

    static Uri Api(Session session, string pathAndQuery)
        => new($"{session.Scheme}://{RegistryHosts.ApiHost(session.Host)}{pathAndQuery}");

    /// <summary>
    /// GET, answering the registry's challenges in the order docs/CREDENTIALS.md sets: anonymously,
    /// then with an anonymous token, then — only if still refused — with the helper's credential.
    /// </summary>
    async Task<HttpResponseMessage> GetAsync(Session session, Uri uri, IReadOnlyList<string> accept, CancellationToken ct)
    {
        var name = RegistryHosts.NameOf(session.Host);
        HttpResponseMessage response;

        try
        {
            response = await SendAsync(uri, accept, session.Authorization, name, ct).ConfigureAwait(false);
        }
        catch (RegistryException ex) when (ex.Failure == RegistryFailure.Unreachable && session.Scheme == "https" && IsLoopback(session.Host))
        {
            session.Scheme = "http";
            uri = new UriBuilder(uri) { Scheme = "http", Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri;
            response = await SendAsync(uri, accept, session.Authorization, name, ct).ConfigureAwait(false);
        }

        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        var challenge = Challenge.From(response);
        response.Dispose();

        // An anonymous token: all a public repository needs, and nothing of the user's is involved.
        if (challenge is { IsBearer: true, Realm: { } realm } && !session.TriedAnonymous)
        {
            session.TriedAnonymous = true;

            if (await TokenAsync(realm, challenge.Service, session.Scope, null, name, ct).ConfigureAwait(false) is { } token)
            {
                session.Authorization = new AuthenticationHeaderValue("Bearer", token);

                response = await SendAsync(uri, accept, session.Authorization, name, ct).ConfigureAwait(false);
                if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) return response;

                response.Dispose();
            }
        }

        // Refused anonymously. Only now is the helper asked.
        if (!session.MayUseCredential || _credentials is null || session.TriedCredential)
        {
            throw new RegistryException(RegistryFailure.SignInNeeded, $"{name} would not answer without signing in.");
        }

        session.TriedCredential = true;

        var credential = await _credentials.FindForAsync(RegistryHosts.CredentialServer(session.Host), ct).ConfigureAwait(false)
            ?? throw new RegistryException(
                RegistryFailure.SignInNeeded,
                $"{name} would not answer without signing in, and Dray has no credential stored for it. "
                + "Either there is no such repository, or it is private — sign in on the Registries page to see it.");

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new RegistryException(
                RegistryFailure.Refused,
                $"{name} wants a credential over plain HTTP. Dray sends credentials only over HTTPS, and did not send it.");
        }

        if (challenge is { IsBearer: true, Realm: { } tokenRealm })
        {
            if (!TrustsRealm(RegistryHosts.ApiHost(session.Host), tokenRealm))
            {
                throw new RegistryException(
                    RegistryFailure.Refused,
                    $"{name} asked for your credential to be sent to {tokenRealm.Host}, which is not part of {name}. Dray did not send it.");
            }

            var token = await TokenAsync(tokenRealm, challenge.Service, session.Scope, credential, name, ct).ConfigureAwait(false)
                ?? throw new RegistryException(RegistryFailure.Refused, $"{name} refused the credential stored for it.");

            session.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            session.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Secret}")));
        }

        response = await SendAsync(uri, accept, session.Authorization, name, ct).ConfigureAwait(false);

        // Not "refused the credential": Docker Hub answers a repository that does not exist with this
        // same 401 even to a valid sign-in, and telling someone their credential is broken because
        // they mistyped a name sends them to fix the wrong thing.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw new RegistryException(
                RegistryFailure.Refused,
                $"{name} would not show this even signed in as {(credential.Username is { Length: > 0 } user ? user : "the stored account")}. "
                + "Either there is no such repository, or that account cannot see it.");
        }

        return response;
    }

    /// <summary>
    /// A bearer token from the registry's token service, or null when it refuses.
    /// <para>
    /// The credential, when there is one, goes as basic auth to the realm and nowhere else, and is
    /// not kept: the token it buys is held by the operation's <see cref="Session"/> and dropped with it.
    /// </para>
    /// </summary>
    async Task<string?> TokenAsync(
        Uri realm, string? service, string scope, RegistryCredential? credential, string name, CancellationToken ct)
    {
        var query = new StringBuilder();
        if (service is { Length: > 0 }) query.Append("service=").Append(Uri.EscapeDataString(service)).Append('&');
        query.Append("scope=").Append(Uri.EscapeDataString(scope));

        var uri = new UriBuilder(realm) { Query = query.ToString() }.Uri;

        var authorization = credential is null
            ? null
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Secret}")));

        using var response = await SendAsync(uri, Json, authorization, name, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return Text(document.RootElement, "token") ?? Text(document.RootElement, "access_token");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a token service may be sent the credential for a registry.
    /// <para>
    /// The registry names its own token service in its challenge, so a registry that wanted someone's
    /// credential could name any host at all. The service is trusted when it is the registry, or sits
    /// under the registry's parent domain — <c>auth.docker.io</c> for <c>registry-1.docker.io</c>,
    /// <c>gitlab.com</c> for <c>registry.gitlab.com</c> — and never when that parent is a bare
    /// public suffix like <c>io</c>, which would trust every <c>.io</c> on the internet.
    /// </para>
    /// </summary>
    internal static bool TrustsRealm(string registryHost, Uri realm)
    {
        if (realm.Scheme != Uri.UriSchemeHttps) return false;

        var host = HostOnly(registryHost).ToLowerInvariant();
        var service = realm.Host.ToLowerInvariant();

        if (service == host) return true;

        var dot = host.IndexOf('.');
        if (dot < 0) return false;

        var parent = host[(dot + 1)..];

        // "co.uk" and "com.au" are public suffixes too; two labels is not enough when the last is a
        // two-letter country code and the one before it is short.
        var labels = parent.Split('.');
        if (labels.Length < 2) return false;
        if (labels.Length == 2 && labels[1].Length == 2 && labels[0].Length <= 3) return false;

        return service == parent || service.EndsWith("." + parent, StringComparison.Ordinal);
    }

    async Task<HttpResponseMessage> SendAsync(
        Uri uri, IReadOnlyList<string> accept, AuthenticationHeaderValue? authorization, string name, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var type in accept) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(type));
        request.Headers.Authorization = authorization;

        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new RegistryException(RegistryFailure.Unreachable, $"Could not reach {name} from this machine: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new RegistryException(RegistryFailure.Unreachable, $"{name} did not answer within {(int)_http.Timeout.TotalSeconds} seconds.", ex);
        }
    }

    static async Task<string> ReadAsync(HttpResponseMessage response, string name, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new RegistryException(RegistryFailure.Unreachable, $"{name} stopped answering partway through: {ex.Message}", ex);
        }
    }

    static void Ensure(HttpResponseMessage response, string name, string notFound)
    {
        if (response.IsSuccessStatusCode) return;

        throw (int)response.StatusCode switch
        {
            404 => new RegistryException(RegistryFailure.NotFound, notFound),
            401 or 403 => new RegistryException(RegistryFailure.SignInNeeded, $"{name} would not answer without signing in."),
            429 => new RegistryException(RegistryFailure.RateLimited, $"{name} is limiting requests from this address. Try again in a minute."),
            var status => new RegistryException(RegistryFailure.Unreadable, $"{name} answered {status} {response.ReasonPhrase}".TrimEnd() + "."),
        };
    }

    /// <summary>The next page, from a <c>Link: &lt;…&gt;; rel="next"</c> header, resolved against this one.</summary>
    static Uri? NextLink(HttpResponseMessage response, Uri current)
    {
        if (!response.Headers.TryGetValues("Link", out var links)) return null;

        foreach (var link in links)
        {
            var match = Regex.Match(link, "<([^>]+)>\\s*;\\s*rel=\"?next\"?", RegexOptions.IgnoreCase);
            if (match.Success && Uri.TryCreate(current, match.Groups[1].Value, out var next)) return next;
        }

        return null;
    }

    /// <summary>A <c>WWW-Authenticate</c> challenge — the scheme, and for bearer, where to get a token.</summary>
    internal sealed record Challenge(string Scheme, Uri? Realm, string? Service)
    {
        public bool IsBearer => Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase);

        public static Challenge? From(HttpResponseMessage response)
        {
            var header = response.Headers.WwwAuthenticate.FirstOrDefault();
            return header is null ? null : Parse(header.Scheme, header.Parameter);
        }

        public static Challenge Parse(string scheme, string? parameters)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in Regex.Matches(parameters ?? string.Empty, "(\\w+)=\"([^\"]*)\""))
                values[match.Groups[1].Value] = match.Groups[2].Value;

            return new Challenge(
                scheme,
                values.TryGetValue("realm", out var realm) && Uri.TryCreate(realm, UriKind.Absolute, out var uri) ? uri : null,
                values.GetValueOrDefault("service"));
        }
    }

    // ---------------------------------------------------------------- reading answers

    /// <summary>Docker Hub's tag listing: names, when each moved, and the image per platform.</summary>
    internal static TagListing ParseHubTags(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var count = root.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : -1;
            var tags = new List<RemoteTag>();

            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    if (Text(item, "name") is not { Length: > 0 } name) continue;

                    List<TagImage>? images = null;

                    if (item.TryGetProperty("images", out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        images = [];

                        foreach (var image in list.EnumerateArray())
                        {
                            var platform = ImagePlatform.From(Text(image, "os"), Text(image, "architecture"), Text(image, "variant"));
                            if (platform is null || platform.IsAttestation) continue;

                            var size = image.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) && bytes > 0 ? bytes : -1;
                            images.Add(new TagImage(platform, size));
                        }
                    }

                    tags.Add(new RemoteTag(name, Date(item, "tag_last_pushed") ?? Date(item, "last_updated"), images, Text(item, "digest")));
                }
            }

            return new TagListing(tags, count, Truncated: count > tags.Count, ByRecency: true);
        }
        catch (JsonException ex)
        {
            throw new RegistryException(RegistryFailure.Unreadable, "Docker Hub's answer could not be read.", ex);
        }
    }

    /// <summary>Docker Hub's description of one repository.</summary>
    internal static ImageSearchResult ParseHubRepository(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            return new ImageSearchResult(
                name,
                ImageSearch.Plain(Text(root, "description")),
                root.TryGetProperty("star_count", out var stars) && stars.TryGetInt32(out var s) ? s : -1,

                // Docker Official Images are, by definition, the library namespace.
                IsOfficial: Text(root, "namespace") == "library",
                Pulls: root.TryGetProperty("pull_count", out var pulls) && pulls.TryGetInt64(out var p) ? p : -1,
                Updated: Date(root, "last_updated"));
        }
        catch (JsonException ex)
        {
            throw new RegistryException(RegistryFailure.Unreadable, "Docker Hub's answer could not be read.", ex);
        }
    }

    internal static IReadOnlyList<string> ParseTagNames(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            // A repository with no tags answers "tags": null on several registries, not an empty list.
            return document.RootElement.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                ? [.. tags.EnumerateArray().Select(t => t.GetString()).OfType<string>().Where(t => t.Length > 0)]
                : [];
        }
        catch (JsonException ex)
        {
            throw new RegistryException(RegistryFailure.Unreadable, "The registry's tag list could not be read.", ex);
        }
    }

    internal static IReadOnlyList<string> ParseCatalog(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.TryGetProperty("repositories", out var names) && names.ValueKind == JsonValueKind.Array
                ? [.. names.EnumerateArray().Select(t => t.GetString()).OfType<string>().Where(t => t.Length > 0)]
                : [];
        }
        catch (JsonException ex)
        {
            throw new RegistryException(RegistryFailure.Unreadable, "The registry's catalog could not be read.", ex);
        }
    }

    /// <summary>What a manifest says: an index's platforms, or an image's layers and config.</summary>
    internal sealed record ManifestFacts(
        IReadOnlyList<(ImagePlatform Platform, string Digest)>? Platforms,
        long? LayerBytes,
        string? ConfigDigest);

    internal static ManifestFacts ParseManifest(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("manifests", out var manifests) && manifests.ValueKind == JsonValueKind.Array)
            {
                var platforms = new List<(ImagePlatform, string)>();

                foreach (var entry in manifests.EnumerateArray())
                {
                    // BuildKit's attestations sit in the index beside the images they describe.
                    if (entry.TryGetProperty("annotations", out var annotations)
                        && Text(annotations, "vnd.docker.reference.type") is "attestation-manifest")
                    {
                        continue;
                    }

                    if (!entry.TryGetProperty("platform", out var p) || Text(entry, "digest") is not { } digest) continue;

                    var platform = ImagePlatform.From(Text(p, "os"), Text(p, "architecture"), Text(p, "variant"));
                    if (platform is null || platform.IsAttestation) continue;

                    platforms.Add((platform, digest));
                }

                return new ManifestFacts(platforms, null, null);
            }

            long? bytes = null;

            if (root.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array)
            {
                bytes = 0;
                foreach (var layer in layers.EnumerateArray())
                {
                    if (layer.TryGetProperty("size", out var size) && size.TryGetInt64(out var value)) bytes += value;
                }
            }

            var config = root.TryGetProperty("config", out var c) ? Text(c, "digest") : null;
            return new ManifestFacts(null, bytes, config);
        }
        catch (JsonException ex)
        {
            throw new RegistryException(RegistryFailure.Unreadable, "The registry's manifest could not be read.", ex);
        }
    }

    static string? Text(JsonElement item, string name)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static DateTimeOffset? Date(JsonElement item, string name)
        => Text(item, name) is { Length: > 0 } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var date)
            ? date
            : null;

    // ---------------------------------------------------------------- small things

    static (string Namespace, string Name) HubName(string path)
    {
        var slash = path.IndexOf('/');
        return slash < 0 ? ("library", path) : (path[..slash], path[(slash + 1)..]);
    }

    /// <summary><c>library/nginx</c> is <c>nginx</c> to everyone but the API.</summary>
    static string Display(string path) => path.StartsWith("library/", StringComparison.Ordinal) ? path[8..] : path;

    static string Escape(string segment) => Uri.EscapeDataString(segment);

    static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    static string HostOnly(string host)
    {
        // "[::1]:5000" and "localhost:5000" — the port is not part of the name.
        if (host.StartsWith('[')) return host[..(host.IndexOf(']') + 1)];

        var colon = host.LastIndexOf(':');
        return colon > 0 ? host[..colon] : host;
    }

    static bool IsLoopback(string host)
    {
        var name = HostOnly(host).Trim('[', ']');
        return name == "localhost" || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(name, out var address) && IPAddress.IsLoopback(address));
    }

    T? Cached<T>(string key, TimeSpan lifetime) where T : class
        => _cache.TryGetValue(key, out var entry) && _time.GetUtcNow() - entry.At < lifetime ? entry.Value as T : null;

    T Remember<T>(string key, T value) where T : class
    {
        // Bounded by forgetting everything, which is crude and enough: these are conveniences for a
        // dialog someone is typing into, and a refetch is the worst that happens.
        if (_cache.Count > 64) _cache.Clear();

        _cache[key] = (_time.GetUtcNow(), value);
        return value;
    }

    public void Dispose() => _http.Dispose();
}
