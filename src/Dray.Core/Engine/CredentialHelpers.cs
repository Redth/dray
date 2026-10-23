using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dray.Core.Engine;

/// <summary>Where a registry's credential actually lives.</summary>
public enum CredentialStorage
{
    /// <summary>A credential helper holds it. The normal, correct case.</summary>
    Helper,

    /// <summary>
    /// Base64 in <c>config.json</c>. Not encryption — an encoding. Dray reads these so a user is
    /// not locked out of a registry they configured, and never writes one.
    /// </summary>
    Plaintext,

    /// <summary>Configured, but nothing is stored. Public pulls still work.</summary>
    None,
}

/// <summary>One registry the user has configured.</summary>
/// <param name="Helper">
/// The helper named for this registry, if any — <c>osxkeychain</c>, <c>ecr-login</c>. The
/// executable is <c>docker-credential-{Helper}</c>.
/// </param>
/// <param name="HelperMissing">
/// The config names a helper that is not on PATH. A first-class state, not an exception: an
/// uninstalled engine takes its bundled helper with it and leaves the config pointing at nothing.
/// </param>
public sealed record RegistryEntry(
    string Server,
    string? Username,
    CredentialStorage Storage,
    string? Helper = null,
    bool HelperMissing = false,
    bool TokenAuth = false)
{
    /// <summary>Docker Hub is stored under a long legacy URL nobody would recognise.</summary>
    public const string DockerHub = "https://index.docker.io/v1/";

    /// <summary>
    /// Docker Hub by name, anything else by host: a registry found in a helper's store is keyed by
    /// URL (<c>https://ghcr.io</c>), and the scheme is noise in a list of registries.
    /// </summary>
    public string DisplayName => string.Equals(Server, DockerHub, StringComparison.OrdinalIgnoreCase)
        ? "Docker Hub"
        : RegistryReader.Strip(Server);

    /// <summary>True when the user could sign in from Dray, rather than the helper minting tokens itself.</summary>
    public bool AcceptsSignIn => !HelperMissing && !IsAmbient;

    /// <summary>
    /// A cloud helper that mints short-lived tokens from an ambient identity. A username and
    /// password field would be the wrong question for these.
    /// </summary>
    public bool IsAmbient => Helper is "ecr-login" or "gcloud" or "gcr" or "acr-env" or "azure";
}

/// <summary>
/// One credential, in memory for the length of one engine call.
/// <para>
/// Never logged, never rendered, never written anywhere. Its <see cref="ToString"/> is overridden
/// because a record's generated one prints every property — so a stray interpolation into a log
/// line or an exception message would print the secret.
/// </para>
/// </summary>
public sealed record RegistryCredential(string Server, string Username, string Secret)
{
    public override string ToString() => $"RegistryCredential {{ Server = {Server}, Username = {Username} }}";
}

/// <summary>What a credential helper's <c>get</c> returns on stdout.</summary>
internal sealed class HelperCredential
{
    [JsonPropertyName("Username")]
    public string? Username { get; set; }

    [JsonPropertyName("Secret")]
    public string? Secret { get; set; }
}

/// <summary>
/// The shape of <c>~/.docker/config.json</c> that Dray reads.
/// </summary>
public sealed class DockerConfigFile
{
    [JsonPropertyName("auths")]
    public Dictionary<string, DockerAuthEntry>? Auths { get; set; }

    /// <summary>The default helper for every registry without a specific one.</summary>
    [JsonPropertyName("credsStore")]
    public string? CredsStore { get; set; }

    /// <summary>Per-registry helpers. These win over <see cref="CredsStore"/>.</summary>
    [JsonPropertyName("credHelpers")]
    public Dictionary<string, string>? CredHelpers { get; set; }
}

public sealed class DockerAuthEntry
{
    /// <summary>base64("user:secret"). Present only for the plaintext case.</summary>
    [JsonPropertyName("auth")]
    public string? Auth { get; set; }

    [JsonPropertyName("identitytoken")]
    public string? IdentityToken { get; set; }
}

