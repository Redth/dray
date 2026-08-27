using System.Globalization;
using Docker.DotNet.Models;
using Dray.Core.Model;

// Inside namespace Dray.Docker the identifier `Docker` binds to this assembly's own namespace, so
// a qualified `Docker.DotNet.Models.X` cannot resolve. These name the two sides explicitly, which
// is needed anyway: PortBinding, MountPoint and NetworkAttachment all exist in both models.
using Wire = global::Docker.DotNet.Models;

namespace Dray.Docker;

/// <summary>
/// Turns the engine's inspect response into <see cref="ContainerInspect"/>.
/// <para>
/// Almost all of this is unwrapping shapes the API chose for its own reasons: environment as a
/// list of <c>KEY=value</c> strings, ports as a dictionary keyed by <c>"8080/tcp"</c>, timestamps
/// as RFC3339 strings that are present-but-zero when the event never happened.
/// </para>
/// </summary>
public static class DockerInspect
{
    public static ContainerInspect Map(ContainerInspectResponse c, string rawJson)
    {
        var state = c.State;
        var config = c.Config;

        return new ContainerInspect
        {
            Id = c.ID,
            Name = c.Name?.TrimStart('/') ?? c.ID,
            Image = config?.Image ?? c.Image,
            ImageId = c.Image,
            Created = DockerTime.From(c.Created),
            StartedAt = ParseTimestamp(state?.StartedAt),
            FinishedAt = FinishedAt(state),
            State = MapState(state),
            Health = MapHealth(state?.Health),
            ExitCode = state?.ExitCode is { } code and not 0 ? (int)code : state?.Running == false ? 0 : null,
            OomKilled = state?.OOMKilled ?? false,
            Error = string.IsNullOrWhiteSpace(state?.Error) ? null : state.Error,
            Pid = state?.Pid is > 0 ? (int)state.Pid : null,
            Entrypoint = config?.Entrypoint?.ToArray() ?? [],
            Command = config?.Cmd?.ToArray() ?? [],
            WorkingDirectory = Blank(config?.WorkingDir),
            User = Blank(config?.User),
            Environment = MapEnvironment(config?.Env),
            Ports = MapPorts(config?.ExposedPorts, c.NetworkSettings?.Ports),
            Mounts = MapMounts(c.Mounts),
            Networks = MapNetworks(c.NetworkSettings?.Networks),
            Labels = config?.Labels is { Count: > 0 } labels
                ? new Dictionary<string, string>(labels, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal),
            RestartPolicy = MapRestartPolicy(c.HostConfig?.RestartPolicy),
            RestartCount = (int)c.RestartCount,
            RawJson = rawJson,
        };
    }

    static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// The engine writes "0001-01-01T00:00:00Z" for an event that has not happened, which would
    /// otherwise render as a real date in the year one.
    /// </summary>
    static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return null;

