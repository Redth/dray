using Microsoft.Maui.ApplicationModel;
using Xaml = Microsoft.UI.Xaml;

namespace Dray.WinUI;

/// <summary>The app's one WinUI window, and the UI thread it lives on.</summary>
public static class WinWindow
{
    /// <summary>The native window, or null before MAUI has created it.</summary>
    public static Xaml.Window? Current =>
        Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Xaml.Window;

    /// <summary>
    /// The window handle, for the WinRT APIs that predate WinUI 3 and must be told which window
    /// owns them — pickers above all, which otherwise fail in an unpackaged app.
    /// </summary>
    public static nint Handle => Current is { } window ? WinRT.Interop.WindowNative.GetWindowHandle(window) : 0;

    /// <summary>The <c>XamlRoot</c> a <c>ContentDialog</c> has to be given to know where to appear.</summary>
    public static Xaml.XamlRoot? XamlRoot => Current?.Content?.XamlRoot;

    /// <summary>Whether the window is the one in front, for deciding whether a notification is noise.</summary>
    public static bool IsActive { get; private set; } = true;

    /// <summary>The MAUI context dialogs build their WebView from. Per window, so never from DI.</summary>
    public static Microsoft.Maui.IMauiContext? MauiContext =>
        Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.MauiContext;

    internal static void TrackActivation(Xaml.Window window)
        => window.Activated += (_, e) => IsActive = e.WindowActivationState != Xaml.WindowActivationState.Deactivated;

    public static Task<T> OnUiAsync<T>(Func<Task<T>> work) => MainThread.InvokeOnMainThreadAsync(work);

    public static Task OnUiAsync(Action work) => MainThread.InvokeOnMainThreadAsync(work);
}
