using Dray.Core.Engine;
using Dray.Core.Model;
using Dray.Core.Shell;

namespace Dray.Ui.Components;

/// <summary>
/// The short sentences the Hosts page and the picker say about a host. In one place so the list,
/// the detail card and the add-a-host flow describe the same host the same way.
/// </summary>
public static class HostFacts
{
    /// <summary>What kind of host this is, as a glyph. Never a vendor's logo — DESIGN.md 10.</summary>
    public static IconRef Icon(DockerHost host) => host.Endpoint.Scheme switch
    {
        EndpointScheme.Ssh => IconRef.Terminal,
        EndpointScheme.Tcp => IconRef.Link,
        EndpointScheme.Wsl => IconRef.Terminal,
        EndpointScheme.AppleContainer => IconRef.Container,
        _ => IconRef.Host,
    };

    /// <summary>
    /// The engine in a few words — "Docker 29.7.2 · Linux" — or where it is when nothing has
    /// answered yet. An engine that has not been asked is not described as if it had.
    /// </summary>
    public static string Summary(DockerHost host)
    {
        if (host.Capabilities.EngineVersion is not { } version) return host.Endpoint.Display;

        List<string> parts = [$"{Flavor(host.Capabilities.Flavor)} {version}"];

        if (host.Capabilities.ContainerOs is ContainerOs.Linux) parts.Add("Linux");
        else if (host.Capabilities.ContainerOs is ContainerOs.Windows) parts.Add("Windows");

        if (host.Capabilities.IsRootless) parts.Add("rootless");

        return string.Join(" · ", parts);
    }

    public static string Flavor(EngineFlavor flavor) => flavor switch
    {
        EngineFlavor.Docker => "Docker",
        EngineFlavor.Podman => "Podman",
        EngineFlavor.Apple => "Apple container",
        _ => "Engine",
    };

    /// <summary>Where Dray learned about the host, as the user would say it.</summary>
    public static string Source(DockerHost host) => host.Origin switch
    {
        HostOrigin.DockerContext => $"Docker context “{host.Id}”",
        HostOrigin.Environment => "DOCKER_HOST in the environment",
        HostOrigin.WslDistro => "WSL2 distribution",
        HostOrigin.AppleContainer => "container on your PATH",
        _ => "Found on this machine",
    };

    /// <summary>
    /// The machine behind the engine — "8 CPUs · 16 GB · arm64". Null when the engine reported
    /// none of it, rather than a row of dashes (DESIGN.md 8.2).
    /// </summary>
    public static string? Machine(RuntimeCapabilities caps)
    {
        List<string> parts = [];

        if (caps.TotalCpus is { } cpus) parts.Add(cpus == 1 ? "1 CPU" : $"{cpus} CPUs");
        if (caps.TotalMemoryBytes is { } memory) parts.Add($"{Humanize.Bytes(memory)} memory");
        if (caps.Architecture is { Length: > 0 } arch) parts.Add(arch);

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// What to call an engine's containers. Spelled out rather than shown as "linux"/"windows",
    /// because the question a user is answering here is "is this the mode I meant to be in".
    /// </summary>
    public static string? ContainerOsLabel(ContainerOs os) => os switch
    {
        ContainerOs.Windows => "Windows containers",
        ContainerOs.Linux => "Linux containers",
        _ => null,
    };
}
