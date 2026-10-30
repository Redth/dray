namespace Dray.Core.Shell;

/// <summary>
/// One entry in a <see cref="ChromePicker"/>'s menu: a choice, or a command such as "Hosts and
/// setup…".
/// <para>
/// Checked rather than coloured: the selected engine is marked the way every platform marks the
/// chosen item in a menu, so the picker reads the same in greyscale (DESIGN.md 2.4).
/// </para>
/// </summary>
/// <param name="IsCommand">
/// Does something rather than being chosen — never checked, and drawn as a plain item where a head
/// draws the choices as a radio group.
/// </param>
public sealed record ChromePickerItem(
    string Id,
    string Label,
    bool IsChecked = false,
    bool IsEnabled = true,
    bool SeparatorBefore = false,
    bool IsCommand = false);

/// <summary>
/// A control in the leading corner of the window that says what everything else is looking at, and
/// drops a menu to change it — the engine picker today, the cluster picker beside it in Phase 6.5
/// (docs/KUBERNETES.md §1).
/// <para>
/// Not part of <see cref="PageChrome"/>, because no page declares it: it is the same on every page,
/// and a page that could omit it could hide which engine the user is looking at. It is a model in
/// the same sense, though — heads draw it and never build its menu themselves. Before this, each
/// head assembled the engine list on its own, three copies of one menu that had to stay identical.
/// </para>
/// </summary>
/// <param name="Title">What the control says: the selected engine's name, or what is happening to it.</param>
/// <param name="Tooltip">The title with what the engine is — the sentence the menu uses for it.</param>
public sealed record ChromePicker(
    string Id,
    string Title,
    string Tooltip,
    IconRef Icon,
    IReadOnlyList<ChromePickerItem> Items)
{
    public bool Equals(ChromePicker? other)
        => other is not null
           && Id == other.Id
           && Title == other.Title
           && Tooltip == other.Tooltip
           && Icon == other.Icon
           && Items.SequenceEqual(other.Items);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Title);
        hash.Add(Tooltip);
        hash.Add(Icon);
        foreach (var item in Items) hash.Add(item);
        return hash.ToHashCode();
    }

    /// <summary>
    /// The menu's shape. A native menu toolbar item can be retitled and re-checked in place, but an
    /// item added or removed means rebuilding it — and on macOS, rebuilding the toolbar. So a head
    /// compares this, and rebuilds only when a host appears or goes.
    /// </summary>
    public string Signature => string.Join('|', Items.Select(i => i.SeparatorBefore ? $"-{i.Id}" : i.Id));
}

/// <summary>
/// Where a head gets a picker and sends a choice. Implemented above the heads, so a head knows the
/// shape of the control and nothing about engines.
/// </summary>
public interface IChromePickerSource
{
    ChromePicker Current { get; }

    /// <summary>Raised only when <see cref="Current"/> actually differs — not for every container event.</summary>
    event Action<ChromePicker>? Changed;

    /// <summary>
    /// The user chose an item. Whatever it means — a switch, a confirmation, a page — is the
    /// source's to do. Returns why it could not be done, or null; how to say so is the head's,
    /// because a native head has a notification and the web head has the space under the control.
    /// </summary>
    Task<string?> ChooseAsync(string itemId);
}
