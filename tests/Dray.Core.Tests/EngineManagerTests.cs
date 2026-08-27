using System.Net.Sockets;
using Dray.Core.Engine;
using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

public class EngineManagerTests
{
    static ContainerSummary Container(string id, string name)
        => new() { Id = id, Name = name, Image = "nginx:1", State = DockerState.Running };

    static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        return condition();
    }

    static (DockerContextReader Reader, FakeRuntimeFactory Factory) Setup(params (string Name, string Host)[] contexts)
    {
        var source = new FakeConfigSource();
        foreach (var (name, host) in contexts) source = source.WithContext(name, host);
        if (contexts.Length > 0) source = source.WithConfig(contexts[0].Name);

        return (new DockerContextReader(source), new FakeRuntimeFactory());
    }

    [Fact]
    public async Task InitializeDiscoversAndConnectsTheCurrentHost()
    {
        var (reader, factory) = Setup(("alpha", "unix:///a.sock"), ("beta", "unix:///b.sock"));
        factory.Containers["unix:///a.sock"] = [Container("1", "web")];

        await using var manager = new EngineManager(reader, factory);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, manager.Hosts.Count);
        Assert.Equal("alpha", manager.Selected!.Id);
        Assert.True(await WaitFor(() => manager.Store.Count == 1));
    }

    [Fact]
    public async Task NoHostsIsTheFirstRunState()
    {
        var (reader, factory) = Setup();

        await using var manager = new EngineManager(reader, factory);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(manager.HasNoHosts);
        Assert.Null(manager.Selected);
    }

    [Fact]
    public async Task SwitchingHostsReplacesTheContainers()
    {
        // The previous host's containers are not this host's containers. Leaving them on screen
        // during the switch would show one engine's rows under another engine's name.
        var (reader, factory) = Setup(("alpha", "unix:///a.sock"), ("beta", "unix:///b.sock"));
        factory.Containers["unix:///a.sock"] = [Container("1", "from-alpha")];
        factory.Containers["unix:///b.sock"] = [Container("2", "from-beta")];

        await using var manager = new EngineManager(reader, factory);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(await WaitFor(() => manager.Store.Count == 1));

        await manager.SelectAsync("beta", TestContext.Current.CancellationToken);

        Assert.True(await WaitFor(() => manager.Store.Containers.Any(c => c.Name == "from-beta")));
        Assert.DoesNotContain(manager.Store.Containers, c => c.Name == "from-alpha");
    }

    [Fact]
    public async Task SwitchingHostsDisposesThePreviousRuntime()
    {
        var (reader, factory) = Setup(("alpha", "unix:///a.sock"), ("beta", "unix:///b.sock"));

        await using var manager = new EngineManager(reader, factory);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        await manager.SelectAsync("beta", TestContext.Current.CancellationToken);

        Assert.True(await WaitFor(() => factory.Created.Count(r => r.Disposed) >= 1));
    }

    [Fact]
    public async Task SelectingTheAlreadySelectedHostDoesNotReconnect()
    {
        var (reader, factory) = Setup(("alpha", "unix:///a.sock"));

        await using var manager = new EngineManager(reader, factory);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(await WaitFor(() => manager.Selected?.State == HostConnectionState.Connected));

        var created = factory.Created.Count;
        await manager.SelectAsync("alpha", TestContext.Current.CancellationToken);

        Assert.Equal(created, factory.Created.Count);
    }

    [Fact]
    public async Task TheSelectedHostCarriesItsConnectionState()
    {
        var (reader, factory) = Setup(("alpha", "unix:///a.sock"));

        await using var manager = new EngineManager(reader, factory);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(await WaitFor(() => manager.Selected?.State == HostConnectionState.Connected));
        Assert.Equal("1.45", manager.Selected!.Capabilities.ApiVersion);
    }

    [Fact]
    public async Task AnUnreachableSelectedHostReportsWhy()
    {
        var (reader, factory) = Setup(("alpha", "unix:///a.sock"));
        factory.Failures["unix:///a.sock"] = new SocketException((int)SocketError.ConnectionRefused);

        await using var manager = new EngineManager(reader, factory);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(await WaitFor(() => manager.Selected?.State == HostConnectionState.Unreachable));
        Assert.Equal("The engine is not running.", manager.Selected!.StateDetail);
    }

    // ---------------------------------------------------------------- probing

    [Fact]
    public async Task ProbingMarksUnselectedHostsWithoutStreamingThem()
    {
        var (reader, factory) = Setup(("alpha", "unix:///a.sock"), ("beta", "unix:///b.sock"));
        factory.Failures["unix:///b.sock"] = new SocketException((int)SocketError.ConnectionRefused);

        await using var manager = new EngineManager(reader, factory);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        await manager.ProbeOthersAsync(TestContext.Current.CancellationToken);

        var beta = manager.Hosts.Single(h => h.Id == "beta");
        Assert.Equal(HostConnectionState.Unreachable, beta.State);
        Assert.Equal("The engine is not running.", beta.StateDetail);

        // A probe connects and disposes; it never starts an event stream.
        Assert.All(factory.Created.Where(r => r.Endpoint == "unix:///b.sock"), r => Assert.False(r.EventsWatched));
    }

    [Fact]
    public async Task ProbingDoesNotDisturbTheSelectedHost()
    {
        var (reader, factory) = Setup(("alpha", "unix:///a.sock"), ("beta", "unix:///b.sock"));

        await using var manager = new EngineManager(reader, factory);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(await WaitFor(() => manager.Selected?.State == HostConnectionState.Connected));

        await manager.ProbeOthersAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HostConnectionState.Connected, manager.Selected!.State);
    }

    [Fact]
    public async Task ConnectedHostsSortAboveUnreachableOnes()
    {
        var (reader, factory) = Setup(("alpha", "unix:///a.sock"), ("beta", "unix:///b.sock"), ("gamma", "unix:///c.sock"));
        factory.Failures["unix:///b.sock"] = new SocketException((int)SocketError.ConnectionRefused);

        await using var manager = new EngineManager(reader, factory);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(await WaitFor(() => manager.Selected?.State == HostConnectionState.Connected));
        await manager.ProbeOthersAsync(TestContext.Current.CancellationToken);

        // The useful hosts are on top; beta, which cannot be reached, is last.
        Assert.Equal("beta", manager.Hosts[^1].Id);
    }

    // ---------------------------------------------------------------- actions

    [Fact]
    public async Task AnActionMarksTheRowPendingUntilAnEventSettlesIt()
    {
        // Dray shows "Stopping" rather than optimistically flipping to Exited. An optimistic
        // state is a guess, and a wrong guess leaves the row quietly lying.
        var (reader, factory) = Setup(("alpha", "unix:///a.sock"));
        factory.Containers["unix:///a.sock"] = [Container("1", "web")];

        await using var manager = new EngineManager(reader, factory);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(await WaitFor(() => manager.Store.Count == 1));

        var error = await manager.PerformAsync("1", ContainerAction.Stop, TestContext.Current.CancellationToken);

        Assert.Null(error);
        Assert.Equal(ContainerAction.Stop, manager.Store.PendingAction("1"));

        // The container is still Running: the request was accepted, not completed.
        Assert.Equal(DockerState.Running, manager.Store.Find("1")!.State);
    }

    [Fact]
    public async Task AFailedActionClearsThePendingMarkAndExplainsWhy()
    {
        // No event is coming, so nothing else would ever clear it and the row would say
        // "Stopping" forever.
        var (reader, factory) = Setup(("alpha", "unix:///a.sock"));
        factory.Containers["unix:///a.sock"] = [Container("1", "web")];

        await using var manager = new EngineManager(reader, factory);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(await WaitFor(() => manager.Store.Count == 1));

        factory.Created[0].ActionFailure = new SocketException((int)SocketError.ConnectionRefused);
        var error = await manager.PerformAsync("1", ContainerAction.Stop, TestContext.Current.CancellationToken);

        Assert.Equal("The engine is not running.", error);
        Assert.Null(manager.Store.PendingAction("1"));
    }

    [Fact]
    public async Task AnActionWithNoConnectionFailsWithoutThrowing()
    {
        var (reader, factory) = Setup();

        await using var manager = new EngineManager(reader, factory);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Not connected to an engine.", await manager.PerformAsync("1", ContainerAction.Stop, TestContext.Current.CancellationToken));
    }

    // ---------------------------------------------------------------- re-discovery

    [Fact]
    public async Task ReDiscoveryKeepsWhatIsAlreadyKnownAboutASurvivingHost()
    {
        // A refresh must not visually reset every row to Disconnected and make the picker flicker.
        var (reader, factory) = Setup(("alpha", "unix:///a.sock"));

        await using var manager = new EngineManager(reader, factory);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(await WaitFor(() => manager.Selected?.State == HostConnectionState.Connected));

        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HostConnectionState.Connected, manager.Hosts.Single().State);
    }
}

