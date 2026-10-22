using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace Dray.WinUI;

/// <summary>
/// Dray's entry point, written rather than generated so it can decide one thing before WinUI
/// starts: whether this is the only Dray.
/// <para>
/// With the window able to close to the notification area, launching Dray again is how people look
/// for a window they cannot see, and a second process would bring a second tray icon, a second
/// connection to every engine and a second copy of every notification. A later launch hands its
/// activation to the running one, which brings its window back, and exits.
/// </para>
/// <para>
/// The rest is what WinUI's generated Main does: COM wrappers, then the application on a thread
/// whose synchronization context is its dispatcher.
/// </para>
/// </summary>
public static class Program
{
    const string InstanceKey = "Dray";

    [STAThread]
    static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (HandedToRunningInstance()) return 0;

        Microsoft.UI.Xaml.Application.Start(start =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });

        return 0;
    }

    static bool HandedToRunningInstance()
    {
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (main.IsCurrent)
        {
            // Raised on a background thread; WinTray marshals to the window's own.
            main.Activated += (_, _) => WinTray.Current?.ShowWindow();
            return false;
        }

        // Windows only lets a process bring itself forward if the one in front allows it, and the
        // one in front is this one, having just been launched.
        AllowSetForegroundWindow(main.ProcessId);

        // Not awaited on this thread: it is the STA the application would have run on, and the
        // redirection must not wait on a thread that pumps nothing.
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        Task.Run(async () => await main.RedirectActivationToAsync(activation)).Wait();
        return true;
    }

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(uint processId);
}
