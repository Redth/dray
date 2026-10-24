using AppKit;
using CoreGraphics;
using Dray.Core.Shell;
using Foundation;

namespace Dray.MacOS;

/// <summary>
/// A dialog on macOS: an <c>NSPanel</c> sheet with an AppKit title, a Blazor body, and an AppKit
/// button row.
/// <para>
/// Not an <c>NSAlert</c>, which is what this was first. An alert closes itself on any button press
/// and lays its buttons out once, so a form could not validate, could not stay open while the engine
/// worked, and a guided flow could not change its buttons between steps — which is why every form
/// in the app had ended up drawing its own buttons in HTML instead. docs/NATIVE-SHELL.md section 4.3
/// names this as the frame for "a body that is more than a form", and every body is now that.
/// </para>
/// <para>
/// What the alert did for free is done here with the same AppKit pieces, not imitated: the default
/// button is an <c>NSButton</c> whose key equivalent is Return, so AppKit draws it in the accent and
/// presses it; Cancel's is Escape; a destructive one is marked with <c>hasDestructiveAction</c>; and
/// it is still a sheet on the document window.
/// </para>
/// </summary>
public sealed class MacDialogSheet(IServiceProvider services)
{
    public Task<string?> ShowAsync(DialogSession session, CancellationToken ct = default)
    {
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            try
            {
                if (session.Request.Size == DialogSize.Window) new MacDialogWindow(services, session).Present();
                else new MacDialogFrame(services, session).Present();
            }
            catch (Exception ex)
            {
                // A dialog that cannot be presented must not leave its caller awaiting forever.
                Console.Error.WriteLine($"[dray:dialog] {ex}");
                session.Dismiss();
            }
        });

        // The window going away dismisses the sheet with it, and the caller gets a dismissal rather
        // than a task that never completes.
        if (ct.CanBeCanceled)
            ct.Register(() => NSApplication.SharedApplication.InvokeOnMainThread(session.Dismiss));

        return session.Completion;
    }
}

/// <summary>
/// One presented sheet, and the AppKit controls that draw its session.
/// </summary>
sealed class MacDialogFrame
{
    /// <summary>Space between the sheet's edge and everything in it — AppKit's own sheet margin.</summary>
    const double Margin = 20;

    /// <summary>
    /// How far the WebView reaches past the margin on every side, and how far the body is padded
    /// inside it — the <c>--s2</c> padding on <c>.surface</c> in DialogSurface.razor.css. A focused
    /// control draws its ring outside its own box, up to four pixels out, and a WebView clips at its
    /// edge; the padding gives the ring room, and widening the view by the same amount keeps the
    /// content aligned with the AppKit title and buttons. The two numbers must stay equal.
    /// </summary>
    const double Inset = 8;

    /// <summary>The shortest body worth drawing, before the content has said how tall it is.</summary>
    const double MinBody = 48;

    readonly IServiceProvider _services;
    readonly DialogSession _session;
    readonly DialogPanel _panel;
    readonly MacDialogBody _body;
    readonly NSTextField _title;
    readonly NSTextField _subtitle;
    readonly MacDialogButtonRow _buttons;
    readonly NSStackView _row;
    readonly NSLayoutConstraint _bodyHeight;
    readonly double _width;

    NSWindow? _parent;
    bool _presented;

    public MacDialogFrame(IServiceProvider services, DialogSession session)
    {
        _services = services;
        _session = session;
        _width = WidthOf(session.Request.Size);

        var bodyWidth = _width - 2 * (Margin - Inset);
        var initial = InitialHeightOf(session.Request.Size);

        _panel = new DialogPanel(session, new CGRect(0, 0, _width, 200));

        _title = NSTextField.CreateLabel(session.Title);
        _title.Font = NSFont.BoldSystemFontOfSize(NSFont.SystemFontSize)!;
        _title.LineBreakMode = NSLineBreakMode.TruncatingTail;

        _subtitle = NSTextField.CreateWrappingLabel(session.Subtitle ?? string.Empty);
        _subtitle.Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize)!;
        _subtitle.TextColor = NSColor.SecondaryLabel;
        _subtitle.PreferredMaxLayoutWidth = (nfloat)(_width - 2 * Margin);

        _body = new MacDialogBody(services, session, new CGSize(bodyWidth, initial));

        _buttons = new MacDialogButtonRow(session);
        _row = _buttons.View;

        var header = NSStackView.FromViews([_title, _subtitle]);
        header.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        header.Alignment = NSLayoutAttribute.Leading;
        header.Spacing = 4;
        header.TranslatesAutoresizingMaskIntoConstraints = false;

        var content = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        content.AddSubview(header);
        content.AddSubview(_body.View);
        content.AddSubview(_row);

        _bodyHeight = _body.View.HeightAnchor.ConstraintEqualTo((nfloat)initial);

