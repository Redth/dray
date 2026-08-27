using Dray.Core.Shell;
using Dray.DevHost.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// The browser draws its own sidebar and toolbar; there is no native chrome here.
builder.Services.AddSingleton(ShellCapabilities.Web(debug: true));
builder.Services.AddScoped<IShellState, ShellState>();

var app = builder.Build();

// Serves Dray.Ui's wwwroot under /_content/Dray.Ui/ via the static web assets manifest.
app.MapStaticAssets();
app.UseAntiforgery();

app.MapRazorComponents<DevHostPage>()
    .AddInteractiveServerRenderMode()
    // Pages live in Dray.Ui, so endpoint routing has to be told about that assembly.
    .AddAdditionalAssemblies(typeof(Dray.Ui.App).Assembly);

app.Run();
