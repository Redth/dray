using Dray.Core.Shell;

namespace Dray.Ui.Services;

/// <summary>
/// The dialog a native head is currently presenting, for the component rendering its body.
/// <para>
/// The body runs in its own WebView with its own Blazor renderer, so the request cannot be passed
/// to it as a parameter from the page that asked for it — the two are in different documents. This
/// carries it across, and it is a singleton because a sheet is modal: there is exactly one.
/// </para>
/// <para>
/// Only the native heads use this. The web head renders the same body inline through
/// <c>DialogHost</c>, where a parameter is all it takes.
/// </para>
/// </summary>
public sealed class NativeDialogState
{
    public DialogRequest? Current { get; private set; }

    public event Action? Changed;

    public void Set(DialogRequest? request)
    {
        Current = request;
        Changed?.Invoke();
    }
}
