using Dray.Core.Engine;
using Dray.Core.Tests.Fakes;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Docker Desktop's mode, read and switched through its own CLI. The JSON here is what Docker
/// Desktop 4.x printed on a real Windows machine, not a guess at its shape.
/// </summary>
public class DockerDesktopTests
{
    const string Root = "/fake/Docker";

    const string Running = "{\n    \"SessionID\": \"191ad2e8-2b86-4282-805f-691f022ddb8e\",\n    \"Status\": \"running\"\n}\n";
    const string WindowsCurrent = "[{\"Name\":\"linux\",\"Description\":\"Linux engine\",\"Current\":false},{\"Name\":\"windows\",\"Description\":\"Windows engine\",\"Current\":true}]";
    const string LinuxCurrent = "[{\"Name\":\"linux\",\"Description\":\"Linux engine\",\"Current\":true},{\"Name\":\"windows\",\"Description\":\"Windows engine\",\"Current\":false}]";

    static FakeConfigSource Installed() => new FakeConfigSource { IsWindows = true }.WithExecutable($"{Root}/Docker Desktop.exe");

    [Fact]
    public void ReadsWhichEngineIsCurrent()
    {
        Assert.Equal(DesktopEngineMode.Windows, DockerDesktop.ParseCurrentMode(WindowsCurrent));
        Assert.Equal(DesktopEngineMode.Linux, DockerDesktop.ParseCurrentMode(LinuxCurrent));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void AnAnswerItCannotReadIsNoMode(string output) => Assert.Null(DockerDesktop.ParseCurrentMode(output));

    [Fact]
    public void ReadsWhetherItIsRunning()
    {
        Assert.True(DockerDesktop.ParseRunning(Running));

        // What it says for the second after a mode switch returns. Up, and finishing.
        Assert.True(DockerDesktop.ParseRunning("{\"SessionID\":\"x\",\"Status\":\"starting\"}"));
        Assert.False(DockerDesktop.ParseRunning("{\"Status\":\"stopped\"}"));
        Assert.False(DockerDesktop.ParseRunning("garbage"));
    }

    /// <summary>
    /// Each of Docker Desktop's engines has a pipe of its own; docker_engine is whichever is current
    /// and so belongs to neither.
    /// </summary>
    [Theory]
    [InlineData("//./pipe/dockerDesktopLinuxEngine", DesktopEngineMode.Linux)]
    [InlineData("npipe:////./pipe/dockerDesktopWindowsEngine", DesktopEngineMode.Windows)]
    [InlineData("//./pipe/docker_engine", null)]
    [InlineData("wsl://Ubuntu", null)]
    public void KnowsWhichPipeIsWhichEngine(string raw, DesktopEngineMode? expected)
        => Assert.Equal(expected, DockerDesktop.ModeOf(DockerEndpoint.Parse(raw)!));

    [Fact]
    public async Task ReportsTheModeOfARunningDesktop()
    {
        var runner = new ScriptedRunner()
            .On(["desktop", "status"], 0, Running)
            .On(["desktop", "engine", "ls"], 0, WindowsCurrent);

        var state = await new DockerDesktop(runner, Installed(), Root).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new DockerDesktopState(true, true, DesktopEngineMode.Windows), state);
    }

