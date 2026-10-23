namespace Dray.Core.Model;

/// <summary>The compose labels the engine puts on every container a stack creates.</summary>
public static class ComposeLabels
{
    public const string Project = "com.docker.compose.project";
    public const string Service = "com.docker.compose.service";
    public const string ConfigFiles = "com.docker.compose.project.config_files";
    public const string WorkingDirectory = "com.docker.compose.project.working_dir";
    public const string ContainerNumber = "com.docker.compose.container-number";

    /// <summary>
    /// Compose's hash of the service's configuration when the container was created. Measured
    /// equal to what <c>compose config --hash</c> prints for an unedited file, which is what makes
    /// "edited, not deployed" a comparison rather than a guess.
    /// </summary>
    public const string ConfigHash = "com.docker.compose.config-hash";
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
    int Replica = 1,
    string? ConfigHash = null)
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
            int.TryParse(labels.GetValueOrDefault(ComposeLabels.ContainerNumber), out var n) ? n : 1,
            labels.GetValueOrDefault(ComposeLabels.ConfigHash));
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

/// <summary>Where a stack's files are, and so what Dray may do with them.</summary>
public enum StackProvenance
{
    /// <summary>
    /// Seen only through its containers' labels; its files are not on this machine. Driven per
    /// container, because there is no compose file here to drive it with.
    /// </summary>
    Observed,

    /// <summary>
    /// Files on this machine, outside the stacks folder. Edited where they are and never deleted by
    /// Dray — they belong to whatever put them there.
    /// </summary>
    Adopted,

    /// <summary>A folder in the stacks folder the user chose for Dray. The one kind Dray may delete.</summary>
    Managed,

    /// <summary>
    /// Files on the engine's own host, reached over SSH, where compose runs too. Driven like an
    /// adopted stack, and like one never deleted: they are that machine's files.
    /// </summary>
    Remote,
}

/// <summary>The state of a stack as a whole. DESIGN.md section 2.4 covers containers; this is its stack-level row.</summary>
public enum StackState
{
    /// <summary>Every container that should be running is.</summary>
    Running,

    /// <summary>Some are running and some are not.</summary>
    Partial,

    /// <summary>Every live container is restarting — a crash loop, which Start would not fix.</summary>
    Restarting,

    /// <summary>Every live container is paused.</summary>
    Paused,

    /// <summary>Containers exist and none is running.</summary>
    Stopped,

    /// <summary>Deployed before, and has no containers now — brought down.</summary>
    Down,

    /// <summary>Files exist and have never been deployed to this host.</summary>
    Draft,
}

/// <summary>
/// One compose project: what its containers say, what its files say, and where those files are.
/// <para>
/// Running stacks are discovered from labels, so one brought up from a terminal appears without
/// being registered. Stacks with no containers come from the registry and the stacks folder,
/// which is what keeps a stack that is down on the list — the thing Docker Desktop gets wrong.
/// </para>
/// </summary>
public sealed record StackSummary
{
    public required string Name { get; init; }

    public IReadOnlyList<StackService> Services { get; init; } = [];

