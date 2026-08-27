using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The CPU figure, which is the one number on this screen people cross-check against
/// <c>docker stats</c> and will not forgive being wrong.
/// </summary>
public class CpuUsageTests
{
    static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    const long OneCoreSecond = 1_000_000_000;

    [Fact]
    public void OneCoreFullyUsedIsOneHundredPercent()
        => Assert.Equal(100, CpuUsage.Percent(OneCoreSecond, OneSecond, onlineCpus: 4)!.Value, 1);

    [Fact]
    public void TwoOfFourCoresIsTwoHundredPercent()
    {
        // Not normalised to the core count: 200% is what docker stats shows, and halving it would
        // make Dray disagree with the tool people check against.
        Assert.Equal(200, CpuUsage.Percent(2 * OneCoreSecond, OneSecond, onlineCpus: 4)!.Value, 1);
    }

    [Fact]
    public void HalfACoreOverTwoSecondsIsFiftyPercent()
        => Assert.Equal(50, CpuUsage.Percent(OneCoreSecond, TimeSpan.FromSeconds(2), 4)!.Value, 1);

    [Fact]
    public void AMeasuredBusyLoopReadsAsOneCore()
    {
        // Captured from podman 6.0.2: a single `while true; do :; done` in ash on a four-core host.
        // Docker's published formula — dividing by the system_cpu_usage delta and multiplying by
        // the core count — reported 398% for this, because podman's system counter is not summed
        // across cores. Wall time needs no such agreement between engines.
        var percent = CpuUsage.Percent(4_866_380_000, TimeSpan.FromSeconds(5.005), onlineCpus: 4);

        Assert.NotNull(percent);
        Assert.InRange(percent.Value, 95, 100);
    }

    [Fact]
    public void TheFirstSampleOfAStreamHasNoRate()
    {
        // The engine sends a zeroed "previous", so there is no interval. Reporting 0% would draw
        // the container idle for a second before it jumped to its real load.
        Assert.Null(CpuUsage.Percent(1_000, TimeSpan.Zero, onlineCpus: 4));
    }

    [Fact]
    public void ACounterResetProducesNoReadingRatherThanASpike()
    {
        // The container restarted between samples.
        Assert.Null(CpuUsage.Percent(-1, OneSecond, onlineCpus: 4));
    }

    [Fact]
    public void APercentageAboveTheCoreCountIsClamped()
    {
        // The counters and the clock are read a moment apart, so a container pegging every core can
        // compute slightly over. 410% would read as a bug rather than as rounding.
        Assert.Equal(400, CpuUsage.Percent(5 * OneCoreSecond, OneSecond, onlineCpus: 4)!.Value, 1);
    }

    [Fact]
    public void AnUnknownCoreCountStillReportsARate()
    {
        // Some engines omit online_cpus. The ceiling is lost; the measurement is not.
        Assert.Equal(100, CpuUsage.Percent(OneCoreSecond, OneSecond, onlineCpus: 0)!.Value, 1);
    }

    // ---------------------------------------------------------------- deltas

    [Fact]
    public void ADeltaIsTheDifference()
        => Assert.Equal(500, CpuUsage.Delta(1_500, 1_000));

    [Fact]
    public void ACounterThatWentBackwardsIsReportedAsAReset()
    {
        // These are unsigned on the wire. Subtracting naively underflows to ~1.8e19 and paints a
        // spike that never happened.
        Assert.Equal(-1, CpuUsage.Delta(10, 1_000));
    }
}

public class ContainerStatsTests
{
    static ContainerStats Sample(long memory, long limit) =>
        new(DateTimeOffset.UnixEpoch, 0, memory, limit, 0, 0, 0, 0, 1);

    [Fact]
    public void MemoryPercentIsAgainstTheLimit()
        => Assert.Equal(25, Sample(256, 1024).MemoryPercent!.Value, 1);

    [Fact]
    public void AContainerWithNoLimitHasNoPercentage()
    {
        // The engine reports an unlimited container with the host's total memory as its limit.
        // "3% of 32 GB" implies a bound the user never set.
        var sample = Sample(256, 0);

        Assert.False(sample.HasMemoryLimit);
        Assert.Null(sample.MemoryPercent);
    }
}

public class StatsHistoryTests
{
    static ContainerStats At(int second, double cpu) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(second), cpu, 0, 0, 0, 0, 0, 0, 1);

    [Fact]
    public void OldSamplesFallOffTheEnd()
    {
        var history = new StatsHistory(capacity: 3);

        for (var i = 0; i < 5; i++) history.Add(At(i, i));

        Assert.Equal(3, history.Samples.Count);
        Assert.Equal(4, history.Latest!.CpuPercent);
    }

    [Fact]
    public void TheSeriesIsScaledToItsOwnPeak()
    {
        // A container using 2% of the host would be a flat line at the bottom against a fixed
        // ceiling, and the shape of that 2% is exactly what someone is looking at.
        var history = new StatsHistory();
        history.Add(At(0, 1));
        history.Add(At(1, 2));

        Assert.Equal([0.5, 1.0], history.Normalized(s => s.CpuPercent));
    }

    [Fact]
    public void AFlatZeroSeriesStaysFlat()
    {
        var history = new StatsHistory();
        history.Add(At(0, 0));
        history.Add(At(1, 0));

        Assert.Equal([0.0, 0.0], history.Normalized(s => s.CpuPercent));
    }

    [Fact]
    public void AMissingReadingPlotsAsZeroRatherThanBreakingTheSeries()
    {
        var history = new StatsHistory();
        history.Add(new ContainerStats(DateTimeOffset.UnixEpoch, null, 0, 0, 0, 0, 0, 0, 1));
        history.Add(At(1, 4));

        Assert.Equal([0.0, 1.0], history.Normalized(s => s.CpuPercent));
    }

    [Fact]
    public void AnEmptyHistoryPlotsNothing()
        => Assert.Empty(new StatsHistory().Normalized(s => s.CpuPercent));

    [Fact]
    public void ClearingResetsTheLatestReadingToo()
    {
        var history = new StatsHistory();
        history.Add(At(0, 5));
        history.Clear();

        Assert.Null(history.Latest);
        Assert.Empty(history.Samples);
    }
}
