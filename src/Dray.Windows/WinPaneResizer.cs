using Microsoft.Maui.Storage;
using Microsoft.UI.Input;
using Xaml = Microsoft.UI.Xaml;

namespace Dray.WinUI;

/// <summary>
/// The sidebar's edge, which the user drags to set its width, as in File Explorer.
/// <para>
/// <c>NavigationView</c> has a width but no way to change it by hand. This is a thin strip along the
/// content area's leading edge, which is where the open pane ends, with the resize cursor. It is
/// only there while the pane is open beside the content: a compact rail or an overlay pane has no
/// edge to drag. The width is remembered across launches.
/// </para>
/// </summary>
public sealed class WinPaneResizer : Xaml.Controls.Grid
{
    /// <summary>Wide enough for the longest entry, with no room to spare.</summary>
    public const double DefaultWidth = 220;

    /// <summary>
    /// How wide the grab area is. The WebView starts after it rather than under it: over the
    /// WebView, WebView2 sets the pointer for its own area and the resize cursor never showed.
    /// </summary>
    public const double GripWidth = 6;

    const double Narrowest = 180;
    const double Widest = 360;
    const string Key = "dray.sidebar.width";

    readonly Xaml.Controls.NavigationView _nav;
    double _startX;
    double _startWidth;
    bool _dragging;

    /// <summary>The width the pane should open at: the user's last choice, or the default.</summary>
    public static double SavedWidth => Clamp(Preferences.Default.Get(Key, DefaultWidth));

    public WinPaneResizer(Xaml.Controls.NavigationView nav)
    {
        _nav = nav;

        Width = GripWidth;
        HorizontalAlignment = Xaml.HorizontalAlignment.Left;

        // Hit-testable without painting anything.
        Background = new Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        Xaml.Automation.AutomationProperties.SetName(this, "Resize sidebar");

        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += (_, e) => End(e.Pointer);
        PointerCaptureLost += (_, e) => End(e.Pointer);

        nav.DisplayModeChanged += (_, _) => UpdateVisibility();
        nav.PaneOpened += (_, _) => UpdateVisibility();
        nav.PaneClosed += (_, _) => UpdateVisibility();
        UpdateVisibility();
    }

    void UpdateVisibility() => Visibility =
        _nav.DisplayMode == Xaml.Controls.NavigationViewDisplayMode.Expanded && _nav.IsPaneOpen
            ? Xaml.Visibility.Visible
            : Xaml.Visibility.Collapsed;

    void OnPressed(object sender, Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!CapturePointer(e.Pointer)) return;

        _dragging = true;
        _startX = e.GetCurrentPoint(null).Position.X;
        _startWidth = _nav.OpenPaneLength;
        e.Handled = true;
    }

    void OnMoved(object sender, Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        _nav.OpenPaneLength = Clamp(_startWidth + (e.GetCurrentPoint(null).Position.X - _startX));
        e.Handled = true;
    }

    void End(Xaml.Input.Pointer pointer)
    {
        if (!_dragging) return;

        _dragging = false;
        ReleasePointerCapture(pointer);
        Preferences.Default.Set(Key, _nav.OpenPaneLength);
    }

    static double Clamp(double width) => Math.Clamp(Math.Round(width), Narrowest, Widest);
}
