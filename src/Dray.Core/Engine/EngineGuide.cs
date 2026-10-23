using Dray.Core.Shell;

namespace Dray.Core.Engine;

/// <summary>Which desktop a guide is written for. The commands differ, and so do the sockets.</summary>
public enum GuidePlatform
{
    MacOS,
    Windows,
    Linux,
}

/// <summary>How a guide ends — what Dray does once the engine exists.</summary>
public enum GuideKind
{
    /// <summary>
    /// An engine on this machine that publishes a socket or pipe discovery already looks for.
    /// Dray's part is to look again; there is nothing to add.
    /// </summary>
    Local,

    /// <summary>
    /// An engine whose endpoint discovery cannot find on its own, so Dray reads it from the
    /// engine's own tooling and records it as a Docker context. minikube is the case: its engine
    /// lives inside a cluster node at an address only <c>minikube docker-env</c> knows.
    /// </summary>
    Minikube,

    /// <summary>A machine elsewhere, reached through the system <c>ssh</c>.</summary>
    Ssh,

    /// <summary>A daemon listening on a TCP port, with or without TLS.</summary>
    Tcp,
}

/// <summary>
/// One step of a guide. A command is shown for the user to read, copy and run. The installs Dray
/// runs itself — shown first, run only when asked (PRODUCT.md) — are EngineSetup's routes; a guide
/// covers the engines and the hosts those routes do not, and says what each one takes.
/// </summary>
/// <param name="Title">What this step achieves, in the user's terms.</param>
/// <param name="Body">Why it is needed, or what to expect. Never omitted.</param>
/// <param name="Command">A command to copy into a terminal, when there is one.</param>
/// <param name="Link">A page to open in the user's browser — a vendor's download, usually.</param>
/// <param name="LinkLabel">What the link button says.</param>
public sealed record GuideStep(
    string Title,
    string Body,
    string? Command = null,
    string? Link = null,
    string? LinkLabel = null);

/// <summary>
/// How to get one kind of engine working, and how to recognise it once it is.
/// <para>
/// The recognition half is what keeps the wizard honest. Every tile states what Dray
/// <b>measured</b> on this machine — an app bundle on disk, an executable on <c>PATH</c>, a live
/// socket, a host that answered — rather than offering the same instructions to someone who
/// already has the engine running.
/// </para>
/// </summary>
public sealed record EngineGuide
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>One line for the chooser tile.</summary>
    public required string Tagline { get; init; }

    /// <summary>A short paragraph at the top of the guide: what this is and when to pick it.</summary>
    public required string Summary { get; init; }

    public required IconRef Icon { get; init; }

    public GuideKind Kind { get; init; } = GuideKind.Local;

    public IReadOnlyList<GuideStep> Steps { get; init; } = [];

    /// <summary>
    /// A caveat worth reading before starting — a licence, an engine choice, a gap. Shown under the
    /// steps, never in place of them.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>Executables whose presence on <c>PATH</c> means the engine is installed.</summary>
    public IReadOnlyList<string> Executables { get; init; } = [];

    /// <summary>App bundles or program files whose presence means the engine is installed.</summary>
    public IReadOnlyList<string> InstalledPaths { get; init; } = [];

    /// <summary>
    /// Unix sockets this engine publishes, absolute or <c>~</c>-relative. A live one is the
    /// engine running; a host whose endpoint resolves to one is this engine.
    /// </summary>
    public IReadOnlyList<string> Sockets { get; init; } = [];

    /// <summary>Windows named pipes this engine publishes, as <c>//./pipe/name</c>.</summary>
    public IReadOnlyList<string> Pipes { get; init; } = [];

    /// <summary>Docker context names this engine registers, or Dray registers for it.</summary>
    public IReadOnlyList<string> ContextNames { get; init; } = [];

    /// <summary>Host origins that are this engine regardless of endpoint — Apple's runtime.</summary>
    public IReadOnlyList<HostOrigin> Origins { get; init; } = [];

    /// <summary>
    /// Why this engine cannot run here, when it cannot. The tile stays visible and says so: a
    /// missing option reads as Dray not knowing about it, and an unexplained one as a bug.
    /// </summary>
    public string? Unavailable { get; init; }

    /// <summary>
    /// A socket discovery does not probe for but this engine publishes — podman's rootless socket
    /// on Linux. When it is live and no host points at it, the guide offers to add one.
    /// </summary>
    public string? AddableSocket { get; init; }
}

