using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dray.Core.Engine;

/// <summary>
/// A stack Dray has seen deployed on a host. What keeps a stack on the list after it is brought
/// down: once its containers are gone, so are the labels it was discovered from.
/// </summary>
/// <param name="Host">The host's id — a context name where there is one.</param>
/// <param name="OnHost">The paths are on the engine's host, reached over SSH, rather than on this machine.</param>
public sealed record StackRecord(string Host, string Project, string WorkingDirectory, IReadOnlyList<string> Files, bool OnHost = false);

/// <summary>
/// What Dray remembers about stacks, in one small JSON file.
/// <para>
/// Paths and names, never values. A stack's variables are in its <c>.env</c>, where compose reads
/// them; copying them here would make a second place a password lives. The file is rewritten
/// whole, through a temporary file, so a crash mid-write leaves the previous version rather than
/// half of one.
/// </para>
/// </summary>
public sealed class StackRegistry
{
    readonly string _path;
    readonly Lock _lock = new();
    State _state;

    /// <summary>The file's write time when this process last read or wrote it.</summary>
    DateTime _seen;

    public StackRegistry(string path)
    {
        _path = path;
        _seen = File.GetLastWriteTimeUtc(path);
        _state = Read(path) ?? new State();
    }

    /// <summary>Raised after anything changes, from whichever thread changed it.</summary>
    public event Action? Changed;

    /// <summary>
    /// The folders scanned for stacks, and where a new one can be written — in the order they were
    /// added, so the first is the one a new stack goes to unless the user picks another. Empty until
    /// the user has said.
    /// </summary>
    public IReadOnlyList<string> StacksFolders
    {
        get { lock (_lock) return _state.FolderList; }
    }

    /// <summary>Folders outside the stacks folder that the user added as stacks.</summary>
    public IReadOnlyList<string> AdoptedFolders
    {
        get { lock (_lock) return [.. _state.Adopted]; }
    }

    /// <summary>The stacks this host has had containers for.</summary>
    public IReadOnlyList<StackRecord> KnownOn(string host)
    {
        lock (_lock) return [.. _state.Stacks.Where(s => s.Host == host)];
    }

    /// <summary>Whether the user told Dray to stop tracking this stack on this host.</summary>
    public bool IsForgotten(string host, string project)
    {
        lock (_lock) return _state.Forgotten.Any(f => f.Host == host && f.Project == project);
    }

    public void AddStacksFolder(string path)
    {
        var full = Normalize(path);
        Update(s => s.FolderList.Contains(full, PathComparer) ? s : s.WithFolders([.. s.FolderList, full]));
    }

    /// <summary>
    /// Stop scanning a folder. Its files are not touched; nor is anything running from it, which
    /// stays listed from its labels for as long as it runs.
    /// <para>
    /// What this host remembered from the folder goes with it. A down stack kept only by its record
    /// would otherwise stay on the list as though it had been added by hand — the opposite of what
    /// removing its folder asked for.
    /// </para>
    /// </summary>
    public void RemoveStacksFolder(string path)
    {
        var full = Normalize(path);

        Update(s => s.FolderList.Contains(full, PathComparer)
            ? s.WithFolders([.. s.FolderList.Where(f => !PathComparer.Equals(f, full))]) with
            {
                Stacks = [.. s.Stacks.Where(r => r.OnHost || !StackCatalog.IsUnder(r.WorkingDirectory, full))],
            }
            : s);
    }

    /// <summary>A folder as the registry keeps it: absolute, and without a trailing separator.</summary>
    static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    static StringComparer PathComparer => StackCatalog.PathComparer;

    public void Adopt(string folder)
    {
        var full = Path.GetFullPath(folder);
        Update(s => s.Adopted.Contains(full) ? s : s with { Adopted = [.. s.Adopted, full] });
    }

