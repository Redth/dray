using Dray.Core.Shell;
using Microsoft.AspNetCore.Components;

namespace Dray.Ui.Components;

/// <summary>
/// The base of every dialog body: the Blazor middle of a native frame.
/// <para>
/// A body never draws a title or a button. It says what its frame should show — <see cref="Title"/>
/// and <see cref="Buttons"/>, read after every render so they follow the form as it is filled in —
/// and it answers presses in <see cref="OnPressedAsync"/>. The head draws the rest with the
/// platform's own controls: an <c>NSPanel</c> sheet on macOS, a <c>ContentDialog</c> on Windows, a
/// <c>&lt;dialog&gt;</c> in a browser. docs/NATIVE-SHELL.md section 4.
/// </para>
/// <para>
/// Opened through <see cref="IShellBridge.ShowDialogAsync(DialogSession, CancellationToken)"/>,
/// never placed in a page — by convention through a static <c>ShowAsync</c> on the body, which
/// returns what the page needs to know. On a native head the body is in a WebView of its own, so a
/// page cannot hand it a callback; it does its work through the same singletons the page uses, and
/// the page reads the button it closed with and <see cref="DialogSession.Value"/>.
/// </para>
/// </summary>
public abstract class DialogBody : ComponentBase
{
    DialogSession? _session;

    /// <summary>
    /// The dialog this body is in. Cascaded by whichever head is presenting it; null only when the
    /// body is rendered outside a dialog, as a gallery specimen.
    /// </summary>
    [CascadingParameter]
    public DialogSession? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value)) return;

            _session = value;
            if (value is not null) value.Pressed = PressedAsync;
        }
    }

    /// <summary>
    /// Cancelled when the dialog closes, however it closes — what a pull or a build listens to so
    /// it stops reading when the user walks away. Never cancelled outside a dialog.
    /// </summary>
    protected CancellationToken Closing => _session?.Closing ?? CancellationToken.None;

    /// <summary>The title the frame should show now, or null to keep the request's.</summary>
    protected virtual string? Title => null;

    /// <summary>The subtitle to show with <see cref="Title"/>.</summary>
    protected virtual string? Subtitle => null;

    /// <summary>The button row the frame should show now, or null to keep the request's.</summary>
    protected virtual IReadOnlyList<DialogButton>? Buttons => null;

    /// <summary>
    /// A button was pressed, on the renderer's thread. Return true to close the dialog with it. The
    /// default closes, which is right for a body that only shows something.
    /// <para>
    /// May take as long as the work does: the frame is not held while it runs, Cancel still closes
    /// the dialog, and <see cref="Closing"/> tells the work to stop.
    /// </para>
    /// </summary>
    protected virtual Task<bool> OnPressedAsync(string id) => Task.FromResult(true);

    /// <summary>Close from inside the body — a choice made in the content rather than the button row.</summary>
    protected void Close(string id, object? value = null) => _session?.Close(id, value);

    /// <summary>
    /// Pushes the frame's state after every render, so a button follows the form without each body
    /// remembering to say so. The session compares by value, so an unchanged row redraws nothing.
    /// </summary>
    protected override void OnAfterRender(bool firstRender)
    {
        if (_session is not { IsClosed: false } session) return;

        if (Title is { } title) session.SetTitle(title, Subtitle);
        if (Buttons is { } buttons) session.SetButtons(buttons);
    }

    /// <summary>
    /// The head's press, moved onto the renderer. A native head reports it from its own UI thread,
    /// and the body's state may only be touched from Blazor's.
    /// </summary>
    Task<bool> PressedAsync(string id)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = InvokeAsync(async () =>
        {
            try
            {
                result.TrySetResult(await OnPressedAsync(id));
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        });

        return result.Task;
    }
}
