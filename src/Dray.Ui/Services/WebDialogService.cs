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
    public DialogSession? Session { get; private set; }

    public event Action? Changed;

    public Task<string?> ShowAsync(DialogSession session, CancellationToken ct = default)
    {
        // A second dialog while one is open would orphan the first task forever. Dismissing the
        // first is the honest answer — it is no longer on screen.
        Session?.Dismiss();

        session.Closed += _ => OnClosed(session);

        if (ct.CanBeCanceled) ct.Register(session.Dismiss);

        Session = session;
        Changed?.Invoke();

        return session.Completion;
    }

    void OnClosed(DialogSession session)
    {
        if (!ReferenceEquals(Session, session)) return;

        Session = null;
        Changed?.Invoke();
    }
}
