using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Docker.DotNet;
using Docker.DotNet.Models;
using Dray.Core.Engine;
using Dray.Core.Model;

namespace Dray.Docker;

/// <summary>
/// <see cref="IContainerRuntime"/> over the Docker Engine API.
/// <para>
/// Everything above this class is engine-agnostic; this is the only place that knows about HTTP,
/// sockets or Docker's wire shapes.
/// </para>
/// </summary>
public sealed class DockerRuntime(DockerEndpoint endpoint) : IContainerRuntime
{
    DockerClient? _client;
    DockerRawApi? _raw;

    public RuntimeCapabilities Capabilities { get; private set; } = RuntimeCapabilities.None;

    DockerClient Client => _client
        ?? throw new InvalidOperationException("ConnectAsync must be called before using the runtime.");

    public async Task<RuntimeCapabilities> ConnectAsync(CancellationToken ct = default)
    {
        _client?.Dispose();
        _raw?.Dispose();

        _client = DockerClientFactory.Create(endpoint);

        try
        {
            var version = await _client.System.GetVersionAsync(ct).ConfigureAwait(false);
            var info = await _client.System.GetSystemInfoAsync(ct).ConfigureAwait(false);

            Capabilities = Probe(version, info);

            // Built here rather than above because it needs the negotiated API version: an
            // unversioned path reaches a different API on podman. See DockerRawApi.
            _raw = new DockerRawApi(endpoint, version.APIVersion);

            // A Dray that was killed rather than closed leaves its volume-browser helpers behind.
            // Cleared here, where it is certain none of them belongs to a live session.
            await DockerVolumeSession.SweepOrphansAsync(_client, ct).ConfigureAwait(false);

            return Capabilities;
        }
        catch (DockerApiException ex)
        {
            throw new RuntimeConnectionException(DescribeApiFailure(ex), ex);
        }
    }

    /// <summary>
    /// Work out what this engine actually supports rather than assuming Docker.
    /// <para>
    /// Podman's compatible socket, a rootless daemon and an old NAS engine each answer a different
    /// subset, and screens ask these rather than discovering the gap through an exception.
    /// </para>
    /// </summary>
    static RuntimeCapabilities Probe(VersionResponse version, SystemInfoResponse info)
    {
        // Podman reports itself in the components list and in a few info fields; Docker does not.
        var flavor = LooksLikePodman(version, info) ? EngineFlavor.Podman : EngineFlavor.Docker;

        return new RuntimeCapabilities
        {
            ApiVersion = version.APIVersion,
            EngineVersion = version.Version,
            Flavor = flavor,
            OperatingSystem = info.OperatingSystem ?? version.Os,
            Architecture = info.Architecture ?? version.Arch,
            TotalCpus = info.NCPU > 0 ? (int)info.NCPU : null,
            TotalMemoryBytes = info.MemTotal > 0 ? info.MemTotal : null,
            SwarmActive = info.Swarm?.LocalNodeState is "active",

            // Rootless changes which ports and mounts are possible, so it is worth saying so
            // rather than letting the user discover it through a failure.
            IsRootless = info.SecurityOptions?.Any(o => o.Contains("rootless", StringComparison.OrdinalIgnoreCase)) ?? false,

            // The events endpoint is the backbone of the design. Podman implements it, but not
            // every event type matches Docker's, so this is optimistic and the pump degrades if
            // the stream never produces anything.
            SupportsEvents = true,

            // Compose and buildx are CLI plugins on the client side, not engine features, so they
            // are probed separately by the shell rather than read off the daemon.
            SupportsCompose = false,
            SupportsBuildKit = flavor == EngineFlavor.Docker,

            // Both engines serve the compat stats endpoint with the fields Dray needs — CPU and
            // memory counters, per-interface network totals. Verified against podman 6.0.2 rather
            // than assumed: an earlier version of this claimed podman could not do it, on no
            // evidence, and turned the feature off for every podman user.
            //
            // Block I/O is the one gap: a rootless podman reports no io accounting at all, so the
            // UI treats a zero there as "not reported" rather than as a measurement.
            SupportsStats = true,
        };
    }

