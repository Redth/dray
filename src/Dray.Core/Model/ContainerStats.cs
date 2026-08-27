namespace Dray.Core.Model;

/// <summary>One sample of a container's resource use.</summary>
/// <param name="CpuPercent">
/// Share of the host's CPUs, so a container saturating two of four cores reads 200%. That is what
/// <c>docker stats</c> reports and what people compare against; normalising to 100% would make the
/// same container look half as busy as the tool they cross-check with.
/// </param>
public sealed record ContainerStats(
    DateTimeOffset Timestamp,
    double? CpuPercent,
    long MemoryBytes,
    long MemoryLimitBytes,
    long NetworkRxBytes,
    long NetworkTxBytes,
    long BlockReadBytes,
    long BlockWriteBytes,
    int ProcessCount)
{
    /// <summary>
    /// Memory as a share of the container's limit, or null when it has none.
    /// <para>
    /// An unlimited container is reported by the engine with the host's total memory as its limit.
    /// Showing "3% of 32 GB" for a container with no limit set implies a bound that does not
    /// exist, so callers are given null and say "no limit" instead.
    /// </para>
    /// </summary>
    public double? MemoryPercent => HasMemoryLimit
        ? MemoryBytes * 100.0 / MemoryLimitBytes
        : null;

    public bool HasMemoryLimit => MemoryLimitBytes > 0;
}

/// <summary>
/// Turning two consecutive samples into a CPU percentage.
/// <para>
/// The engine reports cumulative nanoseconds, not a rate, so a percentage only exists between two
/// samples.
/// </para>
/// <para>
/// <b>Not Docker's published formula.</b> That one divides the container's delta by the host's
/// <c>system_cpu_usage</c> delta and multiplies by the CPU count — which only works if
/// <c>system_cpu_usage</c> is summed across cores. Docker's is. Podman's is not: it advances at
/// roughly one CPU-second per wall second regardless of how many cores exist, so the multiply
/// over-reports by exactly the core count. Measured against a single busy loop on a four-core
/// machine, Docker's formula gave 398% for a process using one core.
/// </para>
/// <para>
/// Elapsed wall time is the denominator instead. It needs no agreement between engines about what
/// a counter means, and it is the definition of the number anyway: CPU-seconds consumed per second
/// of real time, where 100% is one core fully used.
/// </para>
/// </summary>
public static class CpuUsage
{
    /// <summary>
    /// Percentage of one CPU core used between two samples. 100% is one core saturated; a
    /// container using two of four cores reads 200%, which is what <c>docker stats</c> shows and
    /// what people compare against.
    /// </summary>
    /// <param name="containerDeltaNanoseconds">CPU nanoseconds consumed since the previous sample.</param>
    /// <param name="elapsed">Wall time between the two samples.</param>
    /// <param name="onlineCpus">How many CPUs the container can see, used only as a ceiling.</param>
    /// <returns>
    /// Null when there is nothing to measure across. That happens on the first sample of a stream —
    /// the engine sends a zeroed "previous" — and returning 0% there would draw a container as idle
    /// for a second before it jumped to its real load, which reads as a stutter in the graph.
    /// </returns>
    public static double? Percent(long containerDeltaNanoseconds, TimeSpan elapsed, int onlineCpus)
    {
        if (elapsed <= TimeSpan.Zero) return null;

        // A negative delta means the counter was reset — the container restarted between samples.
        // There is no meaningful rate across that boundary.
        if (containerDeltaNanoseconds < 0) return null;

        var percent = containerDeltaNanoseconds / (elapsed.TotalSeconds * 1_000_000_000) * 100.0;

        // The counters are read a moment apart from the clock, so a container pegging every core
        // can compute slightly above the ceiling. Reporting 104% of a single core would look like
        // a bug rather than a rounding artefact.
        if (onlineCpus <= 0) return percent;

        var ceiling = onlineCpus * 100.0;
        return percent > ceiling ? ceiling : percent;
    }

    /// <summary>
    /// Subtract two cumulative counters, tolerating a reset.
    /// <para>
    /// The engine reports these as unsigned and they restart at zero when the container does, so a
    /// naive subtraction underflows into an enormous positive number and paints a spike that never
    /// happened.
    /// </para>
    /// </summary>
    public static long Delta(ulong current, ulong previous)
        => current >= previous ? (long)(current - previous) : -1;
}

/// <summary>
/// A bounded ring of recent samples, for drawing a sparkline.
/// <para>
/// Fixed capacity on purpose: a container watched for an hour would otherwise accumulate thousands
/// of samples nobody can see, in a graph a few hundred pixels wide.
/// </para>
/// </summary>
public sealed class StatsHistory(int capacity = 60)
{
    readonly Queue<ContainerStats> _samples = new(capacity);

    public int Capacity { get; } = capacity;

    public IReadOnlyCollection<ContainerStats> Samples => _samples;

    public ContainerStats? Latest { get; private set; }

    public void Add(ContainerStats sample)
    {
        while (_samples.Count >= Capacity) _samples.Dequeue();

        _samples.Enqueue(sample);
        Latest = sample;
    }

    public void Clear()
    {
        _samples.Clear();
        Latest = null;
    }

    /// <summary>
    /// The series to plot, as values between 0 and 1.
    /// <para>
    /// Scaled to the highest value seen rather than to a fixed ceiling, because a container using
    /// 2% of the host would otherwise be a flat line at the bottom — and the shape of that 2% is
    /// exactly what someone is looking for.
    /// </para>
    /// </summary>
    public IReadOnlyList<double> Normalized(Func<ContainerStats, double?> select)
    {
        var values = _samples.Select(select).Select(v => v ?? 0).ToList();
        if (values.Count == 0) return [];

        var peak = values.Max();

        // A flat zero series stays flat rather than dividing by zero.
        return peak <= 0 ? [.. values.Select(_ => 0.0)] : [.. values.Select(v => v / peak)];
    }
}
