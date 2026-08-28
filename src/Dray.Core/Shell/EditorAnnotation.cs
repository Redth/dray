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
