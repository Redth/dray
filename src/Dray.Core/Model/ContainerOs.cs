namespace Dray.Core.Model;

/// <summary>
/// The operating system the containers on an engine actually run.
/// <para>
/// Not the same question as which OS Dray is running on, and not the same question as which OS the
/// daemon's machine runs. A Windows laptop can be talking to a Linux engine inside WSL2, to a
/// native Windows engine, or to a Linux box in a closet — and on Windows the two local cases share
/// a pipe name, so the name settles nothing. Only <c>/info</c>'s <c>OSType</c> does.
/// </para>
/// <para>
/// This is the one fact everything below the runtime seam branches on: paths, shells, exit codes,
/// stats units and the volume browser's helper image all differ, and each of them fails obscurely
/// rather than loudly when the guess is wrong.
/// </para>
/// </summary>
public enum ContainerOs
{
    /// <summary>The engine did not say. Nothing may be assumed from this — it is not "Linux".</summary>
    Unknown,

    Linux,

    Windows,
}

/// <summary>
/// Paths inside a container, in whichever of the two shapes the engine uses.
/// <para>
/// <see cref="System.IO.Path"/> is wrong here in both directions: it would produce backslashes for
/// a Linux container when Dray runs on Windows, and forward slashes for a Windows container when
/// Dray runs on macOS. The shape belongs to the container, never to the machine Dray is on, so
/// every method here takes the container's OS rather than reading the ambient one.
/// </para>
/// </summary>
public static class ContainerPath
{
    /// <summary>
    /// The drive a Windows container path gets when it names none.
    /// <para>
    /// Every Microsoft base image installs to <c>C:</c>, and the engine reports paths that way, so
    /// a bare <c>\inetpub</c> means <c>C:\inetpub</c>. This is a documented convention rather than
    /// a guess, but it is still the one assumption in this file.
    /// </para>
    /// </summary>
    public const char DefaultWindowsDrive = 'C';

    /// <summary>Where a file browser opens when it has nowhere else to start.</summary>
    public static string Root(ContainerOs os) => os == ContainerOs.Windows ? @"C:\" : "/";

    public static char Separator(ContainerOs os) => os == ContainerOs.Windows ? '\\' : '/';

    /// <summary>
    /// Put a path into the one form Dray compares, caches and sends to the engine.
    /// <para>
    /// Absolute always, because every container path is; separators unified; runs of separators
    /// collapsed; no trailing separator except at a root that consists of nothing else.
    /// </para>
    /// </summary>
    public static string Normalize(string? path, ContainerOs os)
        => os == ContainerOs.Windows ? NormalizeWindows(path) : NormalizePosix(path);

    /// <summary>The containing directory, or null when already at the root.</summary>
    public static string? ParentOf(string path, ContainerOs os)
    {
        path = Normalize(path, os);

        var root = Root(os);
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) return null;

        var cut = path.LastIndexOf(Separator(os));

        if (os == ContainerOs.Windows)
        {
            // "C:\inetpub" cuts at index 2, and the parent is "C:\" — the root keeps its
            // separator, unlike every directory below it.
            return cut <= 2 ? DriveRootOf(path) : path[..cut];
        }

        return cut <= 0 ? "/" : path[..cut];
    }

    /// <summary>Join a directory and one child name into an absolute container path.</summary>
    public static string Combine(string directory, string name, ContainerOs os)
    {
        var separator = Separator(os);
        var parent = Normalize(directory, os);

        // A root already ends in its separator; anything else needs one adding.
        return Normalize(
            parent.EndsWith(separator) ? parent + name : parent + separator + name,
            os);
    }

    /// <summary>
    /// True when a path is already rooted for this OS — <c>/etc</c>, or <c>C:\Windows</c>.
    /// <para>
    /// Used to tell a mount destination the user typed from one they have not finished typing,
    /// which is why it is deliberately strict about the drive letter on Windows.
    /// </para>
    /// </summary>
    public static bool IsAbsolute(string? path, ContainerOs os)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        path = path.Trim();

        return os == ContainerOs.Windows
            ? path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'
            : path.StartsWith('/');
    }

    static string NormalizePosix(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";

        path = path.Replace('\\', '/').Trim();
        if (!path.StartsWith('/')) path = "/" + path;

        while (path.Contains("//", StringComparison.Ordinal))
            path = path.Replace("//", "/", StringComparison.Ordinal);

        return path.Length > 1 ? path.TrimEnd('/') : "/";
    }

    static string NormalizeWindows(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return DefaultWindowsDrive + @":\";

        path = path.Replace('/', '\\').Trim();

        while (path.Contains(@"\\", StringComparison.Ordinal))
            path = path.Replace(@"\\", @"\", StringComparison.Ordinal);

        // Drive-qualified already: normalise the letter's case so two spellings of one directory
        // are one cache key.
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            var drive = char.ToUpperInvariant(path[0]);
            var rest = path[2..];

            if (!rest.StartsWith('\\')) rest = '\\' + rest;

            return rest.Length > 1 ? drive + ":" + rest.TrimEnd('\\') : drive + @":\";
        }

        // Rooted but driveless — "\inetpub" — is what the engine and `dir` both emit.
        var tail = path.TrimStart('\\').TrimEnd('\\');

        return tail.Length == 0
            ? DefaultWindowsDrive + @":\"
            : DefaultWindowsDrive + @":\" + tail;
    }

    /// <summary>The <c>X:\</c> a normalized Windows path sits on.</summary>
    static string DriveRootOf(string normalized)
        => normalized.Length >= 2 && normalized[1] == ':'
            ? normalized[0] + @":\"
            : DefaultWindowsDrive + @":\";
}
