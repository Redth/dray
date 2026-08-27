using Docker.DotNet;
using Docker.DotNet.Models;
using WirePortBinding = Docker.DotNet.Models.PortBinding;
using Dray.Core.Engine;
using Dray.Core.Model;

namespace Dray.Docker;

/// <summary>Creating a container from an image and starting it.</summary>
public static class DockerRun
{
    public static async Task<string> RunAsync(
        DockerClient client, RunRequest request, CancellationToken ct = default)
    {
        var created = await CreateAsync(client, request, ct).ConfigureAwait(false);

        if (request.Start)
        {
            try
            {
                await client.Containers
                    .StartContainerAsync(created, new ContainerStartParameters(), ct)
                    .ConfigureAwait(false);
            }
            catch (DockerApiException ex)
            {
                // The container exists but will not run — a port already in use, a missing bind
                // source. Leaving it behind would litter the list with a container the user did
                // not knowingly create and cannot tell apart from a real one.
                await TryRemoveAsync(client, created).ConfigureAwait(false);

                throw new RuntimeConnectionException(Explain(ex), ex);
            }
        }

        return created;
    }

    static async Task<string> CreateAsync(DockerClient client, RunRequest request, CancellationToken ct)
    {
        var parameters = new CreateContainerParameters
        {
            Image = request.Image,
            Name = request.Name,

            Env = [.. request.Environment.Select(e => $"{e.Key}={e.Value}")],

            // Declaring the port on the container is separate from publishing it on the host, and
            // an image that does not EXPOSE a port still needs this or the binding is ignored.
            ExposedPorts = request.Ports.ToDictionary(
                p => $"{p.ContainerPort}/{p.Protocol}",
                _ => new EmptyStruct()),

            HostConfig = new HostConfig
            {
                PortBindings = request.Ports
                    .GroupBy(p => $"{p.ContainerPort}/{p.Protocol}")
                    .ToDictionary(
                        g => g.Key,
                        g => (IList<WirePortBinding>)[.. g.Select(p => new WirePortBinding
                        {
                            HostPort = p.HostPort.ToString(),
                        })]),

                Binds = [.. request.Mounts.Select(Bind)],
            },
        };

        try
        {
            var created = await client.Containers.CreateContainerAsync(parameters, ct).ConfigureAwait(false);
            return created.ID;
        }
        catch (DockerApiException ex)
        {
            throw new RuntimeConnectionException(Explain(ex), ex);
        }
    }

    /// <summary>
    /// One mount, in the engine's <c>source:destination[:ro]</c> form.
    /// <para>
    /// A named volume and a host path are the same string to the engine, which decides between them
    /// on the leading slash — the same rule <see cref="RunParser.ParseMounts"/> applies, so the two
    /// cannot disagree about what the user typed.
    /// </para>
    /// </summary>
    static string Bind(Dray.Core.Model.MountPoint mount)
        => $"{mount.Source}:{mount.Destination}" + (mount.ReadOnly ? ":ro" : string.Empty);

    static async Task TryRemoveAsync(DockerClient client, string id)
    {
        try
        {
            await client.Containers
                .RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cleaning up after a failure must not replace the failure's own message with its own.
        }
    }

    /// <summary>
    /// The engine's message, in the user's terms.
    /// <para>
    /// The raw ones are usable but buried: the port conflict arrives as a sentence about
    /// <c>bind: address already in use</c> wrapped in an <c>userland proxy</c> prefix, and the
    /// name conflict names a container id nobody recognises.
    /// </para>
    /// </summary>
    internal static string Explain(DockerApiException ex)
    {
        var message = ex.ResponseBody ?? string.Empty;

        // The body is a JSON envelope with one field worth reading.
        if (message.Contains("\"message\"", StringComparison.Ordinal))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(message);
                if (document.RootElement.TryGetProperty("message", out var inner))
                    message = inner.GetString() ?? message;
            }
            catch (System.Text.Json.JsonException)
            {
                // Not the envelope after all; the raw body is still the best thing to show.
            }
        }

        // Podman wraps its own failures in a sentence and escapes the newline inside it, so the
        // raw string reaches the user as: something went wrong with the request: "proxy already
        // running\n". Both the wrapper and the escape are noise.
        const string wrapper = "something went wrong with the request:";
        var wrapped = message.IndexOf(wrapper, StringComparison.OrdinalIgnoreCase);
        if (wrapped >= 0) message = message[(wrapped + wrapper.Length)..];

        message = message
            .Replace("\\n", " ", StringComparison.Ordinal)
            .Trim()
            .Trim('"')
            .Trim();

        // Each engine words a port conflict differently and none of them says "port". Docker
        // Desktop reports the bind failure, the Linux daemon reports the allocation, and podman
        // reports its own proxy — all three mean the same thing to the person who typed 8080.
        if (message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            || message.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase)
            || message.Contains("proxy already running", StringComparison.OrdinalIgnoreCase)
            || message.Contains("bind: permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return "That host port is already in use. Pick another, or stop whatever is holding it.";
        }

        if (message.Contains("is already in use by container", StringComparison.OrdinalIgnoreCase))
            return "A container with that name already exists.";

        if (message.Contains("No such image", StringComparison.OrdinalIgnoreCase))
            return "That image is not on this host. Pull it first.";

        return message.Length == 0 ? "The engine refused to create the container." : message;
    }
}
