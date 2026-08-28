using System.Text.RegularExpressions;

namespace Dray.Core.Model;

/// <summary>How bad a log line says it is.</summary>
public enum LogLevel
{
    /// <summary>The line does not say, which is most lines.</summary>
    None,

    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// The level a program labelled its own line with.
/// <para>
/// Read rather than inferred: this looks for the word the program printed and nothing else. A log
/// viewer that guessed at severity would be wrong on exactly the lines that matter — a message
/// containing the word "failed" is often a program reporting that it handled a failure.
/// </para>
/// <para>
/// Only near the front, and only as a whole word. "no errors found" is not an error, and neither is
/// a stack frame that happens to mention one three hundred characters in.
/// </para>
/// </summary>
public static partial class LogSeverity
{
    /// <summary>
    /// How far in to look. Shorter than the timestamp window, because a level is printed before the
    /// message and the timestamp has already been lifted out by the time this runs.
    /// </summary>
    const int Window = 40;

    [GeneratedRegex(@"\b(ERROR|ERR|FATAL|CRITICAL|CRIT|PANIC|SEVERE|EMERG|ALERT)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Error();

    [GeneratedRegex(@"\b(WARNING|WARN)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Warning();

    [GeneratedRegex(@"\b(INFO|INFORMATION|NOTICE|LOG)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Info();

    [GeneratedRegex(@"\b(DEBUG|TRACE|VERBOSE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Debug();

    public static LogLevel Of(string? text)
    {
        if (string.IsNullOrEmpty(text)) return LogLevel.None;

        var head = text.Length <= Window ? text : text[..Window];

        // Worst first: a line reading "WARN could not parse, treating as error" is a warning, and
        // the program said so before it said anything else.
        if (Error().IsMatch(head)) return LogLevel.Error;
        if (Warning().IsMatch(head)) return LogLevel.Warning;
        if (Info().IsMatch(head)) return LogLevel.Info;
        if (Debug().IsMatch(head)) return LogLevel.Debug;

        return LogLevel.None;
    }
}