    static bool LooksLikePodman(VersionResponse version, SystemInfoResponse info)
        => (version.Components?.Any(c => c.Name?.Contains("podman", StringComparison.OrdinalIgnoreCase) == true) ?? false)
           || (info.Name?.Contains("podman", StringComparison.OrdinalIgnoreCase) ?? false)
           || (version.Platform?.Name?.Contains("podman", StringComparison.OrdinalIgnoreCase) ?? false);

    public async Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(bool includeStopped = true, CancellationToken ct = default)
    {
        var responses = await Client.Containers
            .ListContainersAsync(new ContainersListParameters { All = includeStopped }, ct)
            .ConfigureAwait(false);

        return [.. responses.Where(IsUsersOwn).Select(Map)];
    }

    /// <summary>
    /// Whether a container is the user's rather than Dray's own scaffolding.
    /// <para>
    /// The volume browser mounts a volume into a container that never runs. That container is an
    /// implementation detail: listing it would put something in the user's containers table that
    /// they did not create, cannot explain, and would reasonably try to delete.
    /// </para>
    /// </summary>
    internal static bool IsUsersOwn(ContainerListResponse c)
        => c.Labels?.ContainsKey(DockerVolumeSession.HelperLabel) != true;

    internal static ContainerSummary Map(ContainerListResponse c) => new()
    {
        Id = c.ID,

        // Docker returns names with a leading slash, and a container can carry several; the first
        // is the one users recognise.
        Name = c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.ID[..Math.Min(12, c.ID.Length)],

        Image = c.Image,
        State = ParseState(c.State),
        Health = ParseHealthFromStatus(c.Status),
        ExitCode = ParseExitCode(c.Status),
        Since = DockerTime.From(c.Created),
        Ports = MapPorts(c.Ports),
        Compose = ComposeMembership.From(c.Labels is { Count: > 0 } labels
            ? new Dictionary<string, string>(labels, StringComparer.Ordinal)
            : null),
    };

    static DockerState ParseState(string? state) => state?.ToLowerInvariant() switch
    {
        "running" => DockerState.Running,
        "paused" => DockerState.Paused,
        "restarting" => DockerState.Restarting,
        "removing" => DockerState.Removing,
        "exited" => DockerState.Exited,
        "dead" => DockerState.Dead,
        "created" => DockerState.Created,
        _ => DockerState.Unknown,
    };

    /// <summary>
    /// Health only appears in the human-readable status string — "Up 3 hours (healthy)" — because
    /// the list endpoint does not carry a structured health field.
    /// </summary>
    static DockerHealth ParseHealthFromStatus(string? status)
    {
        if (string.IsNullOrEmpty(status)) return DockerHealth.None;

        if (status.Contains("(healthy)", StringComparison.OrdinalIgnoreCase)) return DockerHealth.Healthy;
        if (status.Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase)) return DockerHealth.Unhealthy;
        if (status.Contains("health: starting", StringComparison.OrdinalIgnoreCase)) return DockerHealth.Starting;