    /// <summary>
    /// Record that a host has this stack. Returns whether anything changed, so a caller can call it
    /// on every store change without rewriting the file each time.
    /// </summary>
    public bool Remember(StackRecord record)
    {
        var changed = false;

        Update(s =>
        {
            var existing = s.Stacks.FirstOrDefault(r => r.Host == record.Host && r.Project == record.Project);
            if (existing is not null && Same(existing, record)) return s;

            changed = true;
            return s with { Stacks = [.. s.Stacks.Where(r => r != existing), record] };
        });

        return changed;
    }

    /// <summary>
    /// Stop tracking a stack on a host: drop its record, its adopted folder, and keep it from being
    /// remembered again just because its containers are still there. Touches nothing on disk but
    /// this file, and nothing on the engine.
    /// </summary>
    public void Forget(string host, string project, string? folder)
    {
        var full = folder is null ? null : Path.GetFullPath(folder);

        Update(s => s with
        {
            Stacks = [.. s.Stacks.Where(r => !(r.Host == host && r.Project == project))],
            Adopted = [.. s.Adopted.Where(a => a != full)],
            Forgotten = [.. s.Forgotten.Where(f => !(f.Host == host && f.Project == project)), new Forgotten(host, project)],
        });
    }

    /// <summary>Undo a forget — the user deployed the stack again from Dray, which is asking for it back.</summary>
    public void Unforget(string host, string project)
        => Update(s => s.Forgotten.Any(f => f.Host == host && f.Project == project)
            ? s with { Forgotten = [.. s.Forgotten.Where(f => !(f.Host == host && f.Project == project))] }
            : s);

    static bool Same(StackRecord a, StackRecord b)
        => a.WorkingDirectory == b.WorkingDirectory && a.Files.SequenceEqual(b.Files) && a.OnHost == b.OnHost;

    void Update(Func<State, State> change)
    {
        bool changed;

        lock (_lock)
        {
            var current = Current();
            var next = change(current);
            changed = !ReferenceEquals(next, current);

            _state = next;

            if (changed)
            {
                Save(_path, next);
                _seen = File.GetLastWriteTimeUtc(_path);
            }
        }

        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// What is on disk, if another process has written it since this one last looked, and what is
    /// in memory otherwise.
    /// <para>
    /// The file is rewritten whole, so a change made to what was read at startup puts back every
    /// line another Dray wrote since — the dev host running beside the app, or a second window.
    /// That is how a stacks folder chosen in one of them used to vanish when the other merely
    /// noticed a stack come up. A file that cannot be read is not an empty one, so it keeps what
    /// is in memory rather than starting again from nothing.
    /// </para>
    /// </summary>
    State Current()
    {
        var stamp = File.GetLastWriteTimeUtc(_path);
        if (stamp == _seen) return _state;

        if (Read(_path) is not { } fresh) return _state;

        _seen = stamp;
        return fresh;
    }

    // ---------------------------------------------------------------- the file

    sealed record Forgotten(string Host, string Project);

    sealed record State
    {
        public int Version { get; init; } = 1;

        /// <summary>The one folder earlier versions kept. Read so it is not lost, never written.</summary>
        public string? StacksFolder { get; init; }

        public IReadOnlyList<string>? StacksFolders { get; init; }

        /// <summary>The folders, with an earlier version's single one as the first.</summary>
        [JsonIgnore]
        public IReadOnlyList<string> FolderList => StacksFolders ?? (StacksFolder is { } one ? [one] : []);

        public State WithFolders(IReadOnlyList<string> folders) => this with { StacksFolder = null, StacksFolders = folders };

        public IReadOnlyList<string> Adopted { get; init; } = [];
        public IReadOnlyList<StackRecord> Stacks { get; init; } = [];
        public IReadOnlyList<Forgotten> Forgotten { get; init; } = [];
    }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The file's contents, or null where there is no file or none Dray can read.</summary>
    static State? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A file Dray cannot read is a file Dray did not write correctly, or one someone edited
            // by hand. At startup that means starting empty: it loses a list of paths, which every
            // stack on screen will put back, where refusing to start would lose the app.
            return null;
        }
    }

    static void Save(string path, State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Json));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory for this session. The cost of failing to persist is a down stack
            // dropping off the list after a restart — not worth failing the operation that got here.
        }
    }
}
