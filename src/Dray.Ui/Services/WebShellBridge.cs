using Dray.Core.Shell;
using Microsoft.JSInterop;

namespace Dray.Ui.Services;

/// <summary>
/// <see cref="IShellBridge"/> for a host with no native chrome — the browser dev host.
/// <para>
/// Everything a browser genuinely cannot do returns a refusal rather than pretending. A file
/// picker that silently does nothing would be worse than one that says it is unavailable here.
/// </para>
/// </summary>
public sealed class WebShellBridge(WebConfirmService confirms, WebDialogService dialogs, IJSRuntime js) : IShellBridge
{
    public Task<ConfirmResult> ConfirmDestructiveAsync(DestructiveConfirm request, CancellationToken ct = default)
        => confirms.AskAsync(request);

    /// <summary>
    /// The one head that draws the whole dialog itself, because a browser has no other frame.
    /// <c>DialogHost</c> renders it over <c>&lt;dialog&gt;</c>.
    /// </summary>
    public Task<string?> ShowDialogAsync(DialogRequest request, CancellationToken ct = default)
        => dialogs.ShowAsync(request);

    public Task<string?> PickFileAsync(FilePickerOptions options, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<string?> PickFolderAsync(string title, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<string?> SaveFileAsync(FilePickerOptions options, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task RevealInFileManagerAsync(string path, CancellationToken ct = default)
        => Task.CompletedTask;

    public async Task OpenExternalAsync(string url, CancellationToken ct = default)
    {
        // Only ever http(s): a container label or inspect field is untrusted input and must not be
        // able to navigate the app itself or launch a custom scheme.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        await js.InvokeVoidAsync("open", ct, uri.AbsoluteUri, "_blank", "noopener");
    }

    public async Task WriteClipboardAsync(string text, CancellationToken ct = default)
    {
        var module = await js.InvokeAsync<IJSObjectReference>("import", ct, "./_content/Dray.Ui/js/dray.js");
        await module.InvokeAsync<bool>("clipboard.write", ct, text);
    }

    /// <summary>
    /// A system notification, but only when the window is not in front.
    /// <para>
    /// The rule is the same on every head: a notification about something the user is watching is
    /// noise. A pull that finishes while its progress bar is on screen has already reported
    /// itself, and telling them twice trains them to dismiss the ones that matter.
    /// </para>
    /// <para>
    /// A browser that refuses permission is a preference, not a failure, so nothing is raised and
    /// nothing is logged.
    /// </para>
    /// </summary>
    public async Task NotifyAsync(
        string title, string? body, NoticeKind kind = NoticeKind.Info, CancellationToken ct = default)
    {
        try
        {
            var module = await js.InvokeAsync<IJSObjectReference>("import", ct, "./_content/Dray.Ui/js/dray.js");

            await using (module.ConfigureAwait(false))
            {
                if (await module.InvokeAsync<bool>("notify.focused", ct)) return;

                await module.InvokeAsync<bool>("notify.show", ct, title, body);
            }
        }
        catch (JSException)
        {
        }
        catch (JSDisconnectedException)
        {
            // The circuit went while the operation was finishing. Nothing to notify.
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void SetBadge(int? count)
    {
    }
}
