using Dray.Core.Engine;
using Dray.Core.Model;

namespace Dray.Core.Kubernetes;

/// <summary>Which cluster the user last chose — a separate file from the engine's, because the two pickers are separate.</summary>
public sealed class ClusterSelectionStore(string? directory = null)
{
    readonly FileHostSelectionStore _file = new(directory, "selected-cluster");

    public string? Load() => _file.Load();

    public void Save(string contextName) => _file.Save(contextName);
}

/// <summary>
/// The clusters in the kubeconfig, the one selected, and its live state.
/// <para>
/// The counterpart of <see cref="EngineManager"/>, and independent of it (docs/KUBERNETES.md §1):
/// selecting a cluster never changes the engine, and never changes <c>kubectl</c>'s current-context
/// either — that is the terminal's, and moving it would aim someone's next <c>kubectl delete</c>
/// somewhere new (§6).
/// </para>
/// <para>
/// Only clusters measured as local connect in v1. A remote context is listed, with the reason, and
/// selecting it is refused rather than attempted.
/// </para>
/// </summary>
public sealed class ClusterManager : IAsyncDisposable
{
    /// <summary>The kinds watched, in the order they are started: the small ones the others refer to first.</summary>
    static readonly ClusterKind[] Watched =
    [
        ClusterKind.Namespace,
        ClusterKind.Node,
        ClusterKind.ReplicaSet,
        ClusterKind.Deployment,
        ClusterKind.StatefulSet,
        ClusterKind.DaemonSet,
        ClusterKind.Job,
        ClusterKind.Service,
        ClusterKind.Pod,
        ClusterKind.Event,
    ];

    readonly KubeconfigReader _reader;
    readonly IClusterClientFactory _factory;
    readonly ClusterSelectionStore? _selection;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Dictionary<ClusterKind, string> _problems = [];
    readonly List<IPortForward> _forwards = [];

    List<KubeContext> _contexts = [];
    IClusterClient? _client;
    CancellationTokenSource? _watching;

    readonly Settings.DraySettings? _settings;

    /// <param name="settings">
    /// Whether Kubernetes support is on. Off, nothing is read and nothing is connected — and turning
    /// it on or off in Settings takes effect at once rather than on the next launch.
    /// </param>
    public ClusterManager(KubeconfigReader reader, IClusterClientFactory factory, ClusterSelectionStore? selection = null, Settings.DraySettings? settings = null)
    {
        _reader = reader;
        _factory = factory;
        _selection = selection;
        _settings = settings;
        Store.Changed += _ => Changed?.Invoke();

        if (settings is not null)
        {
            _enabled = settings.KubernetesEnabled;
            settings.Changed += OnSettingsChanged;
        }
    }

    bool _enabled = true;

    /// <summary>Kubernetes support is on in Settings. Off, there are no contexts and no cluster, so nothing about it is shown.</summary>
    public bool Enabled => _enabled;

    void OnSettingsChanged()
    {
        if (_settings is null || _settings.KubernetesEnabled == _enabled) return;
        _enabled = _settings.KubernetesEnabled;

        _ = _enabled ? InitializeAsync() : TurnOffAsync();
    }

    /// <summary>Let go of everything: the watches, the forwards, the client, and the list of contexts.</summary>
    async Task TurnOffAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await DisconnectAsync();
            Selected = null;
            _contexts = [];
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public ClusterStore Store { get; } = new();

    /// <summary>Raised for any change: a context's state, the selection, or anything in the store.</summary>
    public event Action? Changed;

    public IReadOnlyList<KubeContext> Contexts => _contexts;

    public IReadOnlyList<KubeContext> LocalContexts => [.. _contexts.Where(c => c.IsLocal)];

    public KubeContext? Selected { get; private set; }

    /// <summary>How long connecting may take before the cluster counts as unreachable.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Why a kind's watch stopped, if one has — the page that lists it says so.</summary>
    public string? ProblemFor(ClusterKind kind)
    {
        lock (_problems) return _problems.GetValueOrDefault(kind);
    }