    /// <summary>
    /// The compose files, in the order compose is given them. From the labels for a running stack —
    /// absolute paths on the machine that ran <c>compose up</c>, which is not necessarily this one —
    /// and from the folder for a stack that is not.
    /// </summary>
    public IReadOnlyList<string> ConfigFiles { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public StackProvenance Provenance { get; init; } = StackProvenance.Observed;

    /// <summary>
    /// What the files declare about each service, where the files are readable here: whether it
    /// builds, and which profiles it is in. Empty for a stack known only by its labels.
    /// </summary>
    public IReadOnlyDictionary<string, ComposeServiceEntry> Declared { get; init; } = new Dictionary<string, ComposeServiceEntry>();

    /// <summary>Every profile any service is in, in the order the files first mention them.</summary>
    public IReadOnlyList<string> Profiles => [.. Declared.Values.SelectMany(d => d.Profiles).Distinct(StringComparer.Ordinal)];

    /// <summary>Whether any service has a <c>build:</c> section.</summary>
    public bool Builds => Declared.Values.Any(d => d.Builds);

    /// <summary>
    /// Whether this host has ever had containers for the stack. Tells Down from Draft: both have no
    /// containers, and only one of them has ever run.
    /// </summary>
    public bool WasDeployed { get; init; } = true;

    /// <summary>True when the stack has no containers on this host.</summary>
    public bool IsDown => ContainerCount == 0;

    public int ContainerCount => Services.Sum(s => s.Replicas.Count);

    public int RunningCount => Services.Sum(s => s.RunningCount);

    IEnumerable<ContainerSummary> Containers => Services.SelectMany(s => s.Replicas);

    public StackState State => StackStatusVocabulary.StateOf([.. Containers], WasDeployed);

    /// <summary>Health, kept apart from <see cref="State"/>: running and unhealthy are two facts.</summary>
    public DockerHealth Health => StackStatusVocabulary.HealthOf(Containers);

    /// <summary>The pill for the stack as a whole.</summary>
    public ContainerStatus Status => StackStatusVocabulary.Resolve([.. Containers], WasDeployed);
}

/// <summary>
/// Turning a stack's containers into one state.
/// <para>
/// Two rules the web UIs this borrows from get wrong, and their issue trackers say so loudest. A
/// container that exited 0 finished — a migration, a one-shot init — and counting it against the
/// stack turns every stack with a setup job red. And health is not state: a running stack with an
/// unhealthy service is still running, and saying "unhealthy" instead of "running" loses one of
/// the two facts.
/// </para>
/// </summary>
public static class StackStatusVocabulary
{
    /// <summary>A container that ran to completion. Only a reported zero counts; unknown is not clean.</summary>
    static bool Completed(ContainerSummary c) => c.State == DockerState.Exited && c.ExitCode == 0;

    public static StackState StateOf(IReadOnlyList<ContainerSummary> containers, bool wasDeployed)
    {
        if (containers.Count == 0) return wasDeployed ? StackState.Down : StackState.Draft;

        var live = containers.Where(c => !Completed(c)).ToList();

        if (live.Count == 0) return StackState.Stopped;
        if (live.All(c => c.State == DockerState.Running)) return StackState.Running;
        if (live.All(c => c.State == DockerState.Restarting)) return StackState.Restarting;
        if (live.All(c => c.State == DockerState.Paused)) return StackState.Paused;
        if (live.Any(c => c.State == DockerState.Running)) return StackState.Partial;

        return StackState.Stopped;
    }

    /// <summary>The worst health among running containers that have a healthcheck; None when none do.</summary>
    public static DockerHealth HealthOf(IEnumerable<ContainerSummary> containers)
    {
        var reported = containers
            .Where(c => c.State == DockerState.Running && c.Health != DockerHealth.None)
            .Select(c => c.Health)
            .ToList();

        if (reported.Count == 0) return DockerHealth.None;
        if (reported.Contains(DockerHealth.Unhealthy)) return DockerHealth.Unhealthy;
        if (reported.Contains(DockerHealth.Starting)) return DockerHealth.Starting;

        return DockerHealth.Healthy;
    }

    public static ContainerStatus Resolve(IReadOnlyList<ContainerSummary> containers, bool wasDeployed)
    {
        var state = StateOf(containers, wasDeployed);
        var live = containers.Where(c => !Completed(c)).ToList();
        var failed = live.Count(Failed);

        return state switch
        {
            StackState.Running => new(StateTone.Ok, "●", "Running"),
            StackState.Partial => new(
                StateTone.Warn, "◑", "Partly running",
                $"{live.Count(c => c.State == DockerState.Running)} of {live.Count} running"),
            StackState.Restarting => new(StateTone.Warn, "↻", "Restarting"),
            StackState.Paused => new(StateTone.Warn, "‖", "Paused"),

            // Stopped is ordinary; stopped because something exited with an error is the thing to
            // look at. Same glyph and word, a detail and a tone — as an exited container has.
            StackState.Stopped when failed > 0 => new(
                StateTone.Danger, "■", "Stopped", failed == 1 ? "1 exited with an error" : $"{failed} exited with an error"),
            StackState.Stopped => new(StateTone.Neutral, "■", "Stopped"),

            StackState.Down => new(StateTone.Neutral, "○", "Down"),
            _ => new(StateTone.Neutral, "◇", "Draft", "never deployed here"),
        };
    }

    static bool Failed(ContainerSummary c) => c.State == DockerState.Dead || (c.State == DockerState.Exited && c.ExitCode is not (null or 0));
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