/// <summary>What Dray found on this machine for one guide, strongest evidence first.</summary>
public enum GuideState
{
    /// <summary>Nothing — no app, no executable, no socket, no host.</summary>
    Absent,

    /// <summary>Installed, and nothing is listening.</summary>
    Installed,

    /// <summary>A live socket nobody has added as a host yet.</summary>
    Listening,

    /// <summary>In the host list, and not answering when last asked.</summary>
    NotAnswering,

    /// <summary>In the host list, and not asked yet.</summary>
    Listed,

    /// <summary>In the host list and answering.</summary>
    Connected,
}

/// <param name="State">The strongest thing found.</param>
/// <param name="Host">The host this guide's engine already is, when there is one.</param>
public sealed record GuideStatus(GuideState State, DockerHost? Host = null)
{
    public static readonly GuideStatus Absent = new(GuideState.Absent);
}

/// <summary>
/// Measures each guide against this machine and the hosts discovery found.
/// <para>
/// Filesystem reads only, through <see cref="IDockerConfigSource"/>: the chooser opens instantly
/// and launches no process, and the whole of it is testable against a fake home directory.
/// </para>
/// </summary>
public sealed class EngineGuideProbe(IDockerConfigSource source)
{
    public GuideStatus Check(EngineGuide guide, IReadOnlyList<DockerHost> hosts)
    {
        if (Match(guide, hosts) is { } host)
        {
            var state = host.State switch
            {
                HostConnectionState.Connected or HostConnectionState.Degraded => GuideState.Connected,
                HostConnectionState.Unreachable => GuideState.NotAnswering,
                _ => GuideState.Listed,
            };

            return new GuideStatus(state, host);
        }

        if (AddableSocketPath(guide) is not null) return new GuideStatus(GuideState.Listening);

        if (guide.Sockets.Any(s => Expand(s) is { } path && source.SocketExists(path)))
            return new GuideStatus(GuideState.Listening);

        return IsInstalled(guide) ? new GuideStatus(GuideState.Installed) : GuideStatus.Absent;
    }

    /// <summary>
    /// The live socket a guide could add as a host, or null when there is none or one is already
    /// listed. Only for the sockets discovery does not look for by itself.
    /// </summary>
    public string? AddableSocketPath(EngineGuide guide)
        => guide.AddableSocket is { } socket && Expand(socket) is { } path && source.SocketExists(path)
            ? path
            : null;

    /// <summary>The host in the list that is this guide's engine, if any.</summary>
    public DockerHost? Match(EngineGuide guide, IReadOnlyList<DockerHost> hosts)
    {
        var sockets = guide.Sockets
            .Concat(guide.AddableSocket is { } addable ? [addable] : [])
            .Select(Expand)
            .OfType<string>()
            .Select(source.ResolveSocketPath)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var host in hosts)
        {
            if (guide.Origins.Contains(host.Origin)) return host;

            if (guide.ContextNames.Contains(host.Id, StringComparer.OrdinalIgnoreCase)) return host;

            if (host.Endpoint is { Scheme: EndpointScheme.Unix, Path: { } path }
                && sockets.Contains(source.ResolveSocketPath(path)))
            {
                return host;
            }

            if (host.Endpoint is { Scheme: EndpointScheme.NamedPipe, Path: { } pipe }
                && guide.Pipes.Any(p => string.Equals(PipeName(p), PipeName(pipe), StringComparison.OrdinalIgnoreCase)))
            {
                return host;
            }
        }

