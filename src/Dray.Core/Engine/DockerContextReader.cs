using System.Text.Json;

namespace Dray.Core.Engine;

/// <summary>
/// The filesystem and environment reads context discovery needs, behind an interface so the whole
/// of <see cref="DockerContextReader"/> is testable without touching a real home directory.
/// </summary>
public interface IDockerConfigSource
{
    string? DockerConfigDirectory { get; }

    /// <summary>
    /// Whether discovery should look for Windows' engines: named pipes and WSL distros.
    /// <para>
    /// Part of the source rather than read from <c>OperatingSystem</c> by the reader, because which
    /// platform is being discovered is as much the environment as <c>~/.docker</c> is. Reading it
    /// directly made every discovery test on a Windows machine find that machine's real pipes and
    /// run its real <c>wsl.exe</c> — five hosts where a test expected one.
    /// </para>
    /// </summary>
    bool IsWindows { get; }

    /// <summary>Whether discovery should look for Apple's runtime, which only exists on macOS.</summary>
    bool IsMacOS { get; }

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
    public bool IsWindows => OperatingSystem.IsWindows();

    public bool IsMacOS => OperatingSystem.IsMacOS();

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

    /// <summary>
    /// The pipes worth trying on Windows, in the order they should appear.
    /// <para>
    /// <c>docker_engine</c> is deliberately ambiguous and cannot be resolved from here. Docker
    /// Desktop points it at whichever mode is switched on; a native <c>dockerd.exe</c> installed
    /// without Docker Desktop serves Windows containers on it; and podman's machine forwards its
    /// Linux API to the same name. The picker labels it for what it is — an engine — and
    /// <c>/info</c>'s <c>OSType</c> says what it turned out to be once connected.
    /// </para>
    /// </summary>
    static readonly (string Name, string Pipe)[] WellKnownNamedPipes =
    [
        // Docker Desktop publishes each engine under its own name, and both are live at once —
        // switching modes changes which one the default pipe points at, not which daemons are
        // running. Containers started in either mode keep running across a switch. So Dray offers
        // both and needs no mode of its own: the host picker is the switch, and unlike Docker
        // Desktop's it does not have to stop anything.
        //
        // Docker Desktop normally registers these as the `desktop-linux` and `desktop-windows`
        // contexts, which discovery reads first. They are probed anyway because it does not always
        // manage it — desktop-windows in particular is missing on some installs — and a pipe that
        // is not there simply fails to connect.
        ("Docker Desktop (Linux containers)", "//./pipe/dockerDesktopLinuxEngine"),
        ("Docker Desktop (Windows containers)", "//./pipe/dockerDesktopWindowsEngine"),

        ("Docker Engine", "//./pipe/docker_engine"),

        // Podman's own name for a machine's API. Offered as well as docker_engine because podman
        // only forwards to that one for its default rootful machine, and a second machine is
        // reachable under this name alone.
        ("Podman", "//./pipe/podman-machine-default"),
    ];

