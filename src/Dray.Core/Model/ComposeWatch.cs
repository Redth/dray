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
        => [.. ComposeDocument.Parse(yaml).Services.Where(s => s.DeclaresWatch).Select(s => s.Name)];
}