sealed class FakeRuntimeFactory : IContainerRuntimeFactory
{
    public Dictionary<string, IReadOnlyList<ContainerSummary>> Containers { get; } = [];

    public Dictionary<string, Exception> Failures { get; } = [];

    public List<TrackedRuntime> Created { get; } = [];

    public IContainerRuntime Create(DockerEndpoint endpoint)
    {
        var runtime = new TrackedRuntime(endpoint.Raw)
        {
            Containers = Containers.GetValueOrDefault(endpoint.Raw, []),
            ConnectFailure = Failures.GetValueOrDefault(endpoint.Raw),
        };

        Created.Add(runtime);
        return runtime;
    }
}

sealed class TrackedRuntime(string endpoint) : IContainerRuntime
{
    readonly TaskCompletionSource _never = new();

    public string Endpoint { get; } = endpoint;

    public bool Disposed { get; private set; }

    public bool EventsWatched { get; private set; }

    public IReadOnlyList<ContainerSummary> Containers { get; init; } = [];

    public Exception? ConnectFailure { get; init; }

    public RuntimeCapabilities Capabilities { get; private set; } = RuntimeCapabilities.None;

    public Task<RuntimeCapabilities> ConnectAsync(CancellationToken ct = default)
    {
        if (ConnectFailure is not null) throw ConnectFailure;

        Capabilities = new RuntimeCapabilities { ApiVersion = "1.45", Flavor = EngineFlavor.Docker };
        return Task.FromResult(Capabilities);
    }

