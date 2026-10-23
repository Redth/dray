using System.Text.Json;

namespace Dray.Core.Engine;

/// <summary>
/// The names the user has given hosts, so the picker can say "Work laptop" where discovery found
/// <c>desktop-linux</c>.
/// <para>
/// A name is Dray's alone. It is not written to the Docker context, because the context's name is
/// what <c>docker --context</c> takes and what every other tool shows; renaming it would break the
/// user's scripts to tidy Dray's toolbar. Keyed by host id, which is stable across launches.
/// </para>
/// <para>
/// Not a credential and never contains one (docs/CREDENTIALS.md).
/// </para>
/// </summary>
public interface IHostNameStore
{
    /// <summary>Every name saved, by host id. Empty when there are none.</summary>
    IReadOnlyDictionary<string, string> Load();

    void Save(IReadOnlyDictionary<string, string> names);
}

/// <summary>
/// Keeps the names in a small JSON file in Dray's configuration folder, beside the stacks it
/// remembers: a name is something the user set and would expect to survive, unlike the last
/// selection, which is a cache.
/// </summary>
public sealed class FileHostNameStore(string? directory = null) : IHostNameStore
{
    readonly string _path = Path.Combine(directory ?? DrayPaths.ConfigDirectory, "host-names.json");

    public IReadOnlyDictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new Dictionary<string, string>();

            var names = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
            return names ?? new Dictionary<string, string>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable is the same as never saved: every host shows its own name.
            return new Dictionary<string, string>();
        }
    }

    public void Save(IReadOnlyDictionary<string, string> names)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(names, Indented));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A name that cannot be remembered still applies for this session.
        }
    }

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}

/// <summary>What a display name may be. One rule, for the dialog that edits it and the manager that keeps it.</summary>
public static class HostDisplayName
{
    /// <summary>
    /// Long enough for "Home server (NAS)", short enough to sit in the strip of toolbar above the
    /// macOS sidebar at its usual width. A longer name still works; it is shortened to fit there.
    /// </summary>
    public const int MaxLength = 24;

    /// <summary>
    /// The name to keep, or null to keep none: blank, or the host's own name, both mean "use the
    /// host's name". Storing a name equal to the real one would make a later rename of the context
    /// look like Dray had frozen the old one.
    /// </summary>
    public static string? Normalize(string? name, string hostName)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed == hostName) return null;
        return trimmed.Length > MaxLength ? trimmed[..MaxLength] : trimmed;
    }
}