        NSLayoutConstraint.ActivateConstraints(
        [
            content.WidthAnchor.ConstraintEqualTo((nfloat)_width),

            header.TopAnchor.ConstraintEqualTo(content.TopAnchor, (nfloat)Margin),
            header.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, (nfloat)Margin),
            header.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, (nfloat)(-Margin)),

            // The gaps AppKit's own sheets use — 12 under the title, 16 above the buttons —
            // measured to the content, not to the WebView, which reaches Inset past it.
            _body.View.TopAnchor.ConstraintEqualTo(header.BottomAnchor, (nfloat)(12 - Inset)),
            _body.View.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, (nfloat)(Margin - Inset)),
            _body.View.WidthAnchor.ConstraintEqualTo((nfloat)bodyWidth),
            _bodyHeight,

            _row.TopAnchor.ConstraintEqualTo(_body.View.BottomAnchor, (nfloat)(16 - Inset)),
            _row.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, (nfloat)Margin),
            _row.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, (nfloat)(-Margin)),
            _row.BottomAnchor.ConstraintEqualTo(content.BottomAnchor, (nfloat)(-Margin)),
        ]);

        _panel.ContentView = content;

        DrawTitle();
        _buttons.Draw();

        _session.Changed += OnChanged;
        _session.Closed += OnClosed;
    }

    /// <summary>Widths in points. A form, a guided flow, or something being read.</summary>
    static double WidthOf(DialogSize size) => size switch
    {
        DialogSize.Small => 440,
        DialogSize.Large => 720,
        _ => 560,
    };

    /// <summary>What the body is drawn at before it has measured itself — never seen if it measures in time.</summary>
    static double InitialHeightOf(DialogSize size) => size switch
    {
        DialogSize.Small => 120,
        DialogSize.Large => 420,
        _ => 240,
    };

    public void Present()
    {
        // Key, then main, then whatever there is. A window that is not key is still the window this
        // dialog is about.
        _parent = NSApplication.SharedApplication.KeyWindow
            ?? NSApplication.SharedApplication.MainWindow
            ?? (Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as NSWindow);

        if (_parent is null)
        {
            // Nothing to attach a sheet to. A free-standing panel rather than RunModal, which would
            // block the main thread the body's own WebView needs to render.
            Resize(animate: false);
            _panel.Center();
            _panel.MakeKeyAndOrderFront(null);
            _presented = true;
            return;
        }

        // Shown once the body has measured itself, so the sheet slides out at its real height
        // instead of sliding out and then jumping. Bounded: a body that never reports still opens.
        if (_session.ContentHeight is null)
        {
            NSTimer.CreateScheduledTimer(0.4, false, _ => BeginSheet());
            return;
        }

        BeginSheet();
    }

    void BeginSheet()
    {
        if (_presented || _session.IsClosed || _parent is null) return;

        _presented = true;
        Resize(animate: false);

        _parent.BeginSheet(_panel, _ => { });
    }

    void OnChanged() => NSApplication.SharedApplication.InvokeOnMainThread(() =>
    {
        if (_session.IsClosed) return;

        DrawTitle();
        _buttons.Draw();

        if (!_presented && _session.ContentHeight is not null)
        {
            BeginSheet();
            return;
        }

        Resize(animate: _presented);
    });

    void OnClosed(string? _) => NSApplication.SharedApplication.InvokeOnMainThread(() =>
    {
        _session.Changed -= OnChanged;
        _session.Closed -= OnClosed;

        if (_presented && _panel.SheetParent is { } parent) parent.EndSheet(_panel);
        _panel.OrderOut(null);

        _body.Dispose();
    });

    void DrawTitle()
    {
        _title.StringValue = _session.Title;

        // The panel's own title is never drawn on a sheet, but it is what VoiceOver announces.
        _panel.Title = _session.Title;

        var subtitle = _session.Subtitle ?? string.Empty;
        _subtitle.StringValue = subtitle;
        _subtitle.Hidden = subtitle.Length == 0;
    }

    /// <summary>
    /// Fit the sheet to its content: the body as tall as it measured, up to what the window can hold.
    /// Anything taller scrolls inside the body rather than pushing the buttons off the screen.
    /// </summary>
    void Resize(bool animate)
    {
        var wanted = _session.ContentHeight is { } measured
            ? measured + 2 * Inset
            : InitialHeightOf(_session.Request.Size);

        var available = (_parent?.ContentLayoutRect.Height ?? 800) - 180;
        var height = Math.Clamp(wanted, MinBody, Math.Max(MinBody, available));

        if (Math.Abs(_bodyHeight.Constant - height) >= 1) _bodyHeight.Constant = (nfloat)height;

        _panel.ContentView!.LayoutSubtreeIfNeeded();

        var fitting = _panel.ContentView.FittingSize;
        var frame = _panel.FrameRectFor(new CGRect(0, 0, _width, fitting.Height));
        var current = _panel.Frame;

        // A sheet hangs from the top of its window, so it grows downwards: the top edge stays put.
        var next = new CGRect(current.X, current.Y + current.Height - frame.Height, _width, frame.Height);
        if (Math.Abs(next.Height - current.Height) < 1 && _presented) return;

        _panel.SetFrame(next, display: true, animate: animate);
    }
}

