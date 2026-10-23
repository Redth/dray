namespace Dray.Core.Model;

/// <summary>Something Dray can do to a stack, or to one service in it.</summary>
public enum StackVerb
{
    /// <summary><c>up -d</c> with the deploy options — create what is missing, apply what changed.</summary>
    Deploy,

    /// <summary>Start what exists; bring up what does not.</summary>
    Start,

    Stop,
    Restart,

    /// <summary>Pull newer images and do nothing else.</summary>
    Pull,

    /// <summary>Pull, then bring the stack up again if — and only if — it was running.</summary>
    Update,

    Build,

    /// <summary>One service, rebuilt from its configuration without touching its dependencies.</summary>
    Recreate,

    /// <summary>Stop and remove the containers and networks. Named volumes are kept.</summary>
    Down,

    /// <summary><see cref="Down"/>, and the stack's named volumes with it. The one that loses data.</summary>
    DownWithVolumes,
}

/// <summary>
/// How a deploy runs. The three presets are the three things people mean by "redeploy".
/// </summary>
/// <param name="Pull">Pull images first, so a moved tag is picked up (<c>--pull always</c>).</param>
/// <param name="Build">Rebuild services that have a <c>build:</c> section (<c>--build</c>).</param>
/// <param name="ForceRecreate">Recreate every container even when nothing changed (<c>--force-recreate</c>).</param>
/// <param name="RemoveOrphans">Remove containers for services no longer in the file (<c>--remove-orphans</c>).</param>
public sealed record DeployOptions(bool Pull = false, bool Build = false, bool ForceRecreate = false, bool RemoveOrphans = true)
{
    /// <summary>Apply what changed and leave the rest running. What <c>up</c> does by default.</summary>
    public static readonly DeployOptions OnlyChanged = new();

    /// <summary>Every container recreated — for a config change compose cannot see, like an edited mounted file.</summary>
    public static readonly DeployOptions RecreateAll = new(ForceRecreate: true);

    /// <summary>New images, then whatever they changed — the update most people mean.</summary>
    public static readonly DeployOptions PullAndApply = new(Pull: true);
}

/// <summary>
/// The compose arguments for every stack action, as data.
/// <para>
/// One place, so the command shown before a deploy is the command that runs, and so each flag has a
/// test rather than a comment. An action can be several compose invocations — Update is a pull and
/// then an up — and each step is run, and shown, as itself.
/// </para>
/// </summary>
public static class StackCommands
{
    /// <summary>The compose invocations, in order, for <paramref name="verb"/>.</summary>
    /// <param name="service">One service, or null for the whole stack.</param>
    public static IReadOnlyList<IReadOnlyList<string>> For(
        StackVerb verb, StackSummary stack, DeployOptions? deploy = null, string? service = null)
    {
        string[] target = service is null ? [] : [service];
        var replicas = service is null
            ? stack.ContainerCount
            : stack.Services.FirstOrDefault(s => s.Name == service)?.Replicas.Count ?? 0;

        return verb switch
        {
            StackVerb.Deploy => [Up(deploy ?? DeployOptions.OnlyChanged, service)],

            // `start` keeps the containers that exist — the same ids, the same networks — where `up`
            // might recreate them. With nothing to start, `up` is the only thing that does anything.
            StackVerb.Start => replicas > 0
                ? [["start", .. target]]
                : [service is null ? ["up", "-d", "--remove-orphans"] : ["up", "-d", "--no-deps", service]],

            StackVerb.Stop => [["stop", .. target]],
            StackVerb.Restart => [["restart", .. target]],
            StackVerb.Pull => [["pull", .. target]],

            // A stopped stack stays stopped: updating its images is not a request to start it.
            StackVerb.Update => IsUp(stack, service)
                ? [["pull", .. target], service is null ? ["up", "-d", "--remove-orphans"] : ["up", "-d", "--no-deps", service]]
                : [["pull", .. target]],

            StackVerb.Build => [["build", .. target]],

            // --no-deps: recreating the web server must not restart the database it depends on.
            StackVerb.Recreate => [["up", "-d", "--no-deps", "--force-recreate", .. target]],

            StackVerb.Down => [["down", "--remove-orphans"]],
            StackVerb.DownWithVolumes => [["down", "--remove-orphans", "--volumes"]],

            _ => throw new ArgumentOutOfRangeException(nameof(verb)),
        };
    }

    /// <summary>
    /// Whether the verb needs the stack's compose files, rather than only its project name.
    /// <para>
    /// Measured against compose 5.4.0: with nothing but <c>-p</c>, from an unrelated directory,
    /// <c>start</c>, <c>stop</c>, <c>restart</c> and <c>down</c> all work — compose finds the
    /// containers by their labels. <c>pull</c> refuses ("no configuration file provided"), and so
    /// does anything that creates. A stack whose files are on another machine gets the first four.
    /// </para>
    /// </summary>
    public static bool NeedsFiles(StackVerb verb, StackSummary stack, string? service = null)
        => verb switch
        {
            StackVerb.Stop or StackVerb.Restart or StackVerb.Down or StackVerb.DownWithVolumes => false,
            StackVerb.Start => (service is null
                ? stack.ContainerCount
                : stack.Services.FirstOrDefault(s => s.Name == service)?.Replicas.Count ?? 0) == 0,
            _ => true,
        };

    static IReadOnlyList<string> Up(DeployOptions options, string? service)
    {
        List<string> args = ["up", "-d"];

        if (options.RemoveOrphans && service is null) args.Add("--remove-orphans");
        if (options.Pull) args.AddRange(["--pull", "always"]);
        if (options.Build) args.Add("--build");
        if (options.ForceRecreate) args.Add("--force-recreate");

        if (service is not null)
        {
            args.Add("--no-deps");
            args.Add(service);
        }

        return args;
    }

    static bool IsUp(StackSummary stack, string? service)
        => service is null
            ? stack.State is StackState.Running or StackState.Partial or StackState.Restarting
            : stack.Services.FirstOrDefault(s => s.Name == service)?.RunningCount > 0;
}
