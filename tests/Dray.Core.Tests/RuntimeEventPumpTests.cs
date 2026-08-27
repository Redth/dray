using System.Net.Sockets;
using System.Threading.Channels;
using Dray.Core.Engine;
using Dray.Core.Model;
using Microsoft.Extensions.Time.Testing;
using Dray.Core.Tests.Fakes;
using Xunit;

namespace Dray.Core.Tests;

public class RuntimeEventPumpTests
{
    static ContainerSummary Container(string id, string name, DockerState state = DockerState.Running)
        => new() { Id = id, Name = name, Image = "nginx:1", State = state, Ports = [new PortBinding(8080, 80)] };

    static RuntimeEvent Event(string action, string id, params (string Key, string Value)[] attributes)
        => new(RuntimeEntity.Container, action, id, attributes.ToDictionary(a => a.Key, a => a.Value), DateTimeOffset.UnixEpoch);

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

    [Fact]
    public async Task ConnectingSeedsTheStoreFromAColdListExactlyOnce()
    {
        var runtime = new FakeRuntime { Containers = [Container("a", "web"), Container("b", "api")] };
        var store = new EntityStore();
        await using var pump = new RuntimeEventPump(runtime, store);

        pump.Start();

        Assert.True(await WaitFor(() => pump.State == HostConnectionState.Connected));
        Assert.Equal(2, store.Count);
        Assert.Equal(1, runtime.ListCalls);
    }

    [Fact]
    public async Task EventsMutateTheStoreWithoutRefetchingTheList()
    {
        // The rule this whole design exists for: no poll loop, and no list re-fetch per event.
        var runtime = new FakeRuntime { Containers = [Container("a", "web")] };
        var store = new EntityStore();
        await using var pump = new RuntimeEventPump(runtime, store);

        pump.Start();
        Assert.True(await WaitFor(() => pump.State == HostConnectionState.Connected));

        runtime.Emit(Event("die", "a", ("exitCode", "137")));

        Assert.True(await WaitFor(() => store.Find("a")!.State == DockerState.Exited));
        Assert.Equal(137, store.Find("a")!.ExitCode);
        Assert.Equal(1, runtime.ListCalls);
    }

    [Fact]
    public async Task AnEventForAnUnknownContainerTriggersExactlyOneBackfill()
    {
        var runtime = new FakeRuntime { Containers = [Container("a", "web")] };
        var store = new EntityStore();
        await using var pump = new RuntimeEventPump(runtime, store);

        pump.Start();
        Assert.True(await WaitFor(() => pump.State == HostConnectionState.Connected));

        // The engine now has a container the store has never seen.
        runtime.Containers = [Container("a", "web"), Container("new", "sidecar")];
        runtime.Emit(Event("start", "new"));

        Assert.True(await WaitFor(() => store.Find("new") is not null));
        Assert.Equal("sidecar", store.Find("new")!.Name);
        Assert.Equal(2, runtime.ListCalls);
    }

    // ---------------------------------------------------------------- failure

    // ---------------------------------------------------------------- no event stream

    [Fact]
    public async Task AnEngineWithNoEventStreamIsPolledInstead()
    {
        // Apple's runtime has no `events` subcommand at all. Waiting on a stream that will never
        // speak would leave the list frozen at whatever the cold seed found.
        var runtime = new FakeRuntime { SupportsEvents = false, Containers = [Container("a", "web")] };
        var store = new EntityStore();
        var time = new FakeTimeProvider();

        await using var pump = new RuntimeEventPump(runtime, store, time)
        {
            PollInterval = TimeSpan.FromSeconds(2),
        };

        pump.Start();
        Assert.True(await WaitFor(() => pump.State == HostConnectionState.Connected));
        Assert.Equal(1, runtime.ListCalls);

        runtime.Containers = [Container("a", "web", DockerState.Exited), Container("b", "api")];
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.True(await WaitFor(() => store.Count == 2));
        Assert.Equal(DockerState.Exited, store.Find("a")!.State);
    }