        return null;
    }

    bool IsInstalled(EngineGuide guide)
        => guide.InstalledPaths.Any(p => Expand(p) is { } path && (source.DirectoryExists(path) || source.FileExists(path)))
            || guide.Executables.Any(OnPath);

    bool OnPath(string executable)
    {
        var path = source.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;

        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        string[] names = OperatingSystem.IsWindows() ? [executable + ".exe", executable] : [executable];

        return path
            .Split(separator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => names.Any(name => source.FileExists(Path.Combine(directory, name))));
    }

    /// <summary>
    /// Resolves <c>~</c> and <c>$XDG_RUNTIME_DIR</c>, the two prefixes these paths use. Null when
    /// the variable a path needs is not set — a path built from an empty variable would name
    /// somewhere real and wrong.
    /// </summary>
    internal string? Expand(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(home) ? null : Path.Combine(home, path[2..]);
        }

        const string runtime = "$XDG_RUNTIME_DIR/";
        if (path.StartsWith(runtime, StringComparison.Ordinal))
        {
            var dir = source.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, path[runtime.Length..]);
        }

        return path;
    }

    static string PipeName(string pipe) => pipe.Replace('\\', '/').TrimEnd('/').Split('/')[^1];
}

/// <summary>
/// The engines Dray knows how to help someone get, per platform.
/// <para>
/// Not a recommendation list. PRODUCT.md: Dray is indifferent to which engine a user runs, and the
/// order here is only "the one most people already have" first. Licence terms are stated where
/// they bite, because which engine to run is a licensing decision as much as a technical one.
/// </para>
/// </summary>
public static class EngineGuides
{
    public static GuidePlatform Current =>
        OperatingSystem.IsWindows() ? GuidePlatform.Windows
        : OperatingSystem.IsMacOS() ? GuidePlatform.MacOS
        : GuidePlatform.Linux;

    /// <param name="platform">Which platform's commands and sockets to use.</param>
    /// <param name="apple">
    /// Whether Apple's engine could run here — from <see cref="AppleEngine.Check"/>. Its tile only
    /// appears on a Mac, and explains itself when the Mac is too old or not Apple silicon.
    /// </param>
    public static IReadOnlyList<EngineGuide> For(GuidePlatform platform, AppleEngineAvailability apple)
    {
        List<EngineGuide> guides = [DockerDesktop(platform)];

        if (platform == GuidePlatform.MacOS) guides.Add(OrbStack);
        if (platform != GuidePlatform.Windows) guides.Add(Colima);

        guides.Add(Podman(platform));
        guides.Add(RancherDesktop(platform));

        if (platform == GuidePlatform.MacOS && apple != AppleEngineAvailability.Unsupported)
            guides.Add(AppleContainer(apple));

        guides.Add(Minikube(platform));
        guides.Add(Ssh);
        guides.Add(Tcp);

        return guides;
    }

    /// <summary>The guides for engines on this machine — the chooser's first group.</summary>
    public static bool IsLocal(EngineGuide guide) => guide.Kind is GuideKind.Local or GuideKind.Minikube;

