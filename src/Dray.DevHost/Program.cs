using Dray.Core.Kubernetes;
using Dray.Kubernetes;
using Dray.Core.Engine;
using Dray.Core.Shell;
using Dray.Apple;
using Dray.Docker;
using Dray.Ui.Services;
using Dray.DevHost.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// The browser draws its own sidebar and toolbar; there is no native chrome here.
builder.Services.AddSingleton(ShellCapabilities.Web(debug: true));
builder.Services.AddScoped<IShellState, ShellState>();
builder.Services.AddSingleton<IShellReadySignal, NoOpShellReadySignal>();
builder.Services.AddSingleton<IPlatformTheme, GeneratedPaletteTheme>();

// The engine layer. Singleton because the event stream and its store are per-application, not
// per-connection: switching browser tab must not tear down and re-seed the connection.
builder.Services.AddSingleton<IDockerConfigSource, SystemDockerConfigSource>();
builder.Services.AddSingleton<DockerContextReader>();

// Engine setup for every platform, and CLI-context help. Registered everywhere rather than behind an
// OS check: each reads only its own platform, and a service that exists only on one platform is one the
// pages have to guard around.
builder.Services.AddSingleton<WindowsSetupReader>();
builder.Services.AddSingleton<MacSetupReader>();
builder.Services.AddSingleton<LinuxSetupReader>();
builder.Services.AddSingleton<SetupStepRunner>();
builder.Services.AddSingleton<WslAccount>();
builder.Services.AddSingleton<DockerCliContext>();
builder.Services.AddSingleton<DockerDesktop>();
builder.Services.AddSingleton<IHostSelectionStore, FileHostSelectionStore>();
builder.Services.AddSingleton<IHostNameStore, FileHostNameStore>();
// Two engines behind one seam. The composite dispatches on the endpoint's scheme, so an Apple
// host in the picker gets Apple's runtime and everything else gets Docker's — and no page above
// this line knows either exists.
builder.Services.AddSingleton<IContainerRuntimeFactory>(
    new CompositeRuntimeFactory(new AppleRuntimeFactory(), new DockerRuntimeFactory()));
// Stacks beyond what is running: remembered ones and the stacks folder. Paths only — see
// StackRegistry for why nothing else is written.
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
// Scoped: it holds one live stream per watched container and must go away with the view
// that asked for them.
builder.Services.AddScoped<StatsHub>();
builder.Services.AddScoped<PruneService>();
builder.Services.AddScoped<StackActionService>();
builder.Services.AddSingleton<RegistryReader>();
// The registry's own API, for what the engine cannot answer: tags, and what each was built for.
builder.Services.AddSingleton<RegistryBrowser>();

// No native chrome here, so confirmations are rendered by ConfirmHost.
builder.Services.AddScoped<WebConfirmService>();
builder.Services.AddScoped<WebDialogService>();
builder.Services.AddScoped<IShellBridge, WebShellBridge>();

// Scoped because the bridge it confirms through is.
builder.Services.AddScoped<HostSwitcher>();
builder.Services.AddScoped<EnginePicker>();
builder.Services.AddSingleton<KubeView>();
builder.Services.AddScoped<KubeActions>();

var app = builder.Build();

// Discover and connect before the first render, so the UI opens on real state rather than an
// empty list that fills in a moment later.
await app.Services.GetRequiredService<EngineManager>().InitializeAsync();

// The cluster in the background: a kubeconfig naming a cluster that is not running must not hold
// the page back by the connect timeout.
_ = app.Services.GetRequiredService<ClusterManager>().InitializeAsync();

// Serves Dray.Ui's wwwroot under /_content/Dray.Ui/ via the static web assets manifest.
app.MapStaticAssets();
app.UseAntiforgery();

app.MapRazorComponents<DevHostPage>()
    .AddInteractiveServerRenderMode()
    // Pages live in Dray.Ui, so endpoint routing has to be told about that assembly.
    .AddAdditionalAssemblies(typeof(Dray.Ui.App).Assembly);

app.Run();