    [Fact]
    public async Task PollingNoticesAContainerThatIsGone()
    {
        var runtime = new FakeRuntime { SupportsEvents = false, Containers = [Container("a", "web"), Container("b", "api")] };
        var store = new EntityStore();
        var time = new FakeTimeProvider();

        await using var pump = new RuntimeEventPump(runtime, store, time) { PollInterval = TimeSpan.FromSeconds(2) };

        pump.Start();
        Assert.True(await WaitFor(() => store.Count == 2));

        runtime.Containers = [Container("a", "web")];
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.True(await WaitFor(() => store.Count == 1));
        Assert.Null(store.Find("b"));
    }

    [Fact]
    public async Task AnUnchangedListProducesNoStoreChangesAtAll()
    {
        var runtime = new FakeRuntime { SupportsEvents = false, Containers = [Container("a", "web")] };
        var store = new EntityStore();
        var time = new FakeTimeProvider();

        await using var pump = new RuntimeEventPump(runtime, store, time) { PollInterval = TimeSpan.FromSeconds(2) };

        pump.Start();
        Assert.True(await WaitFor(() => pump.State == HostConnectionState.Connected));

        // Counted after the cold seed, so only what polling itself causes is measured.
        var changes = 0;
        store.Changed += _ => changes++;

        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            Assert.True(await WaitFor(() => runtime.ListCalls >= 2 + i));
        }

        // The whole point of diffing rather than resetting. A reset twice a second would flash the
        // list, clear every pending action, and fire the change highlight on rows nobody touched —
        // and Ports being a list means record equality would report a change every single time.
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task PollingThatFailsDegradesTheHostTheSameWayADroppedStreamDoes()
    {
        var runtime = new FakeRuntime { SupportsEvents = false, Containers = [Container("a", "web")] };
        var store = new EntityStore();
        var time = new FakeTimeProvider();

        await using var pump = new RuntimeEventPump(runtime, store, time)
        {
            PollInterval = TimeSpan.FromSeconds(2),
            InitialRetryDelay = TimeSpan.FromSeconds(1),
        };

        pump.Start();
        Assert.True(await WaitFor(() => pump.State == HostConnectionState.Connected));

        runtime.ConnectFailure = new RuntimeConnectionException("The engine went away.");
        runtime.ListFailure = new RuntimeConnectionException("The engine went away.");
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.True(await WaitFor(() => pump.State == HostConnectionState.Degraded));

        // Still listed, marked stale: the container exists, Dray just cannot see it.
        Assert.Equal(1, store.Count);
        Assert.Equal(DockerState.Unknown, store.Find("a")!.State);
    }

    [Fact]
    public async Task AHostThatNeverConnectedIsUnreachableNotDegraded()
    {
        var runtime = new FakeRuntime { ConnectFailure = new SocketException((int)SocketError.ConnectionRefused) };
        var store = new EntityStore();
        await using var pump = new RuntimeEventPump(runtime, store)
        {
            InitialRetryDelay = TimeSpan.FromMilliseconds(10),
        };

        pump.Start();

        Assert.True(await WaitFor(() => pump.State == HostConnectionState.Unreachable));
        Assert.Equal("The engine is not running.", pump.StateDetail);
    }

    [Fact]
    public async Task LosingAConnectionDegradesRatherThanGoingUnreachable()
    {
        // Degraded says "what you see is real but going stale". Unreachable says "I never got
        // there". Collapsing them would lose a distinction the user acts on.
        var runtime = new FakeRuntime { Containers = [Container("a", "web")] };
        var store = new EntityStore();
        await using var pump = new RuntimeEventPump(runtime, store)
        {
            InitialRetryDelay = TimeSpan.FromMinutes(5),   // never actually retries during the test
        };

        pump.Start();
        Assert.True(await WaitFor(() => pump.State == HostConnectionState.Connected));

        runtime.FailStream(new IOException("connection reset"));

        Assert.True(await WaitFor(() => pump.State == HostConnectionState.Degraded));
    }

