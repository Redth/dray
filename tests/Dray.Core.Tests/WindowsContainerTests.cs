using Dray.Core.Engine;
using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The facts that differ when an engine runs Windows containers.
/// <para>
/// None of this can be exercised against a live engine yet, so each case records the documented
/// behaviour it is derived from. What they protect is Dray's own branching: that a Windows exit
/// code is never read through the Linux table, that pause is never offered where it cannot work,
/// and that a Windows engine's default network driver is not "bridge".
/// </para>
/// </summary>
public class WindowsContainerTests
{
    // ---------------------------------------------------------------- exit codes

    /// <summary>
    /// 137 is the one that matters. On Linux it is 128 + SIGKILL and almost always means the OOM
    /// killer; on Windows it means nothing at all, and saying "killed (out of memory)" about a
    /// Windows container would send someone to raise a memory limit that is not the problem.
    /// </summary>
    [Fact]
    public void LinuxSignalCodesAreNotAppliedToWindows()
    {
        Assert.Equal("killed (out of memory)", ContainerStatusVocabulary.ExplainExitCode(137, ContainerOs.Linux));
        Assert.NotEqual("killed (out of memory)", ContainerStatusVocabulary.ExplainExitCode(137, ContainerOs.Windows));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "general error")]
    [InlineData(127, "command not found")]
    [InlineData(139, "segmentation fault")]
    [InlineData(143, "stopped (SIGTERM)")]
    public void ExplainsLinuxExitCodes(int code, string? expected)
        => Assert.Equal(expected, ContainerStatusVocabulary.ExplainExitCode(code, ContainerOs.Linux));

    /// <summary>
    /// An NTSTATUS arrives as a signed integer through some paths and an unsigned one through
    /// others, so both spellings of the same value must reach the same sentence — a user looking
    /// at "Exited -1073741819" has exactly the problem of one looking at "Exited 3221225477".
    /// </summary>
    [Theory]
    [InlineData(unchecked((int)0xC0000005), "access violation")]
    [InlineData(unchecked((int)0xC0000135), "a required DLL was not found")]
    [InlineData(unchecked((int)0xC0000409), "stack buffer overrun")]
    [InlineData(unchecked((int)0xE0434352), "an unhandled .NET exception")]
    [InlineData(unchecked((int)0xC000013A), "interrupted (Ctrl+C)")]
    [InlineData(-1, "force stopped")]
    [InlineData(1, "general error")]
    [InlineData(0, null)]
    public void ExplainsWindowsExitCodes(int code, string? expected)
        => Assert.Equal(expected, ContainerStatusVocabulary.ExplainExitCode(code, ContainerOs.Windows));

    /// <summary>An unrecognised NTSTATUS still gets its class named rather than nothing.</summary>
    [Fact]
    public void NamesTheClassOfAnUnknownWindowsCrash()
        => Assert.Equal(
            "terminated by Windows",
            ContainerStatusVocabulary.ExplainExitCode(unchecked((int)0xC0000123), ContainerOs.Windows));

    /// <summary>
    /// A container carries its own OS so the pill can explain the code without the engine being in
    /// scope. Both of these are a real container that stopped with the same number.
    /// </summary>
    [Fact]
    public void TheStatePillReadsTheCodeThroughTheContainersOwnOs()
    {
        var linux = new ContainerSummary
        {
            Id = "a", Name = "api", Image = "api:latest",
            State = DockerState.Exited, ExitCode = 137, Os = ContainerOs.Linux,
        };

        var windows = linux with { Os = ContainerOs.Windows };

        Assert.Equal("Exited 137 · killed (out of memory)", linux.Status.Label);
        Assert.Equal("Exited 137", windows.Status.Label);
    }

    /// <summary>
    /// The engine writes an exit code as text in two places, a list row's status and a die event,
    /// and on Windows writes the NTSTATUS unsigned. Found on a real engine: stopping a Nano Server
    /// container reported 3221225786, int parsing dropped it, and the pill showed a clean exit.
    /// </summary>
    [Theory]
    [InlineData("3221225786", unchecked((int)0xC000013A))]
    [InlineData("-1073741510", unchecked((int)0xC000013A))]
    [InlineData("137", 137)]
    [InlineData(" 0 ", 0)]
    [InlineData("4294967296", null)]
    [InlineData("twelve", null)]
    public void ParsesExitCodesAtFullWidth(string text, int? expected)
        => Assert.Equal(expected, ContainerStatusVocabulary.ParseExitCode(text));

    [Fact]
    public void AStoppedWindowsContainerIsExplained()
    {
        var store = new EntityStore();
        store.Reset([new ContainerSummary
        {
            Id = "w", Name = "nano", Image = "nanoserver",
            State = DockerState.Running, Os = ContainerOs.Windows,
        }]);

        store.Apply(new RuntimeEvent(
            RuntimeEntity.Container, "die", "w",
            new Dictionary<string, string> { ["exitCode"] = "3221225786" },
            DateTimeOffset.UnixEpoch));

        Assert.Equal("Exited -1073741510 · interrupted (Ctrl+C)", store.Find("w")!.Status.Label);
    }

    // ---------------------------------------------------------------- capabilities

    /// <summary>
    /// Windows implements pause by suspending a container's utility VM, so there is nothing to
    /// suspend under process isolation. An engine that did not say which it defaults to is treated
    /// as unable, because a Pause button that always fails is worse than no button.
    /// </summary>
    [Theory]
    [InlineData(ContainerOs.Linux, null, true)]
    [InlineData(ContainerOs.Windows, "hyperv", true)]
    [InlineData(ContainerOs.Windows, "process", false)]
    [InlineData(ContainerOs.Windows, null, false)]
    public void PauseFollowsIsolation(ContainerOs os, string? isolation, bool expected)
    {
        var capabilities = new RuntimeCapabilities
        {
            ContainerOs = os,
            DefaultIsolation = isolation,
            SupportsPause = os != ContainerOs.Windows || isolation == "hyperv",
        };

        Assert.Equal(expected, capabilities.SupportsPause);
    }

    /// <summary>
    /// Unknown must not collapse into Linux. An engine that reported no OSType is one Dray knows
    /// nothing about, and IsWindowsContainers deciding paths and shells on a guess is exactly the
    /// failure this type exists to prevent.
    /// </summary>
    [Fact]
    public void UnknownIsNotWindowsAndIsNotLinux()
    {
        var capabilities = new RuntimeCapabilities();

        Assert.Equal(ContainerOs.Unknown, capabilities.ContainerOs);
        Assert.False(capabilities.IsWindowsContainers);
    }

    // ---------------------------------------------------------------- networks

    /// <summary>
    /// There is no bridge driver on a Windows engine; asking for one is rejected. Null means "the
    /// engine's own default", which is what the create dialog sends.
    /// </summary>
    [Theory]
    [InlineData(ContainerOs.Linux, "bridge")]
    [InlineData(ContainerOs.Windows, "nat")]
    [InlineData(ContainerOs.Unknown, "bridge")]
    public void DefaultNetworkDriverFollowsTheEngine(ContainerOs os, string expected)
        => Assert.Equal(expected, NetworkDrivers.DefaultFor(os));

    [Fact]
    public void ANetworkRequestAsksForNoDriverByDefault()
        => Assert.Null(new NetworkRequest("app-net").Driver);

    // ---------------------------------------------------------------- endpoints

    [Fact]
    public void ParsesAWslEndpoint()
    {
        var endpoint = DockerEndpoint.Parse("wsl://Ubuntu-24.04");

        Assert.NotNull(endpoint);
        Assert.Equal(EndpointScheme.Wsl, endpoint.Scheme);
        Assert.Equal("Ubuntu-24.04", endpoint.Distro);
        Assert.Equal("wsl://Ubuntu-24.04", endpoint.Display);

        // A distribution is a virtual machine on this hardware, not somewhere Dray has to reach
        // across a network — so it must not be treated as remote.
        Assert.False(endpoint.IsRemote);
    }

    [Theory]
    [InlineData("wsl://")]
    [InlineData("wsl://Ubuntu/extra")]
    [InlineData("wsl://Ubuntu:2375")]
    public void RejectsAnythingThatIsNotJustADistroName(string raw)
        => Assert.Null(DockerEndpoint.Parse(raw));

    /// <summary>Only a wsl:// endpoint has a distro; asking any other for one gets null.</summary>
    [Fact]
    public void OnlyAWslEndpointHasADistro()
        => Assert.Null(DockerEndpoint.Parse("unix:///var/run/docker.sock")!.Distro);
}