/// <summary>
/// A dialog's AppKit button row, drawn from its session: shared by the sheet and the window, which
/// differ in everything around it and nothing in it.
/// </summary>
sealed class MacDialogButtonRow
{
    readonly DialogSession _session;
    readonly Dictionary<string, NSButton> _buttons = new(StringComparer.Ordinal);
    readonly NSProgressIndicator _spinner = new()
    {
        Style = NSProgressIndicatorStyle.Spinning,
        ControlSize = NSControlSize.Small,
        Indeterminate = true,
    };

    IReadOnlyList<DialogButton> _drawn = [];
    readonly bool _escapeCancels;

    /// <param name="escapeCancels">
    /// Whether Escape presses Cancel. A sheet's yes; a window holding an editor's no — the editor
    /// uses Escape itself, and one stray press would throw away a file being written.
    /// </param>
    public MacDialogButtonRow(DialogSession session, bool escapeCancels = true)
    {
        _session = session;
        _escapeCancels = escapeCancels;
        View = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Spacing = 12,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
    }

    public NSStackView View { get; }

    /// <summary>
    /// Redraw the button row from the session. Buttons are kept by id and updated in place, so a
    /// button that is only changing its label or its enabled state does not flicker or lose focus.
    /// </summary>
    public void Draw()
    {
        var buttons = _session.Buttons;
        if (buttons.SequenceEqual(_drawn)) return;

        _drawn = buttons;

        foreach (var stale in _buttons.Keys.Except(buttons.Select(b => b.Id)).ToList())
        {
            _buttons[stale].RemoveFromSuperview();
            _buttons.Remove(stale);
        }

        // Return presses the default only while it can be pressed. A disabled default keeps no
        // key equivalent, so Return does nothing rather than beeping at a form that is not ready.
        var commit = _session.DefaultButton?.Id;

        foreach (var spec in buttons)
        {
            if (!_buttons.TryGetValue(spec.Id, out var button))
            {
                var id = spec.Id;

                button = new NSButton
                {
                    BezelStyle = NSBezelStyle.Push,
                    ControlSize = NSControlSize.Regular,
                };

                button.Activated += (_, _) => _ = _session.PressAsync(id);
                _buttons[id] = button;
            }

            button.Title = spec.Label;
            button.Enabled = spec.Pressable;
            button.HasDestructiveAction = spec.Role == DialogButtonRole.Destructive;
            button.KeyEquivalent = spec.Id == commit ? "\r"
                : spec.Role == DialogButtonRole.Cancel && _escapeCancels ? "\u001b"
                : string.Empty;
        }

        // Leading buttons at the leading edge in the order given; the rest at the trailing edge in
        // the order every web and GTK dialog also uses — cancel, the others, then the commit
        // rightmost, which is where AppKit's own alerts put it.
        var leading = buttons.Where(b => b.Leading).Select(b => (NSView)_buttons[b.Id]).ToArray();
        var trailing = DialogRequest.Order(buttons, DialogButtonOrder.CommitLast)
            .Select(b => (NSView)_buttons[b.Id])
            .ToList();

        // The platform's progress indicator, beside the row, while anything in it is working.
        if (buttons.Any(b => b.Busy))
        {
            trailing.Insert(0, _spinner);
            _spinner.Hidden = false;
            _spinner.StartAnimation(null);
        }
        else
        {
            _spinner.StopAnimation(null);
            _spinner.Hidden = true;
        }

        View.SetViews(leading, NSStackViewGravity.Leading);
        View.SetViews([.. trailing], NSStackViewGravity.Trailing);
    }

}

/// <summary>
/// The sheet's window. Exists for one override: Escape anywhere inside it — including in the
/// WebView, which passes an unhandled Escape up the responder chain as cancelOperation — is a
/// dismissal, exactly as it is for an alert.
/// </summary>
sealed class DialogPanel : NSPanel
{
    readonly DialogSession _session;

    public DialogPanel(DialogSession session, CGRect frame)
        : base(frame, NSWindowStyle.Titled, NSBackingStore.Buffered, false)
    {
        _session = session;
        ReleaseWhenClosed(false);
    }

    public override bool CanBecomeKeyWindow => true;

    [Export("cancelOperation:")]
    public void CancelOperation(NSObject? sender) => _session.Dismiss();
}