    public Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(bool includeStopped = true, CancellationToken ct = default)
        => Task.FromResult(Containers);

    public List<(string Id, ContainerAction Action)> Performed { get; } = [];

    public Exception? ActionFailure { get; set; }

    public Task PerformAsync(string containerId, ContainerAction action, CancellationToken ct = default)
    {
        if (ActionFailure is not null) throw ActionFailure;

        Performed.Add((containerId, action));
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<LogLine> StreamLogsAsync(string containerId, LogOptions options, CancellationToken ct = default)
        => AsyncEnumerable.Empty<LogLine>();

    public Task<DirectoryListing> ListDirectoryAsync(string containerId, string path, bool containerIsRunning, CancellationToken ct = default)
        => Task.FromResult(new DirectoryListing(path, [], ListingMethod.Exec));

    public Task<byte[]> ReadFileAsync(string containerId, string path, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task WriteFileAsync(string containerId, string path, byte[] content, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default)
        => Task.FromResult(new SystemInfo(0, 0, 0, 0, null, null));

    public Task<DiskUsage> GetDiskUsageAsync(CancellationToken ct = default)
        => Task.FromResult(new DiskUsage(0, 0, 0, 0, 0, 0));

    public async IAsyncEnumerable<RuntimeEvent> WatchEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        EventsWatched = true;

        // Holds the stream open the way a real engine does, until cancelled.
        await using (ct.Register(() => _never.TrySetResult()))
        {
            await _never.Task.ConfigureAwait(false);
        }

        yield break;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _never.TrySetResult();
        return ValueTask.CompletedTask;
    }
}
