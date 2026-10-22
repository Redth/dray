using Dray.Core.Shell;
using Dray.Ui.Services;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Platform;
using Xaml = Microsoft.UI.Xaml;

namespace Dray.WinUI;

/// <summary>
/// A dialog on Windows: a <c>ContentDialog</c> whose content is a Blazor WebView.
/// <para>
/// The three regions of docs/NATIVE-SHELL.md section 4 are a <c>ContentDialog</c>'s own: the title
/// row is <c>Title</c>, the body is <c>Content</c>, and the button row is its primary, secondary
/// and close buttons — placed, styled and bound to Enter and Escape by WinUI rather than by
/// anything written here.
/// </para>
/// <para>
/// The body is a second <c>BlazorWebView</c> built from the app's own service provider, so the
/// component inside talks to the same <c>EngineManager</c> and store as the page behind it.
/// </para>
/// </summary>
public sealed class WinDialog(IServiceProvider services)
{
    /// <summary>
    /// WinUI shows one <c>ContentDialog</c> per window at a time and throws on a second. A
    /// confirmation raised from inside a dialog's body waits its turn rather than failing.
    /// </summary>
    static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Body sizes, in effective pixels. Fixed rather than measured for the reason the macOS sheet
    /// gives: a web body's height is not known until it has rendered, which is after the dialog has
    /// laid itself out.
    /// </summary>
    static (double Width, double Height) SizeOf(DialogSize size) => size switch
    {
        DialogSize.Small => (420, 160),
        DialogSize.Large => (760, 520),
        _ => (560, 320),
    };

    public async Task<string?> ShowAsync(DialogRequest request, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await WinWindow.OnUiAsync(() => PresentAsync(request, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A dialog that cannot be presented must not leave its caller awaiting forever.
            CrashLog.Write($"[dray:dialog] {ex}");
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>A content dialog with no web body, for the confirmations that need none.</summary>
    public static async Task<Xaml.Controls.ContentDialogResult> ShowNativeAsync(
        Xaml.Controls.ContentDialog dialog, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await WinWindow.OnUiAsync(async () =>
            {
                dialog.XamlRoot = WinWindow.XamlRoot;
                dialog.Style = Resource("DefaultContentDialogStyle");
                using var _ = ct.Register(() => dialog.DispatcherQueue.TryEnqueue(dialog.Hide));
                return await dialog.ShowAsync();
            });
        }
        finally
        {
            Gate.Release();
        }
    }

    async Task<string?> PresentAsync(DialogRequest request, CancellationToken ct)
    {
        var context = WinWindow.MauiContext
            ?? throw new InvalidOperationException("No window to present a dialog from.");

        // Set before the body exists: the surface reads it as it mounts, and a request arriving
        // afterwards would render an empty dialog for a frame.
        var state = services.GetRequiredService<NativeDialogState>();
        state.Set(request);

        var webView = new BlazorWebView { HostPage = "wwwroot/dialog.html" };
        webView.RootComponents.Add(new RootComponent
        {
            Selector = "#dialog",
            ComponentType = typeof(Dray.Ui.Components.DialogSurface),
        });
        WebViewSetup.Apply(webView);

        var (width, height) = SizeOf(request.Size);
        var body = (Xaml.FrameworkElement)webView.ToPlatform(context);
        body.Width = width;
        body.Height = height;

        var content = new Xaml.Controls.StackPanel { Spacing = 12 };
        if (!string.IsNullOrEmpty(request.Subtitle))
        {
            content.Children.Add(new Xaml.Controls.TextBlock
            {
                Text = request.Subtitle,
                TextWrapping = Xaml.TextWrapping.Wrap,
            });
        }
        content.Children.Add(body);

        var dialog = new Xaml.Controls.ContentDialog
        {
            Title = request.Title,
            Content = content,
            XamlRoot = WinWindow.XamlRoot,
            Style = Resource("DefaultContentDialogStyle"),
        };

        // ContentDialog caps its width well under a Large body; lifted for this dialog only.
        dialog.Resources["ContentDialogMaxWidth"] = width + 48;
        dialog.Resources["ContentDialogMaxHeight"] = height + 240;

        var map = Map(request, dialog);

        try
        {
            using var _ = ct.Register(() => dialog.DispatcherQueue.TryEnqueue(dialog.Hide));
            var result = await dialog.ShowAsync();
            return map(result);
        }
        finally
        {
            state.Set(null);
            webView.Handler?.DisconnectHandler();
        }
    }

    /// <summary>
    /// Lays the request's buttons onto a ContentDialog's three, and returns how to read its result.
    /// <para>
    /// The committing button is primary, one ordinary choice is secondary, and cancel is the close
    /// button — which is also what Escape presses, matching what <see cref="DialogButtonRole.Cancel"/>
    /// promises. A dialog with nothing to decide puts its one button there too, so Escape and Done
    /// are the same answer. Return commits only <see cref="DialogRequest.DefaultButton"/>; a
    /// destructive button is never default, because Return is pressed by people who have stopped
    /// reading.
    /// </para>
    /// </summary>
    static Func<Xaml.Controls.ContentDialogResult, string?> Map(DialogRequest request, Xaml.Controls.ContentDialog dialog)
    {
        var buttons = request.ButtonList;
        var cancel = request.CancelButton;

        if (buttons.Count == 1)
        {
            var only = buttons[0];
            dialog.CloseButtonText = only.Label;
            dialog.DefaultButton = Xaml.Controls.ContentDialogButton.Close;
            return _ => only.Id;
        }

        var commit = buttons.FirstOrDefault(b => b.Role is DialogButtonRole.Confirm or DialogButtonRole.Destructive);
        var other = buttons.FirstOrDefault(b => b.Role == DialogButtonRole.Normal);

        // Three is what a ContentDialog has. Anything past that is dropped loudly rather than
        // silently — a fourth choice needs a different body, not a hidden button.
        var placed = new[] { commit, other, cancel }.Where(b => b is not null).Count();
        if (placed < buttons.Count)
            CrashLog.Write($"[dray:dialog] '{request.Title}' declares {buttons.Count} buttons; a ContentDialog shows {placed}.");

        if (commit is not null) dialog.PrimaryButtonText = commit.Label;
        if (other is not null) dialog.SecondaryButtonText = other.Label;
        if (cancel is not null) dialog.CloseButtonText = cancel.Label;

        dialog.DefaultButton = request.DefaultButton is { } d && d == commit
            ? Xaml.Controls.ContentDialogButton.Primary
            : Xaml.Controls.ContentDialogButton.None;

        return result => result switch
        {
            Xaml.Controls.ContentDialogResult.Primary => commit?.Id,
            Xaml.Controls.ContentDialogResult.Secondary => other?.Id,
            _ => cancel?.Id,
        };
    }

    static Xaml.Style? Resource(string key)
        => Xaml.Application.Current.Resources.TryGetValue(key, out var value) ? value as Xaml.Style : null;
}
