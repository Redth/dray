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
            if (Map(response) is { } sample) channel.Writer.TryWrite(sample);
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

    internal static ContainerStats? Map(ContainerStatsResponse r)
    {
        if (r.CPUStats is null) return null;

        var containerDelta = CpuUsage.Delta(
            r.CPUStats.CPUUsage?.TotalUsage ?? 0,
            r.PreCPUStats?.CPUUsage?.TotalUsage ?? 0);

        // OnlineCPUs is absent on some engines; the per-CPU array length is the same fact.
        var onlineCpus = (int)(r.CPUStats.OnlineCPUs ?? (ulong)(r.CPUStats.CPUUsage?.PercpuUsage?.Count ?? 0));

        // The interval between the engine's own two readings, not between our two receipts —
        // a sample delayed in our queue must not read as a busier container.
        var elapsed = DockerTime.From(r.PreRead) is { } previous && DockerTime.From(r.Read) is { } current
            ? current - previous
            : TimeSpan.Zero;

        return new ContainerStats(
            Timestamp: DockerTime.FromOrNow(r.Read),
            CpuPercent: CpuUsage.Percent(containerDelta, elapsed, onlineCpus),
            MemoryBytes: WorkingSet(r.MemoryStats),
            MemoryLimitBytes: (long)(r.MemoryStats?.Limit ?? 0),
            NetworkRxBytes: r.Networks?.Values.Sum(n => (long)n.RxBytes) ?? 0,
            NetworkTxBytes: r.Networks?.Values.Sum(n => (long)n.TxBytes) ?? 0,
            BlockReadBytes: BlockBytes(r.BlkioStats, "read"),
            BlockWriteBytes: BlockBytes(r.BlkioStats, "write"),
            ProcessCount: (int)(r.PidsStats?.Current ?? 0));
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
