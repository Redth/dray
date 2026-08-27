namespace Dray.Core.Model;

/// <summary>
/// An action offered for a set of containers, and how many of them it would actually touch.
/// </summary>
/// <param name="Applicable">
/// How many of the selected containers the action applies to. Shown, because "Stop 3" when five
/// are selected is the difference between a clear instruction and a nasty surprise.
/// </param>
public sealed record BulkAction(ContainerAction Action, int Applicable, int Selected)
{
    public bool IsAvailable => Applicable > 0;

    /// <summary>True when the action would skip some of the selection.</summary>
    public bool IsPartial => Applicable < Selected;

    /// <summary>
    /// The button's label. Carries the count whenever it differs from the selection, so the user
    /// never has to work out which of their five containers a Stop will reach.
    /// </summary>
    public string Label => IsPartial
        ? $"{ContainerActions.Label(Action)} {Applicable}"
        : ContainerActions.Label(Action);
}

/// <summary>
/// Working out what can be done to a set of containers at once.
/// <para>
/// The rule is that a bulk action applies to every container in the selection it <i>can</i> apply
/// to, and says how many that is. Requiring the whole selection to be in one state would mean
/// select-all then Stop does nothing the moment one container is already stopped — which is the
/// most common selection there is.
/// </para>
/// </summary>
public static class BulkActions
{
    /// <summary>
    /// The actions worth offering for a selection, in menu order.
    /// <para>
    /// An action that applies to nothing selected is left out entirely rather than disabled: a row
    /// of greyed buttons is noise, and the set changes every time the selection does.
    /// </para>
    /// </summary>
    public static IReadOnlyList<BulkAction> For(IReadOnlyCollection<ContainerSummary> selection)
    {
        if (selection.Count == 0) return [];

        return
        [
            .. ContainerActions.All
                .Select(action => new BulkAction(
                    action,
                    selection.Count(c => ContainerActions.AppliesTo(action, c.State)),
                    selection.Count))
                .Where(b => b.IsAvailable),
        ];
    }

    /// <summary>The containers an action would actually reach.</summary>
    public static IReadOnlyList<ContainerSummary> Targets(
        ContainerAction action, IEnumerable<ContainerSummary> selection)
        => [.. selection.Where(c => ContainerActions.AppliesTo(action, c.State))];

    /// <summary>
    /// The confirmation for a destructive bulk action.
    /// <para>
    /// Names the containers rather than the count where the list is short enough to read, because
    /// "Remove 3 containers?" is a number and "Remove dray-web, dray-api and dray-worker?" is a
    /// decision. Past four it becomes unreadable and the count is clearer.
    /// </para>
    /// </summary>
    public static (string Title, string Body) Confirmation(
        ContainerAction action, IReadOnlyList<ContainerSummary> targets)
    {
        if (targets.Count == 1) return ContainerActions.Confirmation(action, targets[0].Name);

        var subject = targets.Count <= 4
            ? Humanize.Names([.. targets.Select(t => t.Name)])
            : $"{targets.Count} containers";

        var body = action switch
        {
            ContainerAction.Remove =>
                "They will be deleted. Anything written inside them that is not on a volume is lost.",

            ContainerAction.Kill =>
                "Each is sent SIGKILL immediately, with no chance to shut down cleanly. A process mid-write can lose data.",

            _ => string.Empty,
        };

        return ($"{ContainerActions.Label(action)} {subject}?", body);
    }
}

/// <summary>
/// Validating a new container name before the engine sees it.
/// <para>
/// The engine's own rule, applied locally so the user is told while typing rather than after a
/// round trip that ends in a 500 and a message written for a daemon log.
/// </para>
/// </summary>
public static class ContainerName
{
    public const int MaxLength = 255;

    /// <summary>Null when the name is usable, or a sentence explaining what is wrong with it.</summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A container needs a name.";

        var trimmed = name.Trim();

        if (trimmed.Length > MaxLength) return $"Names are at most {MaxLength} characters.";

        // The engine requires the first character to be a letter or digit; the rest may also be
        // dot, dash or underscore.
        if (!char.IsAsciiLetterOrDigit(trimmed[0]))
            return "The first character has to be a letter or a number.";

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))
                return $"'{c}' is not allowed. Names use letters, numbers, dots, dashes and underscores.";
        }

        return null;
    }

    public static bool IsValid(string? name) => Validate(name) is null;
}
