using Dray.Core.Model;

namespace Dray.Core.Engine;

/// <summary>
/// Creates a runtime for an endpoint. Keeps <see cref="EngineManager"/> free of any dependency on
/// a particular engine implementation.
/// </summary>
public interface IContainerRuntimeFactory
{
    /// <summary>
    /// Whether this factory can serve the endpoint.
    /// <para>
    /// Exists because there is now more than one engine. A factory that answered every endpoint
    /// would have to guess, and the wrong guess is a runtime that connects to nothing and reports
    /// the engine as down.
    /// </para>
    /// </summary>
    bool Handles(DockerEndpoint endpoint);

    IContainerRuntime Create(DockerEndpoint endpoint);
}

/// <summary>
/// Dispatches an endpoint to whichever engine can serve it.
/// <para>
/// The composition root's answer to two runtimes existing. Order matters only in that the first
/// factory claiming an endpoint wins, so a more specific factory belongs before a general one.
/// </para>
/// </summary>
public sealed class CompositeRuntimeFactory(params IContainerRuntimeFactory[] factories) : IContainerRuntimeFactory
{
    public bool Handles(DockerEndpoint endpoint) => factories.Any(f => f.Handles(endpoint));

    public IContainerRuntime Create(DockerEndpoint endpoint)
        => factories.FirstOrDefault(f => f.Handles(endpoint))?.Create(endpoint)
           ?? throw new RuntimeConnectionException(
               $"Dray has no engine that can talk to {endpoint.Display}.");
}

/// <summary>
/// Owns every host Dray knows about and the one currently selected.
/// <para>
/// Only the selected host gets a live event stream. Running a pump per host would mean N event
/// streams and N reconnect loops for hosts nobody is looking at; the picker instead probes the
/// others cheaply and on demand, which is also what keeps a dead SSH host from costing anything
/// but its own row.
/// </para>
/// </summary>
public sealed class EngineManager : IAsyncDisposable
{
    readonly DockerContextReader _reader;
    readonly IContainerRuntimeFactory _factory;

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Dictionary<string, DockerHost> _hosts = [];

    IContainerRuntime? _runtime;
    RuntimeEventPump? _pump;

    public EngineManager(DockerContextReader reader, IContainerRuntimeFactory factory)
    {
        _reader = reader;
        _factory = factory;
    }

    /// <summary>How long a health probe of an unselected host may take before it counts as unreachable.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The selected host's containers. Survives host switches; contents do not.</summary>
    public EntityStore Store { get; } = new();

