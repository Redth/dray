namespace Dray.Core.Model;

/// <summary>A stack defined by files on this machine, whether or not it has ever run.</summary>
/// <param name="Project">The compose project name these files produce.</param>
/// <param name="WorkingDirectory">The project directory — where <c>.env</c> is, and what relative paths resolve against.</param>
/// <param name="Files">The compose files, absolute, in the order compose is given them.</param>
public sealed record StackDefinition(string Project, string WorkingDirectory, IReadOnlyList<string> Files);

/// <summary>
/// Reading a folder as a compose project, the way compose itself would.
/// <para>
/// Compose decides the project name and the file list from several places, in an order, and Dray
/// has to reach the same answer or it will show one stack as two — Dockge's bug with a top-level
/// <c>name:</c> that differs from the folder. The order, highest first: <c>COMPOSE_PROJECT_NAME</c>
/// and <c>COMPOSE_FILE</c> in the project's <c>.env</c>, then the file's own <c>name:</c>, then
/// the folder's name normalised.
/// </para>
/// </summary>
public static class StackFolder
{
    /// <summary>The names compose looks for, in its own order of preference.</summary>
    public static readonly IReadOnlyList<string> ComposeFileNames =
        ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"];

    /// <summary>
    /// The override compose loads automatically beside the base file. Passing <c>-f</c> turns that
    /// discovery off, so Dray has to find it itself or an override would silently stop applying.
    /// </summary>
    public static readonly IReadOnlyList<string> OverrideFileNames =
        ["compose.override.yaml", "compose.override.yml", "docker-compose.override.yaml", "docker-compose.override.yml"];

    /// <summary>The compose project in <paramref name="directory"/>, or null when there is none.</summary>
    public static StackDefinition? Read(string directory)
    {
        if (!Directory.Exists(directory)) return null;

        var variables = ReadEnv(directory);
        var files = FilesFrom(directory, variables.GetValueOrDefault("COMPOSE_FILE"));
        if (files.Count == 0) return null;

        string? fromFile = null;

        try
        {
            fromFile = ComposeDocument.Parse(File.ReadAllText(files[0])).ProjectName;
        }
        catch (IOException)
        {
        }

        // A name with ${VAR} in it is compose's to resolve, not Dray's to guess.
        if (fromFile is not null && fromFile.Contains('$', StringComparison.Ordinal)) fromFile = null;

        var name = variables.GetValueOrDefault("COMPOSE_PROJECT_NAME") is { Length: > 0 } fromEnv
            ? fromEnv
            : fromFile ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));

        var project = NormalizeProjectName(name);
        return project.Length == 0 ? null : new StackDefinition(project, Path.GetFullPath(directory), files);
    }

    /// <summary>
    /// Every compose project under <paramref name="root"/>.
    /// <para>
    /// Stops descending at the first folder that is a project: a stack's subfolders are its data —
    /// a database's files, a site's assets — and walking into them is slow at best and finds
    /// another tool's compose files at worst. Hidden folders and <c>node_modules</c> are skipped for
    /// the same reason, and the walk is bounded so a stacks folder pointed at a home directory
    /// costs a moment rather than a minute.
    /// </para>
    /// </summary>
    public static IReadOnlyList<StackDefinition> Scan(string root, int maxDepth = 3, int maxFolders = 2000)
    {
        var found = new List<StackDefinition>();
        if (!Directory.Exists(root)) return found;

        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;

        while (queue.Count > 0 && visited++ < maxFolders)
        {
            var (path, depth) = queue.Dequeue();

            if (depth > 0 && Read(path) is { } definition)
            {
                found.Add(definition);
                continue;
            }

            if (depth >= maxDepth) continue;

            IEnumerable<string> children;

            try
            {
                children = Directory.EnumerateDirectories(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children.Order(StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || name == "node_modules") continue;

                queue.Enqueue((child, depth + 1));
            }
        }

        return found;
    }

    /// <summary>
    /// What compose turns a name into: lower case, and only letters, digits, <c>-</c> and <c>_</c>,
    /// starting with a letter or digit. Compose lowercases an upper-case folder name silently, which
    /// is how one stack once appeared twice in Dockge.
    /// </summary>
    public static string NormalizeProjectName(string name)
    {
        var kept = new string([.. name.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')]);
        return kept.TrimStart('-', '_');
    }

    /// <summary>Whether a name is already a valid project name, unchanged by normalisation.</summary>
    public static bool IsValidProjectName(string name)
        => name.Length > 0 && NormalizeProjectName(name) == name;

    static IReadOnlyList<string> FilesFrom(string directory, string? composeFile)
    {
        if (composeFile is { Length: > 0 })
        {
            // COMPOSE_PATH_SEPARATOR defaults to the platform's path-list separator.
            return
            [
                .. composeFile
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(f => Path.GetFullPath(Path.Combine(directory, f)))
                    .Where(File.Exists),
            ];
        }

        var main = ComposeFileNames.Select(n => Path.Combine(directory, n)).FirstOrDefault(File.Exists);
        if (main is null) return [];

        var overrides = OverrideFileNames.Select(n => Path.Combine(directory, n)).FirstOrDefault(File.Exists);

        return overrides is null ? [Path.GetFullPath(main)] : [Path.GetFullPath(main), Path.GetFullPath(overrides)];
    }

    static Dictionary<string, string> ReadEnv(string directory)
    {
        var path = Path.Combine(directory, DotEnvFile.FileName);

        try
        {
            return File.Exists(path)
                ? DotEnvFile.Parse(File.ReadAllText(path)).GroupBy(v => v.Key).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal)
                : [];
        }
        catch (IOException)
        {
            return [];
        }
    }
}
