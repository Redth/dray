using System.Runtime.InteropServices;
using Xaml = Microsoft.UI.Xaml;

namespace Dray.WinUI;

/// <summary>
/// Keeps the window off screen until it has something real to show.
/// <para>
/// A WinUI window is shown as soon as MAUI activates it, before XAML has drawn a frame or the Mica
/// backdrop has arrived, and for that gap Windows paints the client area white: measured at
/// 125–250 ms on every launch, in dark mode as much as light. Cloaking the window with DWM keeps it
/// composed but invisible, and it is uncloaked once the shell has loaded and drawn two frames — so
/// the first frame anyone sees is the sidebar on Mica, with the loading ring where the page will be.
/// </para>
/// <para>
/// A cloaked window is still shown, focused and in the taskbar; only the pixels are withheld. The
/// timeout uncloaks regardless, so a shell that never loads still leaves a window on screen.
/// </para>
/// </summary>
static class WinFirstFrame
{
    const int DwmwaCloak = 13;

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static void HoldUntilDrawn(Xaml.Window window, Func<Xaml.FrameworkElement?> shell)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (!Cloak(hwnd, true)) return;

        var frames = 0;
        var released = false;
        var timer = window.DispatcherQueue.CreateTimer();

        void Release()
        {
            if (released) return;
            released = true;

            Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            timer.Stop();
            Cloak(hwnd, false);
        }

        // The frame the shell first appears in can still be mid-layout; the one after it is the
        // finished shell.
        void OnRendering(object? sender, object e)
        {
            if (shell() is { IsLoaded: true } && ++frames >= 2) Release();
        }

        Xaml.Media.CompositionTarget.Rendering += OnRendering;

        timer.Interval = Timeout;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Release();
        timer.Start();
    }

    static bool Cloak(nint hwnd, bool cloak)
    {
        var value = cloak ? 1 : 0;
        return DwmSetWindowAttribute(hwnd, DwmwaCloak, ref value, sizeof(int)) == 0;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
