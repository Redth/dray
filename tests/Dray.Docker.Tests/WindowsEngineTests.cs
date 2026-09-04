using Docker.DotNet.Models;
using Dray.Core.Engine;
using Dray.Core.Model;
using Dray.Docker;
using Dray.Docker.Transport;
using Xunit;

namespace Dray.Docker.Tests;

/// <summary>
/// What changes in the Docker layer when the engine runs Windows containers.
/// <para>
/// The stats payloads here are the shape moby's Windows collector produces: no cgroup fields, no
/// memory limit, no pids, a CPU counter in 100-nanosecond intervals and I/O under
/// <c>storage_stats</c> instead of <c>blkio_stats</c>.
/// </para>
/// </summary>
public class WindowsEngineTests
{
    static ContainerStatsResponse WindowsSample(ulong currentCpuTicks, ulong previousCpuTicks)
        => new()
        {
            OSType = "windows",
            Read = new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc),
            PreRead = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            NumProcs = 8,
            CPUStats = new CPUStats { CPUUsage = new CPUUsage { TotalUsage = currentCpuTicks } },
            PreCPUStats = new CPUStats { CPUUsage = new CPUUsage { TotalUsage = previousCpuTicks } },
            MemoryStats = new MemoryStats { PrivateWorkingSet = 256 * 1024 * 1024, Commit = 300 * 1024 * 1024 },
            StorageStats = new StorageStats { ReadSizeBytes = 4096, WriteSizeBytes = 8192 },
        };

    /// <summary>
    /// One core fully busy for one second is 10,000,000 intervals of 100ns. Read as nanoseconds it
    /// would come out as 1% — the quietest possible wrong answer, since a pegged container would
    /// draw as almost idle rather than as broken.
    /// </summary>
    [Fact]
    public void WindowsCpuTicksAreHundredNanosecondsNotNanoseconds()
    {
        var sample = DockerStats.Map(WindowsSample(10_000_000, 0));

        Assert.NotNull(sample);
        Assert.Equal(100.0, sample.CpuPercent!.Value, precision: 1);
    }

    /// <summary>The same counter on a Linux engine is already nanoseconds and must not be scaled.</summary>
    [Fact]
    public void LinuxCpuTicksAreLeftAlone()
    {
        var linux = WindowsSample(1_000_000_000, 0);
        linux.OSType = "linux";
        linux.CPUStats = new CPUStats
        {
            CPUUsage = new CPUUsage { TotalUsage = 1_000_000_000 },
            OnlineCPUs = 8,
        };
        linux.MemoryStats = new MemoryStats { Usage = 100, Limit = 1000 };

        var sample = DockerStats.Map(linux, ContainerOs.Linux);

        Assert.NotNull(sample);
        Assert.Equal(100.0, sample.CpuPercent!.Value, precision: 1);
    }

    /// <summary>
    /// A restart resets the counter, and Delta reports that as -1 so no rate is computed. Scaling
    /// that sentinel by 100 would turn "no measurement" into a measurement of -100.
    /// </summary>
    [Fact]
    public void ACounterResetStillProducesNoRate()
    {
        var sample = DockerStats.Map(WindowsSample(5, 10_000_000));

        Assert.NotNull(sample);
        Assert.Null(sample.CpuPercent);
    }

    [Fact]
    public void WindowsMemoryComesFromPrivateWorkingSet()
    {
        var sample = DockerStats.Map(WindowsSample(10_000_000, 0));

        Assert.NotNull(sample);
        Assert.Equal(256L * 1024 * 1024, sample.MemoryBytes);
    }

    [Fact]
    public void FallsBackToCommitWhenThereIsNoWorkingSet()
    {
        var response = WindowsSample(10_000_000, 0);
        response.MemoryStats = new MemoryStats { Commit = 300 * 1024 * 1024 };

        Assert.Equal(300L * 1024 * 1024, DockerStats.Map(response)!.MemoryBytes);
    }

    /// <summary>
    /// A Windows engine reports no memory limit. Zero is read as "no limit" rather than as a limit
    /// of zero, so no percentage is drawn against a bound that does not exist.
    /// </summary>
    [Fact]
    public void WindowsHasNoMemoryLimitAndSoNoPercentage()
    {
        var sample = DockerStats.Map(WindowsSample(10_000_000, 0));

        Assert.NotNull(sample);
        Assert.False(sample.HasMemoryLimit);
        Assert.Null(sample.MemoryPercent);
    }

    /// <summary>
    /// num_procs is the host's processor count on Windows, not a process count, and there is no
    /// pids_stats at all. Reporting it as a process count would draw eight processes in a
    /// container that might have one, and reporting zero would draw an empty one.
    /// </summary>
    [Fact]
    public void WindowsReportsNoProcessCountRatherThanZero()
    {
        var sample = DockerStats.Map(WindowsSample(10_000_000, 0));

        Assert.NotNull(sample);
        Assert.Null(sample.ProcessCount);
    }

    [Fact]
    public void WindowsBlockIoComesFromStorageStats()
    {
        var sample = DockerStats.Map(WindowsSample(10_000_000, 0));

        Assert.NotNull(sample);
        Assert.Equal(4096, sample.BlockReadBytes);
        Assert.Equal(8192, sample.BlockWriteBytes);
    }

    /// <summary>
    /// The sample's own OSType outranks the capability. The capability describes the engine; the
    /// sample describes this container, and only the former can be stale.
    /// </summary>
    [Fact]
    public void TheSamplesOwnOsTypeWins()
    {
        var sample = DockerStats.Map(WindowsSample(10_000_000, 0), ContainerOs.Linux);

        Assert.NotNull(sample);
        Assert.Equal(100.0, sample.CpuPercent!.Value, precision: 1);
    }

    // ---------------------------------------------------------------- volume helper

    /// <summary>
    /// A volume has no drive letter of its own, so volume paths stay POSIX above the session and
    /// are translated into the container's shape only at the mount point.
    /// </summary>
    [Theory]
    [InlineData("/", @"C:\dray-volume")]
    [InlineData("/data", @"C:\dray-volume\data")]
    [InlineData("/data/logs/app.log", @"C:\dray-volume\data\logs\app.log")]
    public void MapsVolumePathsIntoAWindowsHelper(string volumePath, string expected)
        => Assert.Equal(expected, DockerVolumeSession.ToHelperPath(volumePath, ContainerOs.Windows));

    [Theory]
    [InlineData(@"C:\dray-volume", "/")]
    [InlineData(@"C:\dray-volume\data", "/data")]
    [InlineData(@"C:\dray-volume\data\logs", "/data/logs")]
    public void MapsWindowsHelperPathsBack(string helperPath, string expected)
        => Assert.Equal(expected, DockerVolumeSession.ToVolumePath(helperPath, ContainerOs.Windows));

    [Fact]
    public void WindowsVolumePathsRoundTrip()
    {
        foreach (var path in new[] { "/", "/a", "/a/b", "/a/b/c.txt" })
        {
            Assert.Equal(
                path,
                DockerVolumeSession.ToVolumePath(
                    DockerVolumeSession.ToHelperPath(path, ContainerOs.Windows), ContainerOs.Windows));
        }
    }

    // ---------------------------------------------------------------- relay commands

    /// <summary>
    /// --exec is not an optimisation. Without it wsl.exe runs a login shell, and whatever that
    /// shell prints lands in the middle of an HTTP response — so anyone with a banner or a version
    /// manager's greeting in their profile would see the connection fail to parse.
    /// </summary>
    [Fact]
    public void TheWslRelayRunsWithoutALoginShell()
    {
        var command = RelayCommands.ForWsl("Ubuntu");

        Assert.Equal("wsl.exe", command.FileName);
        Assert.Contains("--exec", command.Arguments);
        Assert.Equal(["-d", "Ubuntu", "-u", "root", "--exec", "docker", "system", "dial-stdio"], command.Arguments);
    }

    /// <summary>
    /// BatchMode is what keeps a dead host degrading one sidebar entry instead of hanging the app:
    /// without it ssh blocks forever on a password prompt with no terminal to appear on.
    /// </summary>
    [Fact]
    public void TheSshRelayNeverPrompts()
    {
        var command = RelayCommands.ForSsh(DockerEndpoint.Parse("ssh://jon@nas:2222")!);

        Assert.Equal("ssh", command.FileName);
        Assert.Contains("BatchMode=yes", command.Arguments);
        Assert.Contains("jon@nas", command.Arguments);
        Assert.Contains("-p", command.Arguments);
        Assert.Contains("2222", command.Arguments);
        Assert.Equal(["docker", "system", "dial-stdio"], command.Arguments.TakeLast(3));
    }

    [Fact]
    public void OmitsThePortWhenTheEndpointDidNotCarryOne()
        => Assert.DoesNotContain("-p", RelayCommands.ForSsh(DockerEndpoint.Parse("ssh://nas")!).Arguments);

    [Theory]
    [InlineData("wsl://Ubuntu", "wsl.exe")]
    [InlineData("ssh://nas", "ssh")]
    public void RoutesRelayableEndpoints(string raw, string expected)
        => Assert.Equal(expected, RelayCommands.For(DockerEndpoint.Parse(raw)!)!.FileName);

    /// <summary>Everything with an address on this machine is reached directly, not through a relay.</summary>
    [Theory]
    [InlineData("unix:///var/run/docker.sock")]
    [InlineData("//./pipe/docker_engine")]
    [InlineData("tcp://10.0.0.4:2375")]
    public void LeavesDirectlyReachableEndpointsAlone(string raw)
        => Assert.Null(RelayCommands.For(DockerEndpoint.Parse(raw)!));
}
