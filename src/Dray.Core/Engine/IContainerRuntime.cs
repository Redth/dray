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
    /// <summary>
    /// The engine could not tell us.
    /// <para>
    /// Distinct from a measured zero, and the distinction matters: "0 B reclaimable" invites the
    /// user to stop looking, while "unknown" invites them to look elsewhere. Not every engine
    /// implements <c>system df</c>, so this is a real state rather than a defensive one.
    /// </para>
    /// </summary>
    public static readonly DiskUsage Unknown = new(-1, -1, -1, -1, -1, -1) { IsKnown = false };

    public bool IsKnown { get; init; } = true;

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

    /// <summary>
    /// Create a container from an image and, unless told otherwise, start it.
    /// <para>
    /// Returns the new container's id. Its own method rather than a <see cref="ContainerAction"/>
    /// for the reason <see cref="RenameAsync"/> is: every action there takes only an id, and this
    /// one carries a whole request.
    /// </para>
    /// <para>
    /// The container appears in the list through the event stream — or the poll — like any other,
    /// so nothing writes it to the store here.
    /// </para>
    /// </summary>
    Task<string> RunAsync(RunRequest request, CancellationToken ct = default);

    /// <summary>
    /// Perform one action on one container.
    /// <para>
    /// Returning does not mean the container has reached the new state — it means the engine
    /// accepted the request. What actually happened arrives on the event stream, which is the
    /// same path an action taken in a terminal takes.
    /// </para>
    /// </summary>
    Task PerformAsync(string containerId, ContainerAction action, CancellationToken ct = default);

    /// <summary>
    /// Stream a container's output.
    /// <para>
    /// Ends when <paramref name="ct"/> fires, or — when not following — once the history has been
    /// delivered. A container that stops while being followed ends the stream too, which is a
    /// normal completion rather than an error.
    /// </para>
    /// </summary>
    IAsyncEnumerable<LogLine> StreamLogsAsync(string containerId, LogOptions options, CancellationToken ct = default);

    /// <summary>
    /// List one directory inside a container.
    /// <para>
    /// The Engine API has no listing endpoint, so implementations build this out of what is
    /// available. <see cref="DirectoryListing.Method"/> records which route was taken, because the
    /// two have different costs and the UI sometimes needs to say so.
    /// </para>
    /// </summary>
    Task<DirectoryListing> ListDirectoryAsync(string containerId, string path, bool containerIsRunning, CancellationToken ct = default);

    /// <summary>Read one file's bytes. Works on a stopped container.</summary>
    Task<byte[]> ReadFileAsync(string containerId, string path, CancellationToken ct = default);

    /// <summary>Write one file back, preserving its mode. Works on a stopped container.</summary>
    Task WriteFileAsync(string containerId, string path, byte[] content, CancellationToken ct = default);

    /// <summary>
    /// Give a container a different name.
    /// <para>
    /// Its own method rather than a <see cref="ContainerAction"/>: every other action takes only an
    /// id, and threading a payload through that enum would make six actions carry a parameter that
    /// only one of them uses.
    /// </para>
    /// </summary>
    Task RenameAsync(string containerId, string name, CancellationToken ct = default);

    /// <summary>
    /// Everything the engine knows about one container.
    /// <para>
    /// Separate from the list call because it is a different shape and a different cost: the list
    /// endpoint returns a summary for every container, this returns the whole record for one.
    /// </para>
    /// </summary>
    Task<ContainerInspect> InspectContainerAsync(string containerId, CancellationToken ct = default);

    Task<IReadOnlyList<ImageSummary>> ListImagesAsync(bool includeDangling = true, CancellationToken ct = default);

    /// <summary>An image's layers, newest first.</summary>
    Task<IReadOnlyList<ImageLayer>> GetImageHistoryAsync(string imageId, CancellationToken ct = default);

    /// <summary>Delete an image. <paramref name="force"/> removes it even when a container uses it.</summary>
    Task RemoveImageAsync(string imageId, bool force = false, CancellationToken ct = default);

    /// <summary>Point another tag at an existing image.</summary>
    Task TagImageAsync(string imageId, string repository, string tag, CancellationToken ct = default);

    /// <summary>
    /// Pull an image, reporting progress as the engine reports it — per layer, out of order.
    /// </summary>
    IAsyncEnumerable<PullProgress> PullImageAsync(string reference, CancellationToken ct = default);

    /// <summary>
    /// Push an image to its registry, reporting progress per layer as the engine reports it.
    /// <para>
    /// The credential is passed in rather than looked up here: the runtime's job is to talk to the
    /// engine, and reading a secret out of the system store is a different concern that
    /// docs/CREDENTIALS.md keeps in one place. Null pushes anonymously, which works for a local
    /// registry and fails clearly everywhere else.
    /// </para>
    /// </summary>
    IAsyncEnumerable<PullProgress> PushImageAsync(
        string reference, RegistryCredential? credential, CancellationToken ct = default);

    /// <summary>
    /// Search the registry for repositories matching a term.
    /// <para>
    /// Through the engine rather than over HTTP from here: the engine already holds the registry
    /// configuration and the credentials, and Dray reaching Docker Hub directly would be a second
    /// network path with its own proxy settings, its own TLS trust and its own way to be wrong.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<ImageSearchResult>> SearchImagesAsync(
        string term, int limit = 25, CancellationToken ct = default);

    /// <summary>
    /// Write an image to a tar archive on this machine.
    /// <para>
    /// The archive is the engine's own format, and the two engines do not agree on it: Docker
    /// writes a docker-archive and Apple's <c>container</c> writes an OCI layout. Dray does not
    /// convert between them, so an archive is reliably loadable by the kind of engine that wrote
    /// it and not promised anywhere else.
    /// </para>
    /// </summary>
    /// <param name="progress">Bytes written so far, for an image that takes a while.</param>
    Task SaveImageAsync(
        string reference, string destinationPath, IProgress<long>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Load images from a tar archive, returning what the engine says it loaded.
    /// <para>
    /// An empty list means the engine accepted the archive and named nothing — usually an archive
    /// with no tags in it. It is not an error, and it is not success worth reporting as "loaded
    /// nginx".
    /// </para>
    /// </summary>
    Task<IReadOnlyList<string>> LoadImageAsync(string archivePath, CancellationToken ct = default);

    /// <summary>
    /// Build an image from a directory containing a Dockerfile, streaming the engine's output.
    /// <para>
    /// The context is tarred and sent to the engine, which is why this takes a directory rather
    /// than a file: a Dockerfile that copies anything needs its neighbours too.
    /// </para>
    /// </summary>
    IAsyncEnumerable<BuildProgress> BuildImageAsync(BuildRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken ct = default);

    Task CreateNetworkAsync(NetworkRequest request, CancellationToken ct = default);

    Task RemoveNetworkAsync(string networkId, CancellationToken ct = default);

    Task ConnectNetworkAsync(string networkId, string containerId, CancellationToken ct = default);

    Task DisconnectNetworkAsync(string networkId, string containerId, bool force = false, CancellationToken ct = default);

    Task<IReadOnlyList<VolumeSummary>> ListVolumesAsync(CancellationToken ct = default);

    Task CreateVolumeAsync(string name, CancellationToken ct = default);

    Task RemoveVolumeAsync(string name, bool force = false, CancellationToken ct = default);

    /// <summary>
    /// What a prune would remove, without removing it.
    /// <para>
    /// Computed from what the engine already reports rather than by asking it to dry-run, because
    /// no engine offers a dry run. PRODUCT.md requires the preview to match reality, so this errs
    /// toward naming exactly what the same filters would delete.
    /// </para>
    /// </summary>
    Task<PrunePreview> PreviewPruneAsync(PruneKind kind, CancellationToken ct = default);

    Task<PruneResult> PruneAsync(PruneKind kind, CancellationToken ct = default);

    /// <summary>
    /// Open a volume for browsing.
    /// <para>
    /// A volume has no filesystem API of its own — the engine only ever exposes storage through a
    /// container — so implementations mount it into one. The session owns whatever it created and
    /// must clean it up on disposal.
    /// </para>
    /// </summary>
    Task<IVolumeSession> OpenVolumeAsync(string volumeName, CancellationToken ct = default);

    /// <summary>
    /// Start an interactive process inside a running container.
    /// <para>
    /// Throws <see cref="NoShellException"/> when the container is not running or the image has no
    /// shell — both ordinary outcomes with an explanation attached, not faults.
    /// </para>
    /// </summary>
    Task<IExecSession> StartExecAsync(string containerId, ExecOptions options, CancellationToken ct = default);

    /// <summary>
    /// Sample a container's resource use until cancelled.
    /// <para>
    /// Streamed, not polled: the engine already emits a sample a second. Ends on its own when the
    /// container stops, which is a normal completion rather than an error.
    /// </para>
    /// </summary>
    IAsyncEnumerable<ContainerStats> StreamStatsAsync(string containerId, CancellationToken ct = default);

    Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default);

    Task<DiskUsage> GetDiskUsageAsync(CancellationToken ct = default);

    /// <summary>
    /// The engine's event stream. Completes only when <paramref name="ct"/> fires; a dropped
    /// connection surfaces as an exception so the pump can decide to reconnect.
    /// </summary>
    IAsyncEnumerable<RuntimeEvent> WatchEventsAsync(CancellationToken ct = default);
}

/// <summary>
/// A volume, open for browsing.
/// <para>
/// Holds engine-side resources for as long as it lives, so it is scoped to one browsing session
/// and disposed when the user navigates away. Paths are relative to the volume's own root: the
/// caller asks for <c>/data</c> and never learns where the session mounted it.
/// </para>
/// </summary>
public interface IVolumeSession : IAsyncDisposable
{
    string VolumeName { get; }

    Task<DirectoryListing> ListDirectoryAsync(string path, CancellationToken ct = default);

    Task<byte[]> ReadFileAsync(string path, CancellationToken ct = default);

    Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default);
}

/// <summary>
/// Could not reach or use the engine. Carries a message written for the user rather than a stack
/// trace — "answer the question in the first screen" applies to failures too.
/// </summary>
public sealed class RuntimeConnectionException(string message, Exception? inner = null)
    : Exception(message, inner);
