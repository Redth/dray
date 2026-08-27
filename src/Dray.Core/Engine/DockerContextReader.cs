using System.Text.Json;

namespace Dray.Core.Engine;

/// <summary>
/// The filesystem and environment reads context discovery needs, behind an interface so the whole
/// of <see cref="DockerContextReader"/> is testable without touching a real home directory.
/// </summary>
public interface IDockerConfigSource
{
    string? DockerConfigDirectory { get; }

    string? GetEnvironmentVariable(string name);

    bool FileExists(string path);

    /// <summary>
    /// True when <paramref name="path"/> is a socket that could plausibly be connected to.
    /// <para>
    /// Distinct from <see cref="FileExists"/> on purpose: a dangling symlink reports as an
    /// existing file. Podman leaves <c>/var/run/docker.sock</c> pointing at a machine socket that
    /// is absent whenever no machine is running, so probing with FileExists offers the user a host
    /// that can never connect.
    /// </para>
    /// </summary>
    bool SocketExists(string path);

    /// <summary>
    /// The canonical path a socket resolves to, following symlinks. Returns <paramref name="path"/>
    /// unchanged when it is not a link.
    /// <para>
    /// Needed for deduplication: podman publishes its API at
    /// <c>~/.local/share/containers/podman/machine/podman.sock</c> and symlinks
    /// <c>/var/run/docker.sock</c> to it, so the two paths are one engine. Comparing raw strings
    /// would list it twice in the host picker.
    /// </para>
    /// </summary>
    string ResolveSocketPath(string path);

    bool DirectoryExists(string path);

    IEnumerable<string> EnumerateDirectories(string path);

    string ReadAllText(string path);
}

/// <summary>Reads the real <c>~/.docker</c> and process environment.</summary>
public sealed class SystemDockerConfigSource : IDockerConfigSource
{
    public string? DockerConfigDirectory
    {
        get
        {
            // DOCKER_CONFIG wins, as it does for the CLI.
            var explicitDir = Environment.GetEnvironmentVariable("DOCKER_CONFIG");
            if (!string.IsNullOrWhiteSpace(explicitDir)) return explicitDir;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".docker");
        }
    }

    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public bool FileExists(string path) => File.Exists(path);

    public bool SocketExists(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;

            // File.Exists says true for a dangling symlink, so resolve to the final target and
            // ask whether THAT is really there.
            var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
            return target is null || target.Exists;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string ResolveSocketPath(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);

    public string ReadAllText(string path) => File.ReadAllText(path);
}

