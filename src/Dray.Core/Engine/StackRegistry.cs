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

    public StackRegistry(string path)
    {
        _path = path;
        _state = Load(path);
    }

    /// <summary>Raised after anything changes, from whichever thread changed it.</summary>
    public event Action? Changed;

    /// <summary>Where new stacks go, and the folder scanned for existing ones. Null until the user has said.</summary>
    public string? StacksFolder
    {
        get { lock (_lock) return _state.StacksFolder; }
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

    public void SetStacksFolder(string path) => Update(s => s with { StacksFolder = Path.GetFullPath(path) });

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
            var next = change(_state);
            changed = !ReferenceEquals(next, _state);

            if (changed)
            {
                _state = next;
                Save(_path, next);
            }
        }

        if (changed) Changed?.Invoke();
    }

    // ---------------------------------------------------------------- the file

    sealed record Forgotten(string Host, string Project);

    sealed record State
    {
        public int Version { get; init; } = 1;
        public string? StacksFolder { get; init; }
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

    static State Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json) ?? new State() : new State();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A file Dray cannot read is a file Dray did not write correctly, or one someone edited
            // by hand. Starting empty loses a list of paths, which every stack on screen will put
            // back; refusing to start would lose the app.
            return new State();
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
