using Dray.Core.Model;

namespace Dray.Core.Engine;

/// <summary>What kind of thing an event happened to.</summary>
public enum RuntimeEntity
{
    Container,
    Image,
    Volume,
    Network,
    Daemon,
}

/// <summary>
/// One change reported by the engine.
/// <para>
/// This is the app's heartbeat. PRODUCT.md: the event stream is the source of truth, and there is
/// no poll loop that redraws a whole list.
/// </para>
/// </summary>
/// <param name="Entity">What changed.</param>
/// <param name="Action">Engine action verb: create, start, die, destroy, health_status, and so on.</param>
/// <param name="Id">Full id of the subject.</param>
/// <param name="Attributes">Engine-supplied labels, including <c>name</c> and compose labels.</param>
/// <param name="Timestamp">When the engine says it happened.</param>
public sealed record RuntimeEvent(
    RuntimeEntity Entity,
    string Action,
    string Id,
    IReadOnlyDictionary<string, string> Attributes,
    DateTimeOffset Timestamp)
{
    public string? Name => Attributes.GetValueOrDefault("name");

    /// <summary>Compose project, from <c>com.docker.compose.project</c>.</summary>
    public string? ComposeProject => Attributes.GetValueOrDefault("com.docker.compose.project");

    /// <summary>
    /// True when this event means the subject no longer exists, so the store removes rather than
    /// refreshes it.
    /// </summary>
    public bool IsRemoval => Action is "destroy" or "remove" or "delete" or "untag";
}

/// <summary>Engine-wide totals for the dashboard.</summary>
public sealed record SystemInfo(
    int ContainersRunning,
    int ContainersPaused,
    int ContainersStopped,
    int Images,
    string? Name,
    string? ServerVersion);

/// <summary>What <c>system df</c> reports, for the disk-usage breakdown.</summary>
public sealed record DiskUsage(
    long ImagesBytes,
    long ImagesReclaimableBytes,
    long ContainersBytes,
    long VolumesBytes,
    long VolumesReclaimableBytes,
    long BuildCacheBytes)
{
    public long TotalBytes => ImagesBytes + ContainersBytes + VolumesBytes + BuildCacheBytes;

    public long ReclaimableBytes => ImagesReclaimableBytes + VolumesReclaimableBytes + BuildCacheBytes;
}

/// <summary>
/// One engine, behind an interface.
/// <para>
/// This is the seam PRODUCT.md describes: <c>Dray.Docker</c> is one implementation, and Apple's
/// <c>container</c> framework can land later as another without touching a page. Nothing above
/// this interface knows what is serving.
/// </para>
/// </summary>
public interface IContainerRuntime : IAsyncDisposable
{
    /// <summary>Probed on connect. Screens ask this rather than assuming (docs/ARCHITECTURE.md 2.1).</summary>
    RuntimeCapabilities Capabilities { get; }

    /// <summary>Reach the engine and probe it. Throws <see cref="RuntimeConnectionException"/> on failure.</summary>
    Task<RuntimeCapabilities> ConnectAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(bool includeStopped = true, CancellationToken ct = default);

    Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default);

    Task<DiskUsage> GetDiskUsageAsync(CancellationToken ct = default);

    /// <summary>
    /// The engine's event stream. Completes only when <paramref name="ct"/> fires; a dropped
    /// connection surfaces as an exception so the pump can decide to reconnect.
    /// </summary>
    IAsyncEnumerable<RuntimeEvent> WatchEventsAsync(CancellationToken ct = default);
}

/// <summary>
/// Could not reach or use the engine. Carries a message written for the user rather than a stack
/// trace — "answer the question in the first screen" applies to failures too.
/// </summary>
public sealed class RuntimeConnectionException(string message, Exception? inner = null)
    : Exception(message, inner);