/// <summary>
/// Reading which registries are configured, and where each one's secret lives.
/// <para>
/// docs/CREDENTIALS.md: Dray speaks the helper protocol and stores nothing itself. This class
/// reads configuration and probes helpers; it never handles a secret at rest.
/// </para>
/// </summary>
public sealed class RegistryReader(IDockerConfigSource source, IProcessRunner? runner = null)
{
    readonly IProcessRunner _runner = runner ?? new SystemProcessRunner();

    /// <summary>
    /// Every configured registry.
    /// <para>
    /// An empty <c>auths</c> entry is not an empty credential — it means "this registry is
    /// configured and its secret is in the helper's store". The file is a list of registries, not
    /// a list of secrets.
    /// </para>
    /// <para>
    /// The default helper's own list counts too. A registry signed into from Dray has no
    /// <c>auths</c> entry, because Dray does not write <c>config.json</c> (docs/CREDENTIALS.md
    /// section 4), and needs none: the CLI asks <c>credsStore</c> for any registry it is not told
    /// otherwise about. Without this it would be signed in and missing from the list.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<RegistryEntry>> ListAsync(CancellationToken ct = default)
    {
        var config = ReadConfig();
        if (config is null) return [];

        var servers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var server in config.Auths?.Keys ?? Enumerable.Empty<string>()) servers.Add(server);
        foreach (var server in config.CredHelpers?.Keys ?? Enumerable.Empty<string>()) servers.Add(server);

        // Each helper's `list` once: it answers both whether the helper is installed and who is
        // signed in where, and it returns no secrets.
        var lists = new Dictionary<string, Dictionary<string, string>?>(StringComparer.Ordinal);
        async Task<Dictionary<string, string>?> ListOf(string helper)
        {
            if (!lists.TryGetValue(helper, out var list))
            {
                list = await HelperListAsync(helper, ct).ConfigureAwait(false);
                lists[helper] = list;
            }

            return list;
        }

        if (config.CredsStore is { } store && await ListOf(store).ConfigureAwait(false) is { } stored)
        {
            foreach (var server in stored.Keys.Where(IsRegistryKey))
            {
                // The helper keys by URL and config.json by host, so the same registry can arrive
                // from both under two spellings.
                if (servers.Any(known => SameServer(known, server))) continue;

                // A per-registry helper owns this one, so the default's copy is stale.
                if (config.CredHelpers?.Keys.Any(k => SameServer(k, server)) == true) continue;

                servers.Add(server);
            }
        }

        var entries = new List<RegistryEntry>(servers.Count);

        foreach (var server in servers)
        {
            // Per-registry wins over the default. Getting this order wrong sends the request to
            // the wrong store and looks like a missing credential.
            var helper = config.CredHelpers?.GetValueOrDefault(server) ?? config.CredsStore;

            var entry = config.Auths?.GetValueOrDefault(server);
            var plaintext = !string.IsNullOrEmpty(entry?.Auth);

            var missing = false;
            string? username = null;

            if (helper is not null)
            {
                var list = await ListOf(helper).ConfigureAwait(false);

                missing = list is null;
                if (list is not null) username = UsernameIn(list, server);
            }

            // A username is readable from a plaintext entry without touching the helper — and it is
            // the half of that string that is not a secret.
            username ??= plaintext ? UsernameFromAuth(entry!.Auth!) : null;

            entries.Add(new RegistryEntry(
                server,
                username,
                helper is not null && !missing ? CredentialStorage.Helper
                    : plaintext ? CredentialStorage.Plaintext
                    : CredentialStorage.None,
                helper,
                missing));
        }