    static EngineGuide DockerDesktop(GuidePlatform platform) => new()
    {
        Id = "docker-desktop",
        Name = "Docker Desktop",
        Tagline = "Docker's own app, with an engine in a managed VM",
        Summary = platform == GuidePlatform.Windows
            ? "Docker's own app. It runs Linux containers in WSL2 and, on Pro and Enterprise editions, "
                + "Windows containers too — Dray shows each engine as its own host."
            : "Docker's own app. It runs the engine in a virtual machine it manages for you, and "
                + "registers a Docker context Dray reads like any other.",
        Icon = IconRef.Host,
        Steps = platform switch
        {
            GuidePlatform.MacOS =>
            [
                new("Install Docker Desktop",
                    "From Docker's site, or with Homebrew if you use it.",
                    "brew install --cask docker-desktop",
                    "https://www.docker.com/products/docker-desktop/", "Download from Docker"),
                new("Start it",
                    "Open the app and accept its terms. The engine is ready when the menu bar item "
                    + "says Docker Desktop is running.",
                    "open -a Docker"),
            ],
            GuidePlatform.Windows =>
            [
                new("Install Docker Desktop",
                    "From Docker's site, or with winget.",
                    "winget install -e --id Docker.DockerDesktop",
                    "https://www.docker.com/products/docker-desktop/", "Download from Docker"),
                new("Start it",
                    "Open Docker Desktop from the Start menu and accept its terms. It sets up WSL2 "
                    + "the first time, which can ask for a restart."),
            ],
            _ =>
            [
                new("Install Docker Desktop",
                    "Docker publishes a package per distribution; the page lists them.",
                    Link: "https://docs.docker.com/desktop/setup/install/linux/", LinkLabel: "Installation guide"),
                new("Start it",
                    "It runs as a user service, so it needs no root.",
                    "systemctl --user start docker-desktop"),
            ],
        },
        Note = "Free for personal use, education and small businesses. Larger companies need a paid "
            + "subscription — Docker's terms say exactly where the line is.",
        InstalledPaths = platform switch
        {
            GuidePlatform.MacOS => ["/Applications/Docker.app"],
            GuidePlatform.Windows => [@"C:\Program Files\Docker\Docker\Docker Desktop.exe"],
            _ => ["/opt/docker-desktop"],
        },
        Sockets = platform == GuidePlatform.Linux ? ["~/.docker/desktop/docker.sock"] : ["~/.docker/run/docker.sock"],
        Pipes = ["//./pipe/dockerDesktopLinuxEngine", "//./pipe/dockerDesktopWindowsEngine"],
        ContextNames = ["desktop-linux", "desktop-windows"],
    };

    static readonly EngineGuide OrbStack = new()
    {
        Id = "orbstack",
        Name = "OrbStack",
        Tagline = "A fast, light engine and Linux VM for the Mac",
        Summary = "A Mac app that runs a Docker engine in a lightweight VM. Quick to start and easy on "
            + "the battery.",
        Icon = IconRef.Host,
        Steps =
        [
            new("Install OrbStack",
                "From OrbStack's site, or with Homebrew.",
                "brew install orbstack",
                "https://orbstack.dev/download", "Download OrbStack"),
            new("Start it",
                "Open the app once. It registers an orbstack context and keeps the engine running in "
                + "the background.",
                "open -a OrbStack"),
        ],
        Note = "Free for personal use. Commercial use needs a licence.",
        Executables = ["orb"],
        InstalledPaths = ["/Applications/OrbStack.app"],
        Sockets = ["~/.orbstack/run/docker.sock"],
        ContextNames = ["orbstack"],
    };

    static readonly EngineGuide Colima = new()
    {
        Id = "colima",
        Name = "Colima",
        Tagline = "Open source, command line, no app to keep open",
        Summary = "A small open-source tool that runs the Docker engine in a Linux VM. No app and no "
            + "licence to think about; it is driven entirely from the terminal.",
        Icon = IconRef.Terminal,
        Steps =
        [
            new("Install Colima and the Docker CLI",
                "Colima provides the engine; the docker formula is the command that talks to it.",
                "brew install colima docker",
                "https://github.com/abiosoft/colima", "Colima on GitHub"),
            new("Start the VM",
                "The first start downloads and boots a Linux VM, so give it a minute. Add "
                + "--cpu and --memory to size it.",
                "colima start"),
        ],
        Note = "Colima's VM stops when the Mac restarts. Run colima start again, or brew services "
            + "start colima to have it come back on its own.",
        Executables = ["colima"],
        Sockets = ["~/.colima/default/docker.sock", "~/.config/colima/default/docker.sock"],
        ContextNames = ["colima"],
    };

