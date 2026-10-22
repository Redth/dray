using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace Dray.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        // A WebView app has no visible stack trace: an unhandled exception surfaces as a bare
        // "something went wrong" banner with the cause nowhere anyone can see. These cover the paths
        // that otherwise vanish silently (docs/NATIVE-SHELL.md section 1.7).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Report("UNHANDLED", e.ExceptionObject);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Report("UNOBSERVED TASK", e.Exception);
            e.SetObserved();
        };

        // The WinUI half of what MarshalManagedException covers on macOS: an exception thrown in a
        // XAML callback crosses back into the framework here, and nowhere else reports it.
        UnhandledException += (_, e) => CrashLog.Report("XAML", e.Exception);

        // WebView2 paints white until its first navigation unless told otherwise, and this is read
        // when the WebView is created — before any handler event could set it. Transparent, so the
        // window's Mica shows through from the first frame rather than after a white flash.
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00000000");

        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => WinProgram.CreateMauiApp();
}
