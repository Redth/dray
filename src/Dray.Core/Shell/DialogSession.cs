namespace Dray.Core.Shell;

/// <summary>
/// One open dialog: what its frame shows right now, and the line between the frame and the body.
/// <para>
/// A <see cref="DialogRequest"/> says what a dialog starts as. A form does not stay that way — its
/// commit button is off until the form is complete, it is busy while the engine works, and a guided
/// flow changes its title and its whole button row from one step to the next. The frame is native
/// on every head that has one, so the body cannot draw any of that itself; it says it here, and the
/// head redraws its own controls from <see cref="Changed"/>.
/// </para>
/// <para>
/// Presses go the other way. The head reports a press with <see cref="PressAsync"/>, the body
/// decides what it means, and the dialog closes only when the body says so — which is what lets a
/// form validate, stay open while the engine works, and report a refusal in place rather than
/// closing on it. A body that registers nothing gets the plain behaviour: any press closes.
/// </para>
/// </summary>
public sealed class DialogSession
{
    readonly TaskCompletionSource<string?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CancellationTokenSource _closing = new();

    string? _pressing;

    public DialogSession(DialogRequest request)
    {
        Request = request;
        Title = request.Title;
        Subtitle = request.Subtitle;
        Buttons = request.ButtonList;
    }

    public DialogRequest Request { get; }

    public string Title { get; private set; }

    public string? Subtitle { get; private set; }

    public IReadOnlyList<DialogButton> Buttons { get; private set; }

    /// <summary>
    /// The height the body's content wants, in CSS pixels, as last measured. A head that sizes its
    /// frame to the content reads this; a browser lays the body out itself and ignores it.
    /// </summary>
    public double? ContentHeight { get; private set; }

    /// <summary>The title, subtitle, buttons or content height changed. Raised only on a real change.</summary>
    public event Action? Changed;

    /// <summary>Raised once, with the button that closed the dialog or null for a dismissal.</summary>
    public event Action<string?>? Closed;

    /// <summary>
    /// What a press means. Given the button's id; returns true to close the dialog with it, false
    /// to keep it open. Null closes on every press.
    /// </summary>
    public Func<string, Task<bool>>? Pressed { get; set; }

    /// <summary>The button that closed the dialog, or null for a dismissal.</summary>
    public Task<string?> Completion => _done.Task;

    /// <summary>The button the dialog closed with, or null while it is open or after a dismissal.</summary>
    public string? Result { get; private set; }

    /// <summary>
    /// What the body produced, for the caller to read once the dialog has closed — the name of the
    /// account it created. Never a secret: it outlives the body.
    /// </summary>
    public object? Value { get; private set; }

    public bool IsClosed => _done.Task.IsCompleted;

    /// <summary>
    /// Cancelled when the dialog closes, however it closes. Work the body started — a pull, a build
    /// — stops reading when the user walks away from it, rather than outliving its own window.
    /// </summary>
    public CancellationToken Closing => _closing.Token;

    /// <summary>The button Return presses right now: the default, if it can be pressed.</summary>
    public DialogButton? DefaultButton => DialogRequest.DefaultOf(Buttons) is { Pressable: true } b ? b : null;

    public void SetTitle(string title, string? subtitle = null)
    {
        if (title == Title && subtitle == Subtitle) return;

        Title = title;
        Subtitle = subtitle;
        Changed?.Invoke();
    }

    /// <summary>
    /// Replace the button row. Compared by value, so a body can call this on every render and the
    /// head only redraws when something it shows is different.
    /// </summary>
    public void SetButtons(IReadOnlyList<DialogButton> buttons)
    {
        if (buttons.SequenceEqual(Buttons)) return;

        Buttons = buttons;
        Changed?.Invoke();
    }

    /// <summary>Change one button in place — enable it, mark it busy, relabel it.</summary>
    public void Update(string id, Func<DialogButton, DialogButton> change)
        => SetButtons([.. Buttons.Select(b => b.Id == id ? change(b) : b)]);

    /// <summary>Reported by the body as its content lays out.</summary>
    public void ReportContentHeight(double height)
    {
        // Sub-pixel jitter from a rerender is not a resize worth animating a window for.
        if (ContentHeight is { } current && Math.Abs(current - height) < 1) return;

        ContentHeight = height;
        Changed?.Invoke();
    }

    /// <summary>
    /// A button in the frame was pressed. Ignored when it cannot be pressed, and while an earlier
    /// press is still being handled — a double click must not submit a form twice.
    /// </summary>
    public async Task PressAsync(string id)
    {
        if (IsClosed) return;
        if (Buttons.FirstOrDefault(b => b.Id == id) is not { } button) return;

        // Cancel always closes, even while another press is still working — it is how a pull or a
        // build is stopped. A body may want to tidy up, and the Closing token tells it to, but the
        // way out of a dialog is never something the body gets to refuse.
        if (button.Role == DialogButtonRole.Cancel)
        {
            if (button.Enabled) Close(id);
            return;
        }

        if (_pressing is not null || !button.Pressable) return;

        if (Pressed is null)
        {
            Close(id);
            return;
        }

        _pressing = id;

        try
        {
            if (await Pressed(id).ConfigureAwait(true)) Close(id);
        }
        finally
        {
            _pressing = null;
        }
    }

    /// <summary>Return was pressed in the body. Presses the default button, if there is one to press.</summary>
    public Task PressDefaultAsync() => DefaultButton is { } button ? PressAsync(button.Id) : Task.CompletedTask;

    /// <summary>Escape, the close box, or the window going away. Always closes.</summary>
    public void Dismiss() => Close(null);

    /// <summary>
    /// Close with this button's id — from the body, once it has done what the press asked — and
    /// optionally with what it produced.
    /// </summary>
    public void Close(string? id, object? value = null)
    {
        if (IsClosed) return;

        Result = id;
        Value = value;

        if (!_done.TrySetResult(id)) return;

        _closing.Cancel();
        Closed?.Invoke(id);
    }
}