    static EngineGuide Podman(GuidePlatform platform) => new()
    {
        Id = "podman",
        Name = "Podman",
        Tagline = "Daemonless and rootless, with a Docker-compatible API",
        Summary = platform == GuidePlatform.Linux
            ? "Red Hat's engine. On Linux it runs without a daemon or root, and serves a "
                + "Docker-compatible API from a socket your user owns."
            : "Red Hat's engine. It runs in a Linux VM it calls a machine, and serves a "
                + "Docker-compatible API Dray connects to like any other.",
        Icon = IconRef.Container,
        Steps = platform switch
        {
            GuidePlatform.MacOS =>
            [
                new("Install Podman", "With Homebrew, or the installer from podman.io.", "brew install podman",
                    "https://podman.io/docs/installation", "Installation guide"),
                new("Create a machine", "A one-time step that downloads the VM image.", "podman machine init"),
                new("Start it", "Starts the VM and publishes the API socket.", "podman machine start"),
            ],
            GuidePlatform.Windows =>
            [
                new("Install Podman", "With winget, or the installer from podman.io.", "winget install -e --id RedHat.Podman",
                    "https://podman.io/docs/installation", "Installation guide"),
                new("Create a machine", "A one-time step. The machine runs in WSL2.", "podman machine init"),
                new("Start it", "Starts the machine and publishes its pipe.", "podman machine start"),
            ],
            _ =>
            [
                new("Install Podman", "From your distribution's packages — this is the Debian and Ubuntu name.",
                    "sudo apt install podman",
                    "https://podman.io/docs/installation", "Installation guide"),
                new("Turn on the API socket",
                    "Podman needs no daemon, but Dray talks to an API. This starts the socket for your "
                    + "user, and on demand only.",
                    "systemctl --user enable --now podman.socket"),
            ],
        },
        Note = "Podman answers most of Docker's API and not all of it. Once connected, the host's "
            + "page says exactly which parts this engine supports.",
        Executables = ["podman"],
        Sockets = platform == GuidePlatform.Linux ? [] : ["~/.local/share/containers/podman/machine/podman.sock"],
        AddableSocket = platform == GuidePlatform.Linux ? "$XDG_RUNTIME_DIR/podman/podman.sock" : null,
        Pipes = ["//./pipe/podman-machine-default"],
        ContextNames = ["podman", "dray-podman"],
    };

    static EngineGuide RancherDesktop(GuidePlatform platform) => new()
    {
        Id = "rancher-desktop",
        Name = "Rancher Desktop",
        Tagline = "Open source app with Kubernetes built in",
        Summary = "SUSE's open-source desktop app. It runs a container engine and, optionally, a "
            + "local Kubernetes cluster on the same engine.",
        Icon = IconRef.Stack,
        Steps =
        [
            platform switch
            {
                GuidePlatform.MacOS => new GuideStep("Install Rancher Desktop", "With Homebrew, or from the site.",
                    "brew install --cask rancher", "https://rancherdesktop.io/", "Download Rancher Desktop"),
                GuidePlatform.Windows => new GuideStep("Install Rancher Desktop", "With winget, or from the site.",
                    "winget install -e --id SUSE.RancherDesktop", "https://rancherdesktop.io/", "Download Rancher Desktop"),
                _ => new GuideStep("Install Rancher Desktop", "The site lists a package per distribution.",
                    Link: "https://docs.rancherdesktop.io/getting-started/installation/", LinkLabel: "Installation guide"),
            },
            new("Start it with dockerd",
                "Rancher Desktop offers two engines, and only dockerd (moby) has the Docker API Dray "
                + "speaks. Choose it in Preferences, under Container Engine, or start it with this.",
                "rdctl start --container-engine moby"),
        ],
        Note = "With containerd selected there is nothing for Dray to connect to — the setting is the "
            + "whole difference.",
        Executables = ["rdctl"],
        InstalledPaths = platform switch
        {
            GuidePlatform.MacOS => ["/Applications/Rancher Desktop.app"],
            GuidePlatform.Windows => [@"C:\Program Files\Rancher Desktop\Rancher Desktop.exe"],
            _ => [],
        },
        Sockets = ["~/.rd/docker.sock"],
        ContextNames = ["rancher-desktop"],
    };

