namespace Dray.Core.Model;

/// <summary>Something a user can do to a container.</summary>
public enum ContainerAction
{
    Start,
    Stop,
    Restart,
    Pause,
    Unpause,

    /// <summary>SIGKILL. Skips the graceful stop, so it is destructive in the way that matters.</summary>
    Kill,

    /// <summary>Delete the container. Anything written inside it that is not on a volume is lost.</summary>
    Remove,
}

/// <summary>
/// Which actions apply to a container in a given state, and how each one should be presented.
/// <para>
/// Centralised rather than decided per view, because an action offered in the wrong state is
/// either a no-op the user does not understand or an error the engine has to reject. Showing Start
/// on a running container is not a small mistake — it teaches the user the UI is guessing.
/// </para>
/// </summary>
public static class ContainerActions
{
    /// <summary>Every action, in the order a menu should present them.</summary>
    public static readonly IReadOnlyList<ContainerAction> All =
    [
        ContainerAction.Start,
        ContainerAction.Stop,
        ContainerAction.Restart,
        ContainerAction.Pause,
        ContainerAction.Unpause,
        ContainerAction.Kill,
        ContainerAction.Remove,
    ];

    /// <summary>Whether this action is meaningful for a container in this state.</summary>
    public static bool AppliesTo(ContainerAction action, DockerState state) => action switch
    {
        ContainerAction.Start => state is DockerState.Exited or DockerState.Created or DockerState.Dead,
        ContainerAction.Stop => state is DockerState.Running or DockerState.Restarting,
        ContainerAction.Restart => state is DockerState.Running or DockerState.Paused or DockerState.Exited or DockerState.Restarting,
        ContainerAction.Pause => state is DockerState.Running,
        ContainerAction.Unpause => state is DockerState.Paused,

        // Kill is a stop that skips the graceful shutdown, so it only applies where a stop would —
        // and to a paused container, which cannot be stopped gracefully at all.
        ContainerAction.Kill => state is DockerState.Running or DockerState.Restarting or DockerState.Paused,

        // Removing a running container requires force, which is a different question. Dray offers
        // Remove only once the container has stopped, and the user reaches Stop first.
        ContainerAction.Remove => state is DockerState.Exited or DockerState.Created or DockerState.Dead,

        _ => false,
    };

    /// <summary>The actions to offer for a container, already filtered by its state.</summary>
    public static IEnumerable<ContainerAction> For(DockerState state)
        => All.Where(a => AppliesTo(a, state));

    /// <summary>
    /// Whether the action destroys something the user cannot get back, and so needs confirming.
    /// <para>
    /// Kill counts: it skips the graceful shutdown, so a database mid-write can lose data. Stop
    /// does not — it is the ordinary way to stop a container and confirming it would train the
    /// user to click through confirmations.
    /// </para>
    /// </summary>
    public static bool IsDestructive(ContainerAction action)
        => action is ContainerAction.Remove or ContainerAction.Kill;

    /// <summary>The verb on the button. Sentence case, and it says exactly what happens.</summary>
    public static string Label(ContainerAction action) => action switch
    {
        ContainerAction.Start => "Start",
        ContainerAction.Stop => "Stop",
        ContainerAction.Restart => "Restart",
        ContainerAction.Pause => "Pause",
        ContainerAction.Unpause => "Resume",
        ContainerAction.Kill => "Force stop",
        ContainerAction.Remove => "Remove",
        _ => action.ToString(),
    };

    /// <summary>What the row shows while the action is in flight — present continuous, not a verb.</summary>
    public static string PendingLabel(ContainerAction action) => action switch
    {
        ContainerAction.Start => "Starting",
        ContainerAction.Stop => "Stopping",
        ContainerAction.Restart => "Restarting",
        ContainerAction.Pause => "Pausing",
        ContainerAction.Unpause => "Resuming",
        ContainerAction.Kill => "Force stopping",
        ContainerAction.Remove => "Removing",
        _ => "Working",
    };

    /// <summary>
    /// The confirmation for a destructive action. Names what will be lost rather than asking
    /// whether the user is sure.
    /// </summary>
    public static (string Title, string Body) Confirmation(ContainerAction action, string containerName) => action switch
    {
        ContainerAction.Remove => (
            $"Remove {containerName}?",
            "The container will be deleted. Anything written inside it that is not on a volume is lost."),

        ContainerAction.Kill => (
            $"Force stop {containerName}?",
            "The container is sent SIGKILL immediately, with no chance to shut down cleanly. A process mid-write can lose data."),

        _ => ($"{Label(action)} {containerName}?", string.Empty),
    };
}
