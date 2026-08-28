using System.Runtime.CompilerServices;
using System.Text.Json;
using Dray.Core.Engine;
using Dray.Core.Model;

namespace Dray.Apple;

/// <summary>
/// <see cref="IContainerRuntime"/> over Apple's <c>container</c> runtime.
/// <para>
/// The second implementation of the seam, and the reason the seam exists. Nothing above
/// <see cref="IContainerRuntime"/> changed to add it: no page, no component, no view model. What
/// this class proves is not that Apple's runtime works — it is that Dray's abstraction was drawn in
/// the right place.
/// </para>
/// <para>
/// It is <b>not</b> a Docker-compatible engine. There is no HTTP API, no socket, no shared field
/// names, and no compatibility shim. Everything here goes through the CLI with
/// <c>--format json</c>, which is the only machine-readable surface it has.
/// </para>
/// <para>
/// Three capabilities are genuinely absent, and are reported as absent rather than faked:
/// </para>
/// <list type="bullet">
/// <item><b>No event stream.</b> There is no <c>events</c> subcommand at all, so
/// <see cref="RuntimeCapabilities.SupportsEvents"/> is false and the pump polls.</item>
/// <item><b>No exit codes.</b> A stopped container reports <c>state: "stopped"</c> and nothing
/// else — checked in <c>inspect</c> too. Dray's exit-code vocabulary has nothing to work with, so
/// a container that failed and one that succeeded look the same. That is the engine's limit, and
/// inventing a zero would be worse than showing none.</item>
/// <item><b>No health checks.</b> The concept does not exist here.</item>
/// </list>
/// </summary>
public sealed class AppleRuntime(IProcessRunner? runner = null, string? executable = null) : IContainerRuntime
{
    /// <summary>The CLI's name, used when discovery did not hand over a path.</summary>
    internal const string DefaultExecutable = "container";

    readonly IProcessRunner _runner = runner ?? new SystemProcessRunner();

    /// <summary>
    /// The CLI this runtime drives — the path discovery found, or the bare name for <c>PATH</c>
    /// resolution. Two installs are two engines, and the endpoint says which one this is.
    /// </summary>
    internal string Executable { get; } = executable is { Length: > 0 } path ? path : DefaultExecutable;

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public RuntimeCapabilities Capabilities { get; private set; } = RuntimeCapabilities.None;

    public async Task<RuntimeCapabilities> ConnectAsync(CancellationToken ct = default)
    {
        var version = await RunAsync(["--version"], ct).ConfigureAwait(false);

        if (version.ExitCode != 0)
        {
            throw new RuntimeConnectionException(
                "Apple's container runtime is installed but not responding. Run `container system start`.");
        }

        // `system status` is what distinguishes "installed" from "running": the CLI answers
        // --version whether or not the API server is up.
        var status = await RunAsync(["system", "status"], ct).ConfigureAwait(false);

        if (status.ExitCode != 0 || !status.StandardOutput.Contains("running", StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeConnectionException(
                "Apple's container service is not running. Start it with `container system start`.");
        }

        Capabilities = new RuntimeCapabilities
        {
            Flavor = EngineFlavor.Apple,
            EngineVersion = ParseVersion(version.StandardOutput),
            OperatingSystem = "macOS",
            Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),

            // Every one of these was measured against the running engine, not assumed. They are
            // what the UI reads to decide what to offer, so a wrong optimistic answer here is a
            // button that always fails.
            SupportsEvents = false,
            SupportsStats = true,
            SupportsCompose = false,
            SupportsBuildKit = false,
            SupportsPause = false,
            SupportsRename = false,
            SupportsNetworks = false,
            SupportsLogMetadata = false,

            // `container exec -i` streams in both directions, and `container volume` manages
            // volumes properly. Both were reported unsupported here on the strength of a glance at
            // the subcommand list; testing them showed otherwise.
            SupportsShell = true,
            SupportsVolumes = true,

            // This one is real and was checked: `cp` and `exec` both refuse a container that is not
            // running, so a stopped container's filesystem is genuinely unreachable.
            SupportsStoppedFileAccess = false,

            // Every container is its own lightweight VM, so nothing here runs as root on the host.
            IsRootless = true,
        };

        return Capabilities;
    }

