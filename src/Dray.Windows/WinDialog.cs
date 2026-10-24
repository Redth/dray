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
        // No second window on this head yet: the largest dialog the window can hold.
        DialogSize.Window => (1000, 640),
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

    public async Task<string?> ShowAsync(DialogSession session, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await WinWindow.OnUiAsync(() => PresentAsync(session, ct));
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

    async Task<string?> PresentAsync(DialogSession session, CancellationToken ct)
    {
        var request = session.Request;
        var context = WinWindow.MauiContext
            ?? throw new InvalidOperationException("No window to present a dialog from.");

        // Set before the body exists: the surface reads it as it mounts, and a session arriving
        // afterwards would render an empty dialog for a frame.
        var state = services.GetRequiredService<NativeDialogState>();
        state.Set(session);

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

        // Seeded before the first paint. The body's own PlatformThemeSync keeps it in step after
        // that, but it runs once Blazor is up, and the first frame would otherwise be the
        // generated palette rather than the Windows one around it.
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

        // Always present and hidden when empty, because a body can give itself a subtitle on a
        // later step and the frame has to have somewhere to put it.
        var subtitle = new Xaml.Controls.TextBlock
        {
            TextWrapping = Xaml.TextWrapping.Wrap,
            Margin = new Xaml.Thickness(0, titleGap, 0, 0),
        };

        var content = new Xaml.Controls.StackPanel();
        content.Children.Add(subtitle);
        content.Children.Add(body);

        var dialog = new Xaml.Controls.ContentDialog
        {
            Title = session.Title,
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

        if (request.ButtonList.Any(b => b.Role == DialogButtonRole.Destructive)) WinDanger.Style(dialog);

        // The body drives the title and the buttons, and decides what a press does; the frame is
        // closed when the session closes, however that happens. See DialogSession.
        using var live = new LiveFrame(dialog, session, subtitle);

        try
        {
            using var _ = ct.Register(() => dialog.DispatcherQueue.TryEnqueue(() =>
            {
                session.Dismiss();
                dialog.Hide();
            }));

            await dialog.ShowAsync();

            // Whatever closed the frame — a press the body accepted, Escape, the window going
            // away — the session has settled by now, and its result is the answer.
            session.Dismiss();
            return session.Result;
        }
        finally
        {
            state.Set(null);
            webView.Handler?.DisconnectHandler();
        }
    }

    /// <summary>
    /// A <see cref="DialogSession"/> projected onto a ContentDialog.
    /// <para>
    /// A ContentDialog has three buttons, and a session can have more and change them between
    /// steps. The slots are reassigned on every change: the committing button is primary, cancel is
    /// the close button — also what Escape presses — and the secondary slot takes a leading button
    /// (a Back) if there is one, else the first ordinary choice. Anything left over is not shown
    /// and is logged, never silently swapped for something else; every body's leftovers are a
    /// second way to the same place (Add host tests before it saves, so Test is a convenience).
    /// A dialog whose only button is a Done puts it in the close slot, so Escape and Done agree.
    /// </para>
    /// <para>
    /// Presses never hold the button event open. Each click is cancelled — the frame stays up — and
    /// handed to the session, which runs the body's work for as long as it takes and closes the
    /// dialog itself when the body says so. That is what lets Cancel stop a pull: no deferral is
    /// pending, so the close button is live the whole time.
    /// </para>
    /// </summary>
    sealed class LiveFrame : IDisposable
    {
        readonly Xaml.Controls.ContentDialog _dialog;
        readonly DialogSession _session;
        readonly Xaml.Controls.TextBlock _subtitle;

        /// <summary>The dialog's queue, taken on the UI thread rather than read off the dialog later.</summary>
        readonly Microsoft.UI.Dispatching.DispatcherQueue _queue;

        DialogButton? _primary;
        DialogButton? _secondary;
        DialogButton? _close;
        string _logged = "";

        public LiveFrame(Xaml.Controls.ContentDialog dialog, DialogSession session, Xaml.Controls.TextBlock subtitle)
        {
            _dialog = dialog;
            _session = session;
            _subtitle = subtitle;
            _queue = dialog.DispatcherQueue;

            Apply();

            session.Changed += OnChanged;
            session.Closed += OnClosed;
            dialog.PrimaryButtonClick += OnPrimary;
            dialog.SecondaryButtonClick += OnSecondary;
            dialog.CloseButtonClick += OnClose;
            dialog.Closing += OnClosing;
        }

        /// <summary>Redraw the title, subtitle and slots from the session. UI thread only.</summary>
        void Apply()
        {
            _dialog.Title = _session.Title;
            _subtitle.Text = _session.Subtitle ?? string.Empty;
            _subtitle.Visibility = string.IsNullOrEmpty(_session.Subtitle)
                ? Xaml.Visibility.Collapsed
                : Xaml.Visibility.Visible;

            var buttons = _session.Buttons;

            if (buttons.Count == 1)
            {
                (_primary, _secondary, _close) = (null, null, buttons[0]);
            }
            else
            {
                _primary = buttons.FirstOrDefault(b => b.Role is DialogButtonRole.Confirm or DialogButtonRole.Destructive);
                _secondary = buttons.FirstOrDefault(b => b.Leading)
                    ?? buttons.FirstOrDefault(b => b.Role == DialogButtonRole.Normal);
                _close = DialogRequest.CancelOf(buttons);
            }

            _dialog.PrimaryButtonText = _primary?.Label ?? string.Empty;
            _dialog.IsPrimaryButtonEnabled = _primary?.Pressable ?? false;
            _dialog.SecondaryButtonText = _secondary?.Label ?? string.Empty;
            _dialog.IsSecondaryButtonEnabled = _secondary?.Pressable ?? false;
            _dialog.CloseButtonText = _close?.Label ?? string.Empty;

            _dialog.DefaultButton = _session.DefaultButton is { } d && d.Id == _primary?.Id
                ? Xaml.Controls.ContentDialogButton.Primary
                : _close is not null && buttons.Count == 1
                    ? Xaml.Controls.ContentDialogButton.Close
                    : Xaml.Controls.ContentDialogButton.None;

            // Said once per distinct row rather than on every redraw: a body re-declares its
            // buttons after each render, and a busy one renders often.
            var placed = new[] { _primary, _secondary, _close }.Where(b => b is not null).Select(b => b!.Id).ToHashSet();
            var dropped = string.Join(", ", buttons.Where(b => !placed.Contains(b.Id)).Select(b => b.Id));
            if (dropped.Length > 0 && dropped != _logged)
                CrashLog.Write($"[dray:dialog] '{_session.Title}' has more buttons than a ContentDialog shows; not shown: {dropped}.");
            _logged = dropped;
        }

        void OnChanged() => Enqueue(Apply);

        /// <summary>
        /// The session closed — a press the body accepted, the body closing itself, or a dismissal.
        /// Arrives on whatever thread the body's work resumed on, so the frame is hidden through
        /// the queue, and a failure to reach it is logged rather than thrown back into the body.
        /// </summary>
        void OnClosed(string? _) => Enqueue(_dialog.Hide);

        void Enqueue(Microsoft.UI.Dispatching.DispatcherQueueHandler action)
        {
            try
            {
                _queue.TryEnqueue(action);
            }
            catch (Exception ex)
            {
                CrashLog.Write($"[dray:dialog] '{_session.Title}': {ex}");
            }
        }

        void OnPrimary(Xaml.Controls.ContentDialog sender, Xaml.Controls.ContentDialogButtonClickEventArgs e) => Press(e, _primary);

        void OnSecondary(Xaml.Controls.ContentDialog sender, Xaml.Controls.ContentDialogButtonClickEventArgs e) => Press(e, _secondary);

        void OnClose(Xaml.Controls.ContentDialog sender, Xaml.Controls.ContentDialogButtonClickEventArgs e) => Press(e, _close);

        /// <summary>
        /// Every click keeps the frame up; the session decides, and closes it when the body agrees.
        /// </summary>
        void Press(Xaml.Controls.ContentDialogButtonClickEventArgs e, DialogButton? button)
        {
            e.Cancel = true;
            if (button is null) return;

            _ = PressAsync(button.Id);
        }

        async Task PressAsync(string id)
        {
            try
            {
                await _session.PressAsync(id);
            }
            catch (Exception ex)
            {
                // The body failed to answer. Staying open keeps whatever it was showing on screen,
                // which is better than closing on an error nobody saw.
                CrashLog.Write($"[dray:dialog] '{_session.Title}' {id}: {ex}");
            }
        }

        /// <summary>
        /// Every close passes through here. After a press or a body's own close the session has
        /// settled and the frame is going because it was told to; what is left is Escape, which
        /// is a dismissal — the way out of a dialog is never something the body gets to refuse.
        /// </summary>
        void OnClosing(Xaml.Controls.ContentDialog sender, Xaml.Controls.ContentDialogClosingEventArgs e)
        {
            if (!_session.IsClosed) _session.Dismiss();
        }

        public void Dispose()
        {
            _session.Changed -= OnChanged;
            _session.Closed -= OnClosed;
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
