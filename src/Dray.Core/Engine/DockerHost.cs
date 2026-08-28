namespace Dray.Core.Engine;

/// <summary>Where Dray learned about a host. Decides ordering and what the user may edit.</summary>
public enum HostOrigin
{
    /// <summary>A context in <c>~/.docker/contexts</c>.</summary>
    DockerContext,

    /// <summary><c>DOCKER_HOST</c> in the environment. Overrides the current context, as the CLI does.</summary>
    Environment,

    /// <summary>A well-known socket Dray probed for, not declared anywhere.</summary>
    Discovered,

    /// <summary>A WSL2 distribution running its own engine.</summary>
    WslDistro,

    /// <summary>Apple's <c>container</c> runtime, found on PATH rather than declared anywhere.</summary>
    AppleContainer,
}

/// <summary>
/// The connection lifecycle for one host.
/// <para>
/// Explicit states rather than a bool, because a dead SSH host must degrade one sidebar entry
/// instead of hanging the app — the exit criterion for this phase.
/// </para>
/// </summary>
public enum HostConnectionState
{
    /// <summary>Known about, never attempted.</summary>
    Disconnected,

    Connecting,

    /// <summary>Engine answered and the event stream is live.</summary>
    Connected,

    /// <summary>
    /// Engine answered but something is impaired — the event stream dropped and is retrying, so
    /// data on screen is real but may be going stale.
    /// </summary>
    Degraded,

    /// <summary>Cannot reach the engine. Rows for this host render dimmed and marked stale.</summary>
    Unreachable,
}

/// <summary>What an engine supports. Probed, never assumed.</summary>
/// <remarks>
/// Podman's Docker-compatible socket, a rootless daemon and an old NAS engine each answer a
/// different subset of the API. Screens ask the capability and explain what is missing rather than
/// surfacing an exception (docs/ARCHITECTURE.md section 2.1).
/// </remarks>
public sealed record RuntimeCapabilities
{
    public static readonly RuntimeCapabilities None = new();

    /// <summary>Engine API version, e.g. "1.45".</summary>
    public string? ApiVersion { get; init; }

    /// <summary>Server version, e.g. "27.3.1" — or podman's version when it is impersonating.</summary>
    public string? EngineVersion { get; init; }

    /// <summary>What is actually serving: Docker, Podman, or something else Docker-compatible.</summary>
    public EngineFlavor Flavor { get; init; } = EngineFlavor.Unknown;

    public string? OperatingSystem { get; init; }

    public string? Architecture { get; init; }

    /// <summary><c>docker compose</c> is available for this host.</summary>
    public bool SupportsCompose { get; init; }

    /// <summary>BuildKit / buildx is available.</summary>
    public bool SupportsBuildKit { get; init; }

    /// <summary>Per-container stats streaming. Absent on some compatible engines.</summary>
    public bool SupportsStats { get; init; } = true;

    /// <summary>The <c>/events</c> stream. Without it Dray must fall back to polling and say so.</summary>
    public bool SupportsEvents { get; init; } = true;

    /// <summary>
    /// Pause and resume. Absent on Apple's runtime, where a container is a VM with no freezer.
    /// <para>
    /// A capability rather than a caught exception, because the alternative is a Pause button that
    /// always fails. ContainerAction.cs is explicit about why that is worse than no button:
    /// "an action offered in the wrong state teaches the user the UI is guessing".
    /// </para>
    /// </summary>
    public bool SupportsPause { get; init; } = true;

    /// <summary>
    /// A shell Dray can attach to. Apple's <c>exec</c> is a terminal command rather than a stream,
    /// so the Shell tab explains itself instead of opening a terminal that cannot take input.
    /// </summary>
    public bool SupportsShell { get; init; } = true;

    /// <summary>
    /// Renaming a container. Apple identifies one <i>by</i> its name, so a rename there means
    /// recreating it — a different operation with different consequences.
    /// </summary>
    public bool SupportsRename { get; init; } = true;

    /// <summary>
    /// Engine-managed volumes. Without them the Volumes page says the engine has no such concept,
    /// rather than showing an empty list that reads as "none yet".
    /// </summary>
    public bool SupportsVolumes { get; init; } = true;

    /// <summary>
    /// Networks Dray can list and manage. Apple attaches a network at creation and exposes nothing
    /// to manage afterwards.
    /// </summary>
    public bool SupportsNetworks { get; init; } = true;

    /// <summary>
    /// Reading a stopped container's filesystem. Docker's archive endpoint can; Apple's runtime
    /// needs the container running, so the Files tab says so rather than showing an empty root.
    /// </summary>
    public bool SupportsStoppedFileAccess { get; init; } = true;

    /// <summary>
    /// Whether log lines arrive with a timestamp and a stream attached.
    /// <para>
    /// Apple's <c>container logs</c> has no timestamps flag and merges stdout and stderr into one
    /// stream, so both facts are simply absent. One flag rather than two because they come from
    /// the same limitation: the CLI hands over plain text and nothing about it.
    /// </para>
    /// </summary>
    public bool SupportsLogMetadata { get; init; } = true;

    /// <summary>
    /// Whether images can be written to and read from a tar archive. Both engines Dray speaks to
    /// can; a remote host reached over a socket still can, because the archive is streamed rather
    /// than written by the engine to a path it can see.
    /// </summary>
    public bool SupportsImageArchive { get; init; } = true;

    /// <summary>
    /// Whether the engine will search a registry on Dray's behalf. Docker and podman both do;
    /// Apple's runtime has no search subcommand, so its images are pulled by name or not at all.
    /// </summary>
    public bool SupportsRegistrySearch { get; init; } = true;

    /// <summary>The daemon is running rootless, which changes what ports and mounts are possible.</summary>
    public bool IsRootless { get; init; }

    /// <summary>Swarm is active. Dray does not manage Swarm, but says so rather than showing a confusing view.</summary>
    public bool SwarmActive { get; init; }

    public int? TotalCpus { get; init; }

    public long? TotalMemoryBytes { get; init; }
}

public enum EngineFlavor
{
    Unknown,
    Docker,
    Podman,

    /// <summary>Apple's containerization framework. Not Docker-compatible in any way.</summary>
    Apple,
}

/// <summary>
/// One engine Dray can talk to, local or remote, and everything known about it.
/// <para>
/// The host picker is the first control in the sidebar, and every screen behind it works the same
/// whichever of these is selected. That is the product's whole positioning, so a remote host is
/// not a variant of this type — it is the same type with a different endpoint.
/// </para>
/// </summary>
public sealed record DockerHost
{
    /// <summary>Stable identity. The context name where there is one.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required DockerEndpoint Endpoint { get; init; }

    public HostOrigin Origin { get; init; } = HostOrigin.DockerContext;

    /// <summary>Context description, or the distro name for WSL2.</summary>
    public string? Description { get; init; }

    /// <summary>True for the context the Docker CLI would use right now.</summary>
    public bool IsCurrent { get; init; }

    public HostConnectionState State { get; init; } = HostConnectionState.Disconnected;

    /// <summary>Why the host is unreachable, in plain language. Null when it is not.</summary>
    public string? StateDetail { get; init; }

    public RuntimeCapabilities Capabilities { get; init; } = RuntimeCapabilities.None;

    /// <summary>Rows sourced from this host are stale rather than wrong, so they dim.</summary>
    public bool IsStale => State is HostConnectionState.Unreachable;

    /// <summary>What the picker shows under the name.</summary>
    public string Subtitle => StateDetail ?? Endpoint.Display;
}
