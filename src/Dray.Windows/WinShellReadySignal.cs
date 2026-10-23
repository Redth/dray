using Dray.Core.Shell;

namespace Dray.WinUI;

/// <summary>
/// Drops the native loading overlay and fades the WebView in once Blazor has painted.
/// <para>
/// The shell is created after the service provider is built, so this is a settable holder rather
/// than a constructor dependency — the same shape as the macOS head's.
/// </para>
/// </summary>
public sealed class WinShellReadySignal : IShellReadySignal
{
    NativeShellHandler? _shell;
    bool _ready;

    public void Attach(NativeShellHandler shell)
    {
        _shell = shell;

        // Blazor can finish before the handler connects; the signal must not be lost in that gap.
        if (_ready) shell.Reveal();
    }

    public void MarkReady()
    {
        _ready = true;
        _shell?.Reveal();
    }
}
