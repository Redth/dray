namespace Dray.Core.Shell;

/// <summary>
/// Blazor telling the host it has painted its first frame.
/// <para>
/// The native loading overlay sits above the WebView from the moment the handler connects and is
/// removed on this signal. Without it the user sees a flash of empty WebView on every launch; with
/// it and no signal, they would see a spinner forever — so the host also reveals on a timeout
/// (docs/NATIVE-SHELL.md section 1.3).
/// </para>
/// </summary>
public interface IShellReadySignal
{
    void MarkReady();
}

/// <summary>Used by hosts that draw no overlay, such as the browser dev host.</summary>
public sealed class NoOpShellReadySignal : IShellReadySignal
{
    public void MarkReady()
    {
    }
}
