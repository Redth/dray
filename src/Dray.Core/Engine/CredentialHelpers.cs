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

    public string DisplayName => Server == DockerHub ? "Docker Hub" : Server;

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
    /// </summary>
    public async Task<IReadOnlyList<RegistryEntry>> ListAsync(CancellationToken ct = default)
    {
        var config = ReadConfig();
        if (config is null) return [];

        var servers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var server in config.Auths?.Keys ?? Enumerable.Empty<string>()) servers.Add(server);
        foreach (var server in config.CredHelpers?.Keys ?? Enumerable.Empty<string>()) servers.Add(server);

        var probed = new Dictionary<string, bool>(StringComparer.Ordinal);
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
                if (!probed.TryGetValue(helper, out var present))
                {
                    present = await IsInstalledAsync(helper, ct).ConfigureAwait(false);
                    probed[helper] = present;
                }

                missing = !present;
                if (present) username = await UsernameAsync(helper, server, ct).ConfigureAwait(false);
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

    DockerConfigFile? ReadConfig()
    {
        var path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker", "config.json");

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

    /// <summary>Whether <c>docker-credential-{helper}</c> is on PATH.</summary>
    async Task<bool> IsInstalledAsync(string helper, CancellationToken ct)
    {
        try
        {
            // `list` is the cheapest subcommand that proves the binary runs. `version` is not part
            // of the protocol and not every helper implements it.
            var result = await _runner
                .RunAsync(Executable(helper), ["list"], null, ct)
                .ConfigureAwait(false);

            return result.ExitCode == 0;
        }
        catch (Exception)
        {
            // Not on PATH, or a dangling symlink into an uninstalled app — the exact failure that
            // motivated docs/CREDENTIALS.md §1.3.
            return false;
        }
    }

    /// <summary>
    /// The username stored for a registry.
    /// <para>
    /// Uses <c>list</c> rather than <c>get</c>: <c>list</c> returns server-to-username pairs and no
    /// secrets at all, so Dray never has a token in memory just to render a table.
    /// </para>
    /// </summary>
    async Task<string?> UsernameAsync(string helper, string server, CancellationToken ct)
    {
        try
        {
            var result = await _runner
                .RunAsync(Executable(helper), ["list"], null, ct)
                .ConfigureAwait(false);

            if (result.ExitCode != 0) return null;

            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(result.StandardOutput);
            if (map is null) return null;

            // The helper keys by URL — "https://ghcr.io" — while config.json keys by bare host.
            // Matching exactly finds nothing, which reads as "no username stored" for every
            // registry on the machine.
            foreach (var (key, value) in map)
            {
                if (SameServer(key, server)) return Normalize(value);
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether two server identifiers name the same registry, ignoring the scheme and any trailing
    /// slash that one side happens to carry and the other does not.
    /// </summary>
    internal static bool SameServer(string a, string b)
        => string.Equals(Strip(a), Strip(b), StringComparison.OrdinalIgnoreCase);

    static string Strip(string server)
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
    /// Pushing an image is the one operation that cannot be done without the secret: the engine
    /// needs an auth header, and there is no way to delegate that to the helper. Anything that can
    /// be done without calling this must not call it.
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
