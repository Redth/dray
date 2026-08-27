namespace Dray.Ui.Components;

/// <summary>
/// Three variants and no more (DESIGN.md section 6). Primary appears at most once per view.
/// <para>
/// Destructive is not a fourth variant — it is the <c>Danger</c> tone applied to one of these, so
/// the weight of a destructive control is chosen independently of the fact that it destroys
/// something. A destructive action in chrome is <c>Ghost + Danger</c>: discoverable without
/// shouting, and never a second filled red sitting beside the brand-filled primary. The filled
/// treatment (<c>Primary + Danger</c>) is reserved for the committing button in a confirmation,
/// where it is the only action that matters.
/// </para>
/// </summary>
public enum ButtonVariant
{
    Primary,
    Secondary,
    Ghost,
}
