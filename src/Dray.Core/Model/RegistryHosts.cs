namespace Dray.Core.Model;

/// <summary>
/// A registry offered in the pull dialog's picker.
/// </summary>
/// <param name="Host">What a reference is prefixed with — <c>ghcr.io</c>, <c>localhost:5000</c>. Docker Hub's is <c>docker.io</c>.</param>
/// <param name="Name">What people call it. The host for anything Dray has no better name for.</param>
/// <param name="SignedInAs">
/// The username the credential helper lists for it, or null. A name and nothing else: the picker is
/// built from the helper's <c>list</c>, which returns no secrets (docs/CREDENTIALS.md).
/// </param>
/// <param name="IsConfigured">In the user's Docker config, as opposed to one Dray offers to everyone.</param>
public sealed record RegistryOption(string Host, string Name, string? SignedInAs = null, bool IsConfigured = false)
{
    /// <summary>The picker's line for it.</summary>
    public string Label => SignedInAs is { Length: > 0 } user ? $"{Name} · {user}" : Name;
}

/// <summary>
/// Registry hosts: which one Docker Hub is, which segment of a reference is a host, and which
/// registries to offer.
/// </summary>
public static class RegistryHosts
{
    /// <summary>Docker Hub as a reference spells it. Its API lives elsewhere; see <see cref="ApiHost"/>.</summary>
    public const string DockerHub = "docker.io";

    /// <summary>
    /// Registries worth offering to anyone, in the order people reach for them.
    /// <para>
    /// Each one is here because it serves public images anonymously over the standard registry API,
    /// which is what listing their tags needs. What each can do beyond that — search, a catalog — is
    /// not declared here: it is measured the first time it is asked, because a registry's support for
    /// either is a fact about that registry today and not something a list can promise.
    /// </para>
    /// </summary>
    public static IReadOnlyList<RegistryOption> WellKnown { get; } =
    [
        new(DockerHub, "Docker Hub"),
        new("ghcr.io", "GitHub"),
        new("quay.io", "Quay"),
        new("mcr.microsoft.com", "Microsoft"),
        new("public.ecr.aws", "Amazon ECR Public"),
    ];

    /// <summary>Every spelling of Docker Hub that a reference, a config key or a person might use.</summary>
    public static bool IsDockerHub(string? host)
        => (host ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant() is "" or DockerHub
            or "index.docker.io" or "registry-1.docker.io" or "registry.hub.docker.com" or "hub.docker.com"
            or "https://index.docker.io/v1" or "index.docker.io/v1";

    /// <summary>
    /// Where the registry API for a host answers. Docker Hub's references say <c>docker.io</c> and its
    /// registry answers at <c>registry-1.docker.io</c>; everything else answers at its own name.
    /// </summary>
    public static string ApiHost(string host) => IsDockerHub(host) ? "registry-1.docker.io" : host;

    /// <summary>
    /// The key a credential for this host is stored under: Docker Hub's legacy URL, which is the only
    /// string a helper matches for it, or the host itself.
    /// </summary>
    public static string CredentialServer(string host) => IsDockerHub(host) ? ImageTag.RegistryEntryDockerHub : host;

    /// <summary>
    /// A host from a key in <c>config.json</c> or a helper's list — <c>https://ghcr.io</c>,
    /// <c>https://index.docker.io/v1/</c> — which carry a scheme and sometimes a path.
    /// </summary>
    public static string FromServer(string server)
    {
        if (IsDockerHub(server)) return DockerHub;

        var host = server.Trim();
        foreach (var scheme in (string[])["https://", "http://"])
        {
            if (host.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) host = host[scheme.Length..];
        }

        var slash = host.IndexOf('/');
        if (slash >= 0) host = host[..slash];

        return IsDockerHub(host) ? DockerHub : host.ToLowerInvariant();
    }

    /// <summary>
    /// Whether the first segment of a reference names a registry: it contains a dot or a port, or is
    /// exactly <c>localhost</c>. The Docker CLI's rule, and not guessable from the shape alone.
    /// </summary>
    public static bool IsRegistryHost(string segment)
        => segment.Length > 0
            && (segment.Contains('.', StringComparison.Ordinal)
                || segment.Contains(':', StringComparison.Ordinal)
                || segment == "localhost");

    /// <summary>What to call a host: its well-known name, or the host itself.</summary>
    public static string NameOf(string host)
        => WellKnown.FirstOrDefault(o => string.Equals(o.Host, IsDockerHub(host) ? DockerHub : host, StringComparison.OrdinalIgnoreCase))?.Name
            ?? host;

    /// <summary>
    /// The picker's contents: the well-known registries, then the ones in the user's config, then the
    /// one in the field if it is neither.
    /// <para>
    /// A configured registry that is also well-known is not listed twice; it takes the well-known
    /// entry's place and adds who is signed in. The current host is always present, because a picker
    /// that cannot show what the field says has two answers to "which registry".
    /// </para>
    /// </summary>
    /// <param name="configured">Each configured registry's server key and signed-in username.</param>
    public static IReadOnlyList<RegistryOption> Offer(
        IEnumerable<(string Server, string? Username)> configured, string? current = null)
    {
        var options = WellKnown.ToList();

        foreach (var (server, username) in configured)
        {
            var host = FromServer(server);
            if (host.Length == 0) continue;

            var index = options.FindIndex(o => string.Equals(o.Host, host, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                options[index] = options[index] with
                {
                    SignedInAs = options[index].SignedInAs ?? username,
                    IsConfigured = true,
                };
            }
            else
            {
                options.Add(new RegistryOption(host, host, username, IsConfigured: true));
            }
        }

        if (current is { Length: > 0 } typed)
        {
            var host = IsDockerHub(typed) ? DockerHub : typed.Trim().ToLowerInvariant();

            if (!options.Any(o => string.Equals(o.Host, host, StringComparison.OrdinalIgnoreCase)))
                options.Add(new RegistryOption(host, host));
        }

        return options;
    }
}