    public IReadOnlyList<IPortForward> Forwards
    {
        get
        {
            lock (_forwards) return [.. _forwards];
        }
    }

    /// <summary>
    /// Read the kubeconfig and connect to the cluster to show: the one chosen last time if it is
    /// still here and local, else <c>kubectl</c>'s current one if that is local, else the first local
    /// one. Never a remote one, whatever the current-context says.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!_enabled) return;

        _contexts = [.. _reader.Read()];
        Changed?.Invoke();

        var remembered = _selection?.Load();
        var choice = _contexts.FirstOrDefault(c => c.IsLocal && c.Name == remembered)
            ?? _contexts.FirstOrDefault(c => c.IsLocal && c.IsCurrent)
            ?? _contexts.FirstOrDefault(c => c.IsLocal);

        if (choice is not null) await ConnectAsync(choice, remember: false, ct);
    }

    /// <summary>Choose a cluster. Null on success; otherwise why not, in words the picker can show.</summary>
    public async Task<string?> SelectAsync(string contextName, CancellationToken ct = default)
    {
        if (_contexts.FirstOrDefault(c => c.Name == contextName) is not { } context) return $"There is no context named {contextName}.";
        if (!context.IsLocal) return $"{context.Name} is not on this machine. {context.LocalityReason} Dray connects to local clusters only for now.";

        await ConnectAsync(context, remember: true, ct);
        return Selected?.State == HostConnectionState.Connected ? null : Selected?.StateDetail;
    }

    /// <summary>Read the kubeconfig again — after a cluster was created, deleted, or set up from a terminal.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!_enabled) return;

        var previous = Selected?.Name;
        var fresh = _reader.Read();

        _contexts = [.. fresh.Select(c => Selected?.Name == c.Name ? Selected with { Server = c.Server, IsCurrent = c.IsCurrent } : c)];
        Changed?.Invoke();

        if (previous is not null && _contexts.All(c => c.Name != previous))
        {
            await DisconnectAsync();
            Selected = null;
            Changed?.Invoke();
        }

        if (Selected is null && LocalContexts.FirstOrDefault() is { } first) await ConnectAsync(first, remember: false, ct);
    }

    async Task ConnectAsync(KubeContext context, bool remember, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await DisconnectAsync();

            Selected = context with { State = HostConnectionState.Connecting, StateDetail = null };
            Replace(Selected);

            var client = _factory.Create(context);
            ClusterCapabilities caps;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ConnectTimeout);
                caps = await client.ConnectAsync(timeout.Token);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                await client.DisposeAsync();
                Selected = Selected with
                {
                    State = HostConnectionState.Unreachable,
                    StateDetail = e is OperationCanceledException
                        ? $"{context.Name} did not answer within {ConnectTimeout.TotalSeconds:0} seconds. Is the cluster running?"
                        : Describe(e),
                };
                Replace(Selected);
                return;
            }

            _client = client;
            Selected = Selected with { State = HostConnectionState.Connected, Capabilities = caps };
            Replace(Selected);
            if (remember) _selection?.Save(context.Name);

            _watching = new CancellationTokenSource();
            foreach (var kind in Watched) _ = PumpAsync(client, kind, _watching.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One kind's watch into the store, for as long as this cluster is selected.</summary>
    async Task PumpAsync(IClusterClient client, ClusterKind kind, CancellationToken ct)
    {
        try
        {
            await foreach (var change in client.WatchAsync(kind, ct))
            {
                if (change is ClusterInterrupted interrupted)
                {
                    lock (_problems) _problems[kind] = interrupted.Reason;
                    Changed?.Invoke();
                    continue;
                }

                bool cleared;
                lock (_problems) cleared = _problems.Remove(kind);

                if (!Store.Apply(change) && cleared) Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            lock (_problems) _problems[kind] = Describe(e);
            Changed?.Invoke();
        }
    }

    async Task DisconnectAsync()
    {
        _watching?.Cancel();
        _watching?.Dispose();
        _watching = null;

        IPortForward[] forwards;
        lock (_forwards)
        {
            forwards = [.. _forwards];
            _forwards.Clear();
        }

        foreach (var forward in forwards) await forward.DisposeAsync();

        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

        lock (_problems) _problems.Clear();
        Store.Clear();
    }

    void Replace(KubeContext context)
    {
        _contexts = [.. _contexts.Select(c => c.Name == context.Name ? context : c)];
        Changed?.Invoke();
    }

    IClusterClient Client => _client ?? throw new InvalidOperationException("No cluster is connected.");

    public IAsyncEnumerable<LogLine> StreamLogsAsync(PodSummary pod, string container, PodLogOptions options, CancellationToken ct = default)
        => Client.StreamLogsAsync(pod.Namespace, pod.Name, container, options, ct);

    public Task<IExecSession> StartExecAsync(PodSummary pod, string container, ExecOptions options, CancellationToken ct = default)
        => Client.StartExecAsync(pod.Namespace, pod.Name, container, options, ct);

    public Task<string> GetRawAsync(ClusterObject item, CancellationToken ct = default)
        => Client.GetRawAsync(item.Kind, item.Namespace, item.Name, ct);

    /// <summary>Forward a pod's port. Null and the forward on success; otherwise why not.</summary>
    public async Task<(IPortForward? Forward, string? Problem)> ForwardAsync(PodSummary pod, int remotePort, CancellationToken ct = default)
    {
        IPortForward? existing;
        lock (_forwards) existing = _forwards.FirstOrDefault(f => f.Namespace == pod.Namespace && f.Pod == pod.Name && f.RemotePort == remotePort);
        if (existing is { Failure: null }) return (existing, null);

        try
        {
            var forward = await Client.ForwardAsync(pod.Namespace, pod.Name, remotePort, remotePort, ct);
            lock (_forwards)
            {
                if (existing is not null) _forwards.Remove(existing);
                _forwards.Add(forward);
            }

            Changed?.Invoke();
            return (forward, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (null, Describe(e));
        }
    }

    public async Task StopForwardAsync(IPortForward forward)
    {
        lock (_forwards) _forwards.Remove(forward);
        await forward.DisposeAsync();
        Changed?.Invoke();
    }

    public Task<string?> DeleteAsync(ClusterObject item, CancellationToken ct = default)
        => Attempt(() => Client.DeleteAsync(item.Kind, item.Namespace, item.Name, ct));

    public Task<string?> ScaleAsync(WorkloadSummary workload, int replicas, CancellationToken ct = default)
        => Attempt(() => Client.ScaleAsync(workload.Kind, workload.Namespace, workload.Name, replicas, ct));

    public Task<string?> RestartAsync(WorkloadSummary workload, CancellationToken ct = default)
        => Attempt(() => Client.RestartAsync(workload.Kind, workload.Namespace, workload.Name, ct));

    static async Task<string?> Attempt(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Describe(e);
        }
    }

    /// <summary>
    /// The cluster's own sentence where it gave one. Never the exception's type name, and never a
    /// header or a certificate: a client error can quote the request, and the request carries the
    /// credential (docs/KUBERNETES.md §9.5).
    /// </summary>
    static string Describe(Exception e) => e switch
    {
        ClusterException cluster => cluster.Message,
        HttpRequestException http => http.InnerException is { } inner ? $"Could not reach the cluster: {inner.Message}" : $"Could not reach the cluster: {http.Message}",
        _ => e.Message,
    };

    public async ValueTask DisposeAsync()
    {
        if (_settings is not null) _settings.Changed -= OnSettingsChanged;
        await DisconnectAsync();
        _gate.Dispose();
    }
}

/// <summary>A refusal from the cluster, in the words the API server used — "pods "x" is forbidden: …".</summary>
public sealed class ClusterException(string message, int? statusCode = null, Exception? inner = null) : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}
