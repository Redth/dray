using System.Text.Json;
using System.Text.Json.Serialization;
using Dray.Core.Model;

namespace Dray.Core.Engine;

/// <summary>One action Dray ran on a stack: what, when, how long, and how it ended.</summary>
/// <param name="Commands">Each step as the command line it was, for showing and for copying.</param>
public sealed record OperationRecord(
    string Host,
    string Stack,
    string Label,
    IReadOnlyList<string> Commands,
    DateTimeOffset Started,
    DateTimeOffset Finished,
    OperationState State,
    int? ExitCode)
{
    public TimeSpan Duration => Finished - Started;
}

/// <summary>
/// What Dray has done to each stack, and what the files it mounts looked like when it last deployed
/// one — kept in one small JSON file beside the registry.
/// <para>
/// Commands and outcomes only, never output. Compose's output is the live log's to show; written to
/// disk it would be the one place a value interpolated into an error message could be kept for
/// good, and the rule is that Dray keeps no secret. The mount hashes are SHA-256 digests of file
/// contents, which say whether a file changed and nothing about what is in it.
/// </para>
/// </summary>
public sealed class StackHistory
{
    /// <summary>Records kept per stack. A history is for "what did I just do", not an audit log.</summary>
    public const int Kept = 50;

    readonly string _path;
    readonly Lock _lock = new();
    State _state;

    public StackHistory(string path)
    {
        _path = path;
        _state = Load(path);
    }

    public event Action? Changed;

    /// <summary>The stack's history on a host, newest first.</summary>
    public IReadOnlyList<OperationRecord> For(string host, string stack)
    {
        lock (_lock) return [.. _state.Operations.Where(o => o.Host == host && o.Stack == stack).OrderByDescending(o => o.Started)];
    }

    public void Add(OperationRecord record)
    {
        lock (_lock)
        {
            var mine = _state.Operations.Where(o => o.Host == record.Host && o.Stack == record.Stack).OrderByDescending(o => o.Started).Skip(Kept - 1).ToHashSet();
            _state = _state with { Operations = [.. _state.Operations.Where(o => !mine.Contains(o)), record] };
            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>The mounted files' hashes at the last deploy Dray ran, or null when it has run none.</summary>
    public StackMountSnapshot? MountsAtDeploy(string host, string stack)
    {
        lock (_lock) return _state.Mounts.FirstOrDefault(m => m.Host == host && m.Stack == stack)?.Snapshot;
    }

    /// <summary>
    /// Record what the mounted files are now, after a deploy. With <paramref name="service"/>, only
    /// that service's entry changes — a recreate of one service is a deploy of that one alone.
    /// </summary>
    public void RecordMounts(string host, string stack, StackMountSnapshot snapshot, string? service = null)
    {
        lock (_lock)
        {
            var existing = _state.Mounts.FirstOrDefault(m => m.Host == host && m.Stack == stack);

            var merged = service is null || existing is null
                ? snapshot
                : new StackMountSnapshot(new Dictionary<string, IReadOnlyDictionary<string, string>>(existing.Snapshot.Services, StringComparer.Ordinal)
                {
                    [service] = snapshot.Services.GetValueOrDefault(service) ?? new Dictionary<string, string>(),
                });

            _state = _state with { Mounts = [.. _state.Mounts.Where(m => m != existing), new Mounted(host, stack, merged)] };
            Save();
        }

        Changed?.Invoke();
    }

    sealed record Mounted(string Host, string Stack, StackMountSnapshot Snapshot);

    sealed record State
    {
        public int Version { get; init; } = 1;
        public IReadOnlyList<OperationRecord> Operations { get; init; } = [];
        public IReadOnlyList<Mounted> Mounts { get; init; } = [];
    }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    static State Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json) ?? new State() : new State();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A history Dray cannot read is a history lost, not an app that will not start.
            return new State();
        }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_state, Json));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
