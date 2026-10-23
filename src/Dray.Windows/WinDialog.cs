using System.Globalization;
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

    const string EscapeMessage = "dray:dialog-escape";

    /// <summary>
    /// Keys a ContentDialog handles that never reach it from a WebView body. Chromium consumes
    /// every key while the body has focus, so Escape did nothing at all once a field had been typed
    /// into. Unhandled by the page (a combobox closing its list claims it first), it is passed to
    /// the host, which closes the dialog the way Escape would: through <c>Closing</c>, where the
    /// body can still refuse. The body's first field is focused once it renders, which is what
    /// showModal() did for the web dialog this replaced.
    /// </summary>
    const string BodyKeys = $$"""
        addEventListener('keydown', e => {
          if (e.key === 'Escape' && !e.defaultPrevented) window.chrome.webview.postMessage('{{EscapeMessage}}');
        });
        (() => {
          const field = () => document.querySelector(
            '[autofocus], input:not([type=hidden]):not([disabled]):not([readonly]), textarea:not([disabled]), select:not([disabled])');
          const focus = el => { if (!document.activeElement || document.activeElement === document.body) el.focus(); };
          const observer = new MutationObserver(() => {
            const el = field();
            if (el) { observer.disconnect(); focus(el); }
          });
          addEventListener('DOMContentLoaded', () => {
            const el = field();
            if (el) focus(el); else observer.observe(document.body, { childList: true, subtree: true });
            setTimeout(() => observer.disconnect(), 5000);
          });
        })();
        """;

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
                dialog.RequestedTheme = ShellTheme;
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

        // The body runs edge to edge between the title and the button row, and the dialog's own
        // margins become the scroller's padding inside it (BodyInsets).
        var (width, height) = SizeOf(request.Size);
        var pad = ThicknessResource("ContentDialogPadding") ?? new Xaml.Thickness(24);
        var titleGap = ThicknessResource("ContentDialogTitleMargin")?.Bottom ?? 12;

        // The dialog's document has no PlatformThemeSync of its own, so without this the body
        // renders in the generated palette rather than the Windows one around it.
        var theme = services.GetRequiredService<IPlatformTheme>();
        WebViewSetup.SeedTheme(webView, theme, BodyInsets(pad, titleGap));

        Xaml.Controls.ContentDialog? shown = null;
        webView.BlazorWebViewInitialized += (_, e) =>
        {
            var core = e.WebView.CoreWebView2;
            _ = core.AddScriptToExecuteOnDocumentCreatedAsync(BodyKeys);
            core.WebMessageReceived += (_, m) =>
            {
                if (m.TryGetWebMessageAsString() == EscapeMessage) shown?.Hide();
            };
        };

        var body = (Xaml.FrameworkElement)webView.ToPlatform(context);

        // A transparent WebView inside a dialog would show the window behind the dialog, and the
        // default ground WinUI substitutes is the button row's grey, not the white of the section
        // the body sits in. Its ground is that section's colour instead: WinUI resolves this
        // resource as the browser's base when the controller is created, which is still to come.
        if (theme is WinTheme win && win.DialogBodyGround() is { } ground && body is Xaml.Controls.WebView2 platform)
            platform.Resources["SolidBackgroundFillColorBase"] = ground;

        // Out through the section's padding to its edges: the dialog's sides, and the separator
        // above the button row. The section keeps its padding for the title, so the body is
        // pulled out by the same amount rather than the padding being taken away. Measured in the
        // same terms as before, so a size's content area is the width it always was.
        body.Width = width + pad.Left + pad.Right;
        body.Height = height + titleGap + pad.Bottom;
        body.Margin = new Xaml.Thickness(-pad.Left, 0, -pad.Right, -pad.Bottom);

        var content = new Xaml.Controls.StackPanel();
        if (!string.IsNullOrEmpty(request.Subtitle))
        {
            content.Children.Add(new Xaml.Controls.TextBlock
            {
                Text = request.Subtitle,
                TextWrapping = Xaml.TextWrapping.Wrap,
                Margin = new Xaml.Thickness(0, titleGap, 0, 0),
            });
        }
        content.Children.Add(body);

        var dialog = new Xaml.Controls.ContentDialog
        {
            Title = request.Title,
            Content = content,
            XamlRoot = WinWindow.XamlRoot,
            Style = Resource("DefaultContentDialogStyle"),
            RequestedTheme = ShellTheme,
        };

        // ContentDialog caps its width well under a Large body; lifted for this dialog only.
        dialog.Resources["ContentDialogMaxWidth"] = body.Width;
        dialog.Resources["ContentDialogMaxHeight"] = height + 240;

        // The gap under the title is the body's top inset now, inside the scroller: content
        // scrolls up to the title rather than stopping a gap short of it.
        dialog.Resources["ContentDialogTitleMargin"] = new Xaml.Thickness(0);

        shown = dialog;

        // The keyboard goes to the body, as it does when a web dialog opens with showModal(): the
        // ContentDialog would otherwise keep it on its own first control, and typing would go
        // nowhere until the field was clicked.
        dialog.Opened += (_, _) => body.Focus(Xaml.FocusState.Programmatic);

        var map = Map(request, dialog);
        if (request.ButtonList.Any(b => b.Role == DialogButtonRole.Destructive)) WinDanger.Style(dialog);

        // A form's body drives the buttons and decides what a press does; a plain question closes
        // on the first one. See DialogController.
        using var live = request.Controller is { } controller ? new LiveButtons(dialog, request, controller) : null;

        try
        {
            using var _ = ct.Register(() => dialog.DispatcherQueue.TryEnqueue(() =>
            {
                request.Controller?.Dismissed();
                dialog.Hide();
            }));

            var result = await dialog.ShowAsync();
            return request.Controller is { } c ? c.Result : map(result);
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

        var (commit, other) = Slots(request);

        // Three is what a ContentDialog has. Anything past that is dropped loudly rather than
        // silently — a fourth choice needs a different body, not a hidden button.
        var placed = new[] { commit, other, cancel }.Where(b => b is not null).Count();
        if (placed < buttons.Count)
            CrashLog.Write($"[dray:dialog] '{request.Title}' declares {buttons.Count} buttons; a ContentDialog shows {placed}.");

        if (commit is not null) dialog.PrimaryButtonText = request.Current(commit).Label;
        if (other is not null) dialog.SecondaryButtonText = request.Current(other).Label;
        if (cancel is not null) dialog.CloseButtonText = request.Current(cancel).Label;

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

    /// <summary>Which request button sits in the primary slot and which in the secondary.</summary>
    static (DialogButton? Commit, DialogButton? Other) Slots(DialogRequest request) => (
        request.ButtonList.FirstOrDefault(b => b.Role is DialogButtonRole.Confirm or DialogButtonRole.Destructive),
        request.ButtonList.FirstOrDefault(b => b.Role == DialogButtonRole.Normal));

    /// <summary>
    /// A <see cref="DialogController"/> projected onto a ContentDialog's three buttons.
    /// <para>
    /// Each click holds the button event open with a deferral while the body decides, and cancels
    /// it when the body keeps the dialog open — which is how a Pull button starts a pull without the
    /// dialog going away. Escape arrives as a close with no button, and is put to the body as its
    /// cancel button, so a body that cannot abandon running work can refuse it. The close button has
    /// no enabled state of its own; the controller refuses a press of a disabled one instead.
    /// </para>
    /// </summary>
    sealed class LiveButtons : IDisposable
    {
        readonly Xaml.Controls.ContentDialog _dialog;
        readonly DialogRequest _request;
        readonly DialogController _controller;

        /// <summary>The dialog's queue, taken on the UI thread rather than read off the dialog later.</summary>
        readonly Microsoft.UI.Dispatching.DispatcherQueue _queue;

        /// <summary>A button press is being answered by the body; set and read on the UI thread.</summary>
        bool _pressing;

        readonly DialogButton? _commit;
        readonly DialogButton? _other;
        readonly DialogButton? _cancel;

        public LiveButtons(Xaml.Controls.ContentDialog dialog, DialogRequest request, DialogController controller)
        {
            _dialog = dialog;
            _request = request;
            _controller = controller;
            _queue = dialog.DispatcherQueue;
            (_commit, _other) = Slots(request);
            _cancel = request.ButtonList.Count == 1 ? request.ButtonList[0] : request.CancelButton;

            Apply();

            controller.Changed += OnChanged;
            controller.CloseRequested += OnCloseRequested;
            dialog.PrimaryButtonClick += OnPrimary;
            dialog.SecondaryButtonClick += OnSecondary;
            dialog.CloseButtonClick += OnClose;
            dialog.Closing += OnClosing;
        }

        void Apply()
        {
            if (_commit is not null)
            {
                var state = _request.Current(_commit);
                _dialog.PrimaryButtonText = state.Label;
                _dialog.IsPrimaryButtonEnabled = state.IsEnabled;
            }

            if (_other is not null)
            {
                var state = _request.Current(_other);
                _dialog.SecondaryButtonText = state.Label;
                _dialog.IsSecondaryButtonEnabled = state.IsEnabled;
            }

            if (_cancel is not null) _dialog.CloseButtonText = _request.Current(_cancel).Label;
        }

        void OnChanged() => _queue.TryEnqueue(Apply);

        /// <summary>
        /// The body closing the dialog itself.
        /// <para>
        /// During a press, the press closes the frame when it returns (<see cref="Press"/>). That is
        /// the common case — a sign-in or a create that closes once its work has succeeded — and
        /// it arrives on whatever thread the body's work resumed on, where queueing to the UI
        /// thread threw: the throw went back up into the body's close, and a sign-in that had worked
        /// stayed on screen. Outside a press, the frame is hidden through the queue, and a failure to
        /// reach it is logged rather than thrown at the body.
        /// </para>
        /// </summary>
        void OnCloseRequested()
        {
            if (_pressing) return;

            try
            {
                _queue.TryEnqueue(_dialog.Hide);
            }
            catch (Exception ex)
            {
                CrashLog.Write($"[dray:dialog] '{_request.Title}' close: {ex}");
            }
        }

        void OnPrimary(Xaml.Controls.ContentDialog sender, Xaml.Controls.ContentDialogButtonClickEventArgs e) => Press(e, _commit);

        void OnSecondary(Xaml.Controls.ContentDialog sender, Xaml.Controls.ContentDialogButtonClickEventArgs e) => Press(e, _other);

        void OnClose(Xaml.Controls.ContentDialog sender, Xaml.Controls.ContentDialogButtonClickEventArgs e) => Press(e, _cancel);

        async void Press(Xaml.Controls.ContentDialogButtonClickEventArgs e, DialogButton? button)
        {
            if (button is null) return;

            var deferral = e.GetDeferral();
            _pressing = true;
            try
            {
                // A body that finishes its work and closes itself during the press (a sign-in, once
                // the helper has stored it) answers "stay open", because it has already closed. A
                // settled controller never holds the frame open: the press closes it.
                e.Cancel = !await _controller.PressAsync(button.Id) && !_controller.IsClosed;
            }
            catch (Exception ex)
            {
                // The body failed to answer. Staying open keeps whatever it was showing on screen,
                // which is better than closing on an error nobody saw.
                CrashLog.Write($"[dray:dialog] '{_request.Title}' {button.Id}: {ex}");
                e.Cancel = true;
            }
            finally
            {
                _pressing = false;
                deferral.Complete();
            }
        }

        /// <summary>
        /// Every close passes through here. After a press or a body's own close the controller has
        /// already settled; what is left is Escape.
        /// </summary>
        async void OnClosing(Xaml.Controls.ContentDialog sender, Xaml.Controls.ContentDialogClosingEventArgs e)
        {
            if (_controller.IsClosed) return;

            if (_cancel is null)
            {
                _controller.Dismissed();
                return;
            }

            var deferral = e.GetDeferral();
            try
            {
                e.Cancel = !await _controller.PressAsync(_cancel.Id);
            }
            catch (Exception ex)
            {
                CrashLog.Write($"[dray:dialog] '{_request.Title}' escape: {ex}");
                e.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        }

        public void Dispose()
        {
            _controller.Changed -= OnChanged;
            _controller.CloseRequested -= OnCloseRequested;
            _dialog.PrimaryButtonClick -= OnPrimary;
            _dialog.SecondaryButtonClick -= OnSecondary;
            _dialog.CloseButtonClick -= OnClose;
            _dialog.Closing -= OnClosing;
        }
    }

    /// <summary>
    /// The theme the window is drawn in. A ContentDialog lives in the popup layer, outside the
    /// window root that MAUI's <c>UserAppTheme</c> sets <c>RequestedTheme</c> on, so it follows the
    /// application's theme, the system's, instead. With the app dark and the system light, every
    /// dialog came up light over a dark window, its danger button in light colours.
    /// </summary>
    static Xaml.ElementTheme ShellTheme =>
        (WinWindow.Current?.Content as Xaml.FrameworkElement)?.ActualTheme ?? Xaml.ElementTheme.Default;

    /// <summary>
    /// The dialog's margins, as the properties <c>DialogSurface</c> pads its scroller by. Top is
    /// the gap the title keeps above its content, moved inside the scroller.
    /// </summary>
    static Dictionary<string, string> BodyInsets(Xaml.Thickness pad, double titleGap) => new()
    {
        ["dialog-inset-top"] = Px(titleGap),
        ["dialog-inset-right"] = Px(pad.Right),
        ["dialog-inset-bottom"] = Px(pad.Bottom),
        ["dialog-inset-left"] = Px(pad.Left),
    };

    static string Px(double value) => string.Create(CultureInfo.InvariantCulture, $"{value:0.##}px");

    static Xaml.Style? Resource(string key)
        => Xaml.Application.Current.Resources.TryGetValue(key, out var value) ? value as Xaml.Style : null;

    /// <summary>
    /// A margin WinUI's theme defines, read rather than written down here, so the body follows a
    /// ContentDialog whose padding changes. Null if the resource is missing.
    /// </summary>
    static Xaml.Thickness? ThicknessResource(string key)
        => Xaml.Application.Current.Resources.TryGetValue(key, out var value) && value is Xaml.Thickness t ? t : null;
}
