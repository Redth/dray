using AppKit;
using Dray.Core.Shell;
using Foundation;
using UniformTypeIdentifiers;

namespace Dray.MacOS;

/// <summary>
/// AppKit implementation of the native surface. Deliberately small: it takes no feature services,
/// which is what keeps it from growing into Sherpa's 780-line, seven-dependency title-bar manager
/// (docs/NATIVE-SHELL.md section 2.5).
/// </summary>
public sealed class MacShellBridge : IShellBridge
{
    public Task<ConfirmResult> ConfirmDestructiveAsync(DestructiveConfirm request, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<ConfirmResult>();

        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            var alert = new NSAlert
            {
                AlertStyle = NSAlertStyle.Critical,
                MessageText = request.Title,
                InformativeText = request.Body,
            };

            alert.AddButton(request.ConfirmLabel);
            alert.AddButton("Cancel");

            // Irreversible bulk operations require the user to type the target's name. An
            // accessory text field is the native way to do that inside an NSAlert.
            NSTextField? confirmField = null;
            if (request.TypeToConfirm is { } phrase)
            {
                confirmField = new NSTextField(new CoreGraphics.CGRect(0, 0, 280, 24))
                {
                    PlaceholderString = $"Type {phrase} to confirm",
                };
                alert.AccessoryView = confirmField;

                // Nothing to commit until the phrase matches exactly.
                alert.Buttons[0].Enabled = false;
                confirmField.Changed += (_, _) =>
                    alert.Buttons[0].Enabled = string.Equals(confirmField.StringValue.Trim(), phrase, StringComparison.Ordinal);
            }

            var window = NSApplication.SharedApplication.KeyWindow;
            if (window is null)
            {
                var modal = (long)alert.RunModal();
                tcs.TrySetResult(modal == (long)NSAlertButtonReturn.First ? ConfirmResult.Confirm : ConfirmResult.Cancel);
                return;
            }

            // A sheet, not a free-floating window: this is a decision about the document in front
            // of the user, and macOS attaches those to it.
            alert.BeginSheet(window, response =>
                tcs.TrySetResult((long)response == (long)NSAlertButtonReturn.First ? ConfirmResult.Confirm : ConfirmResult.Cancel));

            confirmField?.Window?.MakeFirstResponder(confirmField);
        });

        return tcs.Task;
    }

    public Task<string?> PickFileAsync(FilePickerOptions options, CancellationToken ct = default)
        => RunPanelAsync(() =>
        {
            var panel = NSOpenPanel.OpenPanel;
            panel.Title = options.Title;
            panel.CanChooseFiles = true;
            panel.CanChooseDirectories = false;
            panel.AllowsMultipleSelection = false;
            ApplyExtensions(panel, options.Extensions);
            return panel;
        });

    public Task<string?> PickFolderAsync(string title, CancellationToken ct = default)
        => RunPanelAsync(() =>
        {
            var panel = NSOpenPanel.OpenPanel;
            panel.Title = title;
            panel.CanChooseFiles = false;
            panel.CanChooseDirectories = true;
            panel.AllowsMultipleSelection = false;
            return panel;
        });

    public Task<string?> SaveFileAsync(FilePickerOptions options, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<string?>();

        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            var panel = NSSavePanel.SavePanel;
            panel.Title = options.Title;
            if (options.SuggestedName is { } name) panel.NameFieldStringValue = name;
            ApplyExtensions(panel, options.Extensions);

            var path = (long)panel.RunModal() == 1 ? panel.Url?.Path : null;

            // NSSavePanel creates an empty file at the chosen path. Tools that refuse to overwrite
            // an existing file then fail — a real bite Sherpa took with keytool. Clear it here so
            // callers get a path, not a zero-byte file.
            if (path is not null && File.Exists(path) && new FileInfo(path).Length == 0)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // Something else holds it; the caller can still decide what to do.
                }
            }

            tcs.TrySetResult(path);
        });

        return tcs.Task;
    }

    public Task RevealInFileManagerAsync(string path, CancellationToken ct = default)
    {
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
            NSWorkspace.SharedWorkspace.SelectFile(path, string.Empty));
        return Task.CompletedTask;
    }

    public Task OpenExternalAsync(string url, CancellationToken ct = default)
    {
        // Only ever http(s). A container label or an inspect field is untrusted input, and
        // file:// or a custom scheme from one must not be able to launch anything.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Task.CompletedTask;
        }

        NSApplication.SharedApplication.InvokeOnMainThread(() =>
            NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl(uri.AbsoluteUri)));
        return Task.CompletedTask;
    }

    public Task WriteClipboardAsync(string text, CancellationToken ct = default)
    {
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            var pasteboard = NSPasteboard.GeneralPasteboard;
            pasteboard.ClearContents();
            pasteboard.SetStringForType(text, NSPasteboardType.String.GetConstant()!);
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// A system notification, when the app is not the one in front.
    /// <para>
    /// The focus check is the part that works today and the part that matters: a notification
    /// about something the user is watching is noise, and the rule is the same on every head.
    /// </para>
    /// <para>
    /// Posting it is not. <c>UNUserNotificationCenter</c> needs a signed, notification-entitled
    /// bundle, and asking an unsigned one for the notification centre raises rather than returning
    /// a refusal — which is why this is still a no-op here and not a try/catch around a call that
    /// cannot work yet. Phase 7 signs the bundle; the call site, the seam and the focus rule are
    /// all in place for it, so what is left is the entitlement rather than the design.
    /// </para>
    /// </summary>
    public Task NotifyAsync(string title, string? body, NoticeKind kind = NoticeKind.Info, CancellationToken ct = default)
    {
        if (NSApplication.SharedApplication.Active) return Task.CompletedTask;

        return Task.CompletedTask;
    }

    public void SetBadge(int? count)
        => NSApplication.SharedApplication.InvokeOnMainThread(() =>
            NSApplication.SharedApplication.DockTile.BadgeLabel = count is > 0 ? count.Value.ToString() : null);

    static Task<string?> RunPanelAsync(Func<NSOpenPanel> factory)
    {
        var tcs = new TaskCompletionSource<string?>();

        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            var panel = factory();
            tcs.TrySetResult((long)panel.RunModal() == 1 ? panel.Urls.FirstOrDefault()?.Path : null);
        });

        return tcs.Task;
    }

    static void ApplyExtensions(NSSavePanel panel, IReadOnlyList<string>? extensions)
    {
        if (extensions is null || extensions.Count == 0) return;

        // AllowedFileTypes has been obsolete since macOS 12; content types are the current API.
        // An extension macOS does not recognise yields null and is simply dropped.
        var types = extensions
            .Select(e => UTType.CreateFromExtension(e.TrimStart('.')))
            .Where(t => t is not null)
            .Select(t => t!)
            .ToArray();

        if (types.Length > 0) panel.AllowedContentTypes = types;
    }
}
