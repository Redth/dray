using AppKit;
using Dray.Core.Shell;

namespace Dray.MacOS;

/// <summary>
/// Drops the native loading overlay and fades the WebView in once Blazor has painted.
/// <para>
/// The page is registered late — the window does not exist when the service provider is built —
/// so this is a settable holder rather than a constructor dependency.
/// </para>
/// </summary>
public sealed class MacShellReadySignal : IShellReadySignal
{
    BlazorContentPage? _page;

    public void Attach(BlazorContentPage page) => _page = page;

    public void MarkReady()
        => NSApplication.SharedApplication.InvokeOnMainThread(() => _page?.Reveal());
}
