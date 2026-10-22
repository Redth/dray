using Dray.Core.Engine;
using Dray.Core.Tests.Fakes;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Which engine is selected, and which engines are listed: one row per engine rather than per
/// address, a choice that survives a restart, and Docker Desktop's two engines treated as the one
/// running at a time that they are.
/// </summary>
public class EngineSelectionTests
{
    const string DefaultPipe = "npipe:////./pipe/docker_engine";
    const string LinuxPipe = "npipe:////./pipe/dockerDesktopLinuxEngine";
    const string WindowsPipe = "npipe:////./pipe/dockerDesktopWindowsEngine";

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

    /// <summary>The contexts Docker Desktop leaves on a Windows machine, with the CLI on the implicit default.</summary>
    static DockerContextReader DesktopContexts() => new(new FakeConfigSource()
        .WithContext("engine", DefaultPipe)
        .WithContext("desktop-linux", LinuxPipe)
        .WithContext("desktop-windows", WindowsPipe)
        .WithConfig("engine"));

    static DockerDesktop Desktop(string currentMode)
    {
        var runner = new ScriptedRunner()
            .On(["desktop", "status"], 0, "{\"Status\":\"running\"}")
            .On(["desktop", "engine", "ls"], 0, $"[{{\"Name\":\"{currentMode}\",\"Current\":true}}]");

        return new DockerDesktop(runner, new FakeConfigSource { IsWindows = true }.WithExecutable("/dd/Docker Desktop.exe"), "/dd");
    }

    /// <summary>
    /// docker_engine is whichever of Docker Desktop's engines is current, so on this machine it is
    /// the same engine as desktop-windows. Measured by the engine id each answers with, and shown
    /// once — under the name that does not change meaning when Docker Desktop switches.
    /// </summary>
    [Fact]
    public async Task TwoAddressesForOneEngineAreOneRow()
    {
        var factory = new FakeRuntimeFactory();
        factory.EngineIds[DefaultPipe] = "windows-engine";
        factory.EngineIds[WindowsPipe] = "windows-engine";
        factory.Failures[LinuxPipe] = new TimeoutException();

        await using var manager = new EngineManager(DesktopContexts(), factory);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(await WaitFor(() => manager.Hosts.All(h => h.Id != "engine")));
        Assert.True(await WaitFor(() => manager.Selected?.Id == "desktop-windows"));
        Assert.Equal(["desktop-windows", "desktop-linux"], manager.Hosts.Select(h => h.Id));
    }

    [Fact]
    public async Task OpensWhereTheUserLeftIt()
    {
        var store = new MemoryStore("desktop-linux");

        await using var manager = new EngineManager(DesktopContexts(), new FakeRuntimeFactory(), selection: store);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("desktop-linux", manager.Selected!.Id);
    }

    [Fact]
    public async Task RemembersWhatTheUserChoseButNotWhatDrayFellBackTo()
    {
        var store = new MemoryStore(null);

        await using var manager = new EngineManager(DesktopContexts(), new FakeRuntimeFactory(), selection: store);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Null(store.Saved);

        await manager.SelectAsync("desktop-linux", TestContext.Current.CancellationToken);
        Assert.Equal("desktop-linux", store.Saved);
    }

    /// <summary>
    /// The user last chose Linux containers, and has since switched Docker Desktop to Windows from
    /// its tray. Opening on an engine that cannot answer would show an empty app; the one that is
    /// running is opened instead, and the picker offers the switch back.
    /// </summary>
    [Fact]
    public async Task ARememberedEngineDockerDesktopHasSwitchedAwayFromIsPassedOver()
    {
        await using var manager = new EngineManager(
            DesktopContexts(), new FakeRuntimeFactory(), Desktop("windows"), new MemoryStore("desktop-linux"));

        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("desktop-windows", manager.Selected!.Id);
        Assert.Equal(DesktopEngineMode.Linux, manager.SwitchNeededFor(manager.Hosts.Single(h => h.Id == "desktop-linux")));
        Assert.Null(manager.SwitchNeededFor(manager.Hosts.Single(h => h.Id == "desktop-windows")));
    }

    /// <summary>
    /// The inactive engine's pipe exists and answers with a 500 or not at all, which reads as broken.
    /// It is switched off, and the row says so in the words that say what choosing it will do.
    /// </summary>
    [Fact]
    public async Task TheInactiveEngineSaysWhyItIsDown()
    {
        var factory = new FakeRuntimeFactory();
        factory.Failures[LinuxPipe] = new RuntimeConnectionException("500 Internal Server Error");

        await using var manager = new EngineManager(DesktopContexts(), factory, Desktop("windows"));
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        await manager.ProbeOthersAsync(TestContext.Current.CancellationToken);

        var linux = manager.Hosts.Single(h => h.Id == "desktop-linux");
        Assert.Equal(HostConnectionState.Unreachable, linux.State);
        Assert.Contains("Docker Desktop is running Windows containers", linux.StateDetail);
    }

    /// <summary>Opening the Hosts page must not boot every stopped WSL distribution to fill in a column.</summary>
    [Fact]
    public async Task ProbingLeavesAHostThatWouldStartAlone()
    {
        var factory = new FakeRuntimeFactory();

        await using var manager = new EngineManager(new DockerContextReader(new FakeConfigSource()
            .WithContext("alpha", "unix:///a.sock")
            .WithConfig("alpha")), factory);

        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        // Not reachable through discovery on a machine without WSL, so placed directly: the rule
        // under test is the probe's, not the reader's.
        manager.AddForTest(new DockerHost
        {
            Id = "wsl:Ubuntu",
            Name = "Ubuntu",
            Endpoint = DockerEndpoint.Parse("wsl://Ubuntu")!,
            Origin = HostOrigin.WslDistro,
            StartsOnConnect = true,
        });

        await manager.ProbeOthersAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(factory.Created, r => r.Endpoint == "wsl://Ubuntu");
    }

    sealed class MemoryStore(string? initial) : IHostSelectionStore
    {
        public string? Saved { get; private set; }

        public string? Load() => Saved ?? initial;

        public void Save(string hostId) => Saved = hostId;
    }
}
