namespace Dray.Core.Shell;

/// <summary>
/// How many controls of a row still fit, and therefore how many move into its overflow menu.
/// <para>
/// The rule everywhere in Dray is that the most important control is the last one to disappear, so
/// a row is collapsed from its end: the first action stays reachable at any width, and what goes
/// into the menu is whatever was least important to begin with.
/// </para>
/// <para>
/// The measurement comes from the browser — there is no other way to know how wide a button is —
/// but the decision does not, because a decision made in JavaScript is one this project cannot
/// test. Every control in a row is the same size, which is what makes this arithmetic rather than
/// layout: the rows this is used on are icon buttons.
/// </para>
/// </summary>
public static class OverflowFit
{
    /// <summary>
    /// How many controls to keep on the row.
    /// </summary>
    /// <param name="available">Width the row has to fill, in pixels.</param>
    /// <param name="item">Width of one control.</param>
    /// <param name="gap">Space between two controls.</param>
    /// <param name="total">How many controls the row would like to show.</param>
    /// <returns>
    /// How many fit, from the front. A result equal to <paramref name="total"/> means no menu is
    /// needed; anything less means the trigger for one is on the row too, and has been paid for.
    /// </returns>
    public static int Visible(double available, double item, double gap, int total)
    {
        if (total <= 0) return 0;

        // Nothing has been measured yet — the first render, before the observer has reported.
        // Showing everything is the right guess: it is what the row will settle on in the common
        // case, and a row that starts full and collapses reads better than one that starts empty.
        if (item <= 0 || available <= 0) return total;

        if (Width(total, item, gap) <= available) return total;

        // The menu's own trigger takes a slot, and it is the same size as everything else here.
        var usable = available - item - gap;
        if (usable <= 0) return 0;

        var fits = (int)Math.Floor((usable + gap) / (item + gap));

        // Never claim everything fits at this point: if it did, the check above would have said so.
        return Math.Clamp(fits, 0, total - 1);
    }

    static double Width(int count, double item, double gap)
        => count * item + Math.Max(0, count - 1) * gap;
}
