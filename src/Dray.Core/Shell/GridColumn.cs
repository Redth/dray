using Dray.Core.Model;

namespace Dray.Core.Shell;

/// <summary>
/// How a cell is drawn.
/// <para>
/// A closed set, not a template hook. The grid renders its own cells — that is what buys sorting,
/// column resizing and virtualization from one implementation instead of three — so every kind of
/// cell in Dray has to be nameable here. Which is a feature: it is the same argument as
/// <see cref="IconRef"/>, and it is why there is exactly one way to draw a container's state.
/// </para>
/// </summary>
public enum GridCell
{
    Text,

    /// <summary>Identifiers, paths, digests. Monospace, because they are compared character by character.</summary>
    Mono,

    /// <summary>Secondary text — a driver name, a count of nothing.</summary>
    Muted,

    /// <summary>A <see cref="GridState"/>: tint, glyph and word. DESIGN.md section 2.4.</summary>
    State,

    /// <summary>A <see cref="GridLink"/>: a name that goes somewhere, with an optional second line.</summary>
    Link,

    /// <summary>A <see cref="GridChip"/>: a short value that copies its long form when clicked.</summary>
    Chip,

    /// <summary>A byte count, humanized. Sorts by the number, not the text.</summary>
    Bytes,

    /// <summary>A percentage, one decimal place.</summary>
    Percent,

    /// <summary>A timestamp shown as an age. Sorts by the instant.</summary>
    Since,

    /// <summary>The row's ⋯. Never sortable, never hidden, always last.</summary>
    Actions,
}

/// <summary>One column of a grid.</summary>
/// <param name="Field">Key into the row's values.</param>
/// <param name="Priority">
/// What survives as the grid narrows: 1 is kept longest, and the higher the number the sooner the
/// column is dropped. This is the "hide info as the view shrinks" order, and it is a judgement
/// about the data rather than about pixels — a container's name matters more than its CPU at every
/// width, so the ordering is fixed here rather than measured.
/// </param>
/// <param name="Numeric">Right-aligned and tabular. Also how the grid knows to sort numerically.</param>
public sealed record GridColumn(
    string Field,
    string Title,
    GridCell Cell = GridCell.Text,
    int Priority = 3,
    bool Sortable = true,
    bool Numeric = false,
    int? MinWidth = null,
    string? Tooltip = null);

/// <summary>
/// A container's state for a grid cell, already resolved.
/// <para>
/// Resolved in C# by <c>ContainerStatusVocabulary</c> and handed over whole, so the grid's cell and
/// <c>StatePill</c> are two renderers of one vocabulary rather than two vocabularies. Both draw
/// tint, glyph and word; neither decides what they are.
/// </para>
/// </summary>
public sealed record GridState(string Tone, string Glyph, string Word, string? Detail)
{
    public static GridState From(ContainerStatus status) => new(
        status.Tone switch
        {
            StateTone.Ok => "ok",
            StateTone.Warn => "warn",
            StateTone.Danger => "danger",
            _ => "neutral",
        },
        status.Glyph,
        status.Word,
        status.Detail);

    /// <summary>What the cell sorts by: worst first, so a broken container rises to the top.</summary>
    public int Rank => Tone switch { "danger" => 0, "warn" => 1, "neutral" => 2, _ => 3 };
}

/// <summary>A name that goes somewhere, and optionally a quieter second line under it.</summary>
public sealed record GridLink(string Text, string Href, string? Sub = null);

/// <summary>
/// A short form of a long value, which puts the long form on the clipboard when clicked.
/// <para>
/// For digests and ids: the first characters are what people recognise, and the whole thing is
/// what they need to paste. Showing all 64 makes every other column narrower for no one's benefit.
/// </para>
/// </summary>
public sealed record GridChip(string Text, string Copy, string? Tooltip = null);