    internal static string? ParseVersion(string output)
    {
        // "container CLI version 1.3.0 (build: release, commit: unspeci)"
        const string marker = "version ";
        var at = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        var rest = output[(at + marker.Length)..].TrimStart();
        var end = rest.IndexOfAny([' ', '\n', '\r', '(']);

        return end < 0 ? rest.Trim() : rest[..end].Trim();
    }

    // ---------------------------------------------------------------- containers

    public async Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(
        bool includeStopped = true, CancellationToken ct = default)
    {
        var args = includeStopped ? new[] { "ls", "--all", "--format", "json" } : ["ls", "--format", "json"];
        var result = await RunAsync(args, ct).ConfigureAwait(false);

        if (result.ExitCode != 0) throw Failure("list containers", result);

        return [.. Deserialize<List<AppleContainer>>(result.StandardOutput).Select(Map)];
    }

    internal static ContainerSummary Map(AppleContainer c)
    {
        var config = c.Configuration;
        var status = c.Status;

        return new ContainerSummary
        {
            // Apple has no separate id, so the name is the id. Dray's short-id column shows the
            // same text as the name here rather than a hash that does not exist.
            Id = c.Id,
            Name = c.Id,

            Image = config?.Image?.Reference ?? "unknown",
            State = MapState(status?.State),

            // Deliberately null, always. There is no exit code in this engine's output; a zero
            // would read as "finished cleanly" for a container that crashed.
            ExitCode = null,

            // Health checks do not exist here.
            Health = DockerHealth.None,

            Since = status?.StartedDate ?? config?.CreationDate,
            Ports = MapPorts(config?.PublishedPorts),
            Compose = ComposeMembership.From(config?.Labels),
        };
    }

    internal static DockerState MapState(string? state) => state?.ToLowerInvariant() switch
    {
        "running" => DockerState.Running,
        "stopped" => DockerState.Exited,
        "created" => DockerState.Created,
        "stopping" => DockerState.Removing,
        _ => DockerState.Unknown,
    };

    static IReadOnlyList<PortBinding> MapPorts(List<ApplePublishedPort>? ports)
        => ports is null
            ? []
            : [.. ports.Select(p => new PortBinding(p.HostPort, p.ContainerPort, p.Proto ?? "tcp")).OrderBy(p => p.HostPort)];

    /// <summary>
    /// <c>container run -d</c>, or <c>container create</c> when the caller does not want it started.
    /// <para>
    /// The flags line up with Docker's closely enough that <see cref="RunRequest"/> needs no
    /// translation: <c>-p host:container/proto</c>, <c>-e KEY=value</c>, <c>-v source:dest</c>.
    /// </para>
    /// </summary>
    public async Task<string> RunAsync(RunRequest request, CancellationToken ct = default)
    {
        var args = new List<string> { request.Start ? "run" : "create" };

        if (request.Start) args.Add("--detach");

        if (request.Name is { Length: > 0 } name)
        {
            args.Add("--name");
            args.Add(name);
        }

        foreach (var port in request.Ports)
        {
            args.Add("--publish");
            args.Add($"{port.HostPort}:{port.ContainerPort}/{port.Protocol}");
        }

        foreach (var variable in request.Environment)
        {
            args.Add("--env");
            args.Add($"{variable.Key}={variable.Value}");
        }

        foreach (var (key, value) in request.Labels)
        {
            args.Add("--label");
            args.Add($"{key}={value}");
        }

        foreach (var mount in request.Mounts)
        {
            // No read-only suffix: the CLI's --volume takes source:destination and nothing else,
            // so a read-only mount would be silently writable. Refusing is the honest answer.
            if (mount.ReadOnly)
            {
                throw new RuntimeConnectionException(
                    "Apple's runtime cannot mount a volume read-only. Remove the :ro to continue.");
            }

            args.Add("--volume");
            args.Add($"{mount.Source}:{mount.Destination}");
        }

        args.Add(request.Image);

        var result = await RunAsync(args, ct).ConfigureAwait(false);

        if (result.ExitCode != 0) throw Failure("run that image", result);

        // Both subcommands print the new container's id, which here is its name. The output can
        // carry pull progress ahead of it, so the last non-empty line is the one that matters.
        var id = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0);

