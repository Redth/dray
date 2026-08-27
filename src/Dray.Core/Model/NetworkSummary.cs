namespace Dray.Core.Model;

/// <summary>One container attached to a network.</summary>
public sealed record NetworkMember(string ContainerId, string Name, string? IPv4Address, string? MacAddress)
{
    public string ShortId => ContainerId.Length <= 12 ? ContainerId : ContainerId[..12];

    /// <summary>The address without its CIDR suffix, which is the subnet's fact rather than this container's.</summary>
    public string? Address => IPv4Address?.Split('/')[0];
}

/// <summary>One network on the host.</summary>
public sealed record NetworkSummary
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Driver { get; init; }

    public string? Scope { get; init; }

    public DateTimeOffset? Created { get; init; }

    /// <summary>Subnets the network hands addresses out of, as CIDR.</summary>
    public IReadOnlyList<string> Subnets { get; init; } = [];

    public IReadOnlyList<NetworkMember> Members { get; init; } = [];

    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>No route out. Containers here can reach each other and nothing else.</summary>
    public bool IsInternal { get; init; }

    /// <summary>Compose project, when a stack created this network.</summary>
    public string? Stack => Labels.GetValueOrDefault("com.docker.compose.project");

    public bool IsInUse => Members.Count > 0;

    public string ShortId => Id.Length <= 12 ? Id : Id[..12];

    /// <summary>
    /// The engine's own networks, which exist whether or not anyone wants them and cannot be
    /// removed. Offering Delete on one is offering a button that always fails.
    /// </summary>
    public bool IsPredefined => Name is "bridge" or "host" or "none" or "podman";
}

/// <summary>
/// What a prune would remove, worked out before anything is deleted.
/// <para>
/// PRODUCT.md: destructive operations are typed, not clicked. A preview that says "about 20 GB" is
/// not a preview — the point is to know exactly what goes and exactly what comes back, so this
/// carries the items themselves rather than a total.
/// </para>
/// </summary>
/// <param name="Kind">What is being pruned, for the sentence describing it.</param>
/// <param name="Items">What would be removed, named as the user knows them.</param>
/// <param name="ReclaimedBytes">
/// What deleting them actually frees — unique layers, not the sum of the sizes shown in the list.
/// </param>
public sealed record PrunePreview(
    PruneKind Kind,
    IReadOnlyList<string> Items,
    long ReclaimedBytes)
{
    public static PrunePreview Empty(PruneKind kind) => new(kind, [], 0);

    public bool IsEmpty => Items.Count == 0;

    /// <summary>The phrase the user has to type to confirm. Deliberately specific to what is going.</summary>
    public string ConfirmationPhrase => Kind switch
    {
        PruneKind.Images => "prune images",
        PruneKind.Containers => "prune containers",
        PruneKind.Volumes => "prune volumes",
        PruneKind.Networks => "prune networks",
        _ => "prune",
    };

    public string Noun => Kind switch
    {
        PruneKind.Images => Items.Count == 1 ? "image" : "images",
        PruneKind.Containers => Items.Count == 1 ? "container" : "containers",
        PruneKind.Volumes => Items.Count == 1 ? "volume" : "volumes",
        PruneKind.Networks => Items.Count == 1 ? "network" : "networks",
        _ => "items",
    };
}

public enum PruneKind
{
    Images,
    Containers,
    Volumes,
    Networks,
}

/// <summary>What a prune actually did.</summary>
public sealed record PruneResult(PruneKind Kind, int Removed, long ReclaimedBytes);
