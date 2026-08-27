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

    public RuntimeCapabilities Capabilities { get; private set; } = RuntimeCapabilities.None;

    DockerClient Client => _client
        ?? throw new InvalidOperationException("ConnectAsync must be called before using the runtime.");

    public async Task<RuntimeCapabilities> ConnectAsync(CancellationToken ct = default)
    {
        _client?.Dispose();
        _client = DockerClientFactory.Create(endpoint);

        try
        {
            var version = await _client.System.GetVersionAsync(ct).ConfigureAwait(false);
            var info = await _client.System.GetSystemInfoAsync(ct).ConfigureAwait(false);

            Capabilities = Probe(version, info);
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

            // Podman's compat stats endpoint exists but is partial.
            SupportsStats = flavor == EngineFlavor.Docker,
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

        return [.. responses.Select(Map)];
    }

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
        Since = c.Created == default ? null : new DateTimeOffset(c.Created, TimeSpan.Zero),
        Ports = MapPorts(c.Ports),
        Stack = c.Labels is not null && c.Labels.TryGetValue("com.docker.compose.project", out var project) ? project : null,
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

    public Task<DiskUsage> GetDiskUsageAsync(CancellationToken ct = default)
        // Docker.DotNet.Enhanced does not expose `system df`. Phase 4 needs the real breakdown for
        // the prune preview, and will call the endpoint directly; reporting zeroes now is honest
        // in that the dashboard shows nothing rather than a fabricated number.
        => Task.FromResult(new DiskUsage(0, 0, 0, 0, 0, 0));

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
        _client = null;
        return ValueTask.CompletedTask;
    }
}
