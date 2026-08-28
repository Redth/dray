using System.Text.RegularExpressions;

namespace Dray.Core.Model;

/// <summary>
/// Reconciling the two clocks on a log line.
/// <para>
/// The engine records when it received each line, and most programs also print their own. A redis
/// line arrives as <c>1:M 27 Aug 2026 21:49:47.403 * Ready to accept connections</c> and the view
/// used to put <c>21:49:47.403</c> in front of it — the same instant, twice, in two formats.
/// </para>
/// <para>
/// Blanking the column on those lines removed the duplication and left a ragged gutter instead:
/// redis prints its own clock on nearly every line, so nearly every row had an empty column and the
/// occasional one did not. So the program's timestamp is lifted out of the message and the engine's
/// is shown in its place — one column, always filled, and the message starts at the same character
/// on every row.
/// </para>
/// <para>
/// Nothing is lost. The two are the same instant, the engine's is the one Dray can render in the
/// user's timezone and format, and Copy still copies the line exactly as the program wrote it.
/// </para>
/// </summary>
public static partial class LogTimestamps
{
    /// <summary>
    /// How far into a line to look.
    /// <para>
    /// A timestamp a program prints is at the front, after at most a short prefix — redis puts
    /// <c>1:M</c> there, an access log puts the client address. Searching the whole line would find
    /// the one inside a quoted request path and cut a hole in the middle of the message.
    /// </para>
    /// </summary>
    const int Window = 48;

    /// <summary>
    /// A date, if there is one, followed by a clock.
    /// <para>
    /// The alternatives are the ways real programs write a date, gathered from containers rather
    /// than from a specification: <c>2026-08-27</c> and <c>2026/08/27</c> (Go, nginx, postgres),
    /// <c>27 Aug 2026</c> (redis), <c>27/Aug/2026</c> (an access log), and <c>Aug 27</c> (syslog,
    /// which prints no year at all).
    /// </para>
    /// <para>
    /// The clock is the required part. A date without one is not what this is for — it is the time
    /// that repeats in the column, and a line with a date and no clock is a line about a date.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"(\d{4}[-/]\d{2}[-/]\d{2}|\d{1,2}[ /]\w{3}[ /]\d{4}|\w{3} {1,2}\d{1,2})?[T:, ]?\d{1,2}:\d{2}:\d{2}([.,]\d+)?( ?(Z|[+-]\d{2}:?\d{2}|[A-Z]{2,4}))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex Stamp();

    /// <summary>Whether the line prints a clock of its own.</summary>
    public static bool CarriesItsOwn(string? text) => Match(text) is not null;

    /// <summary>
    /// The line with its own timestamp lifted out, or the line unchanged when it has none.
    /// <para>
    /// Only the matched span is removed, and the space it leaves is closed up. Everything else the
    /// program wrote stays — redis's <c>1:M</c> and <c>*</c> are its process role and its severity,
    /// which are worth more than the clock that was duplicated.
    /// </para>
    /// </summary>
    public static string WithoutItsOwn(string? text)
    {
        if (text is null) return string.Empty;
        if (Match(text) is not { } match) return text;

        var stripped = text.Remove(match.Index, match.Length);

        // The brackets an access log wraps its timestamp in are left behind as `[]`, and a line
        // that begins with the removed stamp starts with the space that followed it.
        stripped = EmptyBrackets().Replace(stripped, string.Empty);
        stripped = Gap().Replace(stripped, " ").Trim();

        // A line that was nothing but its timestamp keeps it: an empty row says less than a
        // redundant one, and this is a display change, not an edit.
        return stripped.Length == 0 ? text : stripped;
    }

    static Match? Match(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var head = text.Length <= Window ? text : text[..Window];
        var match = Stamp().Match(head);

        return match.Success ? match : null;
    }

    [GeneratedRegex(@"\[\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyBrackets();

    [GeneratedRegex(@" {2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Gap();
}
