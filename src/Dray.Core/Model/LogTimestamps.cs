using System.Text.RegularExpressions;

namespace Dray.Core.Model;

/// <summary>
/// Whether a log line already begins with a timestamp of its own.
/// <para>
/// The engine records when it received each line, and Dray can show that. Most programs also print
/// their own clock, so a redis line arrives as
/// <c>1:M 27 Aug 2026 21:49:47.403 * Ready to accept connections</c> and the view puts
/// <c>21:49:47.403</c> in front of it — the same instant, twice, in two formats.
/// </para>
/// <para>
/// Neither one is safe to delete. The engine's is the only timestamp on a program that prints
/// none, and the program's is part of its output, which Dray does not edit. So the duplication is
/// resolved by not <i>drawing</i> the engine's where the line already carries one: nothing is
/// hidden that was not already on screen, and the column keeps its width so the text stays aligned.
/// </para>
/// </summary>
public static partial class LogTimestamps
{
    /// <summary>
    /// How far into a line to look.
    /// <para>
    /// A timestamp a program prints is at the front. Searching the whole line would find the one in
    /// an HTTP request path or a quoted error and suppress a column for a line that has no clock on
    /// it at all.
    /// </para>
    /// </summary>
    const int Window = 48;

    /// <summary>
    /// A clock reading — <c>21:49:47</c>, with or without fractional seconds.
    /// <para>
    /// Matching the time rather than the date is what makes this work across formats without
    /// listing them: redis writes <c>27 Aug 2026 21:49:47.403</c>, nginx writes
    /// <c>2026/08/27 17:06:45</c>, an access log writes <c>[27/Aug/2026:17:06:45 +0000]</c> and a
    /// Go program writes <c>2026-08-27T21:47:59.214Z</c>. Every one of them contains hh:mm:ss, and
    /// a line that has hh:mm:ss in its first few dozen characters is a line that is telling you the
    /// time.
    /// </para>
    /// </summary>
    [GeneratedRegex(@"\d{1,2}:\d{2}:\d{2}", RegexOptions.CultureInvariant)]
    private static partial Regex Clock();

    public static bool CarriesItsOwn(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var head = text.Length <= Window ? text : text[..Window];
        return Clock().IsMatch(head);
    }
}