        return entries;
    }

    /// <summary>
    /// The helper a registry's credential goes to: its own in <c>credHelpers</c>, else the default
    /// <c>credsStore</c> — the order the CLI looks in, so a sign-in lands where <c>docker pull</c>
    /// will ask. Null when neither is configured, and Dray will not fall back to plaintext.
    /// </summary>
    public string? HelperFor(string server)
    {
        var config = ReadConfig();
        if (config is null) return null;

        return config.CredHelpers?.FirstOrDefault(kv => SameServer(kv.Key, server)).Value ?? config.CredsStore;
    }

    /// <summary>
    /// A registry as the user typed it, as the CLI names it: nothing, or any of Docker Hub's
    /// hostnames, is Docker Hub's legacy URL, as with <c>docker login</c>; anything else is its host,
    /// without a scheme or a trailing slash.
    /// </summary>
    public static string NormalizeServer(string? typed)
    {
        var host = Strip((typed ?? string.Empty).Trim());

        return host.Length == 0 || host.ToLowerInvariant() is "docker.io" or "index.docker.io"
            or "registry-1.docker.io" or "hub.docker.com" or "index.docker.io/v1"
            ? RegistryEntry.DockerHub
            : host;
    }

    /// <summary>
    /// Whether a key in a helper's list names a registry. Docker Desktop keeps its own sign-in
    /// in the same store, under <c>https://index.docker.io/v1/access-token</c> and
    /// <c>…/refresh-token</c>: a host with a path, which no registry key has, except Docker Hub's
    /// legacy URL itself.
    /// </summary>
    internal static bool IsRegistryKey(string key)
        => string.Equals(key, RegistryEntry.DockerHub, StringComparison.OrdinalIgnoreCase)
            || (Strip(key) is { Length: > 0 } host && !host.Contains('/'));

    DockerConfigFile? ReadConfig()
    {
        // The same directory context discovery reads, so DOCKER_CONFIG moves both together, as it
        // does for the CLI.
        if (source.DockerConfigDirectory is not { } directory) return null;
        var path = System.IO.Path.Combine(directory, "config.json");

        try
        {
            if (!source.FileExists(path)) return null;

            return JsonSerializer.Deserialize<DockerConfigFile>(source.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A malformed or unreadable config is not a crash: the user simply has no registries
            // Dray can list, and public pulls still work.
            return null;
        }
    }

    /// <summary>
    /// What <c>docker-credential-{helper} list</c> holds: server to username, and no secrets at all,
    /// so Dray never has a token in memory just to render a table. Null when the helper is not
    /// installed; empty when it runs but its output is not the protocol's.
    /// <para>
    /// <c>list</c> is also the cheapest subcommand that proves the binary runs. <c>version</c> is not
    /// part of the protocol and not every helper implements it.
    /// </para>
    /// </summary>
    async Task<Dictionary<string, string>?> HelperListAsync(string helper, CancellationToken ct)
    {
        ProcessResult result;
        try
        {
            result = await _runner
                .RunAsync(Executable(helper), ["list"], null, ct)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Not on PATH, or a dangling symlink into an uninstalled app — the exact failure that
            // motivated docs/CREDENTIALS.md §1.3.
            return null;
        }

        if (result.ExitCode != 0) return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(result.StandardOutput) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The username a helper's list holds for a registry. The helper keys by URL —
    /// <c>https://ghcr.io</c> — while config.json keys by bare host; matching exactly finds nothing,
    /// which reads as "no username stored" for every registry on the machine.
    /// </summary>
    static string? UsernameIn(Dictionary<string, string> list, string server)
    {
        foreach (var (key, value) in list)
        {
            if (SameServer(key, server)) return Normalize(value);
        }

        return null;
    }

    /// <summary>
    /// Whether two server identifiers name the same registry, ignoring the scheme and any trailing
    /// slash that one side happens to carry and the other does not.
    /// </summary>
    internal static bool SameServer(string a, string b)
        => string.Equals(Strip(a), Strip(b), StringComparison.OrdinalIgnoreCase);

    internal static string Strip(string server)
    {
        var s = server;

        foreach (var scheme in (string[])["https://", "http://"])
        {
            if (s.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) s = s[scheme.Length..];
        }

        return s.TrimEnd('/');
    }

    /// <summary>
    /// A helper's username, or null when it is a placeholder.
    /// <para>
    /// Azure's registries store the literal <c>&lt;token&gt;</c> as the username to mean "the
    /// secret is a token, not a password". Rendering that in a Username column would look like a
    /// bug; saying nothing is more honest.
    /// </para>
    /// </summary>
    static string? Normalize(string? username)
        => string.IsNullOrWhiteSpace(username) || username is "<token>" or "00000000-0000-0000-0000-000000000000"
            ? null
            : username;

    /// <summary>
    /// The username half of a base64 <c>user:secret</c>.
    /// <para>
    /// The secret half is deliberately dropped on the floor rather than returned anywhere.
    /// </para>
    /// </summary>
    internal static string? UsernameFromAuth(string auth)
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth));
            var colon = decoded.IndexOf(':');

            return colon <= 0 ? null : decoded[..colon];
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static string Executable(string helper) => $"docker-credential-{helper}";

    /// <summary>
    /// Hand a credential to the helper and forget it.
    /// <para>
    /// The secret lives in this method's parameters for the length of one call and is never
    /// returned, logged or rendered.
    /// </para>
    /// </summary>
    public async Task<string?> StoreAsync(
        string helper, string server, string username, string secret, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            ServerURL = server,
            Username = username,
            Secret = secret,
        });

        try
        {
            var result = await _runner
                .RunWithInputAsync(Executable(helper), ["store"], payload, ct)
                .ConfigureAwait(false);

            return result.ExitCode == 0
                ? null
                : $"The credential helper refused: {result.StandardError.Trim()}";
        }
        catch (Exception ex)
        {
            return $"Could not run {Executable(helper)}: {ex.Message}";
        }
    }

    /// <summary>
    /// Fetch a credential from the helper, for the length of one operation.
    /// <para>
    /// <b>The only read path in Dray, and deliberately narrow.</b> docs/CREDENTIALS.md says Dray
    /// stores no secrets, and it still does not: this returns one, the caller hands it to the
    /// engine, and nothing keeps it. Every other screen uses <c>list</c>, which returns usernames
    /// and no secrets at all, precisely so that rendering a table never puts a token in memory.
    /// </para>
    /// <para>
    /// Called only for what cannot be done without the secret — a push, and a pull, update check or
    /// tag listing the registry has already refused anonymously; docs/CREDENTIALS.md section 1.5
    /// lists them. The engine needs an auth header and there is no way to delegate that to the
    /// helper. Anything that can be done without calling this must not call it.
    /// </para>
    /// </summary>
    /// <returns>The credential, or null when the helper has none for this registry.</returns>
    public async Task<RegistryCredential?> GetAsync(string helper, string server, CancellationToken ct = default)
    {
        try
        {
            var result = await _runner
                .RunWithInputAsync(Executable(helper), ["get"], server, ct)
                .ConfigureAwait(false);

            if (result.ExitCode != 0) return null;

            var payload = JsonSerializer.Deserialize<HelperCredential>(result.StandardOutput);

            return payload?.Secret is { Length: > 0 } secret
                ? new RegistryCredential(server, payload.Username ?? string.Empty, secret)
                : null;
        }
        catch (Exception)
        {
            // A helper that is gone or refuses is the same to the caller as one with nothing
            // stored: the push proceeds anonymously and the registry decides.
            return null;
        }
    }

    /// <summary>
    /// The credential for one registry, found by whichever helper is configured for it.
    /// <para>
    /// Wraps <see cref="GetAsync"/> with the config lookup, so a caller pushing an image does not
    /// have to know which helper holds what — the same per-registry-beats-default precedence
    /// <see cref="ListAsync"/> applies.
    /// </para>
    /// </summary>
    public async Task<RegistryCredential?> FindForAsync(string server, CancellationToken ct = default)
    {
        var config = ReadConfig();
        if (config is null) return null;

        var helper = config.CredHelpers?
            .FirstOrDefault(kv => SameServer(kv.Key, server)).Value
            ?? config.CredsStore;

        return helper is null ? null : await GetAsync(helper, server, ct).ConfigureAwait(false);
    }

    /// <summary>Remove a credential from the helper's store.</summary>
    public async Task<string?> EraseAsync(string helper, string server, CancellationToken ct = default)
    {
        try
        {
            var result = await _runner
                .RunWithInputAsync(Executable(helper), ["erase"], server, ct)
                .ConfigureAwait(false);

            return result.ExitCode == 0
                ? null
                : $"The credential helper refused: {result.StandardError.Trim()}";
        }
        catch (Exception ex)
        {
            return $"Could not run {Executable(helper)}: {ex.Message}";
        }
    }
}
