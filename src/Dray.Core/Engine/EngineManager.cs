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
    readonly DockerDesktop? _desktop;
    readonly IHostSelectionStore? _selection;
    readonly IHostNameStore? _nameStore;
    readonly Dictionary<string, string> _names;

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Dictionary<string, DockerHost> _hosts = [];

    IContainerRuntime? _runtime;
    RuntimeEventPump? _pump;

    /// <param name="desktop">
    /// Docker Desktop's controller, on Windows. Null where there is no Docker Desktop to drive, which
    /// leaves every host to stand on its own and no mode to switch.
    /// </param>
    /// <param name="selection">Where the user's choice of host is kept between launches. Null keeps nothing.</param>
    /// <param name="compose">How compose is found and run; a test hands in one over a scripted runner.</param>
    /// <param name="catalog">Stacks beyond what is running — remembered ones and the stacks folder.</param>
    /// <param name="names">The names the user has given hosts. Null keeps none, and every host shows its own.</param>
    public EngineManager(
        DockerContextReader reader,
        IContainerRuntimeFactory factory,
        DockerDesktop? desktop = null,
        IHostSelectionStore? selection = null,
        ComposeCli? compose = null,
        StackCatalog? catalog = null,
        IHostNameStore? names = null)
    {
        _reader = reader;
        _factory = factory;
        _desktop = desktop;
        _selection = selection;
        _nameStore = names;
        _names = new Dictionary<string, string>(names?.Load() ?? new Dictionary<string, string>());
        _compose = compose ?? new ComposeCli();
        Catalog = catalog;

        if (catalog is not null)
        {
            catalog.Changed += () => Changed?.Invoke();
            Store.Changed += OnStoreChangedForStacks;
        }
    }

    /// <summary>
    /// What Docker Desktop is doing: whether it is running and which of its engines is current.
    /// Read on every discovery, because the user can switch it from its own tray menu.
    /// </summary>
    public DockerDesktopState Desktop { get; private set; } = DockerDesktopState.NotInstalled;

    /// <summary>
    /// What is changing engines right now, as a sentence — "Switching Docker Desktop to Linux
    /// containers…" — or null when nothing is.
    /// <para>
    /// A switch takes eleven seconds with nothing to show for most of it, and the picker and the
    /// Hosts page both say so rather than looking hung. Also what keeps a second switch from being
    /// started on top of the first.
    /// </para>
    /// </summary>
    public string? Transition { get; private set; }

    IDisposable Transitioning(string what)
    {
        Transition = what;
        Changed?.Invoke();

        return new Ending(this);
    }

    sealed class Ending(EngineManager owner) : IDisposable
    {
        public void Dispose()
        {
            owner.Transition = null;
            owner.Changed?.Invoke();
        }
    }

    /// <summary>
    /// The stacks Dray knows about beyond what is running — remembered and on disk. Null where the
    /// head did not supply one, in which case <see cref="Stacks"/> is the running ones only.
    /// </summary>
    public StackCatalog? Catalog { get; }

    void OnStoreChangedForStacks(StoreChange change)
    {
        if (Selected is not { } host) return;

        var observed = StackDiscovery.From(Store.Containers);
        Catalog?.Observe(host.Id, observed);

        if (_remote is { } remote) _ = ProbeRemoteStacksAsync(host, remote, observed);
        else if (host.Endpoint.Scheme == EndpointScheme.Wsl) _ = ProbeWslStacksAsync(host, observed);
    }

    /// <summary>
    /// The WSL counterpart of <see cref="ProbeRemoteStacksAsync"/>: a stack whose files are in the
    /// distribution, as Windows reaches them through <c>\wsl.localhost</c>, is the distribution's —
    /// compose runs in there, and its files are opened through the translation.
    /// </summary>
    async Task ProbeWslStacksAsync(DockerHost host, IReadOnlyList<StackSummary> observed)
    {
        var files = new WslStackFiles(host.Endpoint);

        foreach (var stack in observed)
        {
            if (stack.ConfigFiles.Count == 0) continue;

            lock (_onHost)
            {
                if (!_probed.Add(stack.Name)) continue;
            }

            if (!files.AllExist(stack.ConfigFiles) || Selected?.Id != host.Id) continue;

            lock (_onHost) _onHost.Add(stack.Name);

            if (Catalog is { } catalog)
            {
                if (!catalog.Registry.IsForgotten(host.Id, stack.Name))
                {
                    var directory = stack.WorkingDirectory ?? RemoteDirectoryOf(stack.ConfigFiles[0]);
                    catalog.Registry.Remember(new StackRecord(host.Id, stack.Name, directory, stack.ConfigFiles, OnHost: true));
                }

                foreach (var file in stack.ConfigFiles)
                {
                    if (await files.ReadAsync(file).ConfigureAwait(false) is { } bytes)
                        catalog.SetRemoteDeclared(file, ComposeDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes)).Services);
                }
            }

            Changed?.Invoke();
        }
    }

    // ---------------------------------------------------------------- stacks on an SSH host

    /// <summary>Compose and files on the selected host, when it is reached over SSH. Null otherwise.</summary>
    RemoteCompose? _remote;

    /// <summary>Stacks on the selected host whose files were found there.</summary>
    readonly HashSet<string> _onHost = new(StringComparer.Ordinal);

    /// <summary>Stacks already asked about, so a busy event stream asks each one once.</summary>
    readonly HashSet<string> _probed = new(StringComparer.Ordinal);

    void ResetRemote(DockerHost host)
    {
        lock (_onHost)
        {
            _onHost.Clear();
            _probed.Clear();
        }

        _remote = SshShell.Handles(host.Endpoint) ? new RemoteCompose(new SshShell(host.Endpoint, _compose.Runner)) : null;

        // Stacks remembered on this host by their files there are on the host still, down or not.
        if ((_remote is not null || host.Endpoint.Scheme == EndpointScheme.Wsl) && Catalog is { } catalog)
        {
            lock (_onHost)
            {
                foreach (var record in catalog.Registry.KnownOn(host.Id).Where(r => r.OnHost)) _onHost.Add(record.Project);
            }

            _ = ReadRemoteDeclaredAsync(catalog.Registry.KnownOn(host.Id).Where(r => r.OnHost).SelectMany(r => r.Files));
        }
    }

    /// <summary>
    /// For each stack on an SSH host whose files are not on this machine, ask the host whether they
    /// are there — one round trip per stack, once.
    /// <para>
    /// Where they are, the stack is the host's: compose runs there, the editor reads and writes
    /// there, and it is remembered so it stays listed when it is down. Where they are not — compose
    /// was run from another machine — it stays observed, with the controls compose has from labels.
    /// </para>
    /// </summary>
    async Task ProbeRemoteStacksAsync(DockerHost host, RemoteCompose remote, IReadOnlyList<StackSummary> observed)
    {
        foreach (var stack in observed)
        {
            if (stack.ConfigFiles.Count == 0 || stack.ConfigFiles.All(File.Exists)) continue;

            lock (_onHost)
            {
                if (!_probed.Add(stack.Name)) continue;
            }

            try
            {
                if (!await new SshStackFiles(remote.Shell).AllExistAsync(stack.ConfigFiles).ConfigureAwait(false)) continue;
            }
            catch (Exception)
            {
                continue;
            }

            if (Selected?.Id != host.Id) return;

            lock (_onHost) _onHost.Add(stack.Name);

            var directory = stack.WorkingDirectory ?? RemoteDirectoryOf(stack.ConfigFiles[0]);

            if (Catalog is { } catalog && !catalog.Registry.IsForgotten(host.Id, stack.Name))
                catalog.Registry.Remember(new StackRecord(host.Id, stack.Name, directory, stack.ConfigFiles, OnHost: true));

            await ReadRemoteDeclaredAsync(stack.ConfigFiles).ConfigureAwait(false);
            Changed?.Invoke();
        }
    }

    async Task ReadRemoteDeclaredAsync(IEnumerable<string> files)
    {
        if (Catalog is not { } catalog || Selected is not { } host) return;

        IStackFiles? reader = _remote is { } remote
            ? new SshStackFiles(remote.Shell)
            : host.Endpoint.Scheme == EndpointScheme.Wsl ? new WslStackFiles(host.Endpoint) : null;

        if (reader is null) return;

        foreach (var file in files.Distinct(StringComparer.Ordinal))
        {
            try
            {
                if (await reader.ReadAsync(file).ConfigureAwait(false) is { } bytes)
                    catalog.SetRemoteDeclared(file, ComposeDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes)).Services);
            }
            catch (Exception)
            {
                // Unreadable is unknown; the stack still lists what its containers say.
            }
        }
    }

    /// <summary>A POSIX path's directory — the host's paths, whatever this machine's separator is.</summary>
    static string RemoteDirectoryOf(string path) => path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : "/";

    /// <summary>
    /// Where a stack's files are, for reading and writing them: this machine, or the engine's host.
    /// </summary>
    public IStackFiles FilesFor(StackSummary stack)
        => stack.Provenance != StackProvenance.Remote
            ? LocalStackFiles.Instance
            : _remote is { } remote
                ? new SshStackFiles(remote.Shell)
                : Selected?.Endpoint is { Scheme: EndpointScheme.Wsl } wsl ? new WslStackFiles(wsl) : LocalStackFiles.Instance;

    /// <summary>Compose on the host, for a stack whose files are there and a host that has compose. Null otherwise.</summary>
    async Task<RemoteCompose?> RemoteFor(StackSummary stack, CancellationToken ct)
        => stack.Provenance == StackProvenance.Remote && _remote is { } remote && await remote.DetectAsync(ct).ConfigureAwait(false) is not null
            ? remote
            : null;

    static string RemoteDirectory(StackSummary stack)
        => stack.WorkingDirectory ?? (stack.ConfigFiles.Count > 0 ? RemoteDirectoryOf(stack.ConfigFiles[0]) : "/");

    /// <summary>How long a health probe of an unselected host may take before it counts as unreachable.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The selected host's containers. Survives host switches; contents do not.</summary>
    public EntityStore Store { get; } = new();

    /// <summary>
    /// Every distinct engine, the useful ones first.
    /// <para>
    /// Distinct by what answered rather than by address: a host found to be the same engine as a
    /// more specific one is left out (<see cref="CanonicalFor"/>). On a machine with Docker Desktop
    /// the CLI's <c>default</c> context is the same engine as whichever of <c>desktop-linux</c> and
    /// <c>desktop-windows</c> is current, and listing both is the confusion the picker exists to
    /// remove.
    /// </para>
    /// </summary>
    public IReadOnlyList<DockerHost> Hosts => [.. _hosts.Values
        .Where(h => CanonicalFor(h) is null)
        .Select(Named)
        .OrderBy(Rank)
        .ThenBy(h => h.Label, StringComparer.OrdinalIgnoreCase)];

    public DockerHost? Selected
    {
        get => _selected is null ? null : Named(_selected);
        private set => _selected = value;
    }

    DockerHost? _selected;

    /// <summary>
    /// The host with the user's name for it applied. Done here, as hosts are handed out, rather than
    /// wherever a host is discovered, probed or replaced — there are a dozen of those, and a name
    /// missed at any one of them would vanish the next time that host's state changed.
    /// </summary>
    DockerHost Named(DockerHost host)
        => _names.TryGetValue(host.Id, out var name) ? host with { DisplayName = name } : host with { DisplayName = null };

    /// <summary>
    /// Give a host a name of Dray's own, or clear it with null or blank. Applies at once and is
    /// remembered between launches. The host's own name — the one the CLI uses — is untouched.
    /// </summary>
    public void SetDisplayName(string hostId, string? name)
    {
        if (!_hosts.TryGetValue(hostId, out var host)) return;

        var kept = HostDisplayName.Normalize(name, host.Name);
        var current = _names.GetValueOrDefault(hostId);
        if (kept == current) return;

        if (kept is null) _names.Remove(hostId);
        else _names[hostId] = kept;

        _nameStore?.Save(new Dictionary<string, string>(_names));
        Changed?.Invoke();
    }

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
            // Alongside discovery rather than after it: both are a few process launches at most,
            // and neither should wait on the other.
            var desktop = ReadDesktopAsync(ct);
            var discovered = await _reader.DiscoverAsync(ct: ct).ConfigureAwait(false);
            Desktop = await desktop.ConfigureAwait(false);

            // Preserve what is already known about a host that survived re-discovery, so a
            // refresh does not visually reset every row to Disconnected.
            var previous = _hosts.ToDictionary(kv => kv.Key, kv => kv.Value);
            _hosts.Clear();

            foreach (var host in discovered)
            {
                _hosts[host.Id] = Annotate(previous.TryGetValue(host.Id, out var known)
                    ? host with { State = known.State, StateDetail = known.StateDetail, Capabilities = known.Capabilities }
                    : host);
            }

            Changed?.Invoke();
        }
        finally
        {
            _gate.Release();
        }

        if (ChooseInitial() is { } target) await SelectCoreAsync(target, remember: false, force: false, ct).ConfigureAwait(false);

        // Docker Desktop's pipes are cheap to ask and are where aliases hide, so they are checked
        // once now rather than when the Hosts page next opens. Not awaited: an engine that is not
        // answering costs a probe timeout, and the app must not open that much later because of it.
        _ = ProbeNamedPipesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Where the app opens: the host already selected, else the one the user last chose, else the
    /// one the Docker CLI would use.
    /// <para>
    /// A remembered Docker Desktop engine that is not the current one is passed over for the one
    /// that is. Opening on an engine that cannot answer until Docker Desktop is switched would show
    /// an empty app; the picker still offers the switch.
    /// </para>
    /// </summary>
    string? ChooseInitial()
    {
        if (Selected is not null && _hosts.ContainsKey(Selected.Id)) return Selected.Id;

        if (_selection?.Load() is { } remembered && _hosts.TryGetValue(remembered, out var host))
        {
            if (host.DesktopMode is not { } mode || !Desktop.NeedsSwitchTo(mode)) return remembered;

            if (_hosts.Values.FirstOrDefault(h => h.DesktopMode == Desktop.Mode) is { } current) return current.Id;
        }

        return _hosts.Values.FirstOrDefault(h => h.IsCurrent)?.Id ?? _hosts.Keys.FirstOrDefault();
    }

    async Task<DockerDesktopState> ReadDesktopAsync(CancellationToken ct)
        => _desktop is null ? DockerDesktopState.NotInstalled : await _desktop.ReadAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Switch to a host the user chose: tear the old connection down, seed and stream the new one,
    /// and remember the choice for the next launch.
    /// </summary>
    public Task SelectAsync(string hostId, CancellationToken ct = default)
        => SelectCoreAsync(hostId, remember: true, force: false, ct);

    /// <summary>
    /// Select a host and connect to it now, even if it is already selected.
    /// <para>
    /// For when something has just made it reachable — setup started the engine in a distribution,
    /// say. Selecting an already-selected host is otherwise a no-op, and the screen would stay
    /// "unreachable" until the pump's backoff came round to trying again.
    /// </para>
    /// </summary>
    public Task ConnectNowAsync(string hostId, CancellationToken ct = default)
        => SelectCoreAsync(hostId, remember: true, force: true, ct);

    /// <param name="remember">Whether this was the user's choice, as opposed to Dray's fallback.</param>
    /// <param name="force">
    /// Reconnect even if this host is already selected. After Docker Desktop switches mode, the
    /// selected engine has just come up, and waiting out the pump's backoff to notice would leave
    /// the screen empty for no reason.
    /// </param>
    async Task SelectCoreAsync(string hostId, bool remember, bool force, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_hosts.TryGetValue(hostId, out var host)) return;
            if (remember) _selection?.Save(hostId);
            if (!force && Selected?.Id == hostId && _pump is not null) return;

            await TeardownAsync().ConfigureAwait(false);

            // The previous host's containers are not this host's containers.
            Store.Clear();

            Selected = host;
            ResetRemote(host);
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

        var live = _runtime?.Capabilities ?? RuntimeCapabilities.None;

        var updated = Annotate(Selected with
        {
            State = state,
            StateDetail = detail,

            // A runtime that has not connected yet knows nothing, and the host keeps what an earlier
            // probe learned — above all which engine it was, which is what keeps an alias hidden
            // while the connection is coming up.
            Capabilities = live == RuntimeCapabilities.None && Selected.Capabilities.EngineId is not null
                ? Selected.Capabilities
                : WithCompose(Selected, live),
        });

        Selected = updated;
        _hosts[updated.Id] = updated;

        if (state == HostConnectionState.Connected) _ = LearnComposeAsync(updated);
        Changed?.Invoke();

        // Now connected and known to be an engine that another host already names more precisely:
        // move to that one, so the picker's check mark lands on a row it actually shows.
        if (state == HostConnectionState.Connected && CanonicalFor(updated) is { } canonical)
        {
            _ = SelectCoreAsync(canonical.Id, remember: false, force: false, CancellationToken.None);
        }
    }

    /// <summary>
    /// The host that names the same engine more precisely than <paramref name="host"/>, or null when
    /// <paramref name="host"/> is the one to show.
    /// <para>
    /// Docker Desktop's own pipe beats everything, because it means one engine whatever mode is
    /// current; <c>docker_engine</c> means whichever is current, and changes meaning on a switch.
    /// A context beats a pipe Dray found by probing, because it is the name the user knows it by.
    /// </para>
    /// </summary>
    public DockerHost? CanonicalFor(DockerHost host)
    {
        if (host.Capabilities.EngineId is not { } id) return null;

        return _hosts.Values
            .Where(other => other.Id != host.Id && other.Capabilities.EngineId == id)
            .Where(other => Specificity(other) < Specificity(host)
                || (Specificity(other) == Specificity(host) && string.CompareOrdinal(other.Id, host.Id) < 0))
            .OrderBy(Specificity)
            .ThenBy(other => other.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    static int Specificity(DockerHost host) => host switch
    {
        { DesktopMode: not null } => 0,
        { Origin: HostOrigin.DockerContext } => 1,
        { Origin: HostOrigin.Environment } => 2,
        _ => 3,
    };

    /// <summary>
    /// A Docker Desktop engine that is down because Docker Desktop is elsewhere says so, instead of
    /// showing the 500 or the timeout that is all its pipe gives back.
    /// <para>
    /// The inactive engine's pipe does exist, so the error reads as the engine being broken. It is
    /// not broken, it is switched off, and the fix is one action the picker can take.
    /// </para>
    /// </summary>
    DockerHost Annotate(DockerHost host)
    {
        if (host.DesktopMode is not { } mode || host.State == HostConnectionState.Connected || !Desktop.Installed)
            return host;

        if (!Desktop.Running)
            return host with { StateDetail = "Docker Desktop is not running." };

        if (Desktop.NeedsSwitchTo(mode) && Desktop.Mode is { } current)
        {
            return host with
            {
                StateDetail = $"Docker Desktop is running {DockerDesktop.Label(current)}. Switching to this engine switches it to {DockerDesktop.Label(mode)}.",
            };
        }

        return host;
    }

    /// <summary>
    /// Whether choosing <paramref name="host"/> means switching Docker Desktop first, and to which
    /// mode. The picker asks before doing it, because the switch stops the other engine.
    /// </summary>
    public DesktopEngineMode? SwitchNeededFor(DockerHost host)
        => host.DesktopMode is { } mode && Desktop.NeedsSwitchTo(mode) ? mode : null;

    /// <summary>Whether choosing <paramref name="host"/> means starting Docker Desktop first.</summary>
    public bool StartNeededFor(DockerHost host)
        => host.DesktopMode is not null && Desktop is { Installed: true, Running: false };

    /// <summary>
    /// Switch Docker Desktop to <paramref name="mode"/> and connect to that engine. Null on success,
    /// or a sentence saying why not.
    /// </summary>
    public async Task<string?> SwitchDesktopAsync(DesktopEngineMode mode, CancellationToken ct = default)
    {
        if (_desktop is null) return "Docker Desktop is not installed.";

        using var _ = Transitioning($"Switching Docker Desktop to {DockerDesktop.Label(mode)}…");

        var problem = await _desktop.UseAsync(mode, ct).ConfigureAwait(false);
        return problem ?? await FollowDesktopAsync(mode, ct).ConfigureAwait(false);
    }

    /// <summary>Start Docker Desktop and connect to its current engine. Null on success.</summary>
    public async Task<string?> StartDesktopAsync(DesktopEngineMode? mode = null, CancellationToken ct = default)
    {
        if (_desktop is null) return "Docker Desktop is not installed.";

        string? problem;

        using (Transitioning("Starting Docker Desktop…"))
        {
            problem = await _desktop.StartAsync(ct).ConfigureAwait(false);
        }

        if (problem is not null) return problem;

        // Started in whichever mode it was last in. If that is not the one asked for, switch too:
        // the user chose an engine, not "Docker Desktop".
        Desktop = await ReadDesktopAsync(ct).ConfigureAwait(false);

        return mode is { } wanted && Desktop.NeedsSwitchTo(wanted)
            ? await SwitchDesktopAsync(wanted, ct).ConfigureAwait(false)
            : await FollowDesktopAsync(Desktop.Mode, ct).ConfigureAwait(false);
    }

    /// <summary>After Docker Desktop has changed, re-read it, re-label its hosts and select the engine now answering.</summary>
    async Task<string?> FollowDesktopAsync(DesktopEngineMode? mode, CancellationToken ct)
    {
        Desktop = await ReadDesktopAsync(ct).ConfigureAwait(false);

        string? target;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var host in _hosts.Values.Where(h => h.DesktopMode is not null).ToList())
            {
                // The engine that was current is now the inactive one and the reverse, so neither
                // row's last known state is true any more.
                _hosts[host.Id] = Annotate(host with { State = HostConnectionState.Disconnected, StateDetail = null });
            }

            target = _hosts.Values.FirstOrDefault(h => h.DesktopMode is { } m && m == (mode ?? Desktop.Mode))?.Id;
            Changed?.Invoke();
        }
        finally
        {
            _gate.Release();
        }

        if (target is null) return null;

        await SelectCoreAsync(target, remember: true, force: true, ct).ConfigureAwait(false);
        _ = ProbeNamedPipesAsync(CancellationToken.None);
        return null;
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
    /// <summary>
    /// Whether the selected engine's background service is something Dray can see and control.
    /// <para>
    /// A type test rather than a capability flag, because this is not a property of the connection
    /// — it is a property of the engine running on this machine. Dray cannot start a service over
    /// SSH and should not offer to.
    /// </para>
    /// </summary>
    public bool CanControlService => _runtime is IEngineService;

    /// <summary>What the selected engine's service is doing, or null where there is nothing to ask.</summary>
    public Task<EngineServiceState>? ServiceStatusAsync(CancellationToken ct = default)
        => _runtime is IEngineService service ? service.ServiceStatusAsync(ct) : null;

    public async Task<string?> StartServiceAsync(CancellationToken ct = default)
    {
        if (_runtime is not IEngineService service) return "This engine has no service Dray can start.";

        var error = await service.StartServiceAsync(ct).ConfigureAwait(false);

        // A service that has just started changes every answer the app is showing, so the host is
        // reconnected rather than left reporting what was true a moment ago.
        if (error is null && Selected is { } host) await SelectAsync(host.Id, ct).ConfigureAwait(false);

        return error;
    }

    public async Task<string?> StopServiceAsync(CancellationToken ct = default)
    {
        if (_runtime is not IEngineService service) return "This engine has no service Dray can stop.";

        var error = await service.StopServiceAsync(ct).ConfigureAwait(false);
        Changed?.Invoke();

        return error;
    }

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

    readonly ComposeCli _compose;
    readonly BuildxCli _buildx = new();

    /// <summary>
    /// How compose is invoked here, or null when it is not installed.
    /// <para>
    /// Compose is a CLI plugin, not an engine feature, so it is probed on this machine rather than
    /// asked of the daemon — a remote engine can be running a stack Dray cannot drive.
    /// </para>
    /// </summary>
    public Task<ComposeCommand?> DetectComposeAsync(CancellationToken ct = default)
        => _compose.DetectAsync(Selected?.Endpoint, ct);

    /// <summary>Which hosts have a compose that can drive them, as measured — see <see cref="LearnComposeAsync"/>.</summary>
    readonly Dictionary<string, bool> _composeFor = [];

    /// <summary>
    /// Find out whether compose can drive <paramref name="host"/>, and put the answer in its
    /// capabilities.
    /// <para>
    /// The engine cannot say: compose is a client, not a daemon feature, so <c>/info</c> has no
    /// opinion and the runtime reports false. Left at that, every engine claimed it could not run
    /// a stack — the Hosts page said so and the Stacks page said "compose does not target this
    /// engine" under any empty list. Asked once per host, when it first connects.
    /// </para>
    /// </summary>
    async Task LearnComposeAsync(DockerHost host)
    {
        if (_composeFor.ContainsKey(host.Id)) return;

        // Found, and able to reach this host: a compose that cannot address an engine cannot drive it.
        var found = await _compose.DetectAsync(host.Endpoint).ConfigureAwait(false) is { } command
            && ComposeTarget.For(host, command) is not null;
        _composeFor[host.Id] = found;

        if (!found || !_hosts.TryGetValue(host.Id, out var current)) return;

        var updated = current with { Capabilities = WithCompose(current, current.Capabilities) };
        _hosts[host.Id] = updated;
        if (Selected?.Id == host.Id) Selected = updated;

        Changed?.Invoke();
    }

    /// <summary>Capabilities as the runtime reported them, plus compose where it was found to work.</summary>
    RuntimeCapabilities WithCompose(DockerHost host, RuntimeCapabilities capabilities)
        => capabilities.Flavor is EngineFlavor.Docker or EngineFlavor.Podman && _composeFor.GetValueOrDefault(host.Id)
            ? capabilities with { SupportsCompose = true }
            : capabilities;

    /// <summary>
    /// The stacks currently on this host, assembled from the labels compose puts on containers.
    /// <para>
    /// Read from the store rather than the engine, so it follows the event stream like everything
    /// else: a container starting or dying updates its stack without a refresh.
    /// </para>
    /// </summary>
    public IReadOnlyList<StackSummary> Stacks
    {
        get
        {
            if (Catalog is not { } catalog) return StackDiscovery.From(Store.Containers);

            HashSet<string> onHost;
            lock (_onHost) onHost = [.. _onHost];

            return catalog.For(Selected?.Id, Store.Containers, onHost);
        }
    }

    /// <summary>
    /// Run a compose subcommand against the selected host, streaming its output.
    /// <para>
    /// The last item carries the exit code (<see cref="ComposeOutput.IsExit"/>) whenever the
    /// process reported one.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<ComposeOutput> RunComposeAsync(
        StackSummary stack,
        IReadOnlyList<string> arguments,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (await RemoteFor(stack, ct).ConfigureAwait(false) is { } remote)
        {
            await foreach (var line in remote.RunAsync(stack.Name, stack.ConfigFiles, arguments, RemoteDirectory(stack), ct).ConfigureAwait(false))
                yield return line;

            yield break;
        }

        if (await _compose.DetectAsync(Selected?.Endpoint, ct).ConfigureAwait(false) is not { } command)
        {
            yield return new ComposeOutput(Selected?.Endpoint.Scheme == EndpointScheme.Wsl ? $"Compose is not installed in {Selected.Endpoint.Distro}." : "Compose is not installed on this machine.", IsError: true);
            yield break;
        }

        // Read once: the selection can move mid-run, and the run must stay with the host it
        // started on rather than finish against whichever one is on screen by then.
        if (Selected is not { } host || ComposeTarget.For(host, command) is not { } target)
        {
            yield return new ComposeOutput(
                $"{command.Display} has no way to address {Selected?.Endpoint.Display ?? "this host"}.", IsError: true);
            yield break;
        }

        var (files, directory) = Locate(stack, target);

        await foreach (var line in _compose
            .RunAsync(command, target, stack.Name, files, arguments, directory, ct)
            .ConfigureAwait(false))
        {
            yield return line;
        }
    }

    /// <summary>
    /// The files and directory to run compose with — or none at all, for a stack whose files are
    /// not on this machine.
    /// <para>
    /// Paths from labels name the disk of whichever machine ran <c>compose up</c>. Handing compose a
    /// <c>-f</c> that does not exist fails every command; handing it only <c>-p</c> lets the ones
    /// that work from labels alone — start, stop, restart, down — do so
    /// (<see cref="StackCommands.NeedsFiles"/>).
    /// </para>
    /// </summary>
    /// <para>
    /// Inside a WSL distribution the paths are the distribution's own, and compose runs there, so
    /// they are passed as compose recorded them — they mean what they say where they are read.
    /// </para>
    static (IReadOnlyList<string> Files, string? Directory) Locate(StackSummary stack, ComposeTarget target)
    {
        if (target.Distro is not null)
            return (stack.ConfigFiles, stack.WorkingDirectory ?? (stack.ConfigFiles.Count > 0 ? RemoteDirectoryOf(stack.ConfigFiles[0]) : null));

        return stack.ConfigFiles.Count > 0 && stack.ConfigFiles.All(File.Exists)
            ? (stack.ConfigFiles, stack.WorkingDirectory is { } wd && Directory.Exists(wd) ? wd : Path.GetDirectoryName(stack.ConfigFiles[0]))
            : ([], null);
    }

    /// <summary>
    /// What Deploy would change, service by service — see <see cref="StackDrift"/>. Null when there
    /// is nothing to compare: no compose, no files here, or files compose refuses.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ServiceDrift>?> DriftAsync(StackSummary stack, CancellationToken ct = default)
    {
        if (await RemoteFor(stack, ct).ConfigureAwait(false) is { } remote)
        {
            var remoteHashes = await remote.HashesAsync(stack.Name, stack.ConfigFiles, RemoteDirectory(stack), ct).ConfigureAwait(false);
            return remoteHashes is null ? null : StackDrift.Compare(stack, remoteHashes);
        }

        if (await _compose.DetectAsync(Selected?.Endpoint, ct).ConfigureAwait(false) is not { } command) return null;
        if (Selected is not { } host || ComposeTarget.For(host, command) is not { } target) return null;

        var (files, directory) = Locate(stack, target);
        if (files.Count == 0) return null;

        var hashes = await _compose.HashesAsync(command, target, stack.Name, files, directory, ct).ConfigureAwait(false);
        return hashes is null ? null : StackDrift.Compare(stack, hashes);
    }

    /// <summary>
    /// The command line a compose run would use, for showing before it runs. Null when compose is
    /// not installed or cannot reach the selected host — there is then no command to show.
    /// </summary>
    public async Task<string?> DescribeComposeAsync(StackSummary stack, IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        if (await RemoteFor(stack, ct).ConfigureAwait(false) is { } remote)
            return remote.Display(stack.Name, stack.ConfigFiles, arguments, RemoteDirectory(stack));

        if (await _compose.DetectAsync(Selected?.Endpoint, ct).ConfigureAwait(false) is not { } command) return null;
        if (Selected is not { } host || ComposeTarget.For(host, command) is not { } target) return null;

        var (files, _) = Locate(stack, target);
        return ComposeCli.Display(command, target, stack.Name, files, arguments);
    }

    /// <summary>
    /// Have compose check a stack against the selected host — what <c>up</c> would read, without
    /// running it.
    /// </summary>
    public async Task<ComposeConfig> ValidateComposeAsync(StackSummary stack, CancellationToken ct = default)
    {
        if (await RemoteFor(stack, ct).ConfigureAwait(false) is { } remote)
            return await remote.ConfigAsync(stack.Name, stack.ConfigFiles, RemoteDirectory(stack), ct).ConfigureAwait(false);

        if (await _compose.DetectAsync(Selected?.Endpoint, ct).ConfigureAwait(false) is not { } command)
            return new ComposeConfig(null, [new ComposeProblem("Compose is not installed on this machine.", ProblemSeverity.Error)], null);

        if (Selected is not { } host || ComposeTarget.For(host, command) is not { } target)
        {
            return new ComposeConfig(
                null,
                [new ComposeProblem($"{command.Display} has no way to address {Selected?.Endpoint.Display ?? "this host"}.", ProblemSeverity.Error)],
                null);
        }

        var (files, directory) = Locate(stack, target);

        return await _compose
            .ConfigAsync(command, target, stack.Name, files, directory, ct)
            .ConfigureAwait(false);
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

    /// <summary>
    /// Whether an image's tag has moved at its registry. Asked anonymously first, and with the
    /// stored credential only when the registry refuses — fetching a credential can mean the system
    /// asking for permission, and a public image should never cause that.
    /// </summary>
    public async Task<ImageUpdate> CheckImageUpdateAsync(string reference, RegistryReader registries, CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) return ImageUpdate.Unknown("Not connected to an engine.");

        var anonymous = await runtime.CheckImageUpdateAsync(reference, null, ct).ConfigureAwait(false);
        if (!anonymous.NeedsCredential) return anonymous;

        var credential = await registries.FindForAsync(ImageTag.Parse(reference).Registry, ct).ConfigureAwait(false);
        return credential is null
            ? anonymous with { Detail = "The registry would not answer without signing in. Sign in on the Registries page to check it." }
            : await runtime.CheckImageUpdateAsync(reference, credential, ct).ConfigureAwait(false);
    }

    /// <summary>What an image sets on its own, or null when the engine cannot say.</summary>
    public async Task<ImageDefaults?> GetImageDefaultsAsync(string image, CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) return null;

        return await runtime.GetImageDefaultsAsync(image, ct).ConfigureAwait(false);
    }

    /// <returns>Null on success, or a sentence explaining what went wrong.</returns>
    public Task<string?> RemoveImageAsync(string imageId, bool force = false, CancellationToken ct = default)
        => TryAsync(runtime => runtime.RemoveImageAsync(imageId, force, ct));

    public Task<string?> TagImageAsync(string imageId, string repository, string tag, CancellationToken ct = default)
        => TryAsync(runtime => runtime.TagImageAsync(imageId, repository, tag, ct));

    /// <summary>Ask the engine to search a registry. Throws what the engine said, for the caller to show.</summary>
    public async Task<IReadOnlyList<ImageSearchResult>> SearchImagesAsync(
        string term, int limit = 25, ImageSearchFilters? filters = null, CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) return [];

        return await runtime.SearchImagesAsync(term, limit, filters, ct).ConfigureAwait(false);
    }

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

    /// <summary>
    /// Pull an image, streaming per-layer progress — anonymously first, and with the stored
    /// credential only if the registry refuses.
    /// <para>
    /// Anonymous first because almost every pull is of a public image, and fetching a credential can
    /// raise a keychain prompt — the same order the update check uses. The retry happens only when
    /// the refusal arrives before any layer has moved, so a pull is never started twice.
    /// </para>
    /// <para>
    /// With nothing stored, the engine's refusal is passed on with what it most likely means and what
    /// to do about it. The engine's own sentence — "repository does not exist or may require 'docker
    /// login'" — names a command Dray users may never type.
    /// </para>
    /// </summary>
    /// <param name="registries">The credential store, or null to pull anonymously and only that.</param>
    public async IAsyncEnumerable<PullProgress> PullImageAsync(
        string reference,
        RegistryReader? registries = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_runtime is not { } runtime) yield break;

        PullProgress? refusal = null;
        var moved = false;

        await foreach (var step in runtime.PullImageAsync(reference, null, ct).ConfigureAwait(false))
        {
            if (step.IsError && !moved && registries is not null && PullRefusal.NeedsCredential(step.Error))
            {
                refusal = step;
                break;
            }

            moved |= step.LayerId is not null;
            yield return step;
        }

        if (refusal is null || registries is null) yield break;

        var draft = ImageReferenceDraft.Parse(reference);
        var credential = await registries.FindForAsync(RegistryHosts.CredentialServer(draft.Host), ct).ConfigureAwait(false);

        if (credential is null)
        {
            yield return refusal with { Error = PullRefusal.Explain(refusal.Error!, RegistryHosts.NameOf(draft.Host)) };
            yield break;
        }

        await foreach (var step in runtime.PullImageAsync(reference, credential, ct).ConfigureAwait(false))
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
    /// <summary>
    /// Build an image, through the engine or through a named buildx builder.
    /// </summary>
    /// <param name="builder">
    /// A buildx builder to build on, or null for the engine's own builder.
    /// <para>
    /// Two genuinely different paths, not a flag on one. The engine API build is what Dray uses by
    /// default because it needs nothing installed and streams the engine's own output; buildx is a
    /// separate program with its own builders, and choosing one means running it.
    /// </para>
    /// </param>
    public async IAsyncEnumerable<BuildProgress> BuildImageAsync(
        BuildRequest request,
        string? builder = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (builder is { Length: > 0 })
        {
            if (await _buildx.DetectAsync(ct).ConfigureAwait(false) is not { } command)
            {
                yield return new BuildProgress("", "buildx is not installed on this machine.");
                yield break;
            }

            await foreach (var line in _buildx.BuildAsync(command, request, builder, ct).ConfigureAwait(false))
            {
                // buildx writes its progress to stderr — every line of it, not just failures — so
                // a stream marked as error is not one. The exit code is what says whether it
                // worked, and the runner reports that as a final error line.
                yield return new BuildProgress(line.Text);
            }

            yield break;
        }

        if (_runtime is not { } runtime) yield break;

        await foreach (var step in runtime.BuildImageAsync(request, ct).ConfigureAwait(false))
            yield return step;
    }

    /// <summary>buildx, if this machine has it. Probed once.</summary>
    public Task<BuildxCommand?> DetectBuildxAsync(CancellationToken ct = default)
        => _buildx.DetectAsync(ct);

    /// <summary>
    /// The builders buildx has configured, or nothing when it is not installed.
    /// </summary>
    public async Task<IReadOnlyList<BuildxBuilder>> ListBuildersAsync(CancellationToken ct = default)
    {
        if (await _buildx.DetectAsync(ct).ConfigureAwait(false) is not { } command) return [];

        try
        {
            return await _buildx.ListAsync(command, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // buildx is present but would not answer. The picker simply does not appear.
            return [];
        }
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
        // A stopped WSL distribution is left alone: answering would mean booting it, and opening a
        // page is not a reason to start a virtual machine. It is connected when it is chosen.
        var targets = _hosts.Values.Where(h => h.Id != Selected?.Id && !h.StartsOnConnect).ToList();

        // In parallel: one slow host must not delay the rest of the picker.
        await Task.WhenAll(targets.Select(host => ProbeAsync(host, ct))).ConfigureAwait(false);
    }

    /// <summary>
    /// Place a host directly. For tests of rules that discovery cannot reach on the machine running
    /// them — a WSL distribution is only ever discovered on Windows.
    /// </summary>
    internal void AddForTest(DockerHost host) => _hosts[host.Id] = host;

    /// <summary>
    /// Check the named pipes only — the cheap, local hosts where Windows' aliases are.
    /// <para>
    /// <c>docker_engine</c> is one of Docker Desktop's engines under another name, and which one is
    /// only known once both have said who they are. This is what lets the picker show one row per
    /// engine from the moment the app opens, rather than after the Hosts page has been visited.
    /// </para>
    /// </summary>
    async Task ProbeNamedPipesAsync(CancellationToken ct)
    {
        var targets = _hosts.Values
            .Where(h => h.Id != Selected?.Id && h.Endpoint.Scheme == EndpointScheme.NamedPipe)
            .ToList();

        if (targets.Count == 0) return;

        try
        {
            await Task.WhenAll(targets.Select(host => ProbeAsync(host, ct))).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Probing is a nicety on top of a connection that already works; nothing here is worth
            // surfacing, and an unobserved failure must not take the process down.
            return;
        }

        // The selected host may turn out to be another's alias only now that the other has answered.
        if (Selected is { } selected && CanonicalFor(selected) is { } canonical)
        {
            await SelectCoreAsync(canonical.Id, remember: false, force: false, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Connect to an endpoint once and report what answered, without adding it anywhere.
    /// <para>
    /// What the add-a-host flow uses before it writes a context: a host that is saved and then
    /// found not to work is a broken entry in the picker the user has to go and remove.
    /// </para>
    /// </summary>
    /// <returns>The engine's capabilities, or a sentence saying why it did not answer.</returns>
    public async Task<(RuntimeCapabilities? Capabilities, string? Problem)> TestAsync(
        DockerEndpoint endpoint, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);

        await using var runtime = _factory.Create(endpoint);

        try
        {
            return (await runtime.ConnectAsync(timeout.Token).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, "Timed out reaching the host.");
        }
        catch (Exception ex)
        {
            return (null, RuntimeEventPump.Describe(ex));
        }
    }

    async Task ProbeAsync(DockerHost host, CancellationToken ct)
    {
        DockerHost result;

        try
        {
            var (capabilities, problem) = await TestAsync(host.Endpoint, ct).ConfigureAwait(false);

            result = capabilities is not null
                ? host with { State = HostConnectionState.Connected, StateDetail = null, Capabilities = WithCompose(host, capabilities) }
                : host with { State = HostConnectionState.Unreachable, StateDetail = problem };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }

        // The selection may have moved while the probe was in flight; do not overwrite the live
        // state of a host that is now the connected one.
        if (result.Id == Selected?.Id) return;
        if (!_hosts.ContainsKey(result.Id)) return;

        _hosts[result.Id] = Annotate(result);
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