    [Fact]
    public async Task AnUnreachableHostKeepsItsRowsButMarksThemStale()
    {
        var runtime = new FakeRuntime { Containers = [Container("a", "web")] };
        var store = new EntityStore();
        await using var pump = new RuntimeEventPump(runtime, store)
        {
            InitialRetryDelay = TimeSpan.FromMinutes(5),
        };

        pump.Start();
        Assert.True(await WaitFor(() => pump.State == HostConnectionState.Connected));

        runtime.FailStream(new IOException("connection reset"));
        Assert.True(await WaitFor(() => pump.State == HostConnectionState.Degraded));

        // The container still exists on the host; Dray simply cannot see it.
        Assert.Equal(1, store.Count);
        Assert.True(store.Containers[0].Status.IsStale);
    }

    [Fact]
    public async Task ReconnectsAndReseedsAfterAFailure()
    {
        var runtime = new FakeRuntime { ConnectFailure = new SocketException((int)SocketError.ConnectionRefused) };
        var store = new EntityStore();
        await using var pump = new RuntimeEventPump(runtime, store)
        {
            InitialRetryDelay = TimeSpan.FromMilliseconds(10),
        };

        pump.Start();
        Assert.True(await WaitFor(() => pump.State == HostConnectionState.Unreachable));

        // The engine comes back.
        runtime.Containers = [Container("a", "web")];
        runtime.ConnectFailure = null;

        Assert.True(await WaitFor(() => pump.State == HostConnectionState.Connected, 5000));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task RetryDelayBacksOffAndIsCapped()
    {
        var time = new FakeTimeProvider();
        var runtime = new FakeRuntime { ConnectFailure = new SocketException((int)SocketError.ConnectionRefused) };
        var store = new EntityStore();
        await using var pump = new RuntimeEventPump(runtime, store, time)
        {
            InitialRetryDelay = TimeSpan.FromSeconds(1),
            MaxRetryDelay = TimeSpan.FromSeconds(4),
        };

        pump.Start();
        Assert.True(await WaitFor(() => pump.ReconnectCount >= 1));

        // 1s, then 2s, then 4s, then capped at 4s. Advancing by less than the current delay must
        // not produce another attempt.
        foreach (var expected in new[] { 1, 2, 4, 4 })
        {
            var before = pump.ReconnectCount;
            time.Advance(TimeSpan.FromSeconds(expected - 0.1));
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Assert.Equal(before, pump.ReconnectCount);

            time.Advance(TimeSpan.FromSeconds(0.2));
            Assert.True(await WaitFor(() => pump.ReconnectCount > before), $"no retry after {expected}s");
        }
    }

    [Fact]
    public async Task DisposeStopsTheLoop()
    {
        var runtime = new FakeRuntime { Containers = [Container("a", "web")] };
        var store = new EntityStore();
        var pump = new RuntimeEventPump(runtime, store);

        pump.Start();
        Assert.True(await WaitFor(() => pump.State == HostConnectionState.Connected));

        await pump.DisposeAsync();

        Assert.Equal(HostConnectionState.Disconnected, pump.State);
    }

    [Fact]
    public async Task StartIsIdempotent()
    {
        var runtime = new FakeRuntime { Containers = [Container("a", "web")] };
        var store = new EntityStore();
        await using var pump = new RuntimeEventPump(runtime, store);

        pump.Start();
        pump.Start();

        Assert.True(await WaitFor(() => pump.State == HostConnectionState.Connected));
        Assert.Equal(1, runtime.ConnectCalls);
    }

    // ---------------------------------------------------------------- messages

    [Theory]
    [InlineData(SocketError.ConnectionRefused, "The engine is not running.")]
    // What a missing unix socket actually produces — verified against a stopped Docker Desktop,
    // where the assumed FileNotFoundException never appears.
    [InlineData(SocketError.AddressNotAvailable, "The engine is not running.")]
    [InlineData(SocketError.HostNotFound, "Host not found.")]
    [InlineData(SocketError.TimedOut, "Timed out reaching the host.")]
    [InlineData(SocketError.NetworkUnreachable, "The host is unreachable from this network.")]
    [InlineData(SocketError.ConnectionReset, "The engine closed the connection.")]
    public void SocketFailuresGetPlainLanguage(SocketError error, string expected)
        => Assert.Equal(expected, RuntimeEventPump.Describe(new SocketException((int)error)));

    [Fact]
    public void ARealStoppedEngineFailureReadsCorrectly()
    {
        // The exact shape observed connecting to a stopped Docker Desktop: the transport wraps a
        // SocketException in HttpRequestException("Connection failed.").
        var actual = new HttpRequestException(
            "Connection failed.",
            new SocketException((int)SocketError.AddressNotAvailable));

        Assert.Equal("The engine is not running.", RuntimeEventPump.Describe(actual));
    }

    [Fact]
    public void PermissionDeniedNamesTheSocket()
        => Assert.Equal(
            "Permission denied on the Docker socket.",
            RuntimeEventPump.Describe(new UnauthorizedAccessException()));

    [Fact]
    public void AMissingSocketSaysSoRatherThanBlamingTheNetwork()
        => Assert.Equal(
            "The Docker socket no longer exists.",
            RuntimeEventPump.Describe(new FileNotFoundException()));

    [Fact]
    public void HttpFailuresUnwrapToTheRealCause()
    {
        // The transport wraps everything in HttpRequestException; reporting that verbatim would
        // tell the user nothing.
        var wrapped = new HttpRequestException("boom", new SocketException((int)SocketError.ConnectionRefused));
        Assert.Equal("The engine is not running.", RuntimeEventPump.Describe(wrapped));
    }

    [Fact]
    public void AnUnrecognisedFailureStillSaysSomethingTrue()
        => Assert.Equal("Could not reach the engine.", RuntimeEventPump.Describe(new InvalidOperationException("???")));
}

/// <summary>An engine under test control: no sockets, no timing, no Docker.</summary>
sealed class FakeRuntime : StubRuntime
{
    Channel<RuntimeEvent> _events = Channel.CreateUnbounded<RuntimeEvent>();

