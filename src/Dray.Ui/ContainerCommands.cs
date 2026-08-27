using Dray.Core.Engine;
using Dray.Core.Model;
using Dray.Core.Shell;

namespace Dray.Ui;

/// <summary>
/// Running a container action from anywhere in the UI.
/// <para>
/// The same commands appear in three places — the row's hover actions, the detail page's header,
/// and the native toolbar — and they must agree on which actions apply, what they are called, what
/// they look like, and which of them stop to confirm. Three copies of that would eventually be
/// three different answers, and the one that drifts is the confirmation.
/// </para>
/// </summary>
public static class ContainerCommands
{
    /// <summary>
    /// Namespaces the ids these produce, so a page can tell a container command from its own
    /// chrome actions without a lookup table.
    /// </summary>
    public const string IdPrefix = "container:";

    public static IconRef IconFor(ContainerAction action) => action switch
    {
        ContainerAction.Start => IconRef.Play,
        ContainerAction.Stop => IconRef.Stop,
        ContainerAction.Restart => IconRef.Restart,
        ContainerAction.Pause => IconRef.Pause,
        ContainerAction.Unpause => IconRef.Play,
        ContainerAction.Kill => IconRef.Warning,
        ContainerAction.Remove => IconRef.Trash,
        _ => IconRef.More,
    };

    /// <summary>
    /// The container's applicable actions, as chrome a host can project onto a native toolbar.
    /// </summary>
    /// <param name="canPause">
    /// Whether the engine can pause. A native toolbar button that always fails is worse than a
    /// missing one, and the toolbar is the most permanent surface in the app.
    /// </param>
    public static IReadOnlyList<ChromeAction> ChromeActionsFor(ContainerSummary container, bool canPause = true) =>
    [
        .. ContainerActions.For(container.State, canPause).Select(action => new ChromeAction(
            IdPrefix + action,
            ContainerActions.Label(action),
            IconFor(action),

            // Destructive stays destructive in chrome: it is what makes the host render it
            // quietly and what routes it through a confirmation.
            ContainerActions.IsDestructive(action)
                ? ChromeActionKind.Destructive
                : ChromeActionKind.Secondary)),
    ];

    /// <summary>The action an id refers to, or null when the id is not one of these.</summary>
    public static ContainerAction? Parse(string id)
        => id.StartsWith(IdPrefix, StringComparison.Ordinal)
           && Enum.TryParse<ContainerAction>(id[IdPrefix.Length..], out var action)
            ? action
            : null;

    /// <summary>
    /// Confirm if the action destroys something, then ask the engine to do it.
    /// </summary>
    /// <returns>
    /// A sentence when the engine refused, null when it accepted or the user cancelled. Accepted
    /// is not the same as done — what actually happened arrives on the event stream.
    /// </returns>
    public static async Task<string?> RunAsync(
        EngineManager engine,
        IShellBridge shell,
        ContainerSummary container,
        ContainerAction action,
        CancellationToken ct = default)
    {
        if (ContainerActions.IsDestructive(action))
        {
            var (title, body) = ContainerActions.Confirmation(action, container.Name);

            var answer = await shell.ConfirmDestructiveAsync(
                new DestructiveConfirm(title, body, ContainerActions.Label(action)), ct);

            if (answer != ConfirmResult.Confirm) return null;
        }

        return await engine.PerformAsync(container.Id, action, ct);
    }
}
