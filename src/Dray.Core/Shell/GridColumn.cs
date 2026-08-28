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

    /// <summary>A <see cref="GridEntry"/>: an icon and a name. What a file listing's first column is.</summary>
    Entry,

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
/// A cell that displays one way and sorts another.
/// <para>
/// Everything humanized needs this. "702 B", "746 B" and "89 B" sort in that order as text, which
/// is the wrong order and looks like a broken sort rather than a formatting decision — and the same
/// goes for "18h" against "2mo", and "1.4%" against "12.0%". The number is kept beside the words so
/// the column can be read by a person and ordered by a machine.
/// </para>
/// </summary>
public sealed record GridValue(IComparable? Sort, string Display)
{
    public static GridValue Bytes(long? value, string display) => new(value, display);

    public static GridValue When(DateTimeOffset? value, string display) => new(value, display);

    public static GridValue Number(double? value, string display) => new(value, display);
}

/// <summary>
/// An icon and a name, on one line.
/// <para>
/// A file listing's first column, where the icon is doing real work — it is the fastest way to tell
/// a directory from a file, faster than reading either the name or the mode. <paramref name="Note"/>
/// is for what a name alone does not say: a symlink's target.
/// </para>
/// </summary>
public sealed record GridEntry(string Text, IconRef Icon, string? Note = null);

/// <summary>
/// A short form of a long value, which puts the long form on the clipboard when clicked.
/// <para>
/// For digests and ids: the first characters are what people recognise, and the whole thing is
/// what they need to paste. Showing all 64 makes every other column narrower for no one's benefit.
/// </para>
/// </summary>
public sealed record GridChip(string Text, string Copy, string? Tooltip = null);

/// <summary>
/// How a grid sorts a column whose value is not a plain string.
/// <para>
/// Every rich cell carries an object, so each has to say what "less than" means. Sorting a column
/// of state pills by their rendered text is the kind of thing that looks like it works until
/// someone sorts by state and finds Dead between Created and Exited.
/// </para>
/// </summary>
public static class GridSort
{
    /// <summary>What a cell's value compares as.</summary>
    public static IComparable? Key(object? value) => value switch
    {
        null => null,

        // Worst first, so sorting by state brings the broken ones up — which is the only reason
        // anyone sorts by it.
        GridState state => state.Rank,

        // The sort value the cell was given, which is the whole point of GridValue. Null sorts
        // last, and "not measured" is exactly the case that should not lead the column.
        GridValue shown => shown.Sort,

        GridLink link => link.Text,
        GridEntry entry => entry.Text,
        GridChip chip => chip.Copy,
        IComparable comparable => comparable,
        _ => value.ToString(),
    };

    /// <summary>
    /// Compare two cells of one column. Empty sorts last in both directions: a row with no value
    /// has nothing to say about the question being asked, and burying it is more useful than
    /// alternating it between the top and the bottom.
    /// </summary>
    public static int Compare(object? a, object? b)
    {
        var (x, y) = (Key(a), Key(b));

        if (x is null) return y is null ? 0 : 1;
        if (y is null) return -1;

        return x.GetType() == y.GetType() ? x.CompareTo(y) : string.CompareOrdinal(x.ToString(), y.ToString());
    }
}
