namespace Dray.Core.Shell;

/// <summary>
/// What the hosting window already provides, so the Blazor layer knows what NOT to draw.
/// <para>
/// On macOS the sidebar is a real <c>NSOutlineView</c> and the toolbar a real <c>NSToolbar</c>, so
/// the WebView must not draw its own or the user sees two of each. In a plain browser — the
/// component gallery, or a future web build — Blazor draws both.
/// </para>
/// </summary>
public sealed record ShellCapabilities(
    bool HasNativeSidebar,
    bool HasNativeToolbar,
    bool HasNativeDialogs,
    bool IsDebugBuild = false)
{
    /// <summary>A host that draws its own chrome: the macOS, Windows and GTK app heads.</summary>
    public static ShellCapabilities Native(bool debug = false) => new(true, true, true, debug);

    /// <summary>A bare WebView or browser: Blazor draws everything.</summary>
    public static ShellCapabilities Web(bool debug = false) => new(false, false, false, debug);
}
