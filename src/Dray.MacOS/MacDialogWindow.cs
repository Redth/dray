using AppKit;
using CoreGraphics;
using Dray.Core.Shell;
using Foundation;

namespace Dray.MacOS;

/// <summary>
/// A dialog in a window of its own (<see cref="DialogSize.Window"/>): an AppKit title bar, a Blazor
/// body that fills the window, and the same AppKit button row a sheet has.
/// <para>
/// For work that needs room — a compose file being written beside its variables — which a sheet
/// cannot give: it hangs from the window behind, is never taller than that window, and blocks it.
/// This is resizable, remembers nothing it does not need to, and leaves the main window usable,
/// so a stack can be looked up there while its file is being written here.
/// </para>
/// </summary>
sealed class MacDialogWindow
{
    /// <summary>AppKit's own window margin, as the sheet uses.</summary>
    const double Margin = 20;

    readonly DialogSession _session;
    readonly NSWindow _window;
    readonly MacDialogBody _body;
    readonly MacDialogButtonRow _buttons;
    readonly WindowDelegate _delegate;

    public MacDialogWindow(IServiceProvider services, DialogSession session)
    {
        _session = session;

        var size = new CGSize(1100, 720);

        _window = new DialogWindow(new CGRect(0, 0, size.Width, size.Height))
        {
            Title = session.Title,
            MinSize = new CGSize(760, 480),
            TabbingMode = NSWindowTabbingMode.Disallowed,
        };

        // Closing the window is Cancel, as Escape is, so the caller always gets an answer.
        _delegate = new WindowDelegate(session);
        _window.Delegate = _delegate;

        _body = new MacDialogBody(services, session, size, ownSession: true);
        _buttons = new MacDialogButtonRow(session, escapeCancels: false);

        var content = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        content.AddSubview(_body.View);
        content.AddSubview(_buttons.View);

        // Edge to edge under the title bar: the body pads itself, and a window's content runs to its
        // sides where a sheet's is inset. The buttons sit in the window's own margin below it.
        NSLayoutConstraint.ActivateConstraints(
        [
            _body.View.TopAnchor.ConstraintEqualTo(content.TopAnchor),
            _body.View.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            _body.View.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),

            _buttons.View.TopAnchor.ConstraintEqualTo(_body.View.BottomAnchor, 12),
            _buttons.View.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, (nfloat)Margin),
            _buttons.View.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, (nfloat)(-Margin)),
            _buttons.View.BottomAnchor.ConstraintEqualTo(content.BottomAnchor, (nfloat)(-Margin)),
        ]);

        _window.ContentView = content;

        Draw();

        _session.Changed += OnChanged;
        _session.Closed += OnClosed;
    }

    public void Present()
    {
        // Over the window it was opened from, offset the way a new document window is.
        if (NSApplication.SharedApplication.MainWindow is { } parent)
        {
            var frame = parent.Frame;
            _window.SetFrameTopLeftPoint(new CGPoint(frame.X + 40, frame.Y + frame.Height - 40));
        }
        else
        {
            _window.Center();
        }

        _window.MakeKeyAndOrderFront(null);
    }

    void Draw()
    {
        _window.Title = _session.Title;
        _window.Subtitle = _session.Subtitle ?? string.Empty;
        _buttons.Draw();
    }

    void OnChanged() => NSApplication.SharedApplication.InvokeOnMainThread(() =>
    {
        if (!_session.IsClosed) Draw();
    });

    void OnClosed(string? _) => NSApplication.SharedApplication.InvokeOnMainThread(() =>
    {
        _session.Changed -= OnChanged;
        _session.Closed -= OnClosed;

        _window.Delegate = null!;
        _window.OrderOut(null);
        _window.Close();
        _body.Dispose();
    });

    /// <summary>The window's close box, answered as a dismissal.</summary>
    sealed class WindowDelegate(DialogSession session) : NSWindowDelegate
    {
        public override bool WindowShouldClose(NSObject sender)
        {
            session.Dismiss();

            // The session closing closes the window, once — not AppKit closing it underneath.
            return false;
        }
    }

    /// <summary>
    /// A document-style window. Unlike a sheet, Escape does not close it: the body is an editor,
    /// which uses Escape itself, and a stray press must not throw away the file being written.
    /// Cancel and the close box do.
    /// </summary>
    sealed class DialogWindow : NSWindow
    {
        public DialogWindow(CGRect frame)
            : base(frame, NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable, NSBackingStore.Buffered, false)
        {
            ReleaseWhenClosed(false);
        }

        [Export("cancelOperation:")]
        public void CancelOperation(NSObject? sender)
        {
        }
    }
}
