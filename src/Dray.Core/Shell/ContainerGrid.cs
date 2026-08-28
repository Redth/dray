using Dray.Core.Model;

namespace Dray.Core.Shell;

/// <summary>
/// What a container's row says, and in what order it gives way.
/// <para>
/// The columns are ordered by how much they identify the container rather than by how interesting
/// the number is: a name is what you came to find, a state is why you came, and CPU is something
/// you look at once you have found it. So the two on the left survive at any width and the usage
/// figures are the first to go — the opposite of what fits naturally, since the numbers are the
/// narrowest columns.
/// </para>
/// <para>
/// Here rather than in the page because these are decisions, not markup: which fact matters more
/// than which other fact is exactly the sort of thing that gets quietly reversed by whoever next
/// adds a column.
/// </para>
/// </summary>
public static class ContainerGrid
{
    public const string KeyField = "__key";

    /// <summary>
    /// The columns, left to right.
    /// </summary>
    /// <param name="showUsage">
    /// Whether the live figures are on. They cost a stats stream per container, so they are a
    /// choice rather than a default, and the columns are absent rather than empty when off.
    /// </param>
    /// <param name="stacks">
    /// Whether any container on screen belongs to a stack. A column of dashes tells nobody
    /// anything, and on a machine with no compose projects that is every row.
    /// </param>
    public static IReadOnlyList<GridColumn> Columns(bool showUsage, bool stacks) =>
    [
        // 1 — never dropped. The name, with the short id under it: the id is what a command needs
        // and what tells two containers of the same image apart.
        new("name", "Name", GridCell.Link, Priority: 1, MinWidth: 180),

        // 1 — the reason the page is open. Health is folded in rather than given a column of its
        // own: "Unhealthy" is a state, and a second column repeating it in other words is how a
        // row ends up saying "Running / unhealthy" and meaning one thing.
        new("state", "State", GridCell.State, Priority: 1, MinWidth: 110,
            Tooltip: "Health is part of the state — a container failing its healthcheck reads Unhealthy"),

        // 2 — what it is. The tag is under the repository, in the same cell, because neither is
        // read without the other.
        new("image", "Image", GridCell.Link, Priority: 2, MinWidth: 160),

        .. stacks ? new[] { new GridColumn("stack", "Stack", GridCell.Muted, Priority: 3) } : [],

        // 4 — the digest that actually ran. Short on screen, whole on the clipboard: this is the
        // answer to "is this the build I think it is", and that answer is pasted, not read.
        new("digest", "Image ID", GridCell.Chip, Priority: 4),

        new("ip", "IP", GridCell.Mono, Priority: 4, MinWidth: 110),

        new("ports", "Ports", GridCell.Mono, Priority: 5),

        new("uptime", "Uptime", GridCell.Since, Priority: 3, Numeric: true),

        .. showUsage
            ?
            [
                new GridColumn("cpu", "CPU", GridCell.Percent, Priority: 6, Numeric: true),
                new GridColumn("memory", "Memory", GridCell.Bytes, Priority: 6, Numeric: true),
                new GridColumn("net", "Net I/O", GridCell.Bytes, Priority: 7, Numeric: true),
            ]
            : Array.Empty<GridColumn>(),

        // A width of its own: under a fixed layout a column with none takes an equal share of what
        // is left, and the row's controls ended up overlapping the column before them.
        new("actions", "", GridCell.Actions, Priority: 0, Sortable: false, MinWidth: 104),
    ];

    /// <summary>One row's values, keyed by column field.</summary>
    public static IReadOnlyDictionary<string, object?> Row(ContainerSummary c, DateTimeOffset? now = null) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [KeyField] = c.Id,

            ["name"] = new GridLink(c.Name, $"/containers/{c.ShortId}", c.HasDistinctId ? c.ShortId : null),

            ["state"] = GridState.From(c.Status),

            ["image"] = new GridLink(
                Humanize.ImageName(c.Image),
                $"/images/{Uri.EscapeDataString(c.Image)}",
                Humanize.ImageTag(c.Image)),

            ["stack"] = c.Stack ?? "—",

            // Null rather than a dash: the engine not reporting a digest and a container not having
            // one are the same to the user, and an empty cell says so without pretending to a value.
            ["digest"] = c.ImageId is { Length: > 0 } id
                ? new GridChip(Short(id), id, "Click to copy the full image ID")
                : null,

            ["ip"] = c.IpAddress ?? "—",

            ["ports"] = c.Ports.Count == 0 ? "—" : string.Join("  ", c.Ports.Select(p => p.Display)),

            // Humanized for reading, numeric for sorting — see GridValue. Sorting by uptime is
            // the reason anyone sorts this page, and "18h" against "2mo" as text is nonsense.
            ["uptime"] = GridValue.When(c.Since, Humanize.Since(c.Since, now)),

            ["cpu"] = GridValue.Number(c.CpuPercent, Humanize.Percent(c.CpuPercent)),
            ["memory"] = GridValue.Bytes(c.MemoryBytes, c.MemoryBytes is { } bytes ? Humanize.Bytes(bytes) : "—"),
            ["net"] = GridValue.Bytes(c.NetworkBytes, c.NetworkBytes is { } net ? Humanize.Bytes(net) : "—"),
        };

    /// <summary>
    /// A digest as people quote it: the algorithm kept, the hash cut to twelve — the same
    /// shortening the CLI does, so the two can be compared by eye.
    /// </summary>
    static string Short(string digest)
    {
        var colon = digest.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0) return digest.Length <= 12 ? digest : digest[..12];

        var hash = digest[(colon + 1)..];
        return hash.Length <= 12 ? hash : hash[..12];
    }
}