    /// <summary>
    /// Everything <see cref="Discover"/> finds, plus the WSL distributions that could be hosting
    /// an engine of their own.
    /// <para>
    /// Async and separate because reaching WSL means running <c>wsl.exe</c>, and the rest of
    /// discovery is filesystem reads that must not be made to wait on a subprocess. A machine
    /// without WSL — which is every machine that is not Windows — gets the synchronous answer and
    /// no process launch at all.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<DockerHost>> DiscoverAsync(
        WslDistroReader? wsl = null, CancellationToken ct = default)
    {
        var hosts = Discover();

        if (!source.IsWindows) return hosts;

        var distros = await (wsl ?? new WslDistroReader()).ListAsync(ct).ConfigureAwait(false);

        var combined = new List<DockerHost>(hosts);
        var seen = new HashSet<string>(hosts.Select(h => h.Endpoint.Raw), StringComparer.OrdinalIgnoreCase);

        // After the pipes, and never claiming the current slot. A machine with Docker Desktop and
        // three distros should open on Docker Desktop, which is where its containers are; a distro
        // costs a relay process per connection and is the deliberate choice, not the default.
        //
        // Running distros first, because a running one is far more likely to be the one with an
        // engine in it — it is running for a reason.
        foreach (var distro in distros
            .Where(d => d.CanHostAnEngine)
            .OrderByDescending(d => d.IsRunning)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (DockerEndpoint.Parse("wsl://" + distro.Name) is not { } endpoint) continue;
            if (!seen.Add(endpoint.Raw)) continue;

            combined.Add(new DockerHost
            {
                Id = "wsl:" + distro.Name,
                Name = distro.Name,
                Description = distro.IsRunning
                    ? "WSL2 distribution, running"
                    : "WSL2 distribution, stopped — connecting will start it",
                Endpoint = endpoint,
                Origin = HostOrigin.WslDistro,
                StartsOnConnect = !distro.IsRunning,
            });
        }

        return combined;
    }

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

        // Apple's runtime is last on purpose. It is a real engine and belongs in the picker, but
        // it is nobody's default: a machine with both should open on the Docker-compatible one,
        // which is where the user's existing containers are.
        foreach (var apple in ProbeAppleContainer())
        {
            if (seen.Add(Identity(apple.Endpoint))) hosts.Add(apple);
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
    /// <summary>
    /// What makes two endpoints the same engine.
    /// <para>
    /// A pipe is compared by its parsed path, never its raw spelling: Docker Desktop's
    /// <c>desktop-linux</c> context says <c>npipe:////./pipe/dockerDesktopLinuxEngine</c> and the
    /// probe says <c>//./pipe/dockerDesktopLinuxEngine</c>, and comparing those as strings put the
    /// same engine in the picker twice on every machine with Docker Desktop.
    /// </para>
    /// </summary>
    string Identity(DockerEndpoint endpoint) => endpoint switch
    {
        { Scheme: EndpointScheme.Unix, Path: { } path } => "unix:" + source.ResolveSocketPath(path),
        { Scheme: EndpointScheme.NamedPipe, Path: { } pipe } => "npipe:" + pipe,
        _ => endpoint.Raw,
    };

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

    /// <summary>
    /// Apple's <c>container</c> runtime, when it is installed.
    /// <para>
    /// Found by walking <c>PATH</c> rather than by asking the shell, because a probe must not
    /// depend on a login shell's configuration and must not cost a process launch during startup.
    /// </para>
    /// <para>
    /// Installed is not running: this offers the host, and connecting decides. The alternative —
    /// running <c>container system status</c> here — would make discovery wait on a subprocess for
    /// an engine most users do not have.
    /// </para>
    /// </summary>
    IEnumerable<DockerHost> ProbeAppleContainer()
    {
        // Only exists on macOS, and only on Apple silicon. Probing elsewhere finds nothing and
        // costs a PATH walk on every launch.
        if (!source.IsMacOS) yield break;

        if (FindOnPath("container") is not { } executable) yield break;

        yield return new DockerHost
        {
            Id = "apple:container",
            Name = "Apple container",
            Description = "Apple's containerization framework, driven through its CLI",
            Endpoint = new DockerEndpoint
            {
                Scheme = EndpointScheme.AppleContainer,
                Raw = executable,
                Path = executable,
            },
            Origin = HostOrigin.AppleContainer,
        };
    }

    /// <summary>
    /// The first directory on a Unix <c>PATH</c> holding an executable of this name, or null.
    /// <para>
    /// Joined with '/' rather than <c>Path.Combine</c>. This only ever searches macOS's PATH, and
    /// <c>Path.Combine</c> takes its separator from whichever machine the code runs on — which
    /// made the macOS discovery tests produce <c>/opt/homebrew/bin\container</c> on Windows.
    /// </para>
    /// </summary>
    string? FindOnPath(string executable)
    {
        var path = source.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var directory in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = directory.TrimEnd('/') + "/" + executable;
            if (source.FileExists(candidate)) return candidate;
        }

        return null;
    }

    IEnumerable<DockerHost> ProbeWellKnown()
    {
        if (source.IsWindows)
        {
            foreach (var (name, pipe) in WellKnownNamedPipes)
            {
                // Named pipes are not files; existence is decided by connecting, so these are
                // always offered and the connection attempt decides.
                if (DockerEndpoint.Parse(pipe) is { } endpoint)
                {
                    yield return new DockerHost
                    {
                        Id = "discovered:" + pipe,
                        Name = name,
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
