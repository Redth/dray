using Docker.DotNet.Models;
using Xunit;

namespace Dray.Docker.Tests;

/// <summary>
/// Reading a shell probe's exit code. Found on Windows: under Hyper-V isolation the engine records
/// an exec's exit a moment after its stream closes, and an inspect that came first read the
/// probe as running, so every Nano Server container was said to have no shell.
/// </summary>
public class DockerExecProbeTests
{
    /// <summary>An exec that the engine reports running for the first <paramref name="polls"/> inspects.</summary>
    static Func<CancellationToken, Task<ContainerExecInspectResponse>> ExitsAfter(int polls, long exitCode)
    {
        var calls = 0;
        return _ => Task.FromResult(++calls <= polls
            ? new ContainerExecInspectResponse { Running = true }
            : new ContainerExecInspectResponse { Running = false, ExitCode = exitCode });
    }

    [Fact]
    public async Task AnExitRecordedLateIsStillRead()
    {
        var code = await DockerExec.ExitCodeAsync(ExitsAfter(3, 0), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task AFailedProbeKeepsItsCode()
    {
        // 127 is what a missing executable produces on Windows too.
        var code = await DockerExec.ExitCodeAsync(ExitsAfter(0, 127), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(127, code);
    }

    [Fact]
    public async Task AProbeThatNeverFinishesHasNoCode()
    {
        var code = await DockerExec.ExitCodeAsync(ExitsAfter(int.MaxValue, 0), TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.Null(code);
    }
}
