using Dray.Core.Engine;
using Dray.Core.Shell;
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
builder.Services.AddSingleton<IContainerRuntimeFactory, DockerRuntimeFactory>();
builder.Services.AddSingleton<EngineManager>();

// No native chrome here, so confirmations are rendered by ConfirmHost.
builder.Services.AddScoped<WebConfirmService>();
builder.Services.AddScoped<IShellBridge, WebShellBridge>();

var app = builder.Build();

// Discover and connect before the first render, so the UI opens on real state rather than an
// empty list that fills in a moment later.
await app.Services.GetRequiredService<EngineManager>().InitializeAsync();

// Serves Dray.Ui's wwwroot under /_content/Dray.Ui/ via the static web assets manifest.
app.MapStaticAssets();
app.UseAntiforgery();

app.MapRazorComponents<DevHostPage>()
    .AddInteractiveServerRenderMode()
    // Pages live in Dray.Ui, so endpoint routing has to be told about that assembly.
    .AddAdditionalAssemblies(typeof(Dray.Ui.App).Assembly);

app.Run();
