namespace Dray.Core.Model;

/// <summary>The compose labels the engine puts on every container a stack creates.</summary>
public static class ComposeLabels
{
    public const string Project = "com.docker.compose.project";
    public const string Service = "com.docker.compose.service";
    public const string ConfigFiles = "com.docker.compose.project.config_files";
    public const string WorkingDirectory = "com.docker.compose.project.working_dir";
    public const string ContainerNumber = "com.docker.compose.container-number";
}

/// <summary>
/// What compose recorded about a container, read from its labels.
/// </summary>
/// <param name="ConfigFiles">
/// The compose files the stack was created from. Absolute paths on the machine that ran
/// <c>compose up</c>, which is not necessarily this one — a stack on a remote host names files
/// that do not exist here.
/// </param>
/// <param name="Replica">
/// Compose's container number within the service. Used for ordering, so <c>web-2</c> sits after
/// <c>web-1</c> rather than after <c>web-10</c>.
/// </param>
public sealed record ComposeMembership(
    string Project,
    string? Service = null,
    IReadOnlyList<string>? ConfigFiles = null,
    string? WorkingDirectory = null,
    int Replica = 1)
{
    public IReadOnlyList<string> Files => ConfigFiles ?? [];

    /// <summary>Read the membership out of a container's labels, or null when compose made none of it.</summary>
    public static ComposeMembership? From(IReadOnlyDictionary<string, string>? labels)
    {
        if (labels is null) return null;

        var project = labels.GetValueOrDefault(ComposeLabels.Project);
        if (string.IsNullOrEmpty(project)) return null;

        return new ComposeMembership(
            project,
            labels.GetValueOrDefault(ComposeLabels.Service),

            // Compose writes several paths separated by commas when there is an override file.
            [
                .. (labels.GetValueOrDefault(ComposeLabels.ConfigFiles) ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ],

            labels.GetValueOrDefault(ComposeLabels.WorkingDirectory),
            int.TryParse(labels.GetValueOrDefault(ComposeLabels.ContainerNumber), out var n) ? n : 1);
    }
}

/// <summary>One service in a stack, and every container running it.</summary>
/// <param name="Replicas">
/// The containers for this service. More than one means it has been scaled; compose numbers them,
/// and they are ordered by that number rather than by name so <c>web-2</c> sits after <c>web-1</c>
/// and not after <c>web-10</c>.
/// </param>
public sealed record StackService(string Name, IReadOnlyList<ContainerSummary> Replicas)
{
    public int RunningCount => Replicas.Count(r => r.State == DockerState.Running);

    public bool IsScaled => Replicas.Count > 1;

    /// <summary>
    /// The state to show for the service as a whole.
    /// <para>
    /// The worst of its replicas, because a service with three containers where one has crashed is
    /// not healthy — and averaging or taking the first would hide exactly the container worth
    /// looking at.
    /// </para>
    /// </summary>
    public ContainerStatus Status
    {
        get
        {
            if (Replicas.Count == 0) return new ContainerStatus(StateTone.Neutral, "○", "No containers");

            // Ordered worst-first so the first match is the most serious.
            return Replicas
                .Select(r => r.Status)
                .OrderBy(s => s.Tone switch
                {
                    StateTone.Danger => 0,
                    StateTone.Warn => 1,
                    StateTone.Neutral => 2,
                    _ => 3,
                })
                .First();
        }
    }
}

/// <summary>
/// One compose project, assembled from the labels its containers carry.
/// <para>
/// Discovered rather than registered, so a stack brought up from a terminal appears in Dray without
/// anyone telling it to — which is the behaviour the roadmap calls for and the thing Docker Desktop
/// gets wrong.
/// </para>
/// </summary>
public sealed record StackSummary
{
    public required string Name { get; init; }

    public IReadOnlyList<StackService> Services { get; init; } = [];

    /// <summary>
    /// The compose files the stack was created from, as the engine recorded them. Absolute paths on
    /// the machine that ran <c>compose up</c>, which is not necessarily this one.
    /// </summary>
    public IReadOnlyList<string> ConfigFiles { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// True when this stack was found by reading a compose file rather than by finding containers.
    /// <para>
    /// A stack that has been brought down leaves no containers and so no labels, and would vanish
    /// from a list built only from the engine. Keeping it visible is the point.
    /// </para>
    /// </summary>
    public bool IsDown => Services.Sum(s => s.Replicas.Count) == 0;

    public int ContainerCount => Services.Sum(s => s.Replicas.Count);

    public int RunningCount => Services.Sum(s => s.RunningCount);

    /// <summary>The state to show for the stack: the worst of its services.</summary>
    public ContainerStatus Status
    {
        get
        {
            if (IsDown) return new ContainerStatus(StateTone.Neutral, "○", "Down");

            return Services
                .Select(s => s.Status)
                .OrderBy(s => s.Tone switch
                {
                    StateTone.Danger => 0,
                    StateTone.Warn => 1,
                    StateTone.Neutral => 2,
                    _ => 3,
                })
                .First();
        }
    }
}

/// <summary>Assembling stacks out of a flat list of containers.</summary>
public static class StackDiscovery
{
    /// <summary>
    /// Group containers into the compose projects they belong to.
    /// <para>
    /// Containers with no compose labels are not in a stack and are simply absent — Dray does not
    /// invent a project for them.
    /// </para>
    /// </summary>
    public static IReadOnlyList<StackSummary> From(IEnumerable<ContainerSummary> containers)
    {
        var byProject = new Dictionary<string, List<ContainerSummary>>(StringComparer.Ordinal);

        foreach (var container in containers)
        {
            if (container.Compose is not { } compose) continue;

            if (!byProject.TryGetValue(compose.Project, out var list)) byProject[compose.Project] = list = [];
            list.Add(container);
        }

        return
        [
            .. byProject
                .Select(entry => Build(entry.Key, entry.Value))
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    static StackSummary Build(string project, List<ContainerSummary> members)
    {
        var services = members
            .GroupBy(m => m.Compose?.Service ?? FallbackServiceName(project, m))
            .Select(g => new StackService(
                g.Key,
                [
                    .. g.OrderBy(m => m.Compose?.Replica ?? 1)
                        .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
                ]))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Any member carries the same project-level labels; the first is as good as any.
        var compose = members[0].Compose;

        return new StackSummary
        {
            Name = project,
            Services = services,
            ConfigFiles = compose?.Files ?? [],
            WorkingDirectory = compose?.WorkingDirectory,
        };
    }

    /// <summary>
    /// A name for a container that carries the project label but not the service one.
    /// <para>
    /// Rare but real: something created by hand with only the project label, or by a tool that
    /// writes a partial set. Compose names containers <c>project-service-1</c>, so the middle is
    /// the best guess available — and a guess named after the container beats a blank column.
    /// </para>
    /// </summary>
    internal static string FallbackServiceName(string project, ContainerSummary container)
    {
        var name = container.Name;

        var prefix = project + "-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return name;

        var rest = name[prefix.Length..];
        var dash = rest.LastIndexOf('-');

        // Strip a trailing replica number if there is one.
        return dash > 0 && rest[(dash + 1)..].All(char.IsAsciiDigit) ? rest[..dash] : rest;
    }
}