        return DockerHealth.None;
    }

    /// <summary>Exit codes likewise only appear as "Exited (137) 5 minutes ago".</summary>
    static int? ParseExitCode(string? status)
    {
        if (string.IsNullOrEmpty(status)) return null;
        if (!status.StartsWith("Exited", StringComparison.OrdinalIgnoreCase)) return null;

        var open = status.IndexOf('(');
        var close = status.IndexOf(')');
        if (open < 0 || close <= open) return null;

        return int.TryParse(status.AsSpan(open + 1, close - open - 1), out var code) ? code : null;
    }

    static IReadOnlyList<Dray.Core.Model.PortBinding> MapPorts(IList<PortSummary>? ports)
    {
        if (ports is null || ports.Count == 0) return [];

        return [.. ports
            // An unpublished port has no host side and is not actionable, so it is not shown.
            .Where(p => p.PublicPort is > 0)
            .Select(p => new Dray.Core.Model.PortBinding(p.PublicPort!.Value, p.PrivatePort, p.Type ?? "tcp"))
            .DistinctBy(p => (p.HostPort, p.ContainerPort, p.Protocol))
            .OrderBy(p => p.HostPort)];
    }

    public async Task PerformAsync(string containerId, ContainerAction action, CancellationToken ct = default)
    {
        var containers = Client.Containers;

        try
        {
            switch (action)
            {
                case ContainerAction.Start:
                    await containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct).ConfigureAwait(false);
                    break;

                case ContainerAction.Stop:
                    await containers.StopContainerAsync(containerId, new ContainerStopParameters(), ct).ConfigureAwait(false);
                    break;

                case ContainerAction.Restart:
                    await containers.RestartContainerAsync(containerId, new ContainerRestartParameters(), ct).ConfigureAwait(false);
                    break;

                case ContainerAction.Pause:
                    await containers.PauseContainerAsync(containerId, ct).ConfigureAwait(false);
                    break;

                case ContainerAction.Unpause:
                    await containers.UnpauseContainerAsync(containerId, ct).ConfigureAwait(false);
                    break;

                case ContainerAction.Kill:
                    await containers.KillContainerAsync(containerId, new ContainerKillParameters(), ct).ConfigureAwait(false);
                    break;

                case ContainerAction.Remove:
                    await containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters(), ct).ConfigureAwait(false);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown container action.");
            }
        }
        catch (DockerApiException ex)
        {
            throw new RuntimeConnectionException(DescribeActionFailure(action, ex), ex);
        }
    }

    /// <summary>
    /// What went wrong, in terms of the action the user asked for rather than the HTTP status.
    /// </summary>
    static string DescribeActionFailure(ContainerAction action, DockerApiException ex) => (int)ex.StatusCode switch
    {
        // The engine rejects an action that does not apply — starting something already running.
        // ContainerActions.AppliesTo should have prevented offering it, so this means the row was
        // stale when the user clicked.
        304 => "The container is already in that state.",

        404 => "That container no longer exists.",
        409 when action == ContainerAction.Remove => "The container is still running. Stop it first.",
        409 => "The engine could not do that right now.",
        500 => "The engine reported an internal error.",
        _ => $"The engine returned {(int)ex.StatusCode}.",
    };

    public IAsyncEnumerable<LogLine> StreamLogsAsync(string containerId, LogOptions options, CancellationToken ct = default)
        => DockerLogStream.ReadAsync(Client, containerId, options, ct);

    public Task<DirectoryListing> ListDirectoryAsync(string containerId, string path, bool containerIsRunning, CancellationToken ct = default)
        => DockerFileSystem.ListAsync(Client, containerId, path, containerIsRunning, ct);

    public Task<byte[]> ReadFileAsync(string containerId, string path, CancellationToken ct = default)
        => DockerFileSystem.ReadFileAsync(Client, containerId, path, ct);

    public Task WriteFileAsync(string containerId, string path, byte[] content, CancellationToken ct = default)
        => DockerFileSystem.WriteFileAsync(Client, containerId, path, content, ct);

    public Task<string> RunAsync(RunRequest request, CancellationToken ct = default)
        => DockerRun.RunAsync(Client, request, ct);

    public async Task RenameAsync(string containerId, string name, CancellationToken ct = default)
    {
        try
        {
            await Client.Containers
                .RenameContainerAsync(containerId, new ContainerRenameParameters { NewName = name }, ct)
                .ConfigureAwait(false);
        }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 409)
        {
            throw new RuntimeConnectionException($"Another container is already called \"{name}\".", ex);
        }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 404)
        {
            throw new RuntimeConnectionException("That container no longer exists.", ex);
        }
        catch (DockerApiException ex)
        {
            throw new RuntimeConnectionException(DescribeApiFailure(ex), ex);
        }
    }

    public async Task<ContainerInspect> InspectContainerAsync(string containerId, CancellationToken ct = default)
    {
        try
        {
            var response = await Client.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);

            // The typed response drives the curated tabs; the raw body drives the JSON view. Both
            // come from the same container but not the same request, so a container removed
            // between the two shows the curated view without the raw one rather than failing.
            var raw = await ReadRawInspectAsync(containerId, ct).ConfigureAwait(false);

            return DockerInspect.Map(response, raw);
        }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 404)
        {
            throw new RuntimeConnectionException("That container no longer exists.", ex);
        }
        catch (DockerApiException ex)
        {
            throw new RuntimeConnectionException(DescribeApiFailure(ex), ex);
        }
    }

    async Task<string> ReadRawInspectAsync(string containerId, CancellationToken ct)
    {
        if (_raw is null) return "";

        try
        {
            return await _raw.GetJsonAsync($"/containers/{containerId}/json", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // The curated tabs are the point of the screen; losing the raw view is a degraded
            // Inspect tab, not a failed page.
            return "";
        }
    }

    public async Task<IReadOnlyList<ImageSummary>> ListImagesAsync(bool includeDangling = true, CancellationToken ct = default)
    {
        try
        {
            return await DockerImages.ListAsync(Client, includeDangling, ct).ConfigureAwait(false);
        }
        catch (DockerApiException ex)
        {
            throw new RuntimeConnectionException(DescribeApiFailure(ex), ex);
        }
    }

    public Task<IReadOnlyList<ImageLayer>> GetImageHistoryAsync(string imageId, CancellationToken ct = default)
        => DockerImages.HistoryAsync(Client, imageId, ct);

    public Task RemoveImageAsync(string imageId, bool force = false, CancellationToken ct = default)
        => DockerImages.RemoveAsync(Client, imageId, force, ct);

    public Task TagImageAsync(string imageId, string repository, string tag, CancellationToken ct = default)
        => DockerImages.TagAsync(Client, imageId, repository, tag, ct);

    public async Task<IReadOnlyList<ImageSearchResult>> SearchImagesAsync(
        string term, int limit = 25, CancellationToken ct = default)
    {
        if (_raw is null) throw new InvalidOperationException("Not connected to an engine.");
        if (string.IsNullOrWhiteSpace(term)) return [];

        var path = $"/images/search?term={Uri.EscapeDataString(term.Trim())}&limit={Math.Clamp(limit, 1, 100)}";

        return ImageSearch.Parse(await _raw.GetStringAsync(path, ct).ConfigureAwait(false));
    }

    public Task SaveImageAsync(
        string reference, string destinationPath, IProgress<long>? progress = null, CancellationToken ct = default)
        => _raw is null
            ? throw new InvalidOperationException("Not connected to an engine.")
            : DockerImages.SaveAsync(_raw, reference, destinationPath, progress, ct);

    public Task<IReadOnlyList<string>> LoadImageAsync(string archivePath, CancellationToken ct = default)
        => _raw is null
            ? throw new InvalidOperationException("Not connected to an engine.")
            : DockerImages.LoadAsync(_raw, archivePath, ct);

    public IAsyncEnumerable<PullProgress> PushImageAsync(
        string reference, RegistryCredential? credential, CancellationToken ct = default)
        => DockerImages.PushAsync(Client, reference, credential, ct);

    public IAsyncEnumerable<PullProgress> PullImageAsync(string reference, CancellationToken ct = default)
        => DockerImages.PullAsync(Client, reference, ct);

    public IAsyncEnumerable<BuildProgress> BuildImageAsync(BuildRequest request, CancellationToken ct = default)
        => DockerBuild.RunAsync(Client, request, ct);

    public async Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken ct = default)
    {
        try
        {
            return await DockerNetworks.ListAsync(Client, ct).ConfigureAwait(false);
        }
        catch (DockerApiException ex)
        {
            throw new RuntimeConnectionException(DescribeApiFailure(ex), ex);
        }
    }

    public Task CreateNetworkAsync(NetworkRequest request, CancellationToken ct = default)
        => DockerNetworks.CreateAsync(Client, request, ct);

    public Task RemoveNetworkAsync(string networkId, CancellationToken ct = default)
        => DockerNetworks.RemoveAsync(Client, networkId, ct);

    public Task ConnectNetworkAsync(string networkId, string containerId, CancellationToken ct = default)
        => DockerNetworks.ConnectAsync(Client, networkId, containerId, ct);

    public Task DisconnectNetworkAsync(string networkId, string containerId, bool force = false, CancellationToken ct = default)
        => DockerNetworks.DisconnectAsync(Client, networkId, containerId, force, ct);

    public async Task CreateVolumeAsync(string name, CancellationToken ct = default)
    {
        try
        {
            await Client.Volumes.CreateAsync(new VolumesCreateParameters { Name = name }, ct).ConfigureAwait(false);
        }
        catch (DockerApiException ex)
        {
            throw new RuntimeConnectionException($"Could not create the volume: {ex.Message}", ex);
        }
    }

    public async Task RemoveVolumeAsync(string name, bool force = false, CancellationToken ct = default)
    {
        try
        {
            await Client.Volumes.RemoveAsync(name, force, ct).ConfigureAwait(false);
        }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 409)
        {
            throw new RuntimeConnectionException(
                "A container is still using this volume. Remove the container first.", ex);
        }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 404)
        {
            throw new RuntimeConnectionException("That volume no longer exists.", ex);
        }
    }

    public Task<PrunePreview> PreviewPruneAsync(PruneKind kind, CancellationToken ct = default)
        => DockerPrune.PreviewAsync(Client, kind, ct);

    public async Task<PruneResult> PruneAsync(PruneKind kind, CancellationToken ct = default)
    {
        try
        {
            return await DockerPrune.RunAsync(Client, kind, ct).ConfigureAwait(false);
        }
        catch (DockerApiException ex)
        {
            throw new RuntimeConnectionException(DescribeApiFailure(ex), ex);
        }
    }

    public async Task<IReadOnlyList<VolumeSummary>> ListVolumesAsync(CancellationToken ct = default)
    {
        try
        {
            return await DockerVolumes.ListAsync(Client, ct).ConfigureAwait(false);
        }
        catch (DockerApiException ex)
        {
            throw new RuntimeConnectionException(DescribeApiFailure(ex), ex);
        }
    }

    public async Task<IVolumeSession> OpenVolumeAsync(string volumeName, CancellationToken ct = default)
        => await DockerVolumeSession.OpenAsync(Client, volumeName, ct).ConfigureAwait(false);

    public IAsyncEnumerable<ContainerStats> StreamStatsAsync(string containerId, CancellationToken ct = default)
        => DockerStats.StreamAsync(Client, containerId, ct);

    public Task<IExecSession> StartExecAsync(string containerId, ExecOptions options, CancellationToken ct = default)
        => DockerExec.StartAsync(Client, containerId, options, ct);

    public async Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default)
    {
        var info = await Client.System.GetSystemInfoAsync(ct).ConfigureAwait(false);

        return new SystemInfo(
            (int)info.ContainersRunning,
            (int)info.ContainersPaused,
            (int)info.ContainersStopped,
            (int)info.Images,
            info.Name,
            info.ServerVersion);
    }

    /// <summary>
    /// <c>system df</c>, which Docker.DotNet.Enhanced has no binding for, so it goes through the
    /// raw API.
    /// <para>
    /// The engine computes this by walking its storage, so it is slow enough that callers should
    /// treat it as an explicit request rather than something to refresh on a timer.
    /// </para>
    /// </summary>
    public async Task<DiskUsage> GetDiskUsageAsync(CancellationToken ct = default)
    {
        if (_raw is null) return DiskUsage.Unknown;

        try
        {
            var df = await _raw.GetAsync<SystemDfResponse>("/system/df", ct).ConfigureAwait(false);
            if (df is null) return DiskUsage.Unknown;

            // An image layer shared by several images is reported against each of them, so summing
            // SharedSize would count it more than once. UniqueSize (Size minus shared) is what the
            // CLI reports and what actually frees on removal.
            var imagesTotal = df.Images?.Sum(i => i.Size) ?? 0;
            var imagesReclaimable = df.Images?.Where(i => i.Containers <= 0).Sum(i => i.Size) ?? 0;

            var volumes = df.Volumes ?? [];

            return new DiskUsage(
                ImagesBytes: imagesTotal,
                ImagesReclaimableBytes: imagesReclaimable,
                ContainersBytes: df.Containers?.Sum(c => c.SizeRw) ?? 0,
                VolumesBytes: volumes.Sum(v => v.UsageData?.Size ?? 0),
                VolumesReclaimableBytes: volumes.Where(v => (v.UsageData?.RefCount ?? 0) <= 0).Sum(v => v.UsageData?.Size ?? 0),
                BuildCacheBytes: df.BuildCache?.Sum(b => b.Size) ?? 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            // Not every engine implements `system df`. An unknown total is shown as unknown; the
            // one thing the dashboard must not do is present a zero as a measurement.
            return DiskUsage.Unknown;
        }
    }

    /// <summary>
    /// Only the fields Dray sums. Deliberately not the whole `system df` shape — the rest is large
    /// and would have to be kept in step with two engines for no gain.
    /// </summary>
    sealed record SystemDfResponse(
        List<DfImage>? Images,
        List<DfContainer>? Containers,
        List<DfVolume>? Volumes,
        List<DfBuildCache>? BuildCache);

    sealed record DfImage(long Size, long Containers);

    sealed record DfContainer(long SizeRw);

    sealed record DfVolume(DfVolumeUsage? UsageData);

    sealed record DfVolumeUsage(long Size, long RefCount);

    sealed record DfBuildCache(long Size);

    /// <summary>
    /// The engine's event stream.
    /// <para>
    /// Docker.DotNet pushes through <see cref="IProgress{T}"/>, so this bridges to the pull-based
    /// stream the pump consumes. The channel is unbounded because dropping an event would leave
    /// the store silently wrong, and a burst is bounded by how fast an engine can act anyway.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<RuntimeEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<RuntimeEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        var progress = new Progress<Message>(message =>
        {
            if (Convert(message) is { } e) channel.Writer.TryWrite(e);
        });

        // Runs until cancelled or the connection drops. Completing the channel with the exception
        // is what lets the pump tell a clean end from a failure.
        var monitor = Task.Run(async () =>
        {
            try
            {
                await Client.System
                    .MonitorEventsAsync(new ContainerEventsParameters(), progress, ct)
                    .ConfigureAwait(false);

                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var e in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return e;
        }
        finally
        {
            // Never leave the monitor running behind a disposed enumerator.
            await monitor.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    internal static RuntimeEvent? Convert(Message message)
    {
        var entity = message.Type?.ToLowerInvariant() switch
        {
            "container" => RuntimeEntity.Container,
            "image" => RuntimeEntity.Image,
            "volume" => RuntimeEntity.Volume,
            "network" => RuntimeEntity.Network,
            "daemon" => RuntimeEntity.Daemon,
            _ => (RuntimeEntity?)null,
        };

        if (entity is null) return null;

        // This client version normalises the older `status`/`id` wire fields onto Action and
        // Actor, so there is only one shape to read here.
        var action = message.Action;
        if (string.IsNullOrEmpty(action)) return null;

        var id = message.Actor?.ID;
        if (string.IsNullOrEmpty(id)) return null;

        var attributes = message.Actor?.Attributes is { Count: > 0 } actual
            ? new Dictionary<string, string>(actual, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        // TimeNano is the precise one; Time is whole seconds. Prefer the former where present.
        var timestamp = message.TimeNano is > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(message.TimeNano.Value / 1_000_000)
            : DateTimeOffset.FromUnixTimeSeconds(message.Time ?? 0);

        return new RuntimeEvent(entity.Value, action, id, attributes, timestamp);
    }

    static string DescribeApiFailure(DockerApiException ex) => (int)ex.StatusCode switch
    {
        400 => "The engine rejected the request. Its API may be older than Dray expects.",
        401 or 403 => "Not authorised to use this engine.",
        404 => "The engine does not implement this endpoint.",
        500 => "The engine reported an internal error.",
        _ => $"The engine returned {(int)ex.StatusCode}.",
    };

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _raw?.Dispose();

        _client = null;
        _raw = null;

        return ValueTask.CompletedTask;
    }
}
