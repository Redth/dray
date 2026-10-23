using System.Diagnostics;
using Dray.Core.Shell;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.Storage.Pickers;
using DataTransfer = Windows.ApplicationModel.DataTransfer;
using Xaml = Microsoft.UI.Xaml;

namespace Dray.WinUI;

/// <summary>
/// WinUI implementation of the native surface. Like the macOS bridge it takes no feature services —
/// the app's own provider, for dialog bodies, and nothing else (docs/NATIVE-SHELL.md section 2.5).
/// </summary>
public sealed class WinShellBridge(IServiceProvider services) : IShellBridge
{
    readonly WinDialog _dialogs = new(services);
    bool? _notificationsRegistered;

    public Task<string?> ShowDialogAsync(DialogSession session, CancellationToken ct = default)
        => _dialogs.ShowAsync(session, ct);

    public async Task<ConfirmResult> ConfirmDestructiveAsync(DestructiveConfirm request, CancellationToken ct = default)
    {
        var result = await WinWindow.OnUiAsync(async () =>
        {
            var content = new Xaml.Controls.StackPanel { Spacing = 12, MaxWidth = 420 };
            content.Children.Add(new Xaml.Controls.TextBlock
            {
                Text = request.Body,
                TextWrapping = Xaml.TextWrapping.Wrap,
            });

            var dialog = new Xaml.Controls.ContentDialog
            {
                Title = request.Title,
                Content = content,
                PrimaryButtonText = request.ConfirmLabel,
                CloseButtonText = "Cancel",

                // Never the destructive button: Enter is pressed by people who have stopped reading.
                DefaultButton = Xaml.Controls.ContentDialogButton.Close,
            };

            // Irreversible bulk operations require the user to type the target's name, and the
            // button that commits stays disabled until they have.
            if (request.TypeToConfirm is { } phrase)
            {
                var field = new Xaml.Controls.TextBox { PlaceholderText = $"Type {phrase} to confirm" };
                content.Children.Add(field);

                dialog.IsPrimaryButtonEnabled = false;
                field.TextChanged += (_, _) =>
                    dialog.IsPrimaryButtonEnabled = string.Equals(field.Text.Trim(), phrase, StringComparison.Ordinal);

                dialog.Opened += (_, _) => field.Focus(Xaml.FocusState.Programmatic);
            }

            WinDanger.Style(dialog);
            return await WinDialog.ShowNativeAsync(dialog, ct);
        });

        return result == Xaml.Controls.ContentDialogResult.Primary ? ConfirmResult.Confirm : ConfirmResult.Cancel;
    }

    public Task<string?> PickFileAsync(FilePickerOptions options, CancellationToken ct = default)
        => WinWindow.OnUiAsync(async () =>
        {
            var picker = Own(new FileOpenPicker { ViewMode = PickerViewMode.List });

            if (options.Extensions is { Count: > 0 } extensions)
            {
                foreach (var extension in extensions) picker.FileTypeFilter.Add(Dotted(extension));
            }
            else
            {
                picker.FileTypeFilter.Add("*");
            }

            return (await picker.PickSingleFileAsync())?.Path;
        });

    public Task<string?> PickFolderAsync(string title, CancellationToken ct = default)
        => WinWindow.OnUiAsync(async () =>
        {
            var picker = Own(new FolderPicker());
            picker.FileTypeFilter.Add("*");
            return (await picker.PickSingleFolderAsync())?.Path;
        });

