namespace Dray.Core.Shell;

/// <summary>A button's state as the body currently wants it shown.</summary>
/// <param name="Label">What the button says now — "Pull", then "Pulling…".</param>
/// <param name="IsEnabled">False while there is nothing valid to commit, or while work is running.</param>
/// <param name="IsBusy">Work started by this button is in progress. A head shows it if its frame can.</param>
public sealed record DialogButtonState(string Label, bool IsEnabled = true, bool IsBusy = false);

/// <summary>
/// The live half of a dialog: lets its body drive the native button row it cannot draw.
/// <para>
/// A plain <see cref="DialogRequest"/> is a question with fixed answers — the dialog closes on the
/// first button pressed. A form is not. Its commit button is disabled until the fields are valid,
/// says "Pulling…" while it works, keeps the dialog open while it does, and closes it only on
/// success. All of that is state the body owns and the head draws, and the body and the head are
/// in different worlds: on a native head the body is a second WebView inside an <c>NSAlert</c> or a
/// <c>ContentDialog</c>, and the buttons are AppKit's or WinUI's. This is the channel between them.
/// </para>
/// <para>
/// Without it, a form had two choices: become a web <c>&lt;dialog&gt;</c> that draws its own title
/// bar and buttons — the bug docs/NATIVE-SHELL.md section 4 exists to prevent — or lose its live
/// state. Every form in the app took the first.
/// </para>
/// </summary>
public sealed class DialogController
{
    readonly Dictionary<string, DialogButtonState> _states = new(StringComparer.Ordinal);

    /// <summary>
    /// What a button press means, set by the body. Returns true to close the dialog with that button
    /// as its result, false to keep it open.
    /// <para>
    /// Must return promptly: a head waits on it with the platform's button event held open, and a
    /// pull that runs for a minute belongs in a task the body starts, not in this call.
    /// </para>
    /// </summary>
    public Func<string, Task<bool>>? Pressed { get; set; }

    /// <summary>Raised when any button's state changes. Heads redraw their buttons from <see cref="State"/>.</summary>
    public event Action? Changed;

    /// <summary>Raised when the body closes the dialog itself. Heads dismiss their frame with <see cref="Result"/>.</summary>
    public event Action? CloseRequested;

    /// <summary>The button the dialog closed with, or null while open or after a dismissal.</summary>
    public string? Result { get; private set; }

    /// <summary>Whatever the body produced, for the page that asked — the id of a container it ran.</summary>
    public object? Value { get; private set; }

    public bool IsClosed { get; private set; }

    /// <summary>
    /// The current state of a button, or null for one the body never set — in which case the head
    /// shows it as the request declared it.
    /// </summary>
    public DialogButtonState? State(string buttonId) => _states.GetValueOrDefault(buttonId);

    /// <summary>
    /// Set a button's state. Idempotent, so a body may call it on every render: only a real change
    /// raises <see cref="Changed"/>, which is what keeps a head's redraw from re-rendering the body
    /// into a loop.
    /// </summary>
    public void Set(string buttonId, DialogButtonState state)
    {
        if (_states.TryGetValue(buttonId, out var current) && current == state) return;

        _states[buttonId] = state;
        Changed?.Invoke();
    }

    /// <summary>
    /// A head reporting a press. Asks the body; with no body listening, every button closes the
    /// dialog, which is exactly how a dialog without a controller behaves.
    /// </summary>
    public async Task<bool> PressAsync(string buttonId)
    {
        if (IsClosed) return true;
        if (State(buttonId) is { IsEnabled: false }) return false;

        var close = Pressed is not { } pressed || await pressed(buttonId).ConfigureAwait(false);
        if (close) Settle(buttonId, Value);

        return close;
    }

    /// <summary>The body closing the dialog itself — after a create succeeds, say.</summary>
    public void Close(string? buttonId, object? value = null)
    {
        if (IsClosed) return;

        Settle(buttonId, value);
        CloseRequested?.Invoke();
    }

    /// <summary>A head reporting that its frame went away without a press: the window closed under it.</summary>
    public void Dismissed()
    {
        if (!IsClosed) Settle(null, Value);
    }

    void Settle(string? buttonId, object? value)
    {
        IsClosed = true;
        Result = buttonId;
        Value = value;
    }
}
