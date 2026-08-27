namespace Dray.Core.Model;

/// <summary>Which of the container's streams a line came from.</summary>
public enum LogStream
{
    StdOut,

    /// <summary>
    /// Worth distinguishing: plenty of well-behaved programs log everything to stderr, so this is
    /// a channel, not a severity. Dray marks it without treating it as an error.
    /// </summary>
    StdErr,
}

/// <summary>One line of container output.</summary>
/// <param name="Timestamp">
/// The engine's timestamp, when logs were requested with timestamps. Null otherwise — Dray does not
/// substitute the time it happened to read the line, which would be a different and misleading fact.
/// </param>
/// <param name="Stream">Which stream produced it.</param>
/// <param name="Text">The line, with the timestamp prefix and trailing newline already removed.</param>
/// <param name="Sequence">Monotonic index within one streaming session. Used as a stable render key.</param>
public sealed record LogLine(
    DateTimeOffset? Timestamp,
    LogStream Stream,
    string Text,
    long Sequence)
{
    /// <summary>True when the line is only whitespace — kept, because blank lines carry structure.</summary>
    public bool IsBlank => string.IsNullOrWhiteSpace(Text);
}

/// <summary>What to ask the engine for.</summary>
/// <param name="Follow">Keep the stream open and deliver new lines as they arrive.</param>
/// <param name="Tail">
/// How many lines of history to fetch. Null means all of it, which on a long-running container can
/// be enormous, so callers should pass a bound and offer to load more.
/// </param>
/// <param name="Timestamps">Ask the engine to prefix each line with its timestamp.</param>
/// <param name="Since">Only lines after this moment.</param>
public sealed record LogOptions(
    bool Follow = true,
    int? Tail = 500,
    bool Timestamps = true,
    DateTimeOffset? Since = null)
{
    public static readonly LogOptions Default = new();
}

/// <summary>
/// Turns the engine's byte frames into lines.
/// <para>
/// A frame is a chunk of the stream, not a line: one frame can hold several lines, or half of one,
/// and stdout and stderr interleave arbitrarily. Each stream therefore needs its own buffer, or a
/// line split across two frames comes out mangled and a line from the other stream lands in the
/// middle of it.
/// </para>
/// </summary>
public sealed class LogLineAssembler
{
    readonly Dictionary<LogStream, System.Text.StringBuilder> _partial = new()
    {
        [LogStream.StdOut] = new(),
        [LogStream.StdErr] = new(),
    };

    long _sequence;

    /// <summary>Feed a decoded frame and get back whatever complete lines it finished.</summary>
    public IEnumerable<LogLine> Append(LogStream stream, string chunk, bool timestamped)
    {
        var buffer = _partial[stream];

        foreach (var ch in chunk)
        {
            // Normalised away rather than emitted: a container writing CRLF would otherwise leave a
            // stray carriage return at the end of every line.
            if (ch == '\r') continue;

            if (ch != '\n')
            {
                buffer.Append(ch);
                continue;
            }

            yield return Build(stream, buffer.ToString(), timestamped);
            buffer.Clear();
        }
    }

    /// <summary>
    /// Emit whatever is buffered without a terminating newline.
    /// <para>
    /// Called when the stream ends. A container that dies mid-line, or one whose last line has no
    /// trailing newline, would otherwise lose its final — and often most interesting — output.
    /// </para>
    /// </summary>
    public IEnumerable<LogLine> Flush(bool timestamped)
    {
        foreach (var (stream, buffer) in _partial)
        {
            if (buffer.Length == 0) continue;

            yield return Build(stream, buffer.ToString(), timestamped);
            buffer.Clear();
        }
    }

    LogLine Build(LogStream stream, string raw, bool timestamped)
    {
        var (timestamp, text) = timestamped ? SplitTimestamp(raw) : (null, raw);
        return new LogLine(timestamp, stream, text, _sequence++);
    }

    /// <summary>
    /// Split the engine's RFC3339 prefix off the front of a line.
    /// <para>
    /// Returns the whole line as text when the prefix is missing or unparseable, rather than
    /// dropping output. A line Dray cannot date is still a line the user needs to read.
    /// </para>
    /// </summary>
    public static (DateTimeOffset? Timestamp, string Text) SplitTimestamp(string raw)
    {
        var space = raw.IndexOf(' ');
        if (space <= 0) return (null, raw);

        var candidate = raw[..space];

        // Cheap rejection before the expensive parse: the engine's format is always
        // 2026-08-27T12:34:56.789012345Z, so anything without the date separators is not one.
        if (candidate.Length < 20 || candidate[4] != '-' || candidate[10] != 'T') return (null, raw);

        return DateTimeOffset.TryParse(
            candidate,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? (parsed, raw[(space + 1)..])
            : (null, raw);
    }
}
