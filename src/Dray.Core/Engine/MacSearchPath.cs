namespace Dray.Core.Engine;

/// <summary>
/// The <c>PATH</c> a Mac app needs to find the engines' own commands.
/// <para>
/// An app opened from Finder or the Dock is started by launchd, not by a shell, and gets launchd's
/// PATH: <c>/usr/bin:/bin:/usr/sbin:/sbin</c>. None of the commands Dray drives is there — not
/// <c>docker</c> or its compose and buildx plugins, not the credential helpers registry sign-in
/// goes through, not Apple's <c>container</c>, not Colima. Run from a terminal, the app inherits the
/// shell's PATH and everything works, which is exactly why this goes unnoticed in development.
/// </para>
/// <para>
/// The directories added are the system's own list — <c>/etc/paths</c> and <c>/etc/paths.d</c>,
/// what <c>path_helper</c> gives every login shell — and then the places the engines install their
/// commands. A login shell is not asked: it would run the user's shell configuration inside the
/// app's launch, and a slow or interactive one would hold the window back.
/// </para>
/// </summary>
public static class MacSearchPath
{
    /// <summary>
    /// Where the engines put their commands, after the system's list. Homebrew first, because
    /// Colima and the docker command a Colima user has come from there.
    /// </summary>
    static readonly string[] EngineDirectories =
    [
        "/opt/homebrew/bin",
        "/opt/homebrew/sbin",
        "/usr/local/bin",
        "~/.docker/bin",
        "~/.orbstack/bin",
        "~/.rd/bin",
        "/Applications/Docker.app/Contents/Resources/bin",
    ];

    /// <summary>
    /// <paramref name="current"/> with every missing directory appended, in order. What is already
    /// there keeps its place and its precedence: a user who launched from a terminal chose theirs.
    /// </summary>
    /// <param name="current">The PATH the process has.</param>
    /// <param name="systemPaths">The lines of <c>/etc/paths</c> and each file in <c>/etc/paths.d</c>, in order.</param>
    /// <param name="home">The user's home directory, for the entries written with <c>~</c>.</param>
    /// <param name="directoryExists">Whether a directory is there. One that is not is left out.</param>
    public static string Augment(
        string? current,
        IEnumerable<string> systemPaths,
        string home,
        Func<string, bool> directoryExists)
    {
        var entries = (current ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
        var seen = new HashSet<string>(entries.Select(Normalise), StringComparer.Ordinal);

        foreach (var line in systemPaths.Concat(EngineDirectories))
        {
            var directory = Expand(line.Trim(), home);
            if (directory.Length == 0 || directory.StartsWith('#')) continue;
            if (!seen.Add(Normalise(directory)) || !directoryExists(directory)) continue;

            entries.Add(directory);
        }

        return string.Join(':', entries);
    }

    /// <summary>
    /// Give this process the PATH it would have had from a terminal. For the macOS head, before
    /// anything is discovered or run.
    /// </summary>
    public static void Apply()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var augmented = Augment(Environment.GetEnvironmentVariable("PATH"), ReadSystemPaths(), home, Directory.Exists);

        Environment.SetEnvironmentVariable("PATH", augmented);
    }

    /// <summary><c>/etc/paths</c>, then <c>/etc/paths.d</c> in name order — path_helper's order.</summary>
    static IEnumerable<string> ReadSystemPaths()
    {
        var files = new List<string> { "/etc/paths" };

        try
        {
            files.AddRange(Directory.GetFiles("/etc/paths.d").Order(StringComparer.Ordinal));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No paths.d, or not readable: the system list is /etc/paths alone.
        }

        foreach (var file in files)
        {
            string[] lines;

            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var line in lines) yield return line;
        }
    }

    /// <summary>
    /// A leading <c>~</c> as the home directory. paths.d files are read by path_helper, which does
    /// not expand it — but installers write it anyway (the .NET SDK's does), and the directory they
    /// mean is plain.
    /// </summary>
    static string Expand(string entry, string home)
        => entry == "~" ? home
            : entry.StartsWith("~/", StringComparison.Ordinal) ? home.TrimEnd('/') + entry[1..]
            : entry;

    static string Normalise(string directory) => directory.Length > 1 ? directory.TrimEnd('/') : directory;
}
