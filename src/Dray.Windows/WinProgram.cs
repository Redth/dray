using Dray.Core.Kubernetes;
using Dray.Kubernetes;
using Dray.Core.Engine;
using Dray.Core.Shell;
using Dray.Docker;
using Dray.Ui.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;
#if DEBUG
using Microsoft.Maui.DevFlow.Agent;
using Microsoft.Maui.DevFlow.Blazor;
#endif

namespace Dray.WinUI;

public static class WinProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder.UseMauiApp<DrayWinApp>();

        // The shell is a WinUI NavigationView with the WebView inside it. A custom handler rather
        // than MAUI's FlyoutPage, whose Windows pane holds a MAUI page instead of real navigation
        // items — the sidebar would be a web list drawn in a native frame.
        builder.ConfigureMauiHandlers(handlers =>
        {
            handlers.AddHandler<NativeShell, NativeShellHandler>();
            handlers.AddHandler<TitleBarNavigation, TitleBarNavigationHandler>();
            handlers.AddHandler<TitleBarPageLead, TitleBarPageLeadHandler>();
            handlers.AddHandler<TitleBarPageActions, TitleBarPageActionsHandler>();
        });

        // The Blazor services components resolve: NavigationManager, IJSRuntime and the rest.
        builder.Services.AddMauiBlazorWebView();

        // WinUI draws the sidebar, toolbar and dialogs; Blazor must not draw its own.
        builder.Services.AddSingleton(ShellCapabilities.Native(debug: IsDebug));
        builder.Services.AddSingleton<IShellState, ShellState>();
        builder.Services.AddSingleton<IShellBridge, WinShellBridge>();

        // The request a native dialog is presenting, read by the body in its own WebView.
        builder.Services.AddSingleton<NativeDialogState>();
        builder.Services.AddSingleton<IPlatformTheme, WinTheme>();
        builder.Services.AddSingleton<WinEnginePicker>();
        builder.Services.AddSingleton<IChromePickerSource>(sp => sp.GetRequiredService<EnginePicker>());
        builder.Services.AddSingleton<WinTitleBar>();
        builder.Services.AddSingleton<WinTitleBarToolbar>();

        // The engine layer, same registrations as the other heads — less Apple's runtime, which has
        // no engine to find on this OS.
        builder.Services.AddSingleton<IDockerConfigSource, SystemDockerConfigSource>();
        builder.Services.AddSingleton<DockerContextReader>();
        builder.Services.AddSingleton<DockerDesktop>();
        builder.Services.AddSingleton<IHostSelectionStore, FileHostSelectionStore>();
        builder.Services.AddSingleton<IHostNameStore, FileHostNameStore>();
        builder.Services.AddSingleton<WindowsSetupReader>();
        builder.Services.AddSingleton<MacSetupReader>();
        builder.Services.AddSingleton<LinuxSetupReader>();
        builder.Services.AddSingleton<SetupStepRunner>();
        builder.Services.AddSingleton<WslAccount>();
        builder.Services.AddSingleton<DockerCliContext>();
        builder.Services.AddSingleton<IContainerRuntimeFactory>(
            new CompositeRuntimeFactory(new DockerRuntimeFactory()));
        // Stacks beyond what is running: remembered ones and the stacks folder. Paths only — see
        // StackRegistry for why nothing else is written. Missing here, the Stacks page could not
        // be constructed at all on this head: it injects StackOperations and StackActionService.
        builder.Services.AddSingleton(new StackRegistry(Path.Combine(DrayPaths.ConfigDirectory, "stacks.json")));
        builder.Services.AddSingleton<StackCatalog>();
        builder.Services.AddSingleton<EngineManager>();
        // The local cluster beside the engine (docs/KUBERNETES.md): its own manager, its own store,
        // its own remembered choice — the engine picker and the cluster picker are separate.
        builder.Services.AddSingleton<KubeconfigReader>();
        builder.Services.AddSingleton<ClusterSelectionStore>();
        builder.Services.AddSingleton<IClusterClientFactory, KubeClusterClientFactory>();
        builder.Services.AddSingleton<ClusterManager>();
        builder.Services.AddSingleton(new StackHistory(Path.Combine(DrayPaths.ConfigDirectory, "history.json")));
        builder.Services.AddSingleton<StackOperations>();

        // Singleton like the bridge it confirms through: the native sidebar's picker uses it, and
        // the sidebar outlives any page.
        builder.Services.AddSingleton<HostSwitcher>();
        builder.Services.AddSingleton<EnginePicker>();
        builder.Services.AddSingleton<KubeView>();

        // Scoped: it navigates, and the navigator belongs to the WebView.
        builder.Services.AddScoped<KubeActions>();
        // Scoped: it holds one live stream per watched container and must go away with the view
        // that asked for them.
        builder.Services.AddScoped<StatsHub>();
        builder.Services.AddScoped<PruneService>();
        builder.Services.AddScoped<StackActionService>();
        builder.Services.AddSingleton<RegistryReader>();
        // The registry's own API, for what the engine cannot answer: tags, and what each was built for.
        builder.Services.AddSingleton<RegistryBrowser>();

        builder.Services.AddSingleton<WinShellReadySignal>();
        builder.Services.AddSingleton<IShellReadySignal>(sp => sp.GetRequiredService<WinShellReadySignal>());

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Debug);

        // MAUI DevFlow. Neither package self-starts: without these calls the agent never listens.
        //   dotnet maui devflow -ap 9241 ui status
        //   dotnet maui devflow -ap 9241 webview Runtime evaluate -- "<js>"
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
