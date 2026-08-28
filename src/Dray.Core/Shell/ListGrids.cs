using Dray.Core.Model;

namespace Dray.Core.Shell;

/// <summary>
/// The columns for the app's other lists.
/// <para>
/// Same reasoning as <see cref="ContainerGrid"/>: which columns exist, what each says and the order
/// they give way in are judgements about the data, so they live here where they can be tested,
/// rather than in markup where the next person to add a column quietly reverses them.
/// </para>
/// </summary>
public static class ImageGrid
{
    public static IReadOnlyList<GridColumn> Columns() =>
    [
        // What you came to find. The tag goes under the repository in the same cell: neither is
        // read without the other, and two columns for one name is how the row runs out of width.
        new("name", "Image", GridCell.Link, Priority: 1, MinWidth: 200),

        // Size is why anyone opens this page — it is the reclaim question — so it survives with the
        // name rather than sorting as an afterthought.
        new("size", "Size", GridCell.Bytes, Priority: 1, Numeric: true, MinWidth: 90),

        // The fact that decides whether removing it is safe.
        new("used", "Used by", GridCell.Text, Priority: 2, MinWidth: 120),

        new("id", "ID", GridCell.Chip, Priority: 4),
        new("created", "Created", GridCell.Since, Priority: 4, Numeric: true),

        new("actions", "", GridCell.Actions, Priority: 0, Sortable: false, MinWidth: 104),
    ];

    public static IReadOnlyDictionary<string, object?> Row(ImageSummary image, DateTimeOffset? now = null) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = new GridLink(
                image.RepositoryKey,
                $"/images/{Uri.EscapeDataString(image.Id)}",
                image.Tags.Count switch
                {
                    0 => "untagged",
                    1 => image.Tags[0].Tag,

                    // Several tags on one image is normal — :latest and a version usually point at
                    // the same bytes — and the count is more use than the first one alone.
                    _ => $"{image.Tags[0].Tag}  +{image.Tags.Count - 1}",
                }),

            // An engine that does not measure size gets an em dash, not "0 B": a measured zero and
            // an unknown are different answers.
            ["size"] = image.SizeReported ? Humanize.Bytes(image.SizeBytes) : "—",

            ["used"] = image.IsInUse switch
            {
                true => $"{image.ContainerCount} container{(image.ContainerCount == 1 ? "" : "s")}",
                false => "nothing",

                // The engine did not count. "nothing" would be a claim it never made, and the one
                // that makes Remove look safe.
                null => "—",
            },

            ["id"] = new GridChip(image.ShortId, image.Id, "Click to copy the full image ID"),
            ["created"] = image.Created is { } created ? Humanize.Since(created, now) : "—",
        };
}

/// <summary>Volumes: what they are, whether anything is using them, and how much they hold.</summary>
public static class VolumeGrid
{
    public static IReadOnlyList<GridColumn> Columns(bool stacks) =>
    [
        new("name", "Name", GridCell.Link, Priority: 1, MinWidth: 200),

        // Before deleting one, this is the only question.
        new("used", "Used by", GridCell.Text, Priority: 1, MinWidth: 140),

        new("size", "Size", GridCell.Bytes, Priority: 2, Numeric: true, MinWidth: 90),

        .. stacks ? new[] { new GridColumn("stack", "Stack", GridCell.Muted, Priority: 3) } : [],

        new("driver", "Driver", GridCell.Muted, Priority: 5),
        new("created", "Created", GridCell.Since, Priority: 4, Numeric: true),

        new("actions", "", GridCell.Actions, Priority: 0, Sortable: false, MinWidth: 72),
    ];

    public static IReadOnlyDictionary<string, object?> Row(VolumeSummary volume, DateTimeOffset? now = null) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = new GridLink(
                volume.DisplayName,
                $"/volumes/{Uri.EscapeDataString(volume.Name)}",

                // Only when the display name is not the real one. For a named volume they are
                // identical, and printing both puts the same word in one cell twice.
                volume.IsAnonymous ? volume.Name : null),

            ["used"] = volume.UsedBy.Count == 0 ? "nothing" : string.Join(", ", volume.UsedBy),

            // Null means the engine did not measure it, which is not the same as empty and must not
            // read as zero.
            ["size"] = volume.SizeBytes is { } size ? Humanize.Bytes(size) : "—",

            ["stack"] = volume.Stack ?? "—",
            ["driver"] = volume.Driver,
            ["created"] = volume.Created is { } created ? Humanize.Since(created, now) : "—",
        };
}

/// <summary>
/// The containers attached to one network.
/// <para>
/// There is no table of networks to define columns for — that page is cards, because what a
/// network is takes more than a row. This is the table inside one of those cards.
/// </para>
/// </summary>
public static class NetworkMemberGrid
{
    public static IReadOnlyList<GridColumn> Columns() =>
    [
        new("name", "Container", GridCell.Link, Priority: 1, MinWidth: 180),
        new("address", "Address", GridCell.Mono, Priority: 1, MinWidth: 130),
        new("mac", "MAC", GridCell.Mono, Priority: 5, MinWidth: 150),

        new("actions", "", GridCell.Actions, Priority: 0, Sortable: false, MinWidth: 56),
    ];

    public static IReadOnlyDictionary<string, object?> Row(NetworkMember member) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = new GridLink(member.Name, $"/containers/{member.ShortId}"),
            ["address"] = member.Address ?? "—",
            ["mac"] = member.MacAddress ?? "—",
        };
}

/// <summary>
/// The services inside one stack.
/// <para>
/// Narrower than the others on purpose: this table lives inside a stack's card, where the stack's
/// own name and state are already on screen, so the row only has to say which service it is and
/// what that service is doing.
/// </para>
/// </summary>
public static class StackServiceGrid
{
    public static IReadOnlyList<GridColumn> Columns() =>
    [
        new("name", "Service", GridCell.Text, Priority: 1, MinWidth: 140),
        new("state", "State", GridCell.State, Priority: 1, MinWidth: 110),
        new("containers", "Containers", GridCell.Link, Priority: 2, MinWidth: 160),
        new("image", "Image", GridCell.Muted, Priority: 3, MinWidth: 160),

        new("actions", "", GridCell.Actions, Priority: 0, Sortable: false, MinWidth: 72),
    ];

    public static IReadOnlyDictionary<string, object?> Row(StackService service) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = service.Name,
            ["state"] = GridState.From(service.Status),

            // One replica links straight to it; several is a count, because there is no single
            // container for the link to mean.
            ["containers"] = service.Replicas.Count == 1
                ? new GridLink(service.Replicas[0].Name, $"/containers/{service.Replicas[0].ShortId}")
                : new GridLink($"{service.RunningCount} of {service.Replicas.Count} running", "/containers"),

            ["image"] = service.Replicas.FirstOrDefault() is { } replica
                ? $"{Humanize.ImageName(replica.Image)}:{Humanize.ImageTag(replica.Image)}"
                : "—",
        };
}
