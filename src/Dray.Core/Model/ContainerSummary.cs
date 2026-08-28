namespace Dray.Core.Model;

/// <summary>A published port mapping, host side to container side.</summary>
public sealed record PortBinding(int HostPort, int ContainerPort, string Protocol = "tcp")
{
    public string Display => $"{HostPort}:{ContainerPort}" + (Protocol == "tcp" ? "" : $"/{Protocol}");

    /// <summary>Where clicking the port should take the user. Only meaningful for TCP.</summary>
    public string? LocalUrl => Protocol == "tcp" ? $"http://localhost:{HostPort}" : null;
}

/// <summary>
/// One row of the containers list. Deliberately only the fields that answer the question someone
/// opened Dray to ask — state, health, ports, and how long it has been that way. Everything else
/// lives behind the detail pane's Inspect tab (PRODUCT.md: "answer the question in the first screen").
/// </summary>
public sealed record ContainerSummary
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Image { get; init; }

    public required DockerState State { get; init; }

    public DockerHealth Health { get; init; } = DockerHealth.None;

    public int? ExitCode { get; init; }

    /// <summary>When the container last entered its current state.</summary>
    public DateTimeOffset? Since { get; init; }

    public IReadOnlyList<PortBinding> Ports { get; init; } = [];

    /// <summary>
    /// What compose says about this container, when compose created it.
    /// <para>
    /// One field rather than four loose ones: project, service, config files and working directory
    /// are only ever meaningful together, and a container that is not part of a stack has none of
    /// them.
    /// </para>
    /// </summary>
    public ComposeMembership? Compose { get; init; }

    /// <summary>Compose project name. The list's Stack column, and the most-used part of <see cref="Compose"/>.</summary>
    public string? Stack => Compose?.Project;

    /// <summary>
    /// The resolved image id — the digest of what actually ran, which is not the same thing as the
    /// tag it was started from. A tag moves; this does not.
    /// </summary>
    public string? ImageId { get; init; }

    /// <summary>
    /// The address on the first network it is attached to, or null when it has none — a container
    /// on the host network, or a stopped one, genuinely has no address and must not show a stale
    /// one.
    /// </summary>
    public string? IpAddress { get; init; }

    public double? CpuPercent { get; init; }

    public long? MemoryBytes { get; init; }

    /// <summary>Bytes across the network since it started, received and sent together.</summary>
    public long? NetworkBytes { get; init; }

    /// <summary>
    /// Bytes written to the container's own writable layer — not its volumes.
    /// <para>
    /// Null unless the engine was asked for sizes, which is markedly slower on every refresh: it
    /// walks each container's filesystem. Null therefore means "not measured", never "empty".
    /// </para>
    /// </summary>
    public long? DiskBytes { get; init; }

    public ContainerStatus Status => ContainerStatusVocabulary.Resolve(State, Health, ExitCode);

    /// <summary>The short id users actually recognise.</summary>
    public string ShortId => Id.Length <= 12 ? Id : Id[..12];

    /// <summary>
    /// Whether the id is worth showing next to the name.
    /// <para>
    /// On Apple's runtime a container's id <i>is</i> its name, so the short id renders as the name
    /// with its last few characters cut off — which looks like a truncation bug and tells the user
    /// nothing they cannot already see.
    /// </para>
    /// </summary>
    public bool HasDistinctId => !string.Equals(Id, Name, StringComparison.Ordinal);

    /// <summary>
    /// Whether two rows describe the same container in the same state.
    /// <para>
    /// <b>Not</b> record equality, and the difference is not academic. <see cref="Ports"/> is a
    /// list, so two summaries built from two identical engine responses compare unequal by
    /// reference — every time. A poll loop deciding what changed with <c>==</c> would therefore
    /// find that everything changed on every tick, rewrite every row, and fire the change
    /// highlight on a list nobody had touched.
    /// </para>
    /// </summary>
    public bool SameAs(ContainerSummary other)
        => Id == other.Id
        && Name == other.Name
        && Image == other.Image
        && State == other.State
        && Health == other.Health
        && ExitCode == other.ExitCode
        && Since == other.Since
        && CpuPercent == other.CpuPercent
        && MemoryBytes == other.MemoryBytes
        // Project, service and replica are the parts a row renders. The rest of a membership —
        // config files, working directory — is a list and a path that only the stack pages read,
        // and comparing the list would reintroduce exactly the reference problem above.
        && Compose?.Project == other.Compose?.Project
        && Compose?.Service == other.Compose?.Service
        && Compose?.Replica == other.Compose?.Replica
        && Ports.SequenceEqual(other.Ports);
}
