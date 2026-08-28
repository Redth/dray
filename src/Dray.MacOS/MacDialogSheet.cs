using AppKit;
using CoreGraphics;
using Dray.Core.Shell;
using Foundation;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace Dray.MacOS;

/// <summary>
/// A dialog on macOS: an <c>NSAlert</c> sheet whose accessory view is a Blazor WebView.
/// <para>
/// The three regions of docs/NATIVE-SHELL.md section 4 map onto an alert exactly, which is why
/// this is an alert rather than a hand-built <c>NSPanel</c>. <c>MessageText</c> is the title row,
/// <c>InformativeText</c> the subtitle, the accessory view is the body, and <c>AddButton</c> is the
/// button row — with AppKit deciding the order, the default button, the Escape binding and the
/// sheet animation rather than any of that being written here. A panel would have meant
/// reimplementing all four, and getting them slightly wrong on every OS release.
/// </para>
/// <para>
/// The body is a second <c>BlazorWebView</c>. It shares the app's service provider, so the
/// component inside it talks to the same <c>EngineManager</c> and the same store as the page
/// behind — it is another view of the running app, not a second copy of it.
/// </para>
/// </summary>
public sealed class MacDialogSheet(IServiceProvider services)
{
    /// <summary>
    /// What each size is in points.
    /// <para>
    /// Fixed rather than measured: an alert lays its accessory view out once, at the size the view
    /// reports, and a web body's height is not known until after it has rendered — which is after
    /// the alert has already sized itself. Asking for a size up front is the honest version of
    /// that constraint, and it is why <see cref="DialogSize"/> exists rather than a pixel count.
    /// </para>
    /// </summary>
    static CGSize SizeOf(DialogSize size) => size switch
    {
        DialogSize.Small => new CGSize(420, 160),
        DialogSize.Large => new CGSize(760, 520),
        _ => new CGSize(560, 320),
    };

    public Task<string?> ShowAsync(DialogRequest request, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<string?>();

        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            try
            {
                Present(request, tcs);
            }
            catch (Exception ex)
            {
                // A dialog that cannot be presented must not leave its caller awaiting forever.
                Console.Error.WriteLine($"[dray:dialog] {ex}");
                tcs.TrySetResult(null);
            }
        });

        // The window going away cancels the sheet with it, and the caller gets a dismissal rather
        // than a task that never completes.
        if (ct.CanBeCanceled) ct.Register(() => tcs.TrySetResult(null));

        return tcs.Task;
    }

    void Present(DialogRequest request, TaskCompletionSource<string?> tcs)
    {
        var alert = new NSAlert
        {
            AlertStyle = request.ButtonList.Any(b => b.Role == DialogButtonRole.Destructive)
                ? NSAlertStyle.Critical
                : NSAlertStyle.Informational,

            MessageText = request.Title,
            InformativeText = request.Subtitle ?? string.Empty,
        };

        // AppKit's order: the first button added is rightmost and is the default. The sorting
        // itself lives in Core, with tests — the head only says which convention it wants, and
        // this one is the opposite of the browser's.
        var order = request.Ordered(DialogButtonOrder.CommitFirst);

        foreach (var button in order)
        {
            var added = alert.AddButton(button.Label);

            // A destructive button is never the one Return commits. DialogRequest refuses to name
            // one as the default; this makes sure AppKit does not either.
            if (button.Role == DialogButtonRole.Destructive) added.KeyEquivalent = string.Empty;

            if (button.Role == DialogButtonRole.Cancel) added.KeyEquivalent = "";
        }

        var size = SizeOf(request.Size);
        var host = new MacDialogBody(services, request, size);

        alert.AccessoryView = host.View;

        // Key, then main, then whatever there is. A window that is not key is still the window this
        // dialog is about, and falling through to RunModal because the app happened to be in the
        // background blocks the main thread until someone dismisses it — which is a hang, not a
        // dialog. RunModal is for the case where there is genuinely no window to attach to.
        var window = NSApplication.SharedApplication.KeyWindow
            ?? NSApplication.SharedApplication.MainWindow
            ?? (Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as NSWindow);

        void Finish(nint response)
        {
            // NSAlertFirstButtonReturn is 1000 and they count up in the order added.
            var index = (int)(response - (nint)NSAlertButtonReturn.First);

            host.Dispose();
            tcs.TrySetResult(index >= 0 && index < order.Count ? order[index].Id : null);
        }

        if (window is null)
        {
            Finish((nint)(long)alert.RunModal());
            return;
        }

        // A sheet, not a free-floating window: this is about the document in front of the user.
        alert.BeginSheet(window, response => Finish((nint)(long)response));
    }
}