/// <summary>
/// Discovers the hosts Dray can talk to, the way the Docker CLI does.
/// <para>
/// Precedence follows the CLI exactly, because a user whose <c>DOCKER_HOST</c> points somewhere
/// should see Dray agree with their terminal: <c>DOCKER_HOST</c> beats <c>DOCKER_CONTEXT</c> beats
/// <c>currentContext</c> in config.json.
/// </para>
/// </summary>
public sealed class DockerContextReader(IDockerConfigSource source)
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Sockets worth probing when nothing is declared. A user who installed Colima or Rancher and
    /// never ran <c>docker context use</c> still has a working engine, and an empty host list
    /// would be wrong.
    /// </summary>
    static readonly (string Name, string Path)[] WellKnownUnixSockets =
    [
        ("Docker Desktop", ".docker/run/docker.sock"),
        ("OrbStack", ".orbstack/run/docker.sock"),
        ("Colima", ".colima/default/docker.sock"),
        ("Rancher Desktop", ".rd/docker.sock"),
        ("Podman", ".local/share/containers/podman/machine/podman.sock"),
    ];

    static readonly string[] WellKnownNamedPipes =
    [
        "//./pipe/dockerDesktopLinuxEngine",
        "//./pipe/docker_engine",
    ];

    public IReadOnlyList<DockerHost> Discover()
    {
        var hosts = new List<DockerHost>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Set once DOCKER_HOST has claimed the current slot, so no context can also claim it.
        // Deriving this from the list's length instead would silently mean "only the first
        // context may be current", which is a different and wrong rule.
        var currentClaimed = false;

        var configDir = source.DockerConfigDirectory;
        var currentContextName = ReadCurrentContextName(configDir);

        // DOCKER_HOST overrides everything, including the selected context.
        var envHost = source.GetEnvironmentVariable("DOCKER_HOST");
        if (DockerEndpoint.Parse(envHost) is { } envEndpoint)
        {
            hosts.Add(new DockerHost
            {
                Id = "env:DOCKER_HOST",
                Name = "DOCKER_HOST",
                Description = "Set in the environment, overriding the selected context",
                Endpoint = envEndpoint,
                Origin = HostOrigin.Environment,
                IsCurrent = true,
            });
            seen.Add(Identity(envEndpoint));
            currentClaimed = true;
        }

        foreach (var context in ReadContexts(configDir))
        {
            if (!seen.Add(Identity(context.Endpoint))) continue;

            // Only mark a context current when nothing in the environment has overridden it.
            var isCurrent = !currentClaimed
                && string.Equals(context.Id, currentContextName, StringComparison.Ordinal);

            if (isCurrent) currentClaimed = true;

            hosts.Add(context with { IsCurrent = isCurrent });
        }

        // "default" is implicit: the CLI falls back to the platform's standard socket when no
        // context says otherwise, and it never appears in the contexts directory.
        foreach (var probed in ProbeWellKnown())
        {
            if (seen.Add(Identity(probed.Endpoint))) hosts.Add(probed);
        }

        // Nothing is current yet if the selected context was missing; fall back to the first host
        // so the app opens somewhere rather than nowhere.
        if (hosts.Count > 0 && !hosts.Any(h => h.IsCurrent))
            hosts[0] = hosts[0] with { IsCurrent = true };

        return hosts;
    }

    /// <summary>
    /// What makes two endpoints the same engine. Unix sockets resolve through symlinks first;
    /// everything else is its own raw string.
    /// </summary>
    string Identity(DockerEndpoint endpoint)
        => endpoint is { Scheme: EndpointScheme.Unix, Path: { } path }
            ? "unix:" + source.ResolveSocketPath(path)
            : endpoint.Raw;

    string? ReadCurrentContextName(string? configDir)
    {
        // DOCKER_CONTEXT beats the file, matching the CLI.
        var envContext = source.GetEnvironmentVariable("DOCKER_CONTEXT");
        if (!string.IsNullOrWhiteSpace(envContext)) return envContext.Trim();

        if (configDir is null) return null;

        var configPath = Path.Combine(configDir, "config.json");
        if (!source.FileExists(configPath)) return null;

        try
        {
            using var doc = JsonDocument.Parse(source.ReadAllText(configPath));
            return doc.RootElement.TryGetProperty("currentContext", out var value)
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            // A corrupt config should not stop discovery.
            return null;
        }
    }

    IEnumerable<DockerHost> ReadContexts(string? configDir)
    {
        if (configDir is null) yield break;

        var metaRoot = Path.Combine(configDir, "contexts", "meta");
        if (!source.DirectoryExists(metaRoot)) yield break;

        foreach (var dir in source.EnumerateDirectories(metaRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var metaPath = Path.Combine(dir, "meta.json");
            if (!source.FileExists(metaPath)) continue;

            DockerHost? host = null;
            try
            {
                host = ParseContext(source.ReadAllText(metaPath), Path.GetFileName(dir), configDir);
            }
            catch (JsonException)
            {
                // One malformed context degrades that host, not discovery.
            }

            if (host is not null) yield return host;
        }
    }

    DockerHost? ParseContext(string json, string digest, string configDir)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var name = root.TryGetProperty("Name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) return null;

        if (!root.TryGetProperty("Endpoints", out var endpoints)) return null;
        if (!endpoints.TryGetProperty("docker", out var dockerEndpoint)) return null;

        var rawHost = dockerEndpoint.TryGetProperty("Host", out var h) ? h.GetString() : null;
        var skipVerify = dockerEndpoint.TryGetProperty("SkipTLSVerify", out var s) && s.ValueKind == JsonValueKind.True;

        // TLS material lives alongside the metadata, keyed by the same digest.
        var tlsDir = Path.Combine(configDir, "contexts", "tls", digest, "docker");
        EndpointTls? tls = null;
        if (source.DirectoryExists(tlsDir))
        {
            var ca = Path.Combine(tlsDir, "ca.pem");
            var cert = Path.Combine(tlsDir, "cert.pem");
            var key = Path.Combine(tlsDir, "key.pem");

            tls = new EndpointTls(
                source.FileExists(ca) ? ca : null,
                source.FileExists(cert) ? cert : null,
                source.FileExists(key) ? key : null,
                skipVerify);
        }
        else if (skipVerify)
        {
            tls = new EndpointTls(null, null, null, true);
        }

        var endpoint = DockerEndpoint.Parse(rawHost, tls);
        if (endpoint is null) return null;

        string? description = null;
        if (root.TryGetProperty("Metadata", out var metadata)
            && metadata.TryGetProperty("Description", out var d))
        {
            description = d.GetString();
        }

        return new DockerHost
        {
            Id = name,
            Name = name,
            Description = description,
            Endpoint = endpoint,
            Origin = HostOrigin.DockerContext,
        };
    }

    IEnumerable<DockerHost> ProbeWellKnown()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var pipe in WellKnownNamedPipes)
            {
                // Named pipes are not files; existence is decided by connecting, so these are
                // always offered and the connection attempt decides.
                if (DockerEndpoint.Parse(pipe) is { } endpoint)
                {
                    yield return new DockerHost
                    {
                        Id = "discovered:" + pipe,
                        Name = pipe.Contains("DesktopLinux", StringComparison.Ordinal) ? "Docker Desktop" : "Docker Engine",
                        Endpoint = endpoint,
                        Origin = HostOrigin.Discovered,
                    };
                }
            }

            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // The platform default, which never appears as a context.
        if (source.SocketExists("/var/run/docker.sock")
            && DockerEndpoint.Parse("unix:///var/run/docker.sock") is { } systemSocket)
        {
            yield return new DockerHost
            {
                Id = "discovered:/var/run/docker.sock",
                Name = "Local engine",
                Endpoint = systemSocket,
                Origin = HostOrigin.Discovered,
            };
        }

        if (string.IsNullOrEmpty(home)) yield break;

        foreach (var (name, relative) in WellKnownUnixSockets)
        {
            var full = Path.Combine(home, relative);
            if (!source.SocketExists(full)) continue;
            if (DockerEndpoint.Parse("unix://" + full) is not { } endpoint) continue;

            yield return new DockerHost
            {
                Id = "discovered:" + full,
                Name = name,
                Endpoint = endpoint,
                Origin = HostOrigin.Discovered,
            };
        }
    }
}
