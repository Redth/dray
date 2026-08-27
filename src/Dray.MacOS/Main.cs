using AppKit;

namespace Dray.MacOS;

public static class MainClass
{
    static void Main(string[] args)
    {
        // A WebView app has no visible stack trace: an unhandled exception surfaces as a bare
        // "something went wrong" banner with the cause nowhere the user or developer can see.
        // These three cover the paths that otherwise vanish silently.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report("UNHANDLED", e.ExceptionObject);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report("UNOBSERVED TASK", e.Exception);
            e.SetObserved();
        };

        // Managed exceptions crossing back into Objective-C are the ones most likely to be lost.
        ObjCRuntime.Runtime.MarshalManagedException += (_, e) =>
            Report("MARSHAL MANAGED", e.Exception);

        NSApplication.Init();
        NSApplication.SharedApplication.Delegate = new DrayMacApplication();
        NSApplication.Main(args);
    }

    static void Report(string kind, object? error)
        => Console.Error.WriteLine($"[dray:{kind}] {error}");
}
