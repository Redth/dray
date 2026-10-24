using System.Text.Json;
using Dray.Core.Engine;

namespace Dray.Core.Settings;

/// <summary>
/// What the user has chosen in Settings, as it is saved.
/// <para>
/// Preferences only. Not a credential and never one (docs/CREDENTIALS.md), and not the Docker CLI's
/// <c>config.json</c> — this is Dray's own file, in Dray's configuration folder.
/// </para>
/// </summary>
public sealed record DraySettingsData
{
    /// <summary>
    /// Kubernetes support at all. Off, Dray does not read the kubeconfig or connect to a cluster, and
    /// nothing about Kubernetes is in the window — for someone who has a kubeconfig and does not
    /// want a second thing watching it.
    /// </summary>
    public bool Kubernetes { get; init; } = true;

    /// <summary>Sidebar routes the user has turned off, one by one. A route in here is not in the sidebar or the palette.</summary>
    public IReadOnlyList<string> HiddenRoutes { get; init; } = [];
}

public interface ISettingsStore
{
    DraySettingsData Load();

    void Save(DraySettingsData settings);
}

/// <summary>Keeps the settings in <c>settings.json</c> in Dray's configuration folder.</summary>
public sealed class FileSettingsStore(string? directory = null) : ISettingsStore
{
    readonly string _path = Path.Combine(directory ?? DrayPaths.ConfigDirectory, "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DraySettingsData Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<DraySettingsData>(File.ReadAllText(_path), Json) ?? new()
                : new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable is the same as never saved: the defaults, which show everything.
            return new();
        }
    }

    public void Save(DraySettingsData settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A choice that cannot be remembered still applies for this session.
        }
    }
}

/// <summary>
/// The settings, live: read once, changed from the Settings page, saved as they change, and announced
/// so the sidebars, the palette and the cluster manager follow at once rather than on the next launch.
/// </summary>
public sealed class DraySettings
{
    readonly ISettingsStore? _store;

    public DraySettings(ISettingsStore? store = null)
    {
        _store = store;
        Current = store?.Load() ?? new();
    }

    public DraySettingsData Current { get; private set; }

    public event Action? Changed;

    public bool KubernetesEnabled => Current.Kubernetes;

    public IReadOnlyCollection<string> HiddenRoutes => Current.HiddenRoutes;

    public bool IsShown(string route) => !Current.HiddenRoutes.Contains(route, StringComparer.OrdinalIgnoreCase);

    public void SetKubernetes(bool enabled) => Update(Current with { Kubernetes = enabled });

    public void SetShown(string route, bool shown)
    {
        var hidden = Current.HiddenRoutes.Where(r => !string.Equals(r, route, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!shown) hidden.Add(route);
        Update(Current with { HiddenRoutes = hidden });
    }

    void Update(DraySettingsData next)
    {
        if (next.Kubernetes == Current.Kubernetes && next.HiddenRoutes.SequenceEqual(Current.HiddenRoutes, StringComparer.OrdinalIgnoreCase)) return;

        Current = next;
        _store?.Save(next);
        Changed?.Invoke();
    }
}
