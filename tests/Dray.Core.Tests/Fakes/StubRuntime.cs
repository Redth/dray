using System.Runtime.CompilerServices;
using Dray.Core.Engine;
using Dray.Core.Model;

namespace Dray.Core.Tests.Fakes;

/// <summary>
/// An engine that does nothing, for tests to override the one part they care about.
/// <para>
/// <see cref="IContainerRuntime"/> is wide because a container engine is wide, and every fake that
/// implemented it directly had to restate the whole surface — so adding one method to the seam
/// broke every test file at once, for reasons unrelated to any of them. Everything here is virtual
/// and inert; a test overrides what it is actually about, and the rest stays out of the way.
/// </para>
/// </summary>
public abstract class StubRuntime : IContainerRuntime
{
    public virtual RuntimeCapabilities Capabilities { get; protected set; } = RuntimeCapabilities.None;

    public virtual Task<RuntimeCapabilities> ConnectAsync(CancellationToken ct = default)
        => Task.FromResult(Capabilities);

    public virtual Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(bool includeStopped = true, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ContainerSummary>>([]);

    public virtual Task<string> RunAsync(RunRequest request, CancellationToken ct = default)
        => Task.FromResult("stub");

    public virtual IAsyncEnumerable<PullProgress> PushImageAsync(
        string reference, RegistryCredential? credential, CancellationToken ct = default)
        => AsyncEnumerable.Empty<PullProgress>();

    public virtual Task PerformAsync(string containerId, ContainerAction action, CancellationToken ct = default)
        => Task.CompletedTask;

    public virtual IAsyncEnumerable<LogLine> StreamLogsAsync(string containerId, LogOptions options, CancellationToken ct = default)
        => AsyncEnumerable.Empty<LogLine>();

    public virtual Task<DirectoryListing> ListDirectoryAsync(string containerId, string path, bool containerIsRunning, CancellationToken ct = default)
        => Task.FromResult(new DirectoryListing(path, [], ListingMethod.Exec));

    public virtual Task<byte[]> ReadFileAsync(string containerId, string path, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());

    public virtual Task WriteFileAsync(string containerId, string path, byte[] content, CancellationToken ct = default)
        => Task.CompletedTask;

    public virtual Task RenameAsync(string containerId, string name, CancellationToken ct = default)
        => Task.CompletedTask;

    public virtual Task<ContainerInspect> InspectContainerAsync(string containerId, CancellationToken ct = default)
        => Task.FromResult(new ContainerInspect
        {
            Id = containerId,
            Name = containerId,
            Image = "stub",
            State = DockerState.Running,
        });

    public virtual Task<IReadOnlyList<ImageSummary>> ListImagesAsync(bool includeDangling = true, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ImageSummary>>([]);

    public virtual Task<IReadOnlyList<ImageLayer>> GetImageHistoryAsync(string imageId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ImageLayer>>([]);

    public virtual Task RemoveImageAsync(string imageId, bool force = false, CancellationToken ct = default)
        => Task.CompletedTask;

    public virtual Task TagImageAsync(string imageId, string repository, string tag, CancellationToken ct = default)
        => Task.CompletedTask;

    public virtual Task SaveImageAsync(
        string reference, string destinationPath, IProgress<long>? progress = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public virtual Task<IReadOnlyList<string>> LoadImageAsync(string archivePath, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public virtual IAsyncEnumerable<PullProgress> PullImageAsync(string reference, CancellationToken ct = default)
        => AsyncEnumerable.Empty<PullProgress>();

    public virtual IAsyncEnumerable<BuildProgress> BuildImageAsync(BuildRequest request, CancellationToken ct = default)
        => AsyncEnumerable.Empty<BuildProgress>();

    public virtual Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<NetworkSummary>>([]);

    public virtual Task CreateNetworkAsync(NetworkRequest request, CancellationToken ct = default)
        => Task.CompletedTask;

    public virtual Task RemoveNetworkAsync(string networkId, CancellationToken ct = default)
        => Task.CompletedTask;

    public virtual Task ConnectNetworkAsync(string networkId, string containerId, CancellationToken ct = default)
        => Task.CompletedTask;

    public virtual Task DisconnectNetworkAsync(string networkId, string containerId, bool force = false, CancellationToken ct = default)
        => Task.CompletedTask;

    public virtual Task<IReadOnlyList<VolumeSummary>> ListVolumesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<VolumeSummary>>([]);

    public virtual Task CreateVolumeAsync(string name, CancellationToken ct = default)
        => Task.CompletedTask;

    public virtual Task RemoveVolumeAsync(string name, bool force = false, CancellationToken ct = default)
        => Task.CompletedTask;

    public virtual Task<PrunePreview> PreviewPruneAsync(PruneKind kind, CancellationToken ct = default)
        => Task.FromResult(PrunePreview.Empty(kind));

    public virtual Task<PruneResult> PruneAsync(PruneKind kind, CancellationToken ct = default)
        => Task.FromResult(new PruneResult(kind, 0, 0));

    public virtual Task<IVolumeSession> OpenVolumeAsync(string volumeName, CancellationToken ct = default)
        => Task.FromResult<IVolumeSession>(new StubVolumeSession(volumeName));

    public virtual Task<IExecSession> StartExecAsync(string containerId, ExecOptions options, CancellationToken ct = default)
        => throw new NoShellException("This stub runs nothing.");

    public virtual IAsyncEnumerable<ContainerStats> StreamStatsAsync(string containerId, CancellationToken ct = default)
        => AsyncEnumerable.Empty<ContainerStats>();

    public virtual Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default)
        => Task.FromResult(new SystemInfo(0, 0, 0, 0, null, null));

    public virtual Task<DiskUsage> GetDiskUsageAsync(CancellationToken ct = default)
        => Task.FromResult(DiskUsage.Unknown);

    public virtual async IAsyncEnumerable<RuntimeEvent> WatchEventsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Never produces, never completes on its own — the shape of a healthy event stream.
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        yield break;
    }

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>An empty volume, for tests that only need a session to exist.</summary>
public sealed class StubVolumeSession(string volumeName) : IVolumeSession
{
    public string VolumeName { get; } = volumeName;

    public bool Disposed { get; private set; }

    public Task<DirectoryListing> ListDirectoryAsync(string path, CancellationToken ct = default)
        => Task.FromResult(new DirectoryListing(path, [], ListingMethod.Archive));

    public Task<byte[]> ReadFileAsync(string path, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default)
        => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
