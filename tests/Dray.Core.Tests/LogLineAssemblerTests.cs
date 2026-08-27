using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// A frame is a chunk of bytes, not a line. Everything here is about that gap.
/// </summary>
public class LogLineAssemblerTests
{
    [Fact]
    public void OneFrameCanCarrySeveralLines()
    {
        var assembler = new LogLineAssembler();

        var lines = assembler.Append(LogStream.StdOut, "one\ntwo\nthree\n", timestamped: false).ToList();

        Assert.Equal(["one", "two", "three"], lines.Select(l => l.Text));
    }

    [Fact]
    public void ALineSplitAcrossFramesIsRejoined()
    {
        // The engine flushes when it feels like it, so a line arriving in halves is normal rather
        // than exceptional.
        var assembler = new LogLineAssembler();

        Assert.Empty(assembler.Append(LogStream.StdOut, "start of a ", timestamped: false));
        var lines = assembler.Append(LogStream.StdOut, "line\n", timestamped: false).ToList();

        Assert.Equal("start of a line", Assert.Single(lines).Text);
    }

    [Fact]
    public void StdOutAndStdErrDoNotContaminateEachOther()
    {
        // The streams interleave arbitrarily. One buffer would splice a stderr line into the
        // middle of a half-finished stdout line.
        var assembler = new LogLineAssembler();

        assembler.Append(LogStream.StdOut, "out-", timestamped: false).ToList();
        assembler.Append(LogStream.StdErr, "err-", timestamped: false).ToList();

        var errLines = assembler.Append(LogStream.StdErr, "line\n", timestamped: false).ToList();
        var outLines = assembler.Append(LogStream.StdOut, "line\n", timestamped: false).ToList();

        Assert.Equal("err-line", Assert.Single(errLines).Text);
        Assert.Equal("out-line", Assert.Single(outLines).Text);
    }

    [Fact]
    public void CarriageReturnsAreDropped()
    {
        var assembler = new LogLineAssembler();

        var line = Assert.Single(assembler.Append(LogStream.StdOut, "windows\r\n", timestamped: false));

        Assert.Equal("windows", line.Text);
    }

    [Fact]
    public void FlushEmitsATrailingLineWithNoNewline()
    {
        // A container that dies mid-line loses its last and often most interesting output
        // otherwise.
        var assembler = new LogLineAssembler();
        assembler.Append(LogStream.StdErr, "panic: nil pointer", timestamped: false).ToList();

        var flushed = assembler.Flush(timestamped: false).ToList();

        Assert.Equal("panic: nil pointer", Assert.Single(flushed).Text);
    }

    [Fact]
    public void FlushOnAnEmptyBufferEmitsNothing()
    {
        var assembler = new LogLineAssembler();
        assembler.Append(LogStream.StdOut, "complete\n", timestamped: false).ToList();

        Assert.Empty(assembler.Flush(timestamped: false));
    }

    [Fact]
    public void BlankLinesSurviveBecauseTheyCarryStructure()
    {
        var assembler = new LogLineAssembler();

        var lines = assembler.Append(LogStream.StdOut, "a\n\nb\n", timestamped: false).ToList();

        Assert.Equal(3, lines.Count);
        Assert.True(lines[1].IsBlank);
    }

    [Fact]
    public void SequenceIsMonotonicAcrossBothStreams()
    {
        // Used as the render key, so a collision would make Blazor reuse the wrong row.
        var assembler = new LogLineAssembler();

        var all = assembler.Append(LogStream.StdOut, "a\n", timestamped: false)
            .Concat(assembler.Append(LogStream.StdErr, "b\n", timestamped: false))
            .Concat(assembler.Append(LogStream.StdOut, "c\n", timestamped: false))
            .ToList();

        Assert.Equal([0, 1, 2], all.Select(l => l.Sequence));
    }

    // ---------------------------------------------------------------- timestamps

    [Fact]
    public void TheEnginesTimestampPrefixIsSplitOff()
    {
        var assembler = new LogLineAssembler();

        var line = Assert.Single(assembler.Append(
            LogStream.StdOut, "2026-08-27T16:20:47.759123456Z ready for start up\n", timestamped: true));

        Assert.Equal("ready for start up", line.Text);
        Assert.NotNull(line.Timestamp);
        Assert.Equal(2026, line.Timestamp.Value.Year);
    }

    [Fact]
    public void ALineWithoutAParseablePrefixKeepsAllItsText()
    {
        // Dropping output because the prefix was unexpected would lose the very line the user is
        // looking for.
        var assembler = new LogLineAssembler();

        var line = Assert.Single(assembler.Append(LogStream.StdOut, "no timestamp here\n", timestamped: true));

        Assert.Equal("no timestamp here", line.Text);
        Assert.Null(line.Timestamp);
    }

    [Fact]
    public void TimestampsAreNotStrippedWhenNotRequested()
    {
        // A container that legitimately prints a date at the start of its own lines must keep it.
        var assembler = new LogLineAssembler();

        var line = Assert.Single(assembler.Append(
            LogStream.StdOut, "2026-08-27T16:20:47.759Z my own prefix\n", timestamped: false));

        Assert.StartsWith("2026-08-27T", line.Text, StringComparison.Ordinal);
        Assert.Null(line.Timestamp);
    }

    [Theory]
    [InlineData("short line")]
    [InlineData("2026-08-27 no-T-separator")]
    [InlineData("notadate!!!!!!!!!!!!! text")]
    [InlineData("")]
    public void MalformedPrefixesFallBackToTheWholeLine(string raw)
    {
        var (timestamp, text) = LogLineAssembler.SplitTimestamp(raw);

        Assert.Null(timestamp);
        Assert.Equal(raw, text);
    }
}
