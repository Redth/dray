namespace Dray.Core.Engine;

/// <summary>
/// Which host the user last chose, so the app opens where they left it.
/// <para>
/// Without it every launch opened on whatever the Docker CLI's current context was. On Windows
/// that made choosing a WSL distribution in the picker a choice that lasted until the window
/// closed — and the picker is the switch between engines, so a switch that does not stay switched
/// is not one.
/// </para>
/// <para>
/// A host id is all this holds. It is not a credential and never contains one (docs/CREDENTIALS.md).
/// </para>
/// </summary>
public interface IHostSelectionStore
{
    /// <summary>The id last saved, or null when there is none.</summary>
    string? Load();

    void Save(string hostId);
}

/// <summary>Keeps the choice in a one-line file in the user's application data.</summary>
public sealed class FileHostSelectionStore(string? directory = null) : IHostSelectionStore
{
    readonly string _path = Path.Combine(
        directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dray"),
        "selected-host");

    public string? Load()
    {
        try
        {
            return File.Exists(_path) && File.ReadAllText(_path).Trim() is { Length: > 0 } id ? id : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable is the same as never saved: the app opens on the CLI's choice instead.
            return null;
        }
    }

    public void Save(string hostId)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, hostId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A choice that cannot be remembered still applies for this session.
        }
    }
}