    public IReadOnlyList<ContainerSummary> Containers { get; set; } = [];

    public Exception? ConnectFailure { get; set; }

    public int ConnectCalls { get; private set; }

    public int ListCalls { get; private set; }

    /// <summary>Whether this engine has an event stream. False is Apple's runtime.</summary>
    public bool SupportsEvents { get; set; } = true;

    public override Task<RuntimeCapabilities> ConnectAsync(CancellationToken ct = default)
    {
        ConnectCalls++;
        if (ConnectFailure is not null) throw ConnectFailure;

        // A fresh channel per connection, mirroring a real reconnect.
        if (_events.Reader.Completion.IsCompleted) _events = Channel.CreateUnbounded<RuntimeEvent>();

        Capabilities = new RuntimeCapabilities
        {
            ApiVersion = "1.45",
            Flavor = EngineFlavor.Docker,
            SupportsEvents = SupportsEvents,
        };
        return Task.FromResult(Capabilities);
    }

    public Exception? ListFailure { get; set; }

    public override Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(bool includeStopped = true, CancellationToken ct = default)
    {
        ListCalls++;
        if (ListFailure is not null) throw ListFailure;

        // Fresh instances with fresh lists, the way a real runtime deserializing a response
        // produces them. Handing back the same objects every call would let a caller comparing
        // with == look correct while the real thing found every row changed.
        return Task.FromResult<IReadOnlyList<ContainerSummary>>(
            [.. Containers.Select(c => c with { Ports = [.. c.Ports] })]);
    }

    public List<(string Id, ContainerAction Action)> Performed { get; } = [];

    public override Task PerformAsync(string containerId, ContainerAction action, CancellationToken ct = default)
    {
        Performed.Add((containerId, action));
        return Task.CompletedTask;
    }

    public override Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default)
        => Task.FromResult(new SystemInfo(0, 0, 0, 0, "fake", "1.0"));

    public override async IAsyncEnumerable<RuntimeEvent> WatchEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var e in _events.Reader.ReadAllAsync(ct)) yield return e;
    }

    public void Emit(RuntimeEvent e) => _events.Writer.TryWrite(e);

    /// <summary>Drop the stream the way a reset connection would.</summary>
    public void FailStream(Exception error) => _events.Writer.TryComplete(error);

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
