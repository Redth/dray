namespace Dray.Core.Model;

/// <summary>One entry in a container's filesystem.</summary>
public sealed record FileEntry(
    string Name,
    string Path,
    bool IsDirectory,
    long Size,
    DateTimeOffset? Modified = null,
    string? Mode = null,
    string? LinkTarget = null)
{
    public bool IsSymlink => LinkTarget is not null;

    /// <summary>Parent directory, or null at the root.</summary>
    public static string? ParentOf(string path)
    {
        path = Normalize(path);
        if (path == "/") return null;

        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    /// <summary>Join a directory and a child name into an absolute container path.</summary>
    public static string Combine(string directory, string name)
        => Normalize(Normalize(directory) == "/" ? "/" + name : Normalize(directory) + "/" + name);

    /// <summary>
    /// Container paths are always absolute and POSIX, whatever the host running Dray. Using
    /// <c>System.IO.Path</c> here would produce backslashes on Windows and break every lookup.
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";

        path = path.Replace('\\', '/').Trim();
        if (!path.StartsWith('/')) path = "/" + path;

        while (path.Contains("//", StringComparison.Ordinal))
            path = path.Replace("//", "/", StringComparison.Ordinal);

        return path.Length > 1 ? path.TrimEnd('/') : "/";
    }
}

/// <summary>How a listing was obtained. Surfaced because the two have different costs and limits.</summary>
public enum ListingMethod
{
    /// <summary>
    /// <c>ls</c> run inside the container. Fast and cheap, but needs a shell and a running
    /// container.
    /// </summary>
    Exec,

    /// <summary>
    /// Read from a tar of the directory. Works on any image including <c>scratch</c>, and on
    /// stopped containers — but the engine streams the directory's actual contents to produce it.
    /// </summary>
    Archive,
}

/// <summary>The contents of one directory, and how Dray got them.</summary>
public sealed record DirectoryListing(
    string Path,
    IReadOnlyList<FileEntry> Entries,
    ListingMethod Method,
    string? Note = null)
{
    /// <summary>True when the listing is incomplete — the archive path stopped at its budget.</summary>
    public bool IsTruncated => Note is not null;

    /// <summary>Directories first, then files, each alphabetically. What a file browser should do.</summary>
    public IReadOnlyList<FileEntry> Sorted =>
        [.. Entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
}

/// <summary>
/// Parses the output of <c>ls -la</c> from inside a container.
/// <para>
/// Deliberately defensive. The format is not standardised: GNU coreutils, BusyBox and Toybox all
/// differ in their date columns and in whether they print a <c>total</c> header, and Dray has no
/// say in which is present. A line that will not parse is skipped rather than failing the whole
/// listing — one odd entry must not cost the user the other ninety-nine.
/// </para>
/// </summary>
public static class LsParser
{
    public static IReadOnlyList<FileEntry> Parse(string output, string directory)
    {
        var entries = new List<FileEntry>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            // Both GNU and BusyBox lead with a block count.
            if (line.StartsWith("total ", StringComparison.Ordinal)) continue;

            if (TryParseLine(line, directory) is { } entry) entries.Add(entry);
        }

        return entries;
    }

    internal static FileEntry? TryParseLine(string line, string directory)
    {
        // mode links owner group size <date fields…> name
        // The date occupies a different number of columns per implementation, so the name is found
        // by walking forward from the size rather than by counting columns.
        var mode = FirstToken(line, out var afterMode);
        if (mode is null || mode.Length < 10) return null;

        var type = mode[0];
        if (type is not ('-' or 'd' or 'l' or 'c' or 'b' or 's' or 'p')) return null;

        // links, owner, group
        var cursor = afterMode;
        for (var i = 0; i < 3; i++)
        {
            if (NextToken(line, cursor, out cursor) is null) return null;
        }

        var sizeToken = NextToken(line, cursor, out cursor);
        if (sizeToken is null || !long.TryParse(sizeToken, out var size)) return null;

        // Skip date columns: everything up to the name. GNU with --full-time uses three tokens,
        // GNU default and BusyBox use three, but a locale can change that — so instead of counting,
        // consume tokens while they still look like date fragments.
        string? nameStart = null;
        var probe = cursor;
        for (var i = 0; i < 5; i++)
        {
            var next = NextToken(line, probe, out var after);
            if (next is null) return null;

            if (!LooksLikeDateFragment(next))
            {
                nameStart = next;
                cursor = after;
                break;
            }

            probe = after;
            cursor = after;
        }

        if (nameStart is null) return null;

        // The name may contain spaces, so take the rest of the line from where it started rather
        // than the single token.
        var nameIndex = line.Length - cursor.Length - nameStart.Length;
        var name = line[nameIndex..].TrimEnd();
        if (name.Length == 0) return null;

        string? linkTarget = null;
        if (type == 'l')
        {
            var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow > 0)
            {
                linkTarget = name[(arrow + 4)..];
                name = name[..arrow];
            }
        }

        // Present in every listing and never useful to show.
        if (name is "." or "..") return null;

        return new FileEntry(
            name,
            FileEntry.Combine(directory, name),

            // A symlink to a directory is still browsable; the target's type is unknown from here,
            // so following it is resolved when the user opens it.
            IsDirectory: type == 'd',
            Size: size,
            Mode: mode,
            LinkTarget: linkTarget);
    }

    /// <summary>
    /// Whether a token is part of a date rather than the start of a filename.
    /// <para>
    /// Dates are digits, colons, dashes and short month names. A filename could of course look
    /// like that, but a file called "Aug" in a directory listing is rarer than getting the column
    /// count wrong across three <c>ls</c> implementations.
    /// </para>
    /// </summary>
    static bool LooksLikeDateFragment(string token)
    {
        if (token.Length is 0 or > 12) return false;

        if (token.All(c => char.IsAsciiDigit(c) || c is ':' or '-' or '.' or '+')) return true;

        return Months.Contains(token);
    }

    static readonly HashSet<string> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
    };

    static string? FirstToken(string line, out string rest)
    {
        rest = line;
        return NextToken(line, line, out rest);
    }

    /// <summary>
    /// Reads the next whitespace-delimited token from <paramref name="cursor"/>, a suffix of
    /// <paramref name="line"/>, and returns the remainder through <paramref name="rest"/>.
    /// </summary>
    static string? NextToken(string line, string cursor, out string rest)
    {
        var i = 0;
        while (i < cursor.Length && char.IsWhiteSpace(cursor[i])) i++;
        if (i >= cursor.Length)
        {
            rest = string.Empty;
            return null;
        }

        var start = i;
        while (i < cursor.Length && !char.IsWhiteSpace(cursor[i])) i++;

        rest = cursor[i..];
        return cursor[start..i];
    }
}
