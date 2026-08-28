namespace Dray.Core.Shell;

/// <summary>What a dialog's button means to the platform drawing it.</summary>
public enum DialogButtonRole
{
    /// <summary>An ordinary choice.</summary>
    Normal,

    /// <summary>The one Return commits. At most one per dialog.</summary>
    Confirm,

    /// <summary>The one Escape chooses. At most one per dialog.</summary>
    Cancel,

    /// <summary>Marked the platform's way for something that cannot be undone.</summary>
    Destructive,
}

/// <summary>
/// One button in a dialog's button row.
/// <para>
/// The row is native on every head that has one, so a button is described rather than drawn:
/// the platform decides where it sits, which one Return commits, and how a destructive one looks.
/// Those answers differ between macOS, Windows and GTK, and getting them from the platform is the
/// entire point of the rule in docs/NATIVE-SHELL.md section 4.
/// </para>
/// </summary>
public sealed record DialogButton(string Id, string Label, DialogButtonRole Role = DialogButtonRole.Normal)
{
    public static DialogButton Cancel(string label = "Cancel") => new("cancel", label, DialogButtonRole.Cancel);

    public static DialogButton Confirm(string id, string label) => new(id, label, DialogButtonRole.Confirm);

    public static DialogButton Destructive(string id, string label) => new(id, label, DialogButtonRole.Destructive);

    /// <summary>The row for a dialog that only shows something. One button, because there is one thing to do.</summary>
    public static IReadOnlyList<DialogButton> Done(string label = "Done") =>
        [new("done", label, DialogButtonRole.Confirm)];
}

/// <summary>
/// Which end of the row the committing button sits at.
/// <para>
/// Platforms disagree, and each is right about itself: AppKit puts the default rightmost and adds
/// it first, while a browser and GTK put cancel first and commit last. The page never picks — its
/// head does — but the sorting itself is the same everywhere, so it lives here with tests rather
/// than being written out once per head.
/// </para>
/// </summary>
public enum DialogButtonOrder
{
    /// <summary>Commit, then the rest, then cancel. AppKit adds buttons right to left.</summary>
    CommitFirst,

    /// <summary>Cancel, then the rest, then commit. The web and GTK convention.</summary>
    CommitLast,
}

/// <summary>How much room the body needs. The head turns this into pixels its own way.</summary>
public enum DialogSize
{
    /// <summary>A question with a field or two.</summary>
    Small,

    /// <summary>A form.</summary>
    Medium,

    /// <summary>Something being read — a document, a log, a response.</summary>
    Large,
}

/// <summary>
/// A dialog: a native title row, a Blazor body, and a native button row.
/// <para>
/// The page says what it wants and the head decides what that is made of, exactly as
/// <see cref="PageChrome"/> does for the toolbar. Nothing here names a widget, because the answer
/// is an <c>NSAlert</c> sheet on macOS, a <c>ContentDialog</c> on Windows, an
/// <c>AdwMessageDialog</c> on GTK, and a <c>&lt;dialog&gt;</c> in a browser — and a page that knew
/// which would be a page that had to be changed to add the next head.
/// </para>
/// </summary>
/// <param name="Body">
/// A Blazor component type. The body is the one region that is web on every head: it is the part
/// that differs per dialog, and writing it four times is how the heads drift.
/// </param>
/// <param name="Parameters">Parameters for <paramref name="Body"/>, by name.</param>
/// <param name="Buttons">
/// The button row, or null for <see cref="DialogButton.Done"/> — which is the right default,
/// because a dialog with nothing to decide still needs a way out.
/// </param>
public sealed record DialogRequest(
    string Title,
    Type Body,
    IReadOnlyDictionary<string, object?>? Parameters = null,
    IReadOnlyList<DialogButton>? Buttons = null,
    string? Subtitle = null,
    DialogSize Size = DialogSize.Medium)
{
    public IReadOnlyList<DialogButton> ButtonList => Buttons is { Count: > 0 } buttons ? buttons : DialogButton.Done();

    public IReadOnlyDictionary<string, object?> ParameterMap =>
        Parameters ?? new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// The buttons in the order a head wants to lay them out.
    /// <para>
    /// A destructive button sorts with the committing one — it is the decision the dialog exists
    /// to take — but it is never <see cref="DefaultButton"/>, so it takes the position without
    /// taking the Return key.
    /// </para>
    /// </summary>
    public IReadOnlyList<DialogButton> Ordered(DialogButtonOrder order)
    {
        IReadOnlyList<DialogButton> commit =
            [.. ButtonList.Where(b => b.Role is DialogButtonRole.Confirm or DialogButtonRole.Destructive)];

        IReadOnlyList<DialogButton> rest = [.. ButtonList.Where(b => b.Role == DialogButtonRole.Normal)];
        IReadOnlyList<DialogButton> cancel = [.. ButtonList.Where(b => b.Role == DialogButtonRole.Cancel)];

        return order == DialogButtonOrder.CommitFirst
            ? [.. commit, .. rest, .. cancel]
            : [.. cancel, .. rest, .. commit];
    }

    /// <summary>The button Escape chooses, or null when the dialog has no way to be cancelled.</summary>
    public DialogButton? CancelButton => ButtonList.FirstOrDefault(b => b.Role == DialogButtonRole.Cancel);

    /// <summary>
    /// The button Return commits.
    /// <para>
    /// A destructive button is never the default. Return is pressed by people who have stopped
    /// reading, and the whole point of marking something destructive is that it should not happen
    /// by reflex.
    /// </para>
    /// </summary>
    public DialogButton? DefaultButton => ButtonList.FirstOrDefault(b => b.Role == DialogButtonRole.Confirm);
}
