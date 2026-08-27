namespace Dray.Core.Model;

/// <summary>
/// One environment variable, and whether Dray should show its value by default.
/// <para>
/// Containers routinely carry database passwords, API tokens and signing keys in their
/// environment. Every other Docker GUI prints them in plain text, which is fine alone at a desk
/// and wrong in a screen-share, a screenshot in a ticket, or a recorded call. Dray masks the
/// values that look like secrets and offers to reveal them, because the cost of being wrong in
/// that direction is a click and the cost of being wrong the other way is a leaked credential.
/// </para>
/// </summary>
public sealed record EnvVar(string Key, string Value)
{
    /// <summary>
    /// Whether the value is masked until the user asks for it.
    /// <para>
    /// Matched on the name rather than the shape of the value. Entropy scoring would catch more
    /// and would also mask a build hash, and a user who cannot predict what will be hidden stops
    /// trusting the screen. A name-based rule is one the user can hold in their head.
    /// </para>
    /// </summary>
    public bool IsSecret => LooksSecret(Key) || CarriesInlineCredentials(Value);

    static readonly string[] SecretNameParts =
    [
        "PASSWORD", "PASSWD", "PWD", "SECRET", "TOKEN", "APIKEY", "API_KEY", "ACCESS_KEY",
        "PRIVATE_KEY", "CREDENTIAL", "AUTH", "SALT", "CIPHER", "SIGNING", "SESSION_KEY",
    ];

    /// <summary>
    /// Names that end in a key-ish word but point at a secret rather than being one.
    /// <para>
    /// Checked first, and deliberately so: <c>ACCESS_KEY_ID</c> contains <c>ACCESS_KEY</c>, and
    /// AWS pairs a public key id with a separate secret. Masking the id teaches the user that the
    /// mask is noise, which costs more than showing an identifier.
    /// </para>
    /// </summary>
    static readonly string[] PointerSuffixes = ["_KEY_PATH", "_KEY_FILE", "_KEY_ID", "_KEY_NAME"];

    static bool LooksSecret(string key)
    {
        var upper = key.ToUpperInvariant();

        if (PointerSuffixes.Any(suffix => upper.EndsWith(suffix, StringComparison.Ordinal))) return false;

        // "KEY" alone is too broad — KEYCLOAK_URL is not a secret — so the bare word only counts
        // when it is the whole name or the last segment.
        if (upper is "KEY" || upper.EndsWith("_KEY", StringComparison.Ordinal)) return true;

        return SecretNameParts.Any(part => upper.Contains(part, StringComparison.Ordinal));
    }

    /// <summary>
    /// A connection string with a password in its authority — <c>postgres://user:hunter2@host/db</c>.
    /// <para>
    /// Worth catching by value, because the variable is usually called DATABASE_URL and nothing in
    /// that name suggests it carries a credential.
    /// </para>
    /// </summary>
    static bool CarriesInlineCredentials(string value)
    {
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return false;

        var authorityStart = scheme + 3;
        var authorityEnd = value.IndexOf('/', authorityStart);
        var authority = authorityEnd < 0 ? value[authorityStart..] : value[authorityStart..authorityEnd];

        var at = authority.LastIndexOf('@');
        if (at <= 0) return false;

        // user@host is an identifier; user:pass@host is a credential.
        return authority[..at].Contains(':', StringComparison.Ordinal);
    }
}

/// <summary>Where a container's storage comes from.</summary>
public enum MountKind
{
    /// <summary>A named volume the engine manages. Browsable in Dray.</summary>
    Volume,

    /// <summary>A host path mounted in. Browsable on the host, not through the engine.</summary>
    Bind,

    /// <summary>In-memory. Contents vanish with the container.</summary>
    Tmpfs,

    Other,
}

/// <summary>One mount on a container.</summary>
/// <param name="Source">Volume name for a volume, host path for a bind, empty for tmpfs.</param>
/// <param name="Destination">Path inside the container.</param>
/// <param name="ReadOnly">Whether the container can write to it.</param>
public sealed record MountPoint(
    MountKind Kind,
    string Source,
    string Destination,
    bool ReadOnly)
{
    /// <summary>Named volumes are the only mounts Dray can open, since the engine owns them.</summary>
    public bool IsBrowsable => Kind == MountKind.Volume && Source.Length > 0;
}

/// <summary>One network a container is attached to.</summary>
public sealed record NetworkAttachment(
    string Name,
    string? IpAddress,
    string? Gateway,
    string? MacAddress,
    IReadOnlyList<string> Aliases);

/// <summary>
/// A port the image declares, whether or not it is published.
/// <para>
/// Distinct from <see cref="PortBinding"/>, which is the list row's view and only carries mappings
/// that reach the host. Here an unpublished port still matters: it is the usual reason a service
/// is unreachable, and a screen that silently omits it cannot answer "why can't I connect?".
/// </para>
/// </summary>
public sealed record ExposedPort(
    int ContainerPort,
    string Protocol,
    IReadOnlyList<PortBinding> Bindings)
{
    public bool IsPublished => Bindings.Count > 0;
}

/// <summary>Everything the engine knows about one container.</summary>
public sealed record ContainerInspect
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Image { get; init; }

    /// <summary>Resolved image digest or id, which is what actually ran.</summary>
    public string? ImageId { get; init; }

    public DateTimeOffset? Created { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public required DockerState State { get; init; }

    public DockerHealth Health { get; init; } = DockerHealth.None;

    public int? ExitCode { get; init; }

    /// <summary>True when the kernel's OOM killer stopped it, which exit code 137 alone cannot tell you.</summary>
    public bool OomKilled { get; init; }

    /// <summary>The engine's own error string, when it has one.</summary>
    public string? Error { get; init; }

    public int? Pid { get; init; }

    public IReadOnlyList<string> Entrypoint { get; init; } = [];

    public IReadOnlyList<string> Command { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public string? User { get; init; }

    public IReadOnlyList<EnvVar> Environment { get; init; } = [];

    public IReadOnlyList<ExposedPort> Ports { get; init; } = [];

    public IReadOnlyList<MountPoint> Mounts { get; init; } = [];

    public IReadOnlyList<NetworkAttachment> Networks { get; init; } = [];

    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string? RestartPolicy { get; init; }

    public int RestartCount { get; init; }

    /// <summary>
    /// The engine's raw inspect JSON, already indented.
    /// <para>
    /// Kept because no curated view is ever complete, and the alternative is the user leaving for
    /// a terminal. Dray's job is to make that unnecessary, not to pretend the fields it chose are
    /// the only ones that exist.
    /// </para>
    /// </summary>
    public string RawJson { get; init; } = "";

    public ContainerStatus Status => ContainerStatusVocabulary.Resolve(State, Health, ExitCode);

    public string ShortId => Id.Length <= 12 ? Id : Id[..12];

    /// <summary>
    /// Whether the id says anything the name does not. False on Apple's runtime, where they are
    /// the same string and an ID row would just repeat the title.
    /// </summary>
    public bool HasDistinctId => !string.Equals(Id, Name, StringComparison.Ordinal);

    /// <summary>
    /// What actually runs, as a shell would show it. Entrypoint and command concatenated, because
    /// reading them apart is a step the user should not have to do in their head.
    /// </summary>
    public string CommandLine => string.Join(' ', Entrypoint.Concat(Command).Select(Quote));

    static string Quote(string part) =>
        part.Contains(' ', StringComparison.Ordinal) ? $"\"{part}\"" : part;

    /// <summary>Compose project, when this container belongs to a stack.</summary>
    public string? Stack => Labels.GetValueOrDefault("com.docker.compose.project");
}
