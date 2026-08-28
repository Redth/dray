namespace Dray.Core.Model;

/// <summary>One container's row: which networks it is on, and at what address.</summary>
/// <param name="Addresses">
/// One entry per network column, in the same order. Null where the container is not on that
/// network — which is the question the whole view exists to answer, so it is a real value rather
/// than an absent one.
/// </param>
public sealed record TopologyRow(string ContainerId, string Name, IReadOnlyList<string?> Addresses)
{
    public string ShortId => ContainerId.Length <= 12 ? ContainerId : ContainerId[..12];

    public int Count => Addresses.Count(a => a is not null);

    /// <summary>
    /// On more than one network. These are the rows worth looking at: a container that can reach
    /// two networks is how traffic crosses between them, and it is never visible from a list.
    /// </summary>
    public bool Bridges => Count > 1;
}

/// <summary>Which containers share which networks, as a grid.</summary>
public sealed record Topology(IReadOnlyList<NetworkSummary> Networks, IReadOnlyList<TopologyRow> Rows)
{
    public static readonly Topology Empty = new([], []);

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>How many containers reach more than one network.</summary>
    public int Bridges => Rows.Count(r => r.Bridges);
}

/// <summary>
/// The membership grid behind the topology view.
/// <para>
/// A grid rather than a node graph on purpose. The question is "which containers share which
/// network", and the answer is a set of memberships — a graph of it is mostly empty space, gets
/// unreadable past a dozen containers, and hides the one thing worth seeing, which is a row with
/// two marks in it. A grid puts every container on one line, shows the address it has on each
/// network rather than an anonymous dot, and stays legible at fifty rows.
/// </para>
/// </summary>
public static class NetworkTopology
{
    public static Topology Build(IReadOnlyList<NetworkSummary>? networks)
    {
        if (networks is not { Count: > 0 }) return Topology.Empty;

        // Busiest network first, so the column most rows have a mark in is the one nearest the
        // names. A network nobody is on is not a column at all — it would be an empty stripe
        // through the middle of the grid, and the list above already says it exists.
        var columns = networks
            .Where(n => n.Members.Count > 0)
            .OrderByDescending(n => n.Members.Count)
            .ThenBy(n => n.Name, StringComparer.Ordinal)
            .ToList();

        if (columns.Count == 0) return Topology.Empty;

        var rows = new Dictionary<string, string?[]>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var column = 0; column < columns.Count; column++)
        {
            foreach (var member in columns[column].Members)
            {
                if (!rows.TryGetValue(member.ContainerId, out var cells))
                {
                    rows[member.ContainerId] = cells = new string?[columns.Count];
                    names[member.ContainerId] = member.Name;
                }

                // An address the engine did not report still means "on this network", so the mark
                // is an empty string rather than null — the two are different answers.
                cells[column] = member.Address ?? "";
            }
        }

        var built = rows
            .Select(pair => new TopologyRow(pair.Key, names[pair.Key], pair.Value))
            .OrderByDescending(r => r.Count)
            .ThenBy(r => Signature(r), StringComparer.Ordinal)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        return new Topology(columns, built);
    }

    /// <summary>
    /// Which columns a row occupies, as a string that sorts. Rows with the same pattern end up
    /// adjacent, so a stack's containers group together without anything here knowing what a stack
    /// is.
    /// </summary>
    static string Signature(TopologyRow row)
        => string.Concat(row.Addresses.Select(a => a is null ? '0' : '1'));
}
