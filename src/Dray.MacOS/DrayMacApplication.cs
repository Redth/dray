using Foundation;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.Platforms.MacOS.Hosting;
using Microsoft.Maui.Platforms.MacOS.Platform;

namespace Dray.MacOS;

[Register(nameof(DrayMacApplication))]
public sealed class DrayMacApplication : MacOSMauiApplication
{
    protected override MauiApp CreateMauiApp() => MacProgram.CreateMauiApp();

    /// <summary>
    /// Upstream bug: <c>MacOSMauiApplication.ApplicationDidBecomeActive</c> calls
    /// <c>IWindow.Activated()</c> unconditionally, and MAUI throws if the window is already
    /// active — which it is whenever the app re-activates, including on the very first launch
    /// once the WebView takes focus.
    /// <para>
    /// The throw crosses back into Objective-C, which aborts the activation sequence and leaves
    /// the Blazor content dead behind an "unhandled error" banner. Swallowing precisely this
    /// exception is the same workaround MAUI.Sherpa carries.
    /// </para>
    /// </summary>
    [Export("applicationDidBecomeActive:")]
    public new void ApplicationDidBecomeActive(NSNotification notification)
    {
        try
        {
            base.ApplicationDidBecomeActive(notification);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already activated", StringComparison.Ordinal))
        {
        }
    }
}
