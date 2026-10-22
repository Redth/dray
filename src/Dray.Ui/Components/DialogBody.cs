using Dray.Core.Shell;
using Microsoft.AspNetCore.Components;

namespace Dray.Ui.Components;

/// <summary>
/// The body of a form dialog: owns its fields and its work, and drives the native button row it
/// does not draw.
/// <para>
/// Opened with <see cref="IShellBridge.ShowDialogAsync"/> and a <see cref="DialogController"/>. The
/// head draws the title and buttons — an <c>NSAlert</c>'s, a <c>ContentDialog</c>'s, or a
/// <c>&lt;dialog&gt;</c>'s on the web — and this tells it what they say and whether they can be
/// pressed, and decides what a press does. docs/NATIVE-SHELL.md section 4.
/// </para>
/// </summary>
public abstract class DialogBody : ComponentBase
{
    /// <summary>Cascaded by the host. Null only when rendered outside a dialog — the gallery.</summary>
    [CascadingParameter] public DialogController? Dialog { get; set; }

    public override Task SetParametersAsync(ParameterView parameters)
    {
        parameters.SetParameterProperties(this);

        // Here rather than in OnInitialized, which every body overrides for its own setup and
        // would have to remember to chain.
        if (Dialog is { Pressed: null } dialog) dialog.Pressed = PressedAsync;

        return base.SetParametersAsync(ParameterView.Empty);
    }

    /// <summary>
    /// Declare every button's current state. Called after each render, so a body states what its
    /// buttons are rather than remembering to update them; the controller ignores a repeat.
    /// </summary>
    protected abstract void DeclareButtons();

    /// <summary>
    /// A button was pressed, on the renderer's thread. Return true to close the dialog with it.
    /// <para>
    /// Return promptly. Work that takes time — a pull, a build — is started here and left running;
    /// the head is holding its button event open until this returns.
    /// </para>
    /// </summary>
    protected abstract Task<bool> OnButtonAsync(string buttonId);

    protected override void OnAfterRender(bool firstRender) => DeclareButtons();

    /// <summary>Set one button's state.</summary>
    protected void Button(string id, string label, bool enabled = true, bool busy = false)
        => Dialog?.Set(id, new DialogButtonState(label, enabled, busy));

    /// <summary>Close the dialog from the body — after a create succeeds, say.</summary>
    protected void Close(string buttonId, object? value = null) => Dialog?.Close(buttonId, value);

    /// <summary>
    /// The head's press, moved onto the renderer. A native head reports it from its own UI thread,
    /// and the body's state may only be touched from Blazor's.
    /// </summary>
    Task<bool> PressedAsync(string buttonId)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = InvokeAsync(async () =>
        {
            try
            {
                result.TrySetResult(await OnButtonAsync(buttonId));
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        });

        return result.Task;
    }
}
