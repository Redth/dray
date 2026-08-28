namespace Dray.Core.Shell;

/// <summary>
/// One control on a row that can collapse into a menu.
/// <para>
/// Deliberately not <see cref="ChromeAction"/>: that one describes an item in the window's own
/// toolbar, which on macOS becomes a real <c>NSToolbarItem</c> and carries the weight vocabulary
/// that goes with it. This is a control inside the page's content, where the only thing that ranks
/// one above another is the order they are given in.
/// </para>
/// </summary>
/// <param name="Id">Returned to the caller when the action is chosen.</param>
/// <param name="Label">
/// The accessible name, and the text shown once the action is in the menu. It has to identify the
/// action on its own — a page with a row per stack has four buttons reading "Up" on it.
/// </param>
/// <param name="Tooltip">What the action does, for the pointer. The label says which; this says what.</param>
public sealed record RowAction(
    string Id,
    string Label,
    IconRef Icon,
    string? Tooltip = null,
    bool Danger = false,
    bool Disabled = false);
