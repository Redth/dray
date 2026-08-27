namespace Dray.Core.Model;

/// <summary>One volume, as the volumes list shows it.</summary>
public sealed record VolumeSummary
{
    public required string Name { get; init; }

    public required string Driver { get; init; }

    /// <summary>
    /// Where the engine keeps it. Only reachable from the host when the engine runs there — inside
    /// a VM (Docker Desktop, podman machine) this path exists in the VM, not on the user's disk,
    /// so it is shown as provenance rather than offered as something to open.
    /// </summary>
    public string? Mountpoint { get; init; }

    public DateTimeOffset? Created { get; init; }

    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Names of containers currently mounting this volume.
    /// <para>
    /// The question worth answering before deleting one. Empty means nothing holds it, which is
    /// what makes a volume prunable — and also what makes deleting it irreversible with no
    /// warning from the engine.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> UsedBy { get; init; } = [];

    /// <summary>
    /// On-disk size, when the engine reports one. Null is common: computing it means walking the
    /// volume, so engines only return it from <c>system df</c> and not from a plain list.
    /// </summary>
    public long? SizeBytes { get; init; }

    /// <summary>Created by a compose project rather than by hand.</summary>
    public string? Stack => Labels.GetValueOrDefault("com.docker.compose.project");

    public bool IsInUse => UsedBy.Count > 0;

    /// <summary>
    /// Anonymous volumes get a 64-hex-character name from the engine, which is not a name anyone
    /// chose and should not be displayed as though it were.
    /// </summary>
    public bool IsAnonymous =>
        Name.Length == 64 && Name.All(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'));

    public string DisplayName => IsAnonymous ? $"anonymous · {Name[..12]}" : Name;
}
