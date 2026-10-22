using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Docker.DotNet;
using Docker.DotNet.Models;
using Dray.Core.Model;

namespace Dray.Docker;

/// <summary>Streaming a container's resource use.</summary>
public static class DockerStats
{
    /// <summary>
    /// Sample a container until cancelled.
    /// <para>
    /// Streamed rather than polled, for the same reason the rest of the app is event-driven: the
    /// engine already emits a sample a second and asking it repeatedly would be strictly worse.
    /// The stream ends on its own when the container stops, which is a normal completion.
    /// </para>
    /// </summary>
    public static async IAsyncEnumerable<ContainerStats> StreamAsync(
        DockerClient client,
        string containerId,
        ContainerOs os = ContainerOs.Linux,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Docker.DotNet pushes through IProgress, so this bridges to the pull-based stream callers
        // consume. Bounded and dropping: a sample the UI never rendered is worthless the moment
        // the next one arrives, and an unbounded queue behind a slow renderer would grow forever.
        var channel = Channel.CreateBounded<ContainerStats>(
            new BoundedChannelOptions(2)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });

        var progress = new Progress<ContainerStatsResponse>(response =>
        {
            if (Map(response, os) is { } sample) channel.Writer.TryWrite(sample);
        });

        var monitor = Task.Run(async () =>
        {
            try
            {
                await client.Containers
                    .GetContainerStatsAsync(containerId, new ContainerStatsParameters { Stream = true }, progress, ct)
                    .ConfigureAwait(false);

                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var sample in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return sample;
        }
        finally
        {
            // Never leave the monitor running behind a disposed enumerator.
            await monitor.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// The engine's own units for a container's cumulative CPU counter.
    /// <para>
    /// Linux reports nanoseconds. Windows reports 100-nanosecond intervals — the native unit of
    /// every Windows kernel time API — through the same <c>total_usage</c> field, with nothing in
    /// the payload saying so. Reading a Windows counter as nanoseconds under-reports CPU by a
    /// factor of a hundred, which is the quietest possible wrong answer: a busy container draws as
    /// a flat 1%.
    /// </para>
    /// </summary>
    const int WindowsCpuTickNanoseconds = 100;

    internal static ContainerStats? Map(ContainerStatsResponse r, ContainerOs os = ContainerOs.Linux)
    {
        if (r.CPUStats is null) return null;

        // The response carries its own OSType on engines that set it. Trusting it over the
        // capability is right: it describes the container this sample is of, while the capability
        // describes the engine, and only the former can be wrong about a given container.
        var sampleOs = r.OSType?.Trim().ToLowerInvariant() switch
        {
            "windows" => ContainerOs.Windows,
            "linux" => ContainerOs.Linux,
            _ => os,
        };

        var isWindows = sampleOs == ContainerOs.Windows;

        var containerDelta = CpuUsage.Delta(
            r.CPUStats.CPUUsage?.TotalUsage ?? 0,
            r.PreCPUStats?.CPUUsage?.TotalUsage ?? 0);

        // Delta() returns -1 for a counter reset, which Percent() reads as "no rate". Scaling that
        // sentinel would turn it into -100 and lose the meaning, so only a real delta is converted.
        if (isWindows && containerDelta > 0) containerDelta *= WindowsCpuTickNanoseconds;

        // OnlineCPUs is absent on some engines; the per-CPU array length is the same fact. Windows
        // reports neither and puts the processor count in NumProcs instead — the field `docker
        // stats` itself uses for the ceiling on that platform.
        var onlineCpus = isWindows
            ? (int)r.NumProcs
            : (int)(r.CPUStats.OnlineCPUs ?? (ulong)(r.CPUStats.CPUUsage?.PercpuUsage?.Count ?? 0));

        // The interval between the engine's own two readings, not between our two receipts —
        // a sample delayed in our queue must not read as a busier container.
        var elapsed = DockerTime.From(r.PreRead) is { } previous && DockerTime.From(r.Read) is { } current
            ? current - previous
            : TimeSpan.Zero;

        return new ContainerStats(
            Timestamp: DockerTime.FromOrNow(r.Read),
            CpuPercent: CpuUsage.Percent(containerDelta, elapsed, onlineCpus),
            MemoryBytes: isWindows ? WindowsWorkingSet(r.MemoryStats) : WorkingSet(r.MemoryStats),

            // A Windows engine reports no memory limit, so there is no percentage to compute.
            // ContainerStats reads a zero here as "no limit", which is exactly the truth.
            MemoryLimitBytes: isWindows ? 0 : (long)(r.MemoryStats?.Limit ?? 0),

            NetworkRxBytes: r.Networks?.Values.Sum(n => (long)n.RxBytes) ?? 0,
            NetworkTxBytes: r.Networks?.Values.Sum(n => (long)n.TxBytes) ?? 0,

            // Windows has no cgroups and so no blkio; the same numbers arrive under storage_stats.
            BlockReadBytes: isWindows ? (long)(r.StorageStats?.ReadSizeBytes ?? 0) : BlockBytes(r.BlkioStats, "read"),
            BlockWriteBytes: isWindows ? (long)(r.StorageStats?.WriteSizeBytes ?? 0) : BlockBytes(r.BlkioStats, "write"),

            // num_procs is the host's processor count on Windows, not a process count, and there is
            // no pids_stats. The absence is reported as an absence rather than as zero.
            ProcessCount: isWindows ? null : (int?)(r.PidsStats?.Current ?? 0));
    }

    /// <summary>
    /// Memory in use by a Windows container.
    /// <para>
    /// <c>docker stats</c> uses <c>privateworkingset</c> here, and the cgroup-shaped
    /// <c>usage</c> / <c>inactive_file</c> arithmetic the Linux path does has no counterpart —
    /// those fields are simply absent. Commit charge is the documented fallback, and is what the
    /// engine compares a memory limit against when one is set.
    /// </para>
    /// </summary>
    internal static long WindowsWorkingSet(MemoryStats? memory)
    {
        if (memory is null) return 0;

        if (memory.PrivateWorkingSet is > 0 and var privateSet) return (long)privateSet;
        if (memory.Commit is > 0 and var commit) return (long)commit;

        return 0;
    }

    /// <summary>
    /// Memory actually in use.
    /// <para>
    /// The raw <c>usage</c> figure includes the page cache, which makes a container that has merely
    /// read a large file look like it is holding it in memory. <c>docker stats</c> subtracts
    /// <c>inactive_file</c> for this reason, and so does this — where the engine reports it. Podman
    /// does not always, in which case the raw figure is the only answer available and is used
    /// rather than guessed at.
    /// </para>
    /// </summary>
    internal static long WorkingSet(MemoryStats? memory)
    {
        var usage = (long)(memory?.Usage ?? 0);
        if (usage <= 0) return 0;

        if (memory?.Stats is not { } stats) return usage;

        // cgroup v2 calls it inactive_file; v1 called it total_inactive_file.
        var cache = stats.TryGetValue("inactive_file", out var v2) ? (long)v2
            : stats.TryGetValue("total_inactive_file", out var v1) ? (long)v1
            : 0;

        return cache > 0 && cache < usage ? usage - cache : usage;
    }

    /// <summary>
    /// Total bytes read from or written to block devices.
    /// <para>
    /// The engine reports one entry per device and operation, so the figure people mean is the sum.
    /// Absent entirely on a rootless podman, where the cgroup does not expose io accounting — which
    /// reads as zero and is why the UI does not draw it as a measurement.
    /// </para>
    /// </summary>
    internal static long BlockBytes(BlkioStats? blkio, string operation)
        => blkio?.IoServiceBytesRecursive
            ?.Where(e => string.Equals(e.Op, operation, StringComparison.OrdinalIgnoreCase))
            .Sum(e => (long)e.Value) ?? 0;
}
