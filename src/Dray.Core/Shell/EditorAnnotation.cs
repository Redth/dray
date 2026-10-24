namespace Dray.Core.Shell;

/// <summary>
/// One inline annotation in a code editor: text drawn beside the source without being part of it.
/// <para>
/// A record so that two sets compare by value, which is what lets the component skip pushing an
/// unchanged set across the interop boundary on every keystroke.
/// </para>
/// </summary>
/// <param name="Line">1-based, as the editor's decoration API wants.</param>
/// <param name="Column">1-based, at the first character of the annotated span.</param>
/// <param name="Length">Characters of the span being annotated.</param>
/// <param name="Text">What to draw after it.</param>
/// <param name="Kind">
/// <c>ok</c>, <c>default</c>, <c>missing</c> or <c>required</c> — styled by the editor's own
/// stylesheet rather than carrying a colour, so the palette stays in the token system.
/// </param>
/// <param name="Hover">Longer explanation shown on hover, in Markdown. Optional.</param>
public sealed record EditorAnnotation(
    int Line,
    int Column,
    int Length,
    string Text,
    string Kind,
    string? Hover = null);

/// <summary>
/// Something wrong with the text at a place in it, drawn the way an editor draws a compile error:
/// an underline, a gutter mark, a hover, and an entry on the scrollbar.
/// <para>
/// A separate record from <see cref="EditorAnnotation"/> because it is a different thing — an
/// annotation explains what a span will become, a problem says the span is wrong — and Monaco keeps
/// them apart too, as decorations and as markers.
/// </para>
/// </summary>
/// <param name="Line">1-based.</param>
/// <param name="Column">1-based, at the first character.</param>
/// <param name="EndLine">1-based.</param>
/// <param name="EndColumn">1-based, one past the last character, as Monaco's ranges are.</param>
/// <param name="Severity"><c>error</c> or <c>warning</c>.</param>
/// <param name="Source">Who said so, shown after the message — e.g. <c>compose</c>.</param>
public sealed record EditorProblem(
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string Message,
    string Severity,
    string Source);

/// <summary>
/// A clickable label drawn above a line of the editor — a code lens: "Edit network", "Add volume…".
/// </summary>
/// <param name="Line">1-based; the lens sits above it.</param>
/// <param name="Id">Handed back to the owner when the lens is clicked.</param>
public sealed record EditorLens(int Line, string Title, string Id);
