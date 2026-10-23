namespace Dray.Core.Model;

/// <summary>One service and what it waits for.</summary>
/// <param name="DependsOn">
/// Service names this one declares a dependency on. Only direct edges — the transitive closure is
/// derivable and storing it would make the graph lie about what the file says.
/// </param>
public sealed record ServiceDependency(string Service, IReadOnlyList<string> DependsOn);

/// <summary>
/// The <c>depends_on</c> edges in a compose file, and the order they imply.
/// <para>
/// Read from the YAML rather than from the engine, because the engine does not keep it: compose
/// uses <c>depends_on</c> to decide start order and then forgets it. So this is the one thing about
/// a stack that can only come from the file.
/// </para>
/// <para>
/// The file is read by <see cref="ComposeDocument"/>, a real YAML parser, so a service built from
/// an anchor or a merge key has its edges like any other. Text that does not parse draws no graph
/// rather than a wrong one.
/// </para>
/// </summary>
public static class ComposeGraph
{
    /// <summary>
    /// Extract the dependency edges. Services that declare nothing are left out; use
    /// <see cref="Services"/> for the full list.
    /// <para>
    /// Both forms compose accepts are handled: the short list, and the long map whose keys are
    /// service names and whose values are conditions.
    /// </para>
    /// <code>
    /// depends_on:           depends_on:
    ///   - db                  db:
    ///   - cache                 condition: service_healthy
    /// </code>
    /// </summary>
    public static IReadOnlyList<ServiceDependency> Parse(string? yaml)
        => [.. ComposeDocument.Parse(yaml).Services
            .Where(s => s.DependsOn.Count > 0)
            .Select(s => new ServiceDependency(s.Name, s.DependsOn))];

    /// <summary>
    /// Every service the file declares, in the order it declares them.
    /// <para>
    /// The engine only knows about services that have containers, so a service that is scaled to
    /// zero or has never been started is missing from a stack read back from the host. The file
    /// still names it, and a graph that quietly dropped it would be drawing a different stack.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Services(string? yaml)
        => [.. ComposeDocument.Parse(yaml).Services.Select(s => s.Name)];

    /// <summary>
    /// The services in start order, shallowest first, with each level being everything that can
    /// start at once.
    /// <para>
    /// A layered sort rather than a plain topological one, because the useful thing to show is not
    /// a line — it is which services wait for which, and what starts together.
    /// </para>
    /// <para>
    /// A cycle cannot start at all, and compose rejects one. Rather than loop forever or drop the
    /// services involved, whatever is left when no progress can be made is returned as a final
    /// level: the graph then shows every service, and the ones in the cycle are visibly not
    /// resolvable.
    /// </para>
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Levels(
        IEnumerable<string> services, IReadOnlyList<ServiceDependency> edges)
    {
        var all = services.ToList();

        var waitsFor = edges.ToDictionary(
            e => e.Service,
            e => e.DependsOn.Where(all.Contains).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var levels = new List<IReadOnlyList<string>>();
        var placed = new HashSet<string>(StringComparer.Ordinal);

        while (placed.Count < all.Count)
        {
            var ready = all
                .Where(s => !placed.Contains(s))
                .Where(s => !waitsFor.TryGetValue(s, out var deps) || deps.All(placed.Contains))
                .ToList();

            if (ready.Count == 0)
            {
                // No progress: everything left is in or behind a cycle.
                levels.Add([.. all.Where(s => !placed.Contains(s))]);
                break;
            }

            levels.Add(ready);
            foreach (var s in ready) placed.Add(s);
        }

        return levels;
    }

    /// <summary>
    /// Reorder the services inside each level so that fewer edges cross.
    /// <para>
    /// Levels decide how far right a service sits; this decides how far down. The order it starts
    /// from — the file's — is not wrong, it is just arbitrary with respect to the edges, and a graph
    /// whose lines cross for no reason is read as saying something it does not say.
    /// </para>
    /// <para>
    /// The barycentre heuristic: repeatedly put each service next to the average position of its
    /// neighbours, sweeping forwards then backwards. It is not optimal — crossing minimisation is
    /// NP-hard — and it does not need to be. A few sweeps take a stack from tangled to legible, and
    /// the ordering is stable, so a service with no neighbours to be pulled towards keeps the place
    /// the file gave it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Arrange(
        IReadOnlyList<IReadOnlyList<string>> levels, IReadOnlyList<ServiceDependency> edges)
    {
        if (levels.Count < 2) return levels;

        var order = levels.Select(l => l.ToList()).ToList();

        var waitsFor = edges.ToDictionary(
            e => e.Service, e => e.DependsOn, StringComparer.Ordinal);

        var feeds = edges
            .SelectMany(e => e.DependsOn.Select(d => (Dependency: d, e.Service)))
            .GroupBy(p => p.Dependency, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Service).ToList(), StringComparer.Ordinal);

        var row = new Dictionary<string, int>(StringComparer.Ordinal);

        void Index()
        {
            row.Clear();
            foreach (var level in order)
                for (var i = 0; i < level.Count; i++) row[level[i]] = i;
        }

        double Barycentre(string service, int at, IReadOnlyDictionary<string, List<string>> neighbours)
        {
            if (!neighbours.TryGetValue(service, out var list)) return at;

            var rows = list.Where(row.ContainsKey).Select(n => (double)row[n]).ToList();
            return rows.Count == 0 ? at : rows.Average();
        }

        var backwards = waitsFor.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.Ordinal);

        // Four sweeps: enough for the shapes a compose file produces, and bounded so a pathological
        // graph cannot spin here.
        for (var pass = 0; pass < 4; pass++)
        {
            Index();
            for (var i = 1; i < order.Count; i++)
                order[i] = [.. order[i].Select((s, at) => (s, key: Barycentre(s, at, backwards)))
                    .OrderBy(p => p.key).Select(p => p.s)];

            Index();
            for (var i = order.Count - 2; i >= 0; i--)
                order[i] = [.. order[i].Select((s, at) => (s, key: Barycentre(s, at, feeds)))
                    .OrderBy(p => p.key).Select(p => p.s)];
        }

        return [.. order];
    }
}
