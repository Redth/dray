using Dray.Core.Engine;

var cli = new DockerCliContext();
var before = await cli.CurrentAsync();
Console.WriteLine($"context before : {before}");

// The host discovery would produce for this machine's desktop-linux context.
var host = new DockerHost
{
    Id = "desktop-linux",
    Name = "desktop-linux",
    Endpoint = DockerEndpoint.Parse($"unix://{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/.docker/run/docker.sock")!,
};

var error = await cli.UseAsync(host);
Console.WriteLine($"use error      : {error ?? "(none)"}");
Console.WriteLine($"context after  : {await cli.CurrentAsync()}");

// A relayed host cannot be targeted, and must say so rather than failing at the CLI.
var wsl = host with { Id = "wsl:Ubuntu", Name = "Ubuntu", Endpoint = DockerEndpoint.Parse("wsl://Ubuntu")! };
Console.WriteLine($"wsl refusal    : {await cli.UseAsync(wsl)}");