        return id ?? request.Name ?? request.Image;
    }

    public async Task PerformAsync(string containerId, ContainerAction action, CancellationToken ct = default)
    {
        string[] args = action switch
        {
            ContainerAction.Start => ["start", containerId],
            ContainerAction.Stop => ["stop", containerId],

            // No restart subcommand: it is a stop and a start, done here so the seam still offers
            // Restart rather than hiding an action this engine can perform perfectly well.
            ContainerAction.Restart => [],

            ContainerAction.Kill => ["kill", containerId],
            ContainerAction.Remove => ["delete", containerId],

            // Pause and unpause have no equivalent. ContainerActions offers them; this engine
            // cannot honour them, and saying so beats a silent no-op.
            ContainerAction.Pause or ContainerAction.Unpause =>
                throw new RuntimeConnectionException("Apple's runtime cannot pause a container."),

            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown container action."),
        };

        if (action == ContainerAction.Restart)
        {
            await PerformAsync(containerId, ContainerAction.Stop, ct).ConfigureAwait(false);
            await PerformAsync(containerId, ContainerAction.Start, ct).ConfigureAwait(false);
            return;
        }

        var result = await RunAsync(args, ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw Failure(ContainerActions.Label(action).ToLowerInvariant(), result);
    }

    public async Task RenameAsync(string containerId, string name, CancellationToken ct = default)
    {
        // The id *is* the name, so renaming would mean recreating the container under another id —
        // a different operation with different consequences, and not one to do behind a rename.
        await Task.CompletedTask.ConfigureAwait(false);

        throw new RuntimeConnectionException(
            "Apple's runtime identifies a container by its name, so it cannot be renamed. "
            + "Recreate it under the new name instead.");
    }

    public async IAsyncEnumerable<LogLine> StreamLogsAsync(
        string containerId,
        LogOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var args = new List<string> { "logs" };

        if (options.Follow) args.Add("--follow");

        if (options.Tail is { } tail)
        {
            args.Add("-n");
            args.Add(tail.ToString());
        }

        args.Add(containerId);

        // No timestamps flag exists, so every line is reported without one rather than stamped
        // with the moment Dray happened to read it — which would be a different and misleading
        // fact (see LogLine.Timestamp).
        var sequence = 0L;

        await foreach (var line in _runner.StreamAsync(Executable, args, null, ct).ConfigureAwait(false))
        {
            // Everything is StdOut, and that is the truth rather than a shortcut. `container logs`
            // merges the container's two streams into its own stdout — verified by discarding the
            // CLI's stderr and watching a line written to the container's stderr still arrive.
            // The CLI's own stderr carries the CLI's diagnostics, not the container's, so routing
            // it to LogStream.StdErr would label Dray's problems as the container's.
            if (line.IsError) continue;

            yield return new LogLine(null, LogStream.StdOut, line.Text, sequence++);
        }
    }

    public async Task<ContainerInspect> InspectContainerAsync(string containerId, CancellationToken ct = default)
    {
        var result = await RunAsync(["inspect", containerId], ct).ConfigureAwait(false);

        if (result.ExitCode != 0) throw Failure("inspect", result);

        var containers = Deserialize<List<AppleContainer>>(result.StandardOutput);
        var container = containers.FirstOrDefault()
            ?? throw new RuntimeConnectionException("That container no longer exists.");

        return MapInspect(container, Indent(result.StandardOutput));
    }

    internal static ContainerInspect MapInspect(AppleContainer c, string raw)
    {
        var config = c.Configuration;
        var process = config?.InitProcess;

        return new ContainerInspect
        {
            Id = c.Id,
            Name = c.Id,
            Image = config?.Image?.Reference ?? "unknown",
            ImageId = config?.Image?.Descriptor?.Digest,
            Created = config?.CreationDate,
            StartedAt = c.Status?.StartedDate,

            // No finish time either — the engine records when a container started and whether it
            // is still going, and nothing about how it ended.
            FinishedAt = null,

            State = MapState(c.Status?.State),
            ExitCode = null,

            Entrypoint = process?.Executable is { Length: > 0 } exe ? [exe] : [],
            Command = process?.Arguments ?? [],
            WorkingDirectory = process?.WorkingDirectory,
            User = process?.User?.Id is { } id ? $"{id.Uid}:{id.Gid}" : null,

            Environment = process?.Environment is { } env
                ? SecretMarks.Apply(
                    env.Select(ParseEnv).OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase),
                    config?.Labels)
                : [],

            Ports = MapExposedPorts(config?.PublishedPorts),
            Mounts = MapMounts(config?.Mounts),
            Networks = MapNetworks(c.Status?.Networks),

            Labels = config?.Labels is { Count: > 0 } labels
                ? new Dictionary<string, string>(labels, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal),

            RawJson = raw,
        };
    }

    static EnvVar ParseEnv(string entry)
    {
        var split = entry.IndexOf('=');
        return split < 0 ? new EnvVar(entry, "") : new EnvVar(entry[..split], entry[(split + 1)..]);
    }

    static IReadOnlyList<ExposedPort> MapExposedPorts(List<ApplePublishedPort>? ports)
        => ports is null
            ? []
            : [.. ports
                .GroupBy(p => (p.ContainerPort, Protocol: p.Proto ?? "tcp"))
                .Select(g => new ExposedPort(
                    g.Key.ContainerPort,
                    g.Key.Protocol,
                    [.. g.Select(p => new PortBinding(p.HostPort, p.ContainerPort, g.Key.Protocol))]))
                .OrderBy(p => p.ContainerPort)];

    static IReadOnlyList<Dray.Core.Model.MountPoint> MapMounts(List<AppleMount>? mounts)
        => mounts is null
            ? []
            : [.. mounts.Select(m => new Dray.Core.Model.MountPoint(
                // Apple's mount type is a nested object rather than a string, and every mount seen
                // has been a host directory. Reported as a bind rather than guessed at.
                MountKind.Bind,
                m.Source ?? "",
                m.Destination ?? "",
                ReadOnly: false))];

    static IReadOnlyList<Dray.Core.Model.NetworkAttachment> MapNetworks(List<AppleNetworkStatus>? networks)
        => networks is null
            ? []
            : [.. networks.Select(n => new Dray.Core.Model.NetworkAttachment(
                n.Network ?? "default",
                n.Ipv4Address?.Split('/')[0],
                n.Ipv4Gateway,
                n.MacAddress,
                n.Hostname is { Length: > 0 } host ? [host] : []))];

    // ---------------------------------------------------------------- stats

    /// <summary>
    /// How often a sample is wanted.
    /// <para>
    /// Measured, not chosen: <c>container stats --no-stream</c> takes about 2.2 seconds to return
    /// on a healthy engine, so the call itself sets the floor. Asking for one a second — the rate
    /// Docker streams at — would simply queue calls back to back and quietly drift; asking for
    /// three would add lag on top of a call that is already slow. The wait below is the
    /// <i>remainder</i> of this period after the call, so the cadence stays steady rather than
    /// compounding.
    /// </para>
    /// <para>
    /// One consequence to expect in the graph: <c>cpuUsageUsec</c> is updated coarsely, so a
    /// container pegging one core reads between roughly 65% and 105% across consecutive samples
    /// rather than a steady 100%. The rate is right on average; the individual sample is not
    /// precise, and no amount of arithmetic here can make it so.
    /// </para>
    /// </summary>
    static readonly TimeSpan StatsPeriod = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Sampled by polling, because there is nothing to stream.
    /// <para>
    /// Apple reports a cumulative CPU figure and no previous sample, so a rate only exists between
    /// two of Dray's own polls. <c>CpuUsage.Percent</c> already divides by elapsed wall time rather
    /// than by an engine counter, so it works here unchanged — which is the second time that
    /// decision has paid for itself.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<ContainerStats> StreamStatsAsync(
        string containerId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        long? previousCpuUsec = null;
        var previousAt = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            var startedAt = DateTimeOffset.UtcNow;

            ProcessResult result;

            try
            {
                result = await RunAsync(["stats", "--no-stream", "--format", "json", containerId], ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The view moved on mid-call. Two seconds is long enough that this is the normal
                // way the stream ends, not an exceptional one.
                yield break;
            }

            if (result.ExitCode != 0) yield break;

            var sample = Deserialize<List<AppleStats>>(result.StandardOutput).FirstOrDefault();
            if (sample is null) yield break;

            var now = DateTimeOffset.UtcNow;
            var elapsed = now - previousAt;

            var cpuPercent = previousCpuUsec is { } previous
                ? CpuUsage.Percent(
                    // Microseconds on the wire; the calculation is in nanoseconds.
                    CpuUsage.Delta((ulong)sample.CpuUsageUsec * 1000, (ulong)previous * 1000),
                    elapsed,
                    Capabilities.TotalCpus ?? 0)
                : null;

            previousCpuUsec = sample.CpuUsageUsec;
            previousAt = now;

            yield return new ContainerStats(
                now,
                cpuPercent,
                sample.MemoryUsageBytes,
                sample.MemoryLimitBytes,
                sample.NetworkRxBytes,
                sample.NetworkTxBytes,
                sample.BlockReadBytes,
                sample.BlockWriteBytes,
                sample.NumProcesses);

            // Only the remainder of the period: the call already consumed most of it.
            var remaining = StatsPeriod - (DateTimeOffset.UtcNow - startedAt);
            if (remaining <= TimeSpan.Zero) continue;

            try
            {
                await Task.Delay(remaining, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    // ---------------------------------------------------------------- images

    public async Task<IReadOnlyList<ImageSummary>> ListImagesAsync(
        bool includeDangling = true, CancellationToken ct = default)
    {
        var result = await RunAsync(["image", "ls", "--format", "json"], ct).ConfigureAwait(false);

        if (result.ExitCode != 0) throw Failure("list images", result);

        return
        [
            .. Deserialize<List<AppleImage>>(result.StandardOutput)
                .Select(MapImage)
                .OrderBy(i => i.RepositoryKey, StringComparer.OrdinalIgnoreCase),
        ];
    }

    internal static ImageSummary MapImage(AppleImage i) => new()
    {
        Id = i.Id,
        Tags = i.Configuration?.Name is { Length: > 0 } name ? [ImageTag.Parse(name)] : [],
        Created = i.Configuration?.CreationDate,

        // The descriptor's size is the manifest's, not the image's. Reported as unknown rather
        // than as a nine-kilobyte image, which is what the manifest actually weighs.
        SizeBytes = 0,
        SizeReported = false,

        // The engine does not say how many containers use an image.
        ContainerCount = -1,
    };

    public async Task RemoveImageAsync(string imageId, bool force = false, CancellationToken ct = default)
    {
        var result = await RunAsync(["image", "delete", imageId], ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw Failure("remove image", result);
    }

    public async IAsyncEnumerable<PullProgress> PullImageAsync(
        string reference, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // The CLI reports progress as human-readable lines rather than as structured layer events,
        // so each line becomes one status with no layer id and no fraction. A per-layer bar would
        // be inventing detail the engine never sent.
        await foreach (var line in _runner
            .StreamAsync(Executable, ["image", "pull", reference], null, ct)
            .ConfigureAwait(false))
        {
            yield return new PullProgress(null, line.Text);
        }
    }

    /// <summary>
    /// <c>container image push</c>. The credential is ignored: this CLI reads the same
    /// <c>~/.docker/config.json</c> and system helpers, so passing one would be handing it a
    /// secret it is about to look up itself.
    /// </summary>
    public async IAsyncEnumerable<PullProgress> PushImageAsync(
        string reference,
        RegistryCredential? credential,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var line in _runner
            .StreamAsync(Executable, ["image", "push", reference], null, ct)
            .ConfigureAwait(false))
        {
            yield return new PullProgress(null, line.Text);
        }
    }

    public async IAsyncEnumerable<BuildProgress> BuildImageAsync(
        BuildRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var args = new List<string> { "build", "--file", request.Dockerfile };

        if (request.Tag is { } tag)
        {
            args.Add("--tag");
            args.Add(tag);
        }

        if (request.NoCache) args.Add("--no-cache");

        args.Add(request.ContextDirectory);

        await foreach (var line in _runner
            .StreamAsync(Executable, args, request.ContextDirectory, ct)
            .ConfigureAwait(false))
        {
            yield return new BuildProgress(line.Text);
        }
    }

    // ---------------------------------------------------------------- exec and files

    /// <summary>
    /// Open a shell through <c>container exec -i</c>.
    /// <para>
    /// An earlier version of this class refused, on the grounds that the CLI's exec was "a terminal
    /// command rather than a stream". That was asserted rather than tested, and it was wrong:
    /// <c>-i</c> keeps stdin open and the process streams in both directions perfectly well.
    /// </para>
    /// </summary>
    public async Task<IExecSession> StartExecAsync(
        string containerId, ExecOptions options, CancellationToken ct = default)
    {
        // Checked up front rather than inferred from a failure, the same way the Docker runtime
        // does it: "that container is not running" is a sentence, and an exit code is not.
        var containers = await ListContainersAsync(true, ct).ConfigureAwait(false);

        if (containers.FirstOrDefault(c => c.Id == containerId) is not { } container)
            throw new NoShellException("That container no longer exists.");

        if (container.State != DockerState.Running)
        {
            throw new NoShellException(
                "This container is not running, so there is nothing to run a shell in. "
                + "Start it first.");
        }

        return await AppleExecSession.StartAsync(_runner, Executable, containerId, options, ct)
            .ConfigureAwait(false);
    }

    public async Task<DirectoryListing> ListDirectoryAsync(
        string containerId, string path, bool containerIsRunning, CancellationToken ct = default)
    {
        if (!containerIsRunning)
        {
            return new DirectoryListing(
                path, [], ListingMethod.Exec,
                "Apple's runtime can only read a running container's filesystem.");
        }

        var result = await RunAsync(["exec", containerId, "ls", "-la", "--", path], ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            return new DirectoryListing(path, [], ListingMethod.Exec, result.StandardError.Trim());
        }

        return new DirectoryListing(path, LsParser.Parse(result.StandardOutput, path), ListingMethod.Exec);
    }

    public async Task<byte[]> ReadFileAsync(string containerId, string path, CancellationToken ct = default)
    {
        var result = await RunAsync(["exec", containerId, "cat", "--", path], ct).ConfigureAwait(false);

        if (result.ExitCode != 0) throw Failure("read the file", result);

        return System.Text.Encoding.UTF8.GetBytes(result.StandardOutput);
    }

    /// <summary>
    /// Write a file back, by piping the bytes into <c>cat</c> inside the container.
    /// <para>
    /// <b>Not <c>container cp</c>, and that is not a style preference.</b> Copying into a path that
    /// lies inside a <i>mounted volume</i> returns exit code 0 and writes nothing — verified on
    /// 1.3.0 by copying a file in, getting success, and finding the directory still empty. A file
    /// editor that silently discards what the user saved is the worst failure in this application,
    /// so the write goes through the container's own filesystem view instead, where it works for
    /// volumes and ordinary paths alike.
    /// </para>
    /// <para>
    /// Raw bytes, not text: a file being edited here may be a certificate or an image, and
    /// encoding it through a string would corrupt anything that is not valid UTF-16. Verified
    /// byte-exact with a NUL and a multi-byte character in the payload.
    /// </para>
    /// </summary>
    public async Task WriteFileAsync(
        string containerId, string path, byte[] content, CancellationToken ct = default)
    {
        // Single-quoted with any embedded quote escaped, so a path with a space or an apostrophe
        // reaches the shell as one argument rather than several.
        var quoted = "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        var result = await _runner.RunWithBytesAsync(
            Executable,
            ["exec", "--interactive", containerId, "sh", "-c", $"cat > {quoted}"],
            content,
            ct).ConfigureAwait(false);

        if (result.ExitCode != 0) throw Failure("write that file", result);
    }

    // ---------------------------------------------------------------- not this engine's shape

    // ---------------------------------------------------------------- volumes

    /// <summary>
    /// <c>container volume ls</c>.
    /// <para>
    /// This engine does manage volumes, and an earlier version of this class wrongly said it did
    /// not — the claim was made from the subcommand list and never tested. It is worth naming
    /// because it is the exact failure the capability system exists to prevent, made by the code
    /// that implements it.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<VolumeSummary>> ListVolumesAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(["volume", "ls", "--format", "json"], ct).ConfigureAwait(false);

        if (result.ExitCode != 0) throw Failure("list volumes", result);

        // Who uses what comes from the containers' own mount lists, which only `ls` carries — the
        // volume record does not say. One call for the whole page rather than one per volume.
        var users = await VolumeUsersAsync(ct).ConfigureAwait(false);

        return
        [
            .. Deserialize<List<AppleVolume>>(result.StandardOutput)
                .Select(v => MapVolume(v, users))
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// Volume name to the containers mounting it.
    /// <para>
    /// Derived from the containers rather than asked of the volume: the engine does not report
    /// users, and "nothing is holding this" is the question worth answering before a delete.
    /// </para>
    /// </summary>
    async Task<ILookup<string, string>> VolumeUsersAsync(CancellationToken ct)
    {
        var result = await RunAsync(["ls", "--all", "--format", "json"], ct).ConfigureAwait(false);

        if (result.ExitCode != 0) return Array.Empty<(string, string)>().ToLookup(p => p.Item1, p => p.Item2);

        return Deserialize<List<AppleContainer>>(result.StandardOutput)
            .SelectMany(c => (c.Configuration?.Mounts ?? [])
                .Where(m => m.Source is { Length: > 0 } && !m.Source.StartsWith('/'))
                .Select(m => (Volume: m.Source!, Container: c.Id)))
            .ToLookup(p => p.Volume, p => p.Container, StringComparer.Ordinal);
    }

    internal static VolumeSummary MapVolume(AppleVolume volume, ILookup<string, string> users)
    {
        var name = volume.Configuration?.Name ?? volume.Id;

        return new VolumeSummary
        {
            Name = name,
            Driver = volume.Configuration?.Driver ?? "local",
            Created = volume.Configuration?.CreationDate,

            // Genuinely a path on the user's own machine here — the volume is a disk image in
            // Application Support, not a directory inside a VM the user cannot reach.
            Mountpoint = volume.Configuration?.Source,

            // sizeInBytes is the image's allocated size — half a terabyte for an empty volume,
            // because the file is sparse. Reporting it would say every volume is enormous.
            SizeBytes = null,

            Labels = volume.Configuration?.Labels is { Count: > 0 } labels
                ? new Dictionary<string, string>(labels, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal),

            UsedBy = [.. users[name].OrderBy(c => c, StringComparer.OrdinalIgnoreCase)],
        };
    }

    public async Task CreateVolumeAsync(string name, CancellationToken ct = default)
    {
        var result = await RunAsync(["volume", "create", name], ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw Failure("create the volume", result);
    }

    public async Task RemoveVolumeAsync(string name, bool force = false, CancellationToken ct = default)
    {
        var result = await RunAsync(["volume", "delete", name], ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw Failure("remove the volume", result);
    }

    /// <summary>
    /// Browse a volume by mounting it into a throwaway container, the same trick
    /// <c>DockerVolumeSession</c> uses: a volume has no filesystem API of its own on any engine.
    /// </summary>
    public async Task<IVolumeSession> OpenVolumeAsync(string volumeName, CancellationToken ct = default)
        => await AppleVolumeSession.OpenAsync(this, _runner, Executable, volumeName, ct).ConfigureAwait(false);

    public Task<IReadOnlyList<ImageLayer>> GetImageHistoryAsync(string imageId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ImageLayer>>([]);

    public Task TagImageAsync(string imageId, string repository, string tag, CancellationToken ct = default)
        => throw new RuntimeConnectionException("Apple's runtime does not expose image tagging.");

    /// <summary>
    /// Empty, and <see cref="RuntimeCapabilities.SupportsNetworks"/> says why.
    /// <para>
    /// Containers do report the network they joined, so a synthetic "default" row could be
    /// derived. It would be a row the user cannot inspect, connect to, disconnect from or remove —
    /// a page pretending to manage something it cannot touch. The capability is the honest answer.
    /// </para>
    /// </summary>
    public Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<NetworkSummary>>([]);

    public Task CreateNetworkAsync(NetworkRequest request, CancellationToken ct = default)
        => throw new RuntimeConnectionException("Apple's runtime does not support creating networks from Dray.");

    public Task RemoveNetworkAsync(string networkId, CancellationToken ct = default)
        => throw new RuntimeConnectionException("Apple's runtime does not support removing networks from Dray.");

    public Task ConnectNetworkAsync(string networkId, string containerId, CancellationToken ct = default)
        => throw new RuntimeConnectionException("Apple's runtime attaches networks at creation only.");

    public Task DisconnectNetworkAsync(string networkId, string containerId, bool force = false, CancellationToken ct = default)
        => throw new RuntimeConnectionException("Apple's runtime attaches networks at creation only.");

    public Task<PrunePreview> PreviewPruneAsync(PruneKind kind, CancellationToken ct = default)
        => Task.FromResult(PrunePreview.Empty(kind));

    public async Task<PruneResult> PruneAsync(PruneKind kind, CancellationToken ct = default)
    {
        if (kind != PruneKind.Containers) return new PruneResult(kind, 0, 0);

        var result = await RunAsync(["prune"], ct).ConfigureAwait(false);
        return new PruneResult(kind, result.ExitCode == 0 ? 1 : 0, 0);
    }

    public async Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default)
    {
        var containers = await ListContainersAsync(true, ct).ConfigureAwait(false);
        var images = await ListImagesAsync(true, ct).ConfigureAwait(false);

        return new SystemInfo(
            containers.Count(c => c.State == DockerState.Running),
            0,
            containers.Count(c => c.State == DockerState.Exited),
            images.Count,
            "Apple container",
            Capabilities.EngineVersion);
    }

    /// <summary>Unknown rather than zero: the engine reports no disk accounting at all.</summary>
    public Task<DiskUsage> GetDiskUsageAsync(CancellationToken ct = default)
        => Task.FromResult(DiskUsage.Unknown);

    /// <summary>
    /// There is no event stream. This completes only on cancellation, and
    /// <see cref="RuntimeCapabilities.SupportsEvents"/> is false so the pump polls instead.
    /// </summary>
    public async IAsyncEnumerable<RuntimeEvent> WatchEventsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---------------------------------------------------------------- plumbing

    Task<ProcessResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
        => _runner.RunAsync(Executable, args, null, ct);

    /// <summary>
    /// The CLI's one-line JSON, unwrapped from its array and indented.
    /// <para>
    /// <c>container inspect</c> prints a single-element array, minified. The Inspect tab exists so
    /// a user does not have to leave for a terminal, and a 2 KB single line is worse than the
    /// terminal — so the record is unwrapped and formatted. Nothing is added or removed.
    /// </para>
    /// </summary>
    static string Indent(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            var element = document.RootElement.ValueKind == JsonValueKind.Array
                          && document.RootElement.GetArrayLength() > 0
                ? document.RootElement[0]
                : document.RootElement;

            return JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            // Unparseable is still the engine's answer, and showing it verbatim is more useful
            // than showing nothing.
            return json;
        }
    }

    static T Deserialize<T>(string json) where T : new()
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json) ?? new T();
        }
        catch (JsonException)
        {
            // The CLI prints progress lines to stdout alongside JSON in some modes. A parse
            // failure means no data, not a crash.
            return new T();
        }
    }

    static RuntimeConnectionException Failure(string what, ProcessResult result)
    {
        var detail = result.StandardError.Trim();
        if (detail.Length == 0) detail = result.StandardOutput.Trim();

        return new RuntimeConnectionException(
            detail.Length == 0 ? $"Could not {what}." : $"Could not {what}: {detail}");
    }
}
