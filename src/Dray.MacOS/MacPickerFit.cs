using AppKit;

namespace Dray.MacOS;

/// <summary>
/// Fits the engine picker's title into the strip of toolbar above the sidebar.
/// <para>
/// That strip is what is left of the sidebar's width after the window's own buttons, and AppKit does
/// not shorten a toolbar item that will not fit: it moves the item into the overflow menu at the far
/// end of the toolbar. Measured: "Apple container · unreachable" did exactly that at the sidebar's
/// default width, and the sidebar can be dragged down to 150 points, where even "desktop-linux" would
/// follow it. The control that says what every page is about cannot be the one that disappears, so
/// the title is shortened to the room there is — with an ellipsis, then to no words at all, leaving
/// the glyph and the chevron. The whole name is always in the tooltip and the menu.
/// </para>
/// </summary>
static class MacPickerFit
{
    /// <summary>
    /// The item's own width besides its title — the glyph, the chevron and the item's padding — plus
    /// the gap AppKit leaves after the window buttons and the sidebar's inset from its edge. Read off
    /// the running toolbar: with the title "desktop-linux" the item spanned 105 to 225 points against
    /// window buttons ending at 78 and a sidebar edge at 249.
    /// </summary>
    const double Reserved = 70;

    /// <summary>Slack, so a measurement a point off never decides between fitting and overflowing.</summary>
    const double Margin = 6;

    /// <summary>With the sidebar collapsed the item sits beside the window buttons, with the toolbar's whole width beyond.</summary>
    const double Roomy = 220;

    static readonly NSStringAttributes Attributes = new() { Font = NSFont.SystemFontOfSize(NSFont.SystemFontSize) };

    /// <summary>The title to show in <paramref name="window"/>, given the sidebar as it is now.</summary>
    public static string Fit(string title, NSWindow? window)
    {
        var room = Room(window);
        if (Width(title) <= room) return title;

        // Too narrow for even a couple of letters and an ellipsis: say nothing rather than "D…",
        // which reads as a name. The glyph still says what the control is.
        if (room < Width("Ab…")) return string.Empty;

        for (var length = title.Length - 1; length > 0; length--)
        {
            var shortened = title[..length].TrimEnd() + "…";
            if (Width(shortened) <= room) return shortened;
        }

        return string.Empty;
    }

    static double Width(string text) => new Foundation.NSAttributedString(text, Attributes).Size.Width;

    static double Room(NSWindow? window)
    {
        if (window?.ContentView is not { } content) return Roomy;

        var sidebar = FindSidebar(content)?.EnclosingScrollView;
        if (sidebar is null || sidebar.IsHiddenOrHasHiddenAncestor || sidebar.Frame.Width < 1) return Roomy;

        var sidebarRight = sidebar.ConvertRectToView(sidebar.Bounds, null).Right;
        var buttonsRight = window.StandardWindowButton(NSWindowButton.ZoomButton) is { } zoom
            ? zoom.ConvertRectToView(zoom.Bounds, null).Right
            : 78;

        return Math.Max(0, sidebarRight - buttonsRight - Reserved - Margin);
    }

    static NSOutlineView? FindSidebar(NSView view)
    {
        if (view is NSOutlineView outline) return outline;

        foreach (var child in view.Subviews)
        {
            if (FindSidebar(child) is { } found) return found;
        }

        return null;
    }
}