    public IReadOnlyList<DockerHost> Hosts => [.. _hosts.Values.OrderBy(Rank).ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase)];

    public DockerHost? Selected { get; private set; }

    /// <summary>True when discovery found nothing at all — the first-run state.</summary>
    public bool HasNoHosts => _hosts.Count == 0;

    /// <summary>Any change to the host list, the selection, or a host's connection state.</summary>
    public event Action? Changed;

    /// <summary>
    /// Discover hosts and connect to the current one. Safe to call again to re-discover.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var discovered = _reader.Discover();

            // Preserve what is already known about a host that survived re-discovery, so a
            // refresh does not visually reset every row to Disconnected.
            var previous = _hosts.ToDictionary(kv => kv.Key, kv => kv.Value);
            _hosts.Clear();

            foreach (var host in discovered)
            {
                _hosts[host.Id] = previous.TryGetValue(host.Id, out var known)
                    ? host with { State = known.State, StateDetail = known.StateDetail, Capabilities = known.Capabilities }
                    : host;
            }

            Changed?.Invoke();
        }
        finally
        {
            _gate.Release();
        }

        // A host that no longer exists cannot stay selected.
        var target = Selected is not null && _hosts.ContainsKey(Selected.Id)
            ? Selected.Id
            : _hosts.Values.FirstOrDefault(h => h.IsCurrent)?.Id ?? _hosts.Keys.FirstOrDefault();

        if (target is not null) await SelectAsync(target, ct).ConfigureAwait(false);
    }

    /// <summary>Switch to a host: tear the old connection down, seed and stream the new one.</summary>
    public async Task SelectAsync(string hostId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_hosts.TryGetValue(hostId, out var host)) return;
            if (Selected?.Id == hostId && _pump is not null) return;

            await TeardownAsync().ConfigureAwait(false);

            // The previous host's containers are not this host's containers.
            Store.Clear();

            Selected = host;
            Changed?.Invoke();

            _runtime = _factory.Create(host.Endpoint);
            _pump = new RuntimeEventPump(_runtime, Store);
            _pump.StateChanged += OnPumpStateChanged;
            _pump.Start();
        }
        finally
        {
            _gate.Release();
        }
    }

    void OnPumpStateChanged(HostConnectionState state, string? detail)
    {
        if (Selected is null) return;

        var updated = Selected with
        {
            State = state,
            StateDetail = detail,
            Capabilities = _runtime?.Capabilities ?? RuntimeCapabilities.None,
        };

        Selected = updated;
        _hosts[updated.Id] = updated;
        Changed?.Invoke();
    }

    /// <summary>
    /// Ask the engine to do something to a container.
    /// <para>
    /// The row shows the action in flight until an event settles it, rather than optimistically
    /// flipping to the state the user asked for. An optimistic state is a guess, and a wrong guess
    /// leaves the row quietly lying about the container.
    /// </para>
    /// </summary>
    /// <returns>Null on success, or a sentence explaining what went wrong.</returns>
    public async Task<string?> PerformAsync(string containerId, ContainerAction action, CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) return "Not connected to an engine.";

        Store.MarkPending(containerId, action);

        try
        {
            await runtime.PerformAsync(containerId, action, ct).ConfigureAwait(false);

            // Deliberately not clearing the pending mark here: the request was accepted, not
            // completed. The event stream reports what actually happened, and that is what
            // clears it — the same path an action taken in a terminal follows.
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Store.ClearPending(containerId);
            throw;
        }
        catch (Exception ex)
        {
            // No event is coming, so nothing else will clear this.
            Store.ClearPending(containerId);
            return RuntimeEventPump.Describe(ex);
        }
    }

    /// <summary>
    /// Stream a container's output from the selected host.
    /// <para>
    /// The stream belongs to the caller and ends with their cancellation token, so a view that
    /// goes away does not leave a connection open against the engine.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<LogLine> StreamLogsAsync(
        string containerId,
        LogOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Captured once: a host switch replaces the field, and the caller's stream should end
        // rather than silently continue against a different engine.
        if (_runtime is not { } runtime) yield break;

        await foreach (var line in runtime.StreamLogsAsync(containerId, options, ct).ConfigureAwait(false))
            yield return line;
    }

    /// <summary>
    /// Everything the engine knows about one container.
    /// <para>
    /// Not cached and not folded into the store. The store holds what the event stream can keep
    /// current; an inspect response is a snapshot of far more than events report, so caching one
    /// would mean showing stale detail with no way to know it had gone stale.
    /// </para>
    /// </summary>
    public async Task<ContainerInspect> InspectAsync(string containerId, CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) throw new RuntimeConnectionException("Not connected to an engine.");

        return await runtime.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What the engine says is on disk, or <see cref="DiskUsage.Unknown"/> when it cannot say.
    /// <para>
    /// Not folded into the store and not kept current: <c>system df</c> walks every image layer and
    /// every volume, which is the most expensive call the engine offers. It is fetched when a
    /// screen asks and not on a timer.
    /// </para>
    /// </summary>
    public async Task<DiskUsage> GetDiskUsageAsync(CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) return DiskUsage.Unknown;

        try
        {
            return await runtime.GetDiskUsageAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An engine that cannot answer is the same to the caller as one that does not
            // implement it: unknown, which is a state the UI already renders honestly.
            return DiskUsage.Unknown;
        }
    }

    public async Task<IReadOnlyList<VolumeSummary>> ListVolumesAsync(CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) return [];

        return await runtime.ListVolumesAsync(ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- compose

    readonly ComposeCli _compose = new();

    /// <summary>
    /// How compose is invoked here, or null when it is not installed.
    /// <para>
    /// Compose is a CLI plugin, not an engine feature, so it is probed on this machine rather than
    /// asked of the daemon — a remote engine can be running a stack Dray cannot drive.
    /// </para>
    /// </summary>
    public Task<ComposeCommand?> DetectComposeAsync(CancellationToken ct = default)
        => _compose.DetectAsync(ct);

    /// <summary>
    /// The stacks currently on this host, assembled from the labels compose puts on containers.
    /// <para>
    /// Read from the store rather than the engine, so it follows the event stream like everything
    /// else: a container starting or dying updates its stack without a refresh.
    /// </para>
    /// </summary>
    public IReadOnlyList<StackSummary> Stacks => StackDiscovery.From(Store.Containers);

    /// <summary>Run a compose subcommand, streaming its output.</summary>
    public async IAsyncEnumerable<ComposeOutput> RunComposeAsync(
        StackSummary stack,
        IReadOnlyList<string> arguments,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (await _compose.DetectAsync(ct).ConfigureAwait(false) is not { } command)
        {
            yield return new ComposeOutput("Compose is not installed on this machine.", IsError: true);
            yield break;
        }

        await foreach (var line in _compose
            .RunAsync(command, stack.Name, stack.ConfigFiles, arguments, stack.WorkingDirectory, ct)
            .ConfigureAwait(false))
        {
            yield return line;
        }
    }

    // ---------------------------------------------------------------- images

    public async Task<IReadOnlyList<ImageSummary>> ListImagesAsync(bool includeDangling = true, CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) return [];

        return await runtime.ListImagesAsync(includeDangling, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ImageLayer>> GetImageHistoryAsync(string imageId, CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) return [];

        return await runtime.GetImageHistoryAsync(imageId, ct).ConfigureAwait(false);
    }

    /// <returns>Null on success, or a sentence explaining what went wrong.</returns>
    public Task<string?> RemoveImageAsync(string imageId, bool force = false, CancellationToken ct = default)
        => TryAsync(runtime => runtime.RemoveImageAsync(imageId, force, ct));

    public Task<string?> TagImageAsync(string imageId, string repository, string tag, CancellationToken ct = default)
        => TryAsync(runtime => runtime.TagImageAsync(imageId, repository, tag, ct));

    /// <returns>Null on success, or a sentence explaining what went wrong.</returns>
    public Task<string?> SaveImageAsync(
        string reference, string destinationPath, IProgress<long>? progress = null, CancellationToken ct = default)
        => TryAsync(runtime => runtime.SaveImageAsync(reference, destinationPath, progress, ct));

    /// <summary>
    /// Load an archive, returning what the engine says it loaded and whatever went wrong.
    /// <para>
    /// Both are returned because both are worth reporting: an archive can load and name nothing,
    /// which is not a failure and not something to claim an image for.
    /// </para>
    /// </summary>
    public async Task<(IReadOnlyList<string> Loaded, string? Error)> LoadImageAsync(
        string archivePath, CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) return ([], "Not connected to an engine.");

        try
        {
            return (await runtime.LoadImageAsync(archivePath, ct).ConfigureAwait(false), null);
        }
        catch (Exception ex)
        {
            return ([], RuntimeEventPump.Describe(ex));
        }
    }

    /// <summary>
    /// Create a container from an image and start it.
    /// <para>
    /// Nothing is written to the store: the new container arrives the way every other one does,
    /// through the event stream or the poll. Writing it here would put a row on screen that the
    /// engine has not yet confirmed, and the whole design says the engine is the source of truth.
    /// </para>
    /// </summary>
    /// <returns>A sentence explaining what went wrong, or null on success.</returns>
    public Task<string?> RunAsync(RunRequest request, CancellationToken ct = default)
        => TryAsync(runtime => runtime.RunAsync(request, ct));

    public async IAsyncEnumerable<PullProgress> PullImageAsync(
        string reference,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) yield break;

        await foreach (var step in runtime.PullImageAsync(reference, ct).ConfigureAwait(false))
            yield return step;
    }

    /// <summary>
    /// Push an image, streaming per-layer progress.
    /// <para>
    /// The credential is fetched here, from the system helper, and lives only for the length of
    /// this call — see <see cref="RegistryReader.GetAsync"/>. A registry with nothing stored
    /// pushes anonymously, which works for a local registry and fails clearly everywhere else,
    /// rather than Dray refusing before the registry has had a chance to answer.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<PullProgress> PushImageAsync(
        string reference,
        RegistryReader registries,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) yield break;

        var credential = await registries.FindForAsync(ImageTag.Parse(reference).Registry, ct)
            .ConfigureAwait(false);

        await foreach (var step in runtime.PushImageAsync(reference, credential, ct).ConfigureAwait(false))
            yield return step;
    }

    /// <summary>Build an image, streaming the engine's output.</summary>
    public async IAsyncEnumerable<BuildProgress> BuildImageAsync(
        BuildRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) yield break;

        await foreach (var step in runtime.BuildImageAsync(request, ct).ConfigureAwait(false))
            yield return step;
    }

    // ---------------------------------------------------------------- networks

    public async Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) return [];

        return await runtime.ListNetworksAsync(ct).ConfigureAwait(false);
    }

    public Task<string?> CreateNetworkAsync(NetworkRequest request, CancellationToken ct = default)
        => TryAsync(runtime => runtime.CreateNetworkAsync(request, ct));

    public Task<string?> RemoveNetworkAsync(string networkId, CancellationToken ct = default)
        => TryAsync(runtime => runtime.RemoveNetworkAsync(networkId, ct));

    public Task<string?> ConnectNetworkAsync(string networkId, string containerId, CancellationToken ct = default)
        => TryAsync(runtime => runtime.ConnectNetworkAsync(networkId, containerId, ct));

    public Task<string?> DisconnectNetworkAsync(string networkId, string containerId, bool force = false, CancellationToken ct = default)
        => TryAsync(runtime => runtime.DisconnectNetworkAsync(networkId, containerId, force, ct));

    // ---------------------------------------------------------------- volumes

    public Task<string?> CreateVolumeAsync(string name, CancellationToken ct = default)
        => TryAsync(runtime => runtime.CreateVolumeAsync(name, ct));

    public Task<string?> RemoveVolumeAsync(string name, bool force = false, CancellationToken ct = default)
        => TryAsync(runtime => runtime.RemoveVolumeAsync(name, force, ct));

    // ---------------------------------------------------------------- prune

    public async Task<PrunePreview> PreviewPruneAsync(PruneKind kind, CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) return PrunePreview.Empty(kind);

        return await runtime.PreviewPruneAsync(kind, ct).ConfigureAwait(false);
    }

    public async Task<PruneResult> PruneAsync(PruneKind kind, CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) return new PruneResult(kind, 0, 0);

        return await runtime.PruneAsync(kind, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Run something that either works or explains itself.
    /// <para>
    /// Every mutating call in this class has the same shape — do it, and turn a failure into a
    /// sentence the UI can show — and repeating that try/catch a dozen times is how one of them
    /// ends up rethrowing a stack trace at the user.
    /// </para>
    /// </summary>
    async Task<string?> TryAsync(Func<IContainerRuntime, Task> action)
    {
        if (_runtime is not { } runtime) return "Not connected to an engine.";

        try
        {
            await action(runtime).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return RuntimeEventPump.Describe(ex);
        }
    }

    /// <summary>
    /// Open a volume for browsing. The caller owns the session and must dispose it — it holds
    /// engine-side resources for as long as it lives.
    /// </summary>
    public async Task<IVolumeSession> OpenVolumeAsync(string volumeName, CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) throw new RuntimeConnectionException("Not connected to an engine.");

        return await runtime.OpenVolumeAsync(volumeName, ct).ConfigureAwait(false);
    }

    /// <summary>Rename a container.</summary>
    /// <returns>Null on success, or a sentence explaining what went wrong.</returns>
    public async Task<string?> RenameAsync(string containerId, string name, CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) return "Not connected to an engine.";

        try
        {
            await runtime.RenameAsync(containerId, name, ct).ConfigureAwait(false);

            // Written straight to the store rather than waiting for the stream. Docker emits a
            // rename event; podman emits nothing at all, so waiting would leave the old name on
            // screen indefinitely. The engine has already accepted this exact name, so it is a
            // fact rather than a prediction — see EntityStore.Rename.
            Store.Rename(containerId, name);

            return null;
        }
        catch (Exception ex)
        {
            return RuntimeEventPump.Describe(ex);
        }
    }

    /// <summary>
    /// Sample a container's resource use. The stream belongs to the caller and ends with their
    /// cancellation token, so a view that goes away stops sampling.
    /// </summary>
    public async IAsyncEnumerable<ContainerStats> StreamStatsAsync(
        string containerId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) yield break;

        await foreach (var sample in runtime.StreamStatsAsync(containerId, ct).ConfigureAwait(false))
            yield return sample;
    }

    /// <summary>
    /// Open a shell inside a container. The caller owns the session and must dispose it — an exec
    /// left running holds a process inside the user's container.
    /// </summary>
    public async Task<IExecSession> StartExecAsync(
        string containerId, ExecOptions options, CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) throw new RuntimeConnectionException("Not connected to an engine.");

        return await runtime.StartExecAsync(containerId, options, ct).ConfigureAwait(false);
    }

    public async Task<DirectoryListing> ListDirectoryAsync(
        string containerId, string path, bool containerIsRunning, CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) throw new RuntimeConnectionException("Not connected to an engine.");

        return await runtime.ListDirectoryAsync(containerId, path, containerIsRunning, ct).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadFileAsync(string containerId, string path, CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) throw new RuntimeConnectionException("Not connected to an engine.");

        return await runtime.ReadFileAsync(containerId, path, ct).ConfigureAwait(false);
    }

    public async Task WriteFileAsync(string containerId, string path, byte[] content, CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) throw new RuntimeConnectionException("Not connected to an engine.");

        await runtime.WriteFileAsync(containerId, path, content, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Check the hosts that are not selected, so the picker can show which are alive.
    /// <para>
    /// Deliberately on demand rather than on a timer: probing a remote host over SSH costs a
    /// connection, and doing it every few seconds for hosts nobody is looking at is exactly the
    /// polling this design exists to avoid.
    /// </para>
    /// </summary>
    public async Task ProbeOthersAsync(CancellationToken ct = default)
    {
        var targets = _hosts.Values.Where(h => h.Id != Selected?.Id).ToList();

        // In parallel: one slow host must not delay the rest of the picker.
        await Task.WhenAll(targets.Select(host => ProbeAsync(host, ct))).ConfigureAwait(false);
    }

    async Task ProbeAsync(DockerHost host, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);

        DockerHost result;
        await using var runtime = _factory.Create(host.Endpoint);

        try
        {
            var capabilities = await runtime.ConnectAsync(timeout.Token).ConfigureAwait(false);
            result = host with { State = HostConnectionState.Connected, StateDetail = null, Capabilities = capabilities };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            result = host with { State = HostConnectionState.Unreachable, StateDetail = "Timed out reaching the host." };
        }
        catch (Exception ex)
        {
            result = host with { State = HostConnectionState.Unreachable, StateDetail = RuntimeEventPump.Describe(ex) };
        }

        // The selection may have moved while the probe was in flight; do not overwrite the live
        // state of a host that is now the connected one.
        if (result.Id == Selected?.Id) return;
        if (!_hosts.ContainsKey(result.Id)) return;

        _hosts[result.Id] = result;
        Changed?.Invoke();
    }

    /// <summary>Connected first, then reachable, then everything else — the useful ones on top.</summary>
    static int Rank(DockerHost host) => host.State switch
    {
        HostConnectionState.Connected => 0,
        HostConnectionState.Degraded => 1,
        HostConnectionState.Connecting => 2,
        HostConnectionState.Disconnected => 3,
        _ => 4,
    };

    async Task TeardownAsync()
    {
        if (_pump is not null)
        {
            _pump.StateChanged -= OnPumpStateChanged;
            await _pump.DisposeAsync().ConfigureAwait(false);
            _pump = null;
        }

        if (_runtime is not null)
        {
            await _runtime.DisposeAsync().ConfigureAwait(false);
            _runtime = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await TeardownAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
