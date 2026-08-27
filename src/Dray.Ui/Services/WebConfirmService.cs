using Dray.Core.Shell;

namespace Dray.Ui.Services;

/// <summary>
/// Carries a destructive confirmation between <see cref="WebShellBridge"/> and the component that
/// renders it, for hosts with no native dialog of their own.
/// </summary>
public sealed class WebConfirmService
{
    TaskCompletionSource<ConfirmResult>? _pending;

    public DestructiveConfirm? Request { get; private set; }

    public event Action? Changed;

    public Task<ConfirmResult> AskAsync(DestructiveConfirm request)
    {
        // A second request while one is open would orphan the first task forever.
        _pending?.TrySetResult(ConfirmResult.Cancel);

        Request = request;
        _pending = new TaskCompletionSource<ConfirmResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Changed?.Invoke();

        return _pending.Task;
    }

    public void Answer(ConfirmResult result)
    {
        var pending = _pending;
        _pending = null;
        Request = null;
        Changed?.Invoke();

        pending?.TrySetResult(result);
    }
}
