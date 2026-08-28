using Dray.Apple;
using Dray.Core.Engine;
using Xunit;

namespace Dray.Apple.Tests;

/// <summary>
/// Reading and controlling Apple's container service.
/// <para>
/// The case that motivated <see cref="IEngineService"/>: the CLI answers <c>--version</c> whether
/// or not its API server is up, so an engine that looks installed can fail every call — and the fix
/// is one command the user has no reason to know.
/// </para>
/// </summary>
public class AppleServiceTests
{
    // The shape the real CLI prints, captured from `container system status --format json` on
    // container 1.3.0.
    const string RunningJson =
        """{"apiServerAppName":"container-apiserver","apiServerVersion":"container-apiserver version 1.3.0 (build: release)","appRoot":"/Users/x/Library/Application Support/com.apple.container/","installRoot":"/opt/homebrew/Cellar/container/1.3.0/","status":"running"}""";

    static AppleRuntime Runtime(ScriptedProcessRunner runner) => new(runner, "/opt/homebrew/bin/container");

    [Fact]
    public async Task ReadsTheServiceAsRunning()
    {
        var runner = new ScriptedProcessRunner().Returns("system status", RunningJson);

        var state = await Runtime(runner).ServiceStatusAsync(TestContext.Current.CancellationToken);

        Assert.True(state.Running);
        Assert.Contains("1.3.0", state.Version, StringComparison.Ordinal);
        Assert.Contains("/opt/homebrew", state.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsksForJsonRatherThanReadingTheTable()
    {
        // The table is a display format and its columns are not a contract.
        var runner = new ScriptedProcessRunner().Returns("system status", RunningJson);

        await Runtime(runner).ServiceStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal("system status --format json", Assert.Single(runner.Invocations));
    }

    [Fact]
    public async Task ANonZeroExitIsAStateNotAFailure()
    {
        // This is how the CLI answers when the service is down. Throwing here would turn the one
        // thing the user can act on into an error nobody can act on.
        var runner = new ScriptedProcessRunner().Returns("system status", "", 1, "connection refused");

        var state = await Runtime(runner).ServiceStatusAsync(TestContext.Current.CancellationToken);

        Assert.False(state.Running);
        Assert.Equal("connection refused", state.Detail);
    }

    [Fact]
    public async Task AnUnreadableAnswerIsNotReportedAsStopped()
    {
        // It exited zero, so something is answering. Reporting it stopped would be a state nobody
        // measured, and would offer a Start button for a service already running.
        var runner = new ScriptedProcessRunner().Returns("system status", "not json at all");

        Assert.True((await Runtime(runner).ServiceStatusAsync(TestContext.Current.CancellationToken)).Running);
    }

    [Fact]
    public async Task StartingSaysNothingWhenItWorked()
    {
        var runner = new ScriptedProcessRunner().Returns("system start", "");

        Assert.Null(await Runtime(runner).StartServiceAsync(TestContext.Current.CancellationToken));
        Assert.Equal("system start", Assert.Single(runner.Invocations));
    }

    [Fact]
    public async Task ARefusalComesBackAsTheEnginesOwnWords()
    {
        var runner = new ScriptedProcessRunner()
            .Returns("system start", "", 1, "launchd refused: already loaded\nsecond line nobody needs");

        var error = await Runtime(runner).StartServiceAsync(TestContext.Current.CancellationToken);

        // The first line only: the rest is a stack of detail for a log, not a sentence for a user.
        Assert.Equal("launchd refused: already loaded", error);
    }

    [Fact]
    public async Task StoppingIsTheSameShape()
    {
        var runner = new ScriptedProcessRunner().Returns("system stop", "");

        Assert.Null(await Runtime(runner).StopServiceAsync(TestContext.Current.CancellationToken));
        Assert.Equal("system stop", Assert.Single(runner.Invocations));
    }
}
