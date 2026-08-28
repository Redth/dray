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
/// Deliberately not a YAML parser. It reads two shapes of one key out of a file whose overall
/// structure it does not care about, and a line it cannot make sense of costs that edge rather than
/// the graph — a stack should still draw when someone has used an anchor or a merge key.
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
        => [.. Scan(yaml)
            .Where(s => s.Deps.Count > 0)
            .Select(s => new ServiceDependency(s.Name, [.. s.Deps.Distinct(StringComparer.Ordinal)]))];

    /// <summary>
    /// Every service the file declares, in the order it declares them.
    /// <para>
    /// The engine only knows about services that have containers, so a service that is scaled to
    /// zero or has never been started is missing from a stack read back from the host. The file
    /// still names it, and a graph that quietly dropped it would be drawing a different stack.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Services(string? yaml)
        => [.. Scan(yaml).Select(s => s.Name)];

    /// <summary>
    /// Walk the <c>services:</c> block once, returning each service and whatever it declares.
    /// </summary>
    static List<(string Name, List<string> Deps)> Scan(string? yaml)
    {
        var found = new List<(string Name, List<string> Deps)>();

        if (string.IsNullOrWhiteSpace(yaml)) return found;

        var servicesIndent = -1;
        var serviceIndent = -1;

        string? service = null;
        List<string> deps = [];

        var dependsIndent = -1;
        var collecting = false;

        void Close()
        {
            if (service is not null) found.Add((service, deps));

            service = null;
            deps = [];
            collecting = false;
        }

        foreach (var raw in yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#')) continue;

            var indent = line.Length - line.TrimStart().Length;
            var text = line.Trim();

            // Everything is relative to the `services:` block; a top-level `volumes:` has keys that
            // look exactly like service names.
            if (servicesIndent < 0)
            {
                if (text is "services:") servicesIndent = indent;
                continue;
            }

            // Left the services block entirely — another top-level key.
            if (indent <= servicesIndent && text.EndsWith(':'))
            {
                Close();
                serviceIndent = -1;
                servicesIndent = text is "services:" ? indent : -1;
                continue;
            }

            // Inside a depends_on block, gathering names until the indentation says otherwise.
            if (collecting)
            {
                if (indent > dependsIndent)
                {
                    // "- db" in the short form, "db:" in the long one. A "condition: …" line sits
                    // deeper still and is turned away by the name check.
                    var name = text.StartsWith("- ", StringComparison.Ordinal)
                        ? text[2..].Trim().TrimEnd(':')
                        : text.EndsWith(':') ? text[..^1].Trim() : null;

                    if (name is { Length: > 0 } && IsServiceName(name)) deps.Add(name);
                    continue;
                }

                collecting = false;
            }

            // A service name: one level in from `services:`.
            if ((serviceIndent < 0 || indent == serviceIndent) && text.EndsWith(':'))
            {
                Close();

                serviceIndent = indent;
                service = text[..^1].Trim();
                continue;
            }

            if (service is not null && text.StartsWith("depends_on:", StringComparison.Ordinal))
            {
                dependsIndent = indent;
                collecting = true;

                // The inline form, `depends_on: [db, cache]`.
                var inline = text["depends_on:".Length..].Trim();
                if (inline.StartsWith('[') && inline.EndsWith(']'))
                {
                    deps.AddRange(inline[1..^1]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(IsServiceName));

                    collecting = false;
                }
            }
        }

        Close();
        return found;
    }

    /// <summary>A plausible service name, so a stray `condition:` value never becomes a node.</summary>
    static bool IsServiceName(string name)
        => name.Length > 0
           && !name.Contains(' ', StringComparison.Ordinal)
           && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

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
