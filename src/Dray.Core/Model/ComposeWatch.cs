namespace Dray.Core.Model;

/// <summary>
/// Whether <c>compose watch</c> is a thing this stack can do.
/// <para>
/// Two separate questions, and both have to be yes. Compose has to be new enough to have the
/// subcommand, and the file has to declare what to watch — <c>watch</c> without a
/// <c>develop.watch</c> block exits immediately with a message about there being nothing to do,
/// which is a worse answer than not offering the button.
/// </para>
/// </summary>
public static class ComposeWatch
{
    /// <summary>The release that made <c>watch</c> a real subcommand rather than an alpha one.</summary>
    public static readonly Version Minimum = new(2, 22);

    /// <summary>
    /// Whether a compose version string has the subcommand.
    /// </summary>
    /// <param name="version">
    /// As compose reports it: <c>v2.39.1</c>, <c>2.39.1</c>, or occasionally with a suffix. An
    /// unreadable version is treated as too old — offering a button that fails is worse than
    /// hiding one that would have worked.
    /// </param>
    public static bool IsSupported(string? version)
        => Parse(version) is { } parsed && parsed >= Minimum;

    static Version? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        var text = version.Trim().TrimStart('v', 'V');

        // Take the leading numeric run, so "2.39.1-desktop.1" compares as 2.39.1.
        var end = 0;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.')) end++;

        text = text[..end].Trim('.');

        return Version.TryParse(text, out var parsed)
            ? parsed
            : int.TryParse(text, out var major) ? new Version(major, 0) : null;
    }

    /// <summary>
    /// The services that declare something to watch, in file order.
    /// <para>
    /// Same deliberate shallowness as <see cref="ComposeGraph"/>: this reads one key out of a file
    /// whose overall structure it does not care about. A line it cannot make sense of costs that
    /// service, not the answer.
    /// </para>
    /// <code>
    /// services:
    ///   web:
    ///     develop:
    ///       watch:
    ///         - action: sync
    ///           path: ./src
    ///           target: /app/src
    /// </code>
    /// </summary>
    public static IReadOnlyList<string> Declares(string? yaml)
    {
        var found = new List<string>();

        if (string.IsNullOrWhiteSpace(yaml)) return found;

        var servicesIndent = -1;
        var serviceIndent = -1;
        var developIndent = -1;

        string? service = null;

        foreach (var raw in yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#')) continue;

            var indent = line.Length - line.TrimStart().Length;
            var text = line.Trim();

            if (servicesIndent < 0)
            {
                if (text is "services:") servicesIndent = indent;
                continue;
            }

            // Another top-level key — out of the services block.
            if (indent <= servicesIndent && text.EndsWith(':'))
            {
                service = null;
                serviceIndent = -1;
                developIndent = -1;
                servicesIndent = text is "services:" ? indent : -1;
                continue;
            }

            if (developIndent >= 0 && indent <= developIndent) developIndent = -1;

            // A service name: one level in from `services:`.
            if ((serviceIndent < 0 || indent == serviceIndent) && text.EndsWith(':'))
            {
                serviceIndent = indent;
                service = text[..^1].Trim();
                developIndent = -1;
                continue;
            }

            if (service is null) continue;

            if (text is "develop:")
            {
                developIndent = indent;
                continue;
            }

            // `watch:` only counts inside develop. A top-level `watch:` on a service is not a
            // thing compose reads, and counting it would offer a button that does nothing.
            if (developIndent >= 0
                && indent > developIndent
                && text.StartsWith("watch:", StringComparison.Ordinal)
                && !found.Contains(service, StringComparer.Ordinal))
            {
                found.Add(service);
            }
        }

        return found;
    }
}
