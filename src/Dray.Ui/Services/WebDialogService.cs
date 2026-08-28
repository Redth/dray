using Dray.Core.Shell;

namespace Dray.Ui.Services;

/// <summary>
/// Carries a dialog between <see cref="WebShellBridge"/> and the component that renders it, for
/// hosts with no native dialog of their own.
/// <para>
/// The same shape as <see cref="WebConfirmService"/>, and for the same reason: the bridge is asked
/// from anywhere and the rendering happens once, in the layout.
/// </para>
/// </summary>
public sealed class WebDialogService
{
    TaskCompletionSource<string?>? _pending;

    public DialogRequest? Request { get; private set; }

    public event Action? Changed;

    public Task<string?> ShowAsync(DialogRequest request)
    {
        // A second dialog while one is open would orphan the first task forever. Dismissing the
        // first is the honest answer — it is no longer on screen.
        _pending?.TrySetResult(null);

        Request = request;
        _pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Changed?.Invoke();

        return _pending.Task;
    }

    /// <param name="buttonId">The button pressed, or null for a dismissal.</param>
    public void Close(string? buttonId)
    {
        var pending = _pending;
        _pending = null;
        Request = null;
        Changed?.Invoke();

        pending?.TrySetResult(buttonId);
    }
}
