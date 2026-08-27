using Dray.Core.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Platforms.MacOS.Essentials;
using Microsoft.Maui.Platforms.MacOS.Handlers;
using Microsoft.Maui.Platforms.MacOS.Hosting;
#if DEBUG
using Microsoft.Maui.DevFlow.Agent;
using Microsoft.Maui.DevFlow.Blazor;
#endif

namespace Dray.MacOS;

public static class MacProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiAppMacOS<DrayMacApp>()
            .AddMacOSBlazorWebView()
            .AddMacOSEssentials();

        // MacOSFlyoutPage.SetUseNativeSidebar is inert without this: the default
        // FlyoutPageHandler has no NSSplitViewController to configure, so the flag is read by
        // nobody and you silently get MAUI's own flyout instead of a real NSOutlineView.
        builder.ConfigureMauiHandlers(handlers =>
            handlers.AddHandler<FlyoutPage, NativeSidebarFlyoutPageHandler>());

        // AddMacOSBlazorWebView above registers the platform handler; this registers the Blazor
        // services the components actually resolve (NavigationManager, IJSRuntime, and friends).
        // Both are required — with only the first, the app boots and then dies on the first
        // component that injects NavigationManager.
        builder.Services.AddMauiBlazorWebView();

        // AppKit draws the sidebar, toolbar, menus and dialogs; Blazor must not draw its own.
        builder.Services.AddSingleton(ShellCapabilities.Native(debug: IsDebug));
        builder.Services.AddSingleton<IShellState, ShellState>();
        builder.Services.AddSingleton<IShellBridge, MacShellBridge>();
        builder.Services.AddSingleton<IPlatformTheme, MacTheme>();

        builder.Services.AddSingleton<MacShellReadySignal>();
        builder.Services.AddSingleton<IShellReadySignal>(sp => sp.GetRequiredService<MacShellReadySignal>());

#if DEBUG
        // Without a provider, a Blazor startup exception surfaces only as "an unhandled error"
        // in the WebView with nothing in the terminal. Run the .app's binary directly to read it.
        builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Debug);

        // MAUI DevFlow. Both packages are referenced in Debug only, and neither self-starts:
        // without these calls the agent never listens and `dotnet maui devflow` cannot connect.
        //   dotnet maui devflow ui status
        //   dotnet maui devflow ui screenshot --output shot.png
        //   dotnet maui devflow webview snapshot
        builder.AddMauiDevFlowAgent();
        builder.AddMauiBlazorDevFlowTools();
#endif

        return builder.Build();
    }

    static bool IsDebug =>
#if DEBUG
        true;
#else
        false;
#endif
}