    /// <summary>Not asked at all when it is not there: no process launch on a machine without it.</summary>
    [Fact]
    public async Task IsNotAskedWhenNotInstalled()
    {
        var runner = new ScriptedRunner();

        var state = await new DockerDesktop(runner, new FakeConfigSource { IsWindows = true }, Root).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DockerDesktopState.NotInstalled, state);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task AStoppedDesktopHasNoMode()
    {
        var runner = new ScriptedRunner().On(["desktop", "status"], 0, "{\"Status\":\"stopped\"}");

        var state = await new DockerDesktop(runner, Installed(), Root).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new DockerDesktopState(true, false, null), state);
    }

    [Fact]
    public async Task SwitchesWithTheCliAndSaysWhyWhenItCannot()
    {
        var runner = new ScriptedRunner().On(["desktop", "engine", "use"], 1, stderr: "unable to set engine to \"linux\": something broke\n");
        var desktop = new DockerDesktop(runner, Installed(), Root);

        var problem = await desktop.UseAsync(DesktopEngineMode.Linux, TestContext.Current.CancellationToken);

        Assert.Equal(["desktop", "engine", "use", "linux"], runner.Calls.Single().Arguments);
        Assert.Contains("something broke", problem);
    }

    const string MacApp = "/Applications/Docker.app";

    static FakeConfigSource MacInstalled() => new FakeConfigSource { IsMacOS = true }
        .WithDirectory(MacApp)
        .WithExecutable($"{MacApp}/Contents/Resources/bin/docker");

    /// <summary>
    /// A Mac's Docker Desktop has one engine and no <c>engine</c> command; running means its Linux
    /// engine is up. Asked through the app's own CLI, which has the desktop plugin wherever PATH
    /// points.
    /// </summary>
    [Fact]
    public async Task ARunningMacDesktopIsItsLinuxEngine()
    {
        var runner = new ScriptedRunner().On(["desktop", "status"], 0, Running);

        var state = await new DockerDesktop(runner, MacInstalled(), MacApp).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new DockerDesktopState(true, true, DesktopEngineMode.Linux), state);
        var call = Assert.Single(runner.Calls);
        Assert.Equal($"{MacApp}/Contents/Resources/bin/docker", call.Executable);
    }

    [Fact]
    public async Task AStoppedMacDesktopNeedsStarting()
    {
        var runner = new ScriptedRunner().On(["desktop", "status"], 0, "{\"Status\":\"stopped\"}");

        var state = await new DockerDesktop(runner, MacInstalled(), MacApp).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new DockerDesktopState(true, false, null), state);
    }

    [Fact]
    public async Task AMacWithoutDockerDesktopIsNotAsked()
    {
        var runner = new ScriptedRunner();

        var state = await new DockerDesktop(runner, new FakeConfigSource { IsMacOS = true }, MacApp).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DockerDesktopState.NotInstalled, state);
        Assert.Empty(runner.Calls);
    }

    /// <summary>
    /// Docker Desktop for Mac's socket is its engine, and the only one there is. OrbStack's and
    /// Colima's are not Docker Desktop's, and neither is the system socket that links to it — that
    /// one is an alias, settled by the engine's identity like every other.
    /// </summary>
    [Theory]
    [InlineData("unix:///Users/jon/.docker/run/docker.sock", DesktopEngineMode.Linux)]
    [InlineData("unix:///Users/jon/.orbstack/run/docker.sock", null)]
    [InlineData("unix:///Users/jon/.colima/default/docker.sock", null)]
    [InlineData("unix:///var/run/docker.sock", null)]
    public void KnowsWhichMacSocketIsDockerDesktops(string raw, DesktopEngineMode? expected)
        => Assert.Equal(expected, DockerDesktop.ModeOf(DockerEndpoint.Parse(raw)!));

    /// <summary>A Docker Desktop that has not said which mode it is in is not switched on a guess.</summary>
    [Fact]
    public void OnlySwitchesWhenTheModeIsKnownToDiffer()
    {
        Assert.True(new DockerDesktopState(true, true, DesktopEngineMode.Windows).NeedsSwitchTo(DesktopEngineMode.Linux));
        Assert.False(new DockerDesktopState(true, true, DesktopEngineMode.Linux).NeedsSwitchTo(DesktopEngineMode.Linux));
        Assert.False(new DockerDesktopState(true, true, null).NeedsSwitchTo(DesktopEngineMode.Linux));
        Assert.False(new DockerDesktopState(true, false, null).NeedsSwitchTo(DesktopEngineMode.Linux));
    }
}