    public Task<string?> SaveFileAsync(FilePickerOptions options, CancellationToken ct = default)
        => WinWindow.OnUiAsync(async () =>
        {
            var picker = Own(new FileSavePicker());

            if (options.SuggestedName is { } name) picker.SuggestedFileName = Path.GetFileNameWithoutExtension(name);

            // The save picker refuses to open without at least one file type. The declared ones
            // first, then the suggested name's own, then ".". There is no spelling for "any file":
            // "*" and ".*" throw, and "." lists only names without an extension while saving the
            // name exactly as typed. So it is labelled for what it does, which is also what saving
            // an extensionless file out of a container needs.
            List<string> extensions = options.Extensions is { Count: > 0 } declared
                ? declared.Select(Dotted).ToList()
                : Path.GetExtension(options.SuggestedName) is { Length: > 1 } own ? [own] : [];

            foreach (var extension in extensions) picker.FileTypeChoices.Add(extension.TrimStart('.').ToUpperInvariant(), [extension]);
            picker.FileTypeChoices.Add("Name as typed", ["."]);

            var path = (await picker.PickSaveFileAsync())?.Path;

            // As on macOS: a picker that pre-creates an empty file at the chosen path makes tools
            // that refuse to overwrite fail. Callers get a path, not a zero-byte file.
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

            return path;
        });

    public Task RevealInFileManagerAsync(string path, CancellationToken ct = default)
    {
        // Explorer parses its own command line, and /select only works with the path quoted inside
        // the switch rather than the whole argument quoted — so not ArgumentList, which would.
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
        return Task.CompletedTask;
    }

    public async Task OpenExternalAsync(string url, CancellationToken ct = default)
    {
        // Only ever http(s). A container label or an inspect field is untrusted input, and file://
        // or a custom scheme from one must not be able to launch anything.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        await global::Windows.System.Launcher.LaunchUriAsync(uri);
    }

    public Task WriteClipboardAsync(string text, CancellationToken ct = default)
        => WinWindow.OnUiAsync(() =>
        {
            var package = new DataTransfer.DataPackage();
            package.SetText(text);
            DataTransfer.Clipboard.SetContent(package);

            // Without a flush the text belongs to this process and vanishes from the clipboard
            // when Dray quits — copy a container id, close the app, and the paste is empty.
            DataTransfer.Clipboard.Flush();
        });

    /// <summary>
    /// A toast, when the app is not the one in front — the same focus rule as every head: a
    /// notification about something the user is watching is noise.
    /// <para>
    /// Unlike macOS this works unsigned: an unpackaged Windows App SDK app registers itself for
    /// notifications at runtime. If registration is refused, notifications stay off for the run and
    /// the reason is logged once; nothing the user did could have avoided it, so nothing is shown.
    /// </para>
    /// </summary>
    public Task NotifyAsync(string title, string? body, NoticeKind kind = NoticeKind.Info, CancellationToken ct = default)
    {
        if (WinWindow.IsActive || !EnsureNotifications()) return Task.CompletedTask;

        var builder = new AppNotificationBuilder().AddText(title);
        if (!string.IsNullOrEmpty(body)) builder.AddText(body);

        AppNotificationManager.Default.Show(builder.BuildNotification());
        return Task.CompletedTask;
    }

    bool EnsureNotifications()
    {
        if (_notificationsRegistered is { } known) return known;

        try
        {
            // Before Register, or clicking a toast starts a second copy of the app to receive it
            // instead of bringing this one forward.
            AppNotificationManager.Default.NotificationInvoked += (_, _) =>
                _ = WinWindow.OnUiAsync(() => WinWindow.Current?.Activate());

            AppNotificationManager.Default.Register();
            _notificationsRegistered = true;
        }
        catch (Exception ex)
        {
            CrashLog.Write($"[dray:notify] registration refused: {ex.Message}");
            _notificationsRegistered = false;
        }

        return _notificationsRegistered.Value;
    }

    /// <summary>
    /// No badge. A taskbar badge belongs to a packaged app's identity, and an unpackaged one has
    /// none to attach it to; the overlay-icon alternative means rendering a number into an icon.
    /// Both arrive with MSIX packaging in Phase 7. The call site and the seam are already in place.
    /// </summary>
    public void SetBadge(int? count)
    {
    }

    /// <summary>
    /// Ties a WinRT picker to the window. An unpackaged app's picker has no window of its own to
    /// parent to, and opening one without this throws rather than showing anything.
    /// </summary>
    static T Own<T>(T picker) where T : class
    {
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinWindow.Handle);
        return picker;
    }

    static string Dotted(string extension) => extension.StartsWith('.') ? extension : "." + extension;
}