    static EngineGuide AppleContainer(AppleEngineAvailability availability) => new()
    {
        Id = "apple-container",
        Name = "Apple container",
        Tagline = "Apple's own engine, one lightweight VM per container",
        Summary = "Apple's open-source containerization framework. Each container runs in its own small "
            + "VM. It is not Docker-compatible, so a few things work differently — the host's page "
            + "lists them once it is connected.",
        Icon = IconRef.Container,
        Steps =
        [
            new("Install it",
                "Apple publishes a signed installer. Homebrew's formula installs the same engine.",
                "brew install container",
                AppleEngine.Downloads, "Download from Apple"),
            new("Start its service",
                "Starts the API server the command talks to. The first start offers to download a "
                + "Linux kernel; it needs one.",
                "container system start"),
        ],
        Executables = ["container"],
        Origins = [HostOrigin.AppleContainer],
        Unavailable = availability == AppleEngineAvailability.NeedsNewerMacOS
            ? $"Needs macOS {AppleEngine.MinimumMacOSMajor} or later."
            : null,
    };

    static EngineGuide Minikube(GuidePlatform platform) => new()
    {
        Id = "minikube",
        Name = "Kubernetes (minikube)",
        Tagline = "The Docker engine inside a local cluster node",
        Summary = "minikube runs a one-node Kubernetes cluster, and with the docker runtime that node "
            + "has its own engine. Dray connects to it, so you see the containers Kubernetes is "
            + "running and the images it can use.",
        Icon = IconRef.Stack,
        Kind = GuideKind.Minikube,
        Steps =
        [
            platform switch
            {
                GuidePlatform.MacOS => new GuideStep("Install minikube", "With Homebrew, or from the site.",
                    "brew install minikube", "https://minikube.sigs.k8s.io/docs/start/", "Get started"),
                GuidePlatform.Windows => new GuideStep("Install minikube", "With winget, or from the site.",
                    "winget install -e --id Kubernetes.minikube", "https://minikube.sigs.k8s.io/docs/start/", "Get started"),
                _ => new GuideStep("Install minikube", "The site has a package for each distribution.",
                    Link: "https://minikube.sigs.k8s.io/docs/start/", LinkLabel: "Get started"),
            },
            new("Start a cluster with the docker runtime",
                "The runtime is the part that matters — with containerd or CRI-O the node has no "
                + "Docker engine to connect to.",
                "minikube start --container-runtime=docker"),
            new("Connect Dray to it",
                "Dray asks minikube where its engine is and saves that as a Docker context named "
                + "dray-minikube, so the docker command can use it too."),
        ],
        Note = "Dray shows the node's containers and images, not Kubernetes objects. kind and k3d run "
            + "their nodes as containers on an engine you already have, and Docker Desktop's and "
            + "Rancher Desktop's Kubernetes use their own engine — none of those need a host of their own.",
        Executables = ["minikube"],
        ContextNames = [MinikubeDockerEnv.ContextName, "minikube"],
    };

    static readonly EngineGuide Ssh = new()
    {
        Id = "ssh",
        Name = "Over SSH",
        Tagline = "A server, NAS or VM you can already ssh into",
        Summary = "Any machine running Docker that you can reach with ssh. Dray uses your own ssh — "
            + "your config, your agent and your keys — and stores nothing.",
        Icon = IconRef.Terminal,
        Kind = GuideKind.Ssh,
    };

    static readonly EngineGuide Tcp = new()
    {
        Id = "tcp",
        Name = "Over TCP",
        Tagline = "A daemon listening on a port, ideally with TLS",
        Summary = "A Docker daemon that listens on a network port. With TLS, the client certificate "
            + "is what lets you in; without it, anyone who can reach the port controls the machine.",
        Icon = IconRef.Link,
        Kind = GuideKind.Tcp,
    };
}