        return parsed.Year <= 1 ? null : parsed;
    }

    /// <summary>
    /// When the container last stopped, or null if it is still going.
    /// <para>
    /// The engine always sends a FinishedAt, even for a container that is running — podman reports
    /// the previous run's, a moment <i>before</i> the current StartedAt. Rendering that verbatim
    /// puts a "finished" time on a healthy container, which reads as though it had crashed.
    /// </para>
    /// </summary>
    static DateTimeOffset? FinishedAt(State? state)
    {
        if (state is null) return null;
        if (state.Running || state.Restarting || state.Paused) return null;

        var finished = ParseTimestamp(state.FinishedAt);
        if (finished is null) return null;

        // A finish that precedes the start belongs to an earlier run, whatever the engine says.
        return ParseTimestamp(state.StartedAt) is { } started && finished <= started ? null : finished;
    }

    /// <summary>
    /// The inspect response carries booleans rather than the list endpoint's single status string,
    /// and more than one can be set at once — a restarting container is also running.
    /// </summary>
    static DockerState MapState(State? state)
    {
        if (state is null) return DockerState.Unknown;

        // Ordered by specificity: the narrower fact wins over the broader one.
        if (state.Restarting) return DockerState.Restarting;
        if (state.Paused) return DockerState.Paused;
        if (state.Dead) return DockerState.Dead;
        if (state.Running) return DockerState.Running;

        return state.Status?.ToLowerInvariant() switch
        {
            "created" => DockerState.Created,
            "exited" => DockerState.Exited,
            "removing" => DockerState.Removing,
            _ => DockerState.Exited,
        };
    }

    static DockerHealth MapHealth(Health? health) => health?.Status?.ToLowerInvariant() switch
    {
        "healthy" => DockerHealth.Healthy,
        "unhealthy" => DockerHealth.Unhealthy,
        "starting" => DockerHealth.Starting,
        _ => DockerHealth.None,
    };

    /// <summary>
    /// Environment arrives as <c>KEY=value</c> strings. A value may itself contain '=', so only
    /// the first one separates.
    /// </summary>
    internal static IReadOnlyList<EnvVar> MapEnvironment(IList<string>? env)
    {
        if (env is null || env.Count == 0) return [];

        var result = new List<EnvVar>(env.Count);

        foreach (var entry in env)
        {
            if (string.IsNullOrEmpty(entry)) continue;

            var split = entry.IndexOf('=');

            // A bare name with no '=' is legal and means an empty value.
            result.Add(split < 0
                ? new EnvVar(entry, "")
                : new EnvVar(entry[..split], entry[(split + 1)..]));
        }

        // Alphabetical: the engine's order reflects how the image was layered, which is not an
        // order anyone reads in.
        return [.. result.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Ports come from two places that must be merged. <c>Config.ExposedPorts</c> is every port the
    /// image declares; <c>NetworkSettings.Ports</c> is which of those reach the host. A port in the
    /// first and not the second is unpublished — the usual reason a service is unreachable, and so
    /// the single most useful thing this tab can show.
    /// </summary>
    internal static IReadOnlyList<ExposedPort> MapPorts(
        IDictionary<string, EmptyStruct>? exposed,
        IDictionary<string, IList<Wire.PortBinding>>? published)
    {
        var ports = new Dictionary<(int Port, string Protocol), List<Core.Model.PortBinding>>();

        foreach (var key in exposed?.Keys ?? [])
        {
            if (ParsePortKey(key) is { } parsed) ports.TryAdd(parsed, []);
        }

        foreach (var (key, bindings) in published ?? new Dictionary<string, IList<Wire.PortBinding>>())
        {
            if (ParsePortKey(key) is not { } parsed) continue;

            if (!ports.TryGetValue(parsed, out var list)) ports[parsed] = list = [];

            foreach (var binding in bindings ?? [])
            {
                // An entry with no HostPort is the engine saying "declared, not mapped".
                if (!int.TryParse(binding?.HostPort, out var hostPort) || hostPort <= 0) continue;

                var mapped = new Core.Model.PortBinding(hostPort, parsed.Port, parsed.Protocol);
                if (!list.Contains(mapped)) list.Add(mapped);
            }
        }

        return
        [
            .. ports
                .OrderBy(p => p.Key.Port)
                .ThenBy(p => p.Key.Protocol, StringComparer.Ordinal)
                .Select(p => new ExposedPort(
                    p.Key.Port,
                    p.Key.Protocol,
                    [.. p.Value.OrderBy(b => b.HostPort)])),
        ];
    }

    /// <summary>Parses the API's <c>"8080/tcp"</c> port key. Bare <c>"8080"</c> means tcp.</summary>
    internal static (int Port, string Protocol)? ParsePortKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        var slash = key.IndexOf('/');
        var portPart = slash < 0 ? key : key[..slash];
        var protocol = slash < 0 ? "tcp" : key[(slash + 1)..];

        return int.TryParse(portPart, out var port) && port > 0
            ? (port, protocol.Length == 0 ? "tcp" : protocol.ToLowerInvariant())
            : null;
    }

    internal static IReadOnlyList<Core.Model.MountPoint> MapMounts(IList<Wire.MountPoint>? mounts)
    {
        if (mounts is null || mounts.Count == 0) return [];

        return
        [
            .. mounts.Select(m => new Core.Model.MountPoint(
                Kind: m.Type?.ToLowerInvariant() switch
                {
                    "volume" => MountKind.Volume,
                    "bind" => MountKind.Bind,
                    "tmpfs" => MountKind.Tmpfs,
                    _ => MountKind.Other,
                },

                // A named volume identifies itself by Name; a bind has only a host path.
                Source: (string.IsNullOrEmpty(m.Name) ? m.Source : m.Name) ?? "",
                Destination: m.Destination ?? "",
                ReadOnly: !m.RW))
                .OrderBy(m => m.Destination, StringComparer.Ordinal),
        ];
    }

    static IReadOnlyList<Core.Model.NetworkAttachment> MapNetworks(IDictionary<string, EndpointSettings>? networks)
    {
        if (networks is null || networks.Count == 0) return [];

        return
        [
            .. networks
                .OrderBy(n => n.Key, StringComparer.Ordinal)
                .Select(n => new Core.Model.NetworkAttachment(
                    n.Key,
                    Blank(n.Value?.IPAddress),
                    Blank(n.Value?.Gateway),
                    Blank(n.Value?.MacAddress),
                    n.Value?.Aliases?.ToArray() ?? [])),
        ];
    }

    /// <summary>
    /// Rendered the way the user would have written it on the command line, because that is the
    /// form they can act on.
    /// </summary>
    static string? MapRestartPolicy(RestartPolicy? policy)
    {
        var name = policy?.Name.ToString();
        if (string.IsNullOrEmpty(name) || name is "Undefined" or "No") return null;

        var kebab = name switch
        {
            "Always" => "always",
            "UnlessStopped" => "unless-stopped",
            "OnFailure" => "on-failure",
            _ => name.ToLowerInvariant(),
        };

        return kebab == "on-failure" && policy!.MaximumRetryCount > 0
            ? $"on-failure:{policy.MaximumRetryCount}"
            : kebab;
    }
}
