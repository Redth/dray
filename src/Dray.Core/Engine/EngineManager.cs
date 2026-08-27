using Dray.Core.Model;

namespace Dray.Core.Engine;

/// <summary>
/// Creates a runtime for an endpoint. Keeps <see cref="EngineManager"/> free of any dependency on
/// a particular engine implementation.
/// </summary>
public interface IContainerRuntimeFactory
{
    IContainerRuntime Create(DockerEndpoint endpoint);
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
