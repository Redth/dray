namespace Dray.Core.Engine;

/// <summary>What Dray observed about a Mac's container options.</summary>
/// <param name="HasEngine">Whether any engine answered. The only signal that settles the question.</param>
/// <param name="BrewPath">Homebrew's <c>brew</c>, by full path, or null when Homebrew is not installed.</param>
/// <param name="Colima">Whether Colima is installed in Homebrew's prefix.</param>
/// <param name="DockerCli">Whether a <c>docker</c> command is installed, which Colima needs beside it.</param>
/// <param name="DockerDesktop">Whether Docker Desktop is in /Applications.</param>
/// <param name="OrbStack">Whether OrbStack is in /Applications.</param>
public sealed record MacSetupState(
    bool HasEngine,
    string? BrewPath,
    bool Colima,
    bool DockerCli,
    bool DockerDesktop,
    bool OrbStack)
{
    /// <summary>The directory Homebrew installs commands into — /opt/homebrew/bin on Apple silicon.</summary>
    public string? BrewBin => BrewPath is { } path ? path[..path.LastIndexOf('/')] : null;
}

/// <summary>
/// The routes to an engine on a Mac. docs/ARCHITECTURE.md section 2.4b's rules, on a platform
/// where there are more choices and none of them is built in.
/// <para>
/// Three routes, side by side, because the difference between them is cost and taste rather than
/// capability: Colima is free and has no interface of its own, OrbStack and Docker Desktop are
/// applications with licences. One that is already installed comes first, because it is one action
/// away. Apple's own engine is offered separately, by the Hosts page, because whether it is an
/// option at all depends on the Mac.
/// </para>
/// </summary>
public static class MacSetupPlan
{
    /// <summary>
    /// Homebrew's documented install command. It asks for the user's password and to press Return,
    /// so it runs in Terminal where it can — Dray is never the thing that collects a password.
    /// </summary>
    public const string InstallHomebrew = "/bin/bash -c \"$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)\"";

    /// <summary>The prompt on a Mac is for the user's own password — there is no "administrator" to ask for.</summary>
    const string PasswordPrompt = "Asks for your password.";

    public const string OrbStackDownload = "https://orbstack.dev/download";

    public const string DockerDesktopDownload = "https://www.docker.com/products/docker-desktop/";

    public static IReadOnlyList<SetupRoute> Routes(MacSetupState state)
    {
        if (state.HasEngine) return [];

        var installedApp = state.OrbStack || state.DockerDesktop;

        var colima = new SetupRoute(
            "mac.colima",
            "Colima",
            "Docker's engine in a small Linux virtual machine, installed with Homebrew. Free and open source, "
            + "with no application of its own — Dray is its interface.",
            ColimaSteps(state),
            Recommended: !installedApp);

        var orbstack = new SetupRoute(
            "mac.orbstack",
            "OrbStack",
            "A fast, light application that runs Docker's engine. Free for personal use; commercial use needs a paid licence.",
            OrbStackSteps(state),
            Recommended: state.OrbStack);

        var desktop = new SetupRoute(
            "mac.desktop",
            "Docker Desktop",
            "Docker's own application. Free for personal use, education and small businesses; larger organisations need a paid subscription.",
            DesktopSteps(state),
            Recommended: state.DockerDesktop && !state.OrbStack);

        // The recommended route first, then the rest in the same order every time.
        if (state.OrbStack) return [orbstack, colima, desktop];
        if (state.DockerDesktop) return [desktop, colima, orbstack];
        return [colima, orbstack, desktop];
    }

    static IReadOnlyList<SetupStep> ColimaSteps(MacSetupState state)
    {
        if (state.BrewPath is not { } brew) return [HomebrewStep()];

        var path = BrewEnvironment(state);

        if (!state.Colima || !state.DockerCli)
        {
            return
            [
                new SetupStep(
                    "colima.install",
                    "Install Colima and the docker command",
                    "Both from Homebrew. Colima runs the engine; the docker command is what Colima starts it with, "
                    + "and what your terminal will use.",
                    brew,
                    ["install", "colima", "docker"],
                    NeedsElevation: false,
                    NeedsRestart: false)
                {
                    Kind = SetupStepKind.InApp,
                    Environment = path,
                    Duration = "Takes a minute or two.",
                },
            ];
        }

        return
        [
            new SetupStep(
                "colima.start",
                "Start Colima",
                "Creates its virtual machine the first time and starts the engine in it. Dray finds it as soon as it answers.",
                state.BrewBin + "/colima",
                ["start"],
                NeedsElevation: false,
                NeedsRestart: false)
            {
                Kind = SetupStepKind.InApp,
                Environment = path,
                Duration = "The first start downloads a virtual machine image and can take a few minutes.",
            },
        ];
    }

    static IReadOnlyList<SetupStep> OrbStackSteps(MacSetupState state)
    {
        if (state.OrbStack) return [OpenApp("orbstack.start", "Start OrbStack", "OrbStack")];

        return state.BrewPath is { } brew
            ?
            [
                new SetupStep(
                    "orbstack.install",
                    "Install OrbStack",
                    "From Homebrew, which installs the application into /Applications.",
                    brew,
                    ["install", "--cask", "orbstack"],
                    NeedsElevation: false,
                    NeedsRestart: false)
                {
                    Kind = SetupStepKind.InApp,
                    Environment = BrewEnvironment(state),
                    Duration = "Takes a minute or two.",
                },
            ]
            : [Download("orbstack.download", "Download OrbStack", OrbStackDownload)];
    }

    static IReadOnlyList<SetupStep> DesktopSteps(MacSetupState state)
    {
        if (state.DockerDesktop) return [OpenApp("desktop.open", "Start Docker Desktop", "Docker")];

        if (state.BrewPath is not { } brew) return [Download("desktop.download", "Download Docker Desktop", DockerDesktopDownload)];

        var command = $"{brew} install --cask docker-desktop";

        return
        [
            new SetupStep(
                "desktop.install",
                "Install Docker Desktop",
                "From Homebrew. Its installer asks for your password to set up its helper, so it runs in Terminal "
                + "where it can ask.",
                "/bin/bash",
                ["-c", command],
                NeedsElevation: true,
                NeedsRestart: false)
            {
                Kind = SetupStepKind.Terminal,
                ElevationNote = PasswordPrompt,
                Script = command,
                ScriptContext = "In a new Terminal window:",
            },
        ];
    }

    static SetupStep HomebrewStep() => new(
        "brew.install",
        "Install Homebrew",
        "The package manager most Mac developers use, and how the rest of this is installed. Its installer asks "
        + "for your password and to press Return, so it runs in Terminal.",
        "/bin/bash",
        ["-c", InstallHomebrew],
        NeedsElevation: true,
        NeedsRestart: false)
    {
        Kind = SetupStepKind.Terminal,
        ElevationNote = PasswordPrompt,
        Script = InstallHomebrew,
        ScriptContext = "In a new Terminal window:",
    };

    static SetupStep OpenApp(string id, string title, string app) => new(
        id,
        title,
        $"{app} is installed but not running. It takes a moment to start; Dray finds its engine once it answers.",
        "/usr/bin/open",
        ["-a", app],
        NeedsElevation: false,
        NeedsRestart: false)
    {
        Kind = SetupStepKind.InApp,
    };

    static SetupStep Download(string id, string title, string url) => new(
        id,
        title,
        "Homebrew is not installed, so this comes from its maker's site. Install Homebrew first to have Dray do it.",
        "open",
        [url],
        NeedsElevation: false,
        NeedsRestart: false)
    {
        Kind = SetupStepKind.OpenUrl,
        Url = url,
    };

    /// <summary>Homebrew's directory first, then the system's — what a login shell would have.</summary>
    static Dictionary<string, string> BrewEnvironment(MacSetupState state)
        => new() { ["PATH"] = $"{state.BrewBin}:/usr/bin:/bin:/usr/sbin:/sbin" };
}

/// <summary>What Dray observed about a Linux machine's engine.</summary>
/// <param name="HasEngine">Whether an engine answered.</param>
/// <param name="EngineInstalled">Whether <c>dockerd</c> is installed.</param>
/// <param name="SocketExists">Whether the engine's socket is there — installed and started.</param>
/// <param name="InDockerGroup">Whether this user can use the socket without root.</param>
/// <param name="Pkexec">Whether polkit's <c>pkexec</c> is there to ask for privileges in a dialog of the desktop's own.</param>
/// <param name="UserName">Who to add to the docker group.</param>
public sealed record LinuxSetupState(
    bool HasEngine,
    bool EngineInstalled,
    bool SocketExists,
    bool InDockerGroup,
    bool Pkexec,
    string UserName);

/// <summary>
/// The route to an engine on Linux, which needs no virtual machine: Docker's engine runs on the
/// kernel that is already there.
/// <para>
/// Privileges come from polkit (<c>pkexec</c>), which asks for the password in the desktop's own
/// dialog and leaves the command's output for Dray to show. Without polkit Dray has no honest way
/// to ask, so the command is shown with <c>sudo</c> for the user to run themselves.
/// </para>
/// </summary>
public static class LinuxSetupPlan
{
    /// <summary>
    /// Docker's convenience script, then the user into the docker group. The user name is a
    /// positional argument rather than part of the script's text.
    /// </summary>
    public const string InstallScript =
        "set -e\n"
        + "curl -fsSL https://get.docker.com | sh\n"
        + "systemctl enable --now docker\n"
        + "usermod -aG docker \"$1\"\n";

    public static IReadOnlyList<SetupRoute> Routes(LinuxSetupState state)
    {
        if (state.HasEngine) return [];

        return
        [
            new SetupRoute(
                "linux.engine",
                "Docker Engine",
                "Docker's open-source engine, running directly on this machine. Free for any use.",
                Steps(state),
                Recommended: true),
        ];
    }

    static IReadOnlyList<SetupStep> Steps(LinuxSetupState state)
    {
        if (!state.EngineInstalled)
        {
            return
            [
                Privileged(
                    state,
                    "engine.install",
                    "Install Docker Engine",
                    "Docker's own install script, which adds Docker's package repository for this distribution and installs "
                    + "the engine with compose and buildx. Then it starts the engine with the machine, and adds you to the "
                    + "docker group so Dray and your terminal can use it without sudo.",
                    ["sh", "-c", InstallScript, "sh", state.UserName],
                    InstallScript,
                    needsSignIn: true) with
                {
                    Duration = "Takes a minute or two.",
                },
            ];
        }

        if (!state.SocketExists)
        {
            return
            [
                Privileged(
                    state,
                    "engine.start",
                    "Start Docker Engine",
                    "It is installed but not running. This starts it, and starts it with the machine from now on.",
                    ["systemctl", "enable", "--now", "docker"],
                    null,
                    needsSignIn: false),
            ];
        }

        if (!state.InDockerGroup)
        {
            return
            [
                Privileged(
                    state,
                    "engine.group",
                    "Let your account use Docker",
                    "The engine is running, but its socket belongs to root and the docker group, and you are in neither. "
                    + "This adds you to the group. It takes effect the next time you sign in.",
                    ["usermod", "-aG", "docker", state.UserName],
                    null,
                    needsSignIn: true),
            ];
        }

        return [];
    }

    /// <summary>
    /// A step that needs root: through polkit and in Dray where polkit exists, shown for sudo
    /// where it does not.
    /// </summary>
    static SetupStep Privileged(
        LinuxSetupState state, string id, string title, string why, string[] command, string? script, bool needsSignIn)
    {
        if (state.Pkexec)
        {
            return new SetupStep(id, title, why, "/usr/bin/pkexec", command, NeedsElevation: true, NeedsRestart: needsSignIn)
            {
                Kind = SetupStepKind.InApp,
                Script = script,
                ScriptContext = "As root, once you have given your password:",
                ElevationNote = PasswordPrompt,
                RestartNote = SignInNote,
            };
        }

        var shown = script is null
            ? $"sudo {string.Join(' ', command)}"
            : $"sudo sh -c '{script.Replace("\"$1\"", state.UserName).TrimEnd('\n').Replace("\n", "; ")}'";

        return new SetupStep(id, title, why + " Dray has no way to ask for your password on this desktop, so run it yourself:", "sudo", command, NeedsElevation: true, NeedsRestart: needsSignIn)
        {
            Kind = SetupStepKind.Manual,
            Script = shown,
            ScriptContext = "In a terminal:",
            ElevationNote = "sudo asks for your password.",
            RestartNote = SignInNote,
        };
    }

    const string PasswordPrompt = "Asks for your password, in your desktop's own dialog.";

    /// <summary>A group joined takes effect at the next sign-in, not at a restart.</summary>
    const string SignInNote = "Takes effect when you next sign in.";
}

/// <summary>Looks at a Mac and reports which engine routes are installed. File checks only; no process is run.</summary>
public sealed class MacSetupReader(IDockerConfigSource? source = null)
{
    readonly IDockerConfigSource _source = source ?? new SystemDockerConfigSource();

    /// <summary>Apple silicon's prefix first, then Intel's.</summary>
    static readonly string[] BrewLocations = ["/opt/homebrew/bin/brew", "/usr/local/bin/brew"];

    public MacSetupState Read(IReadOnlyList<DockerHost> hosts)
    {
        var brew = BrewLocations.FirstOrDefault(_source.FileExists);
        var bin = brew is null ? null : brew[..brew.LastIndexOf('/')];

        return new MacSetupState(
            HasEngine: hosts.Any(h => h.State == HostConnectionState.Connected),
            BrewPath: brew,
            Colima: bin is not null && _source.FileExists(bin + "/colima"),
            DockerCli: (bin is not null && _source.FileExists(bin + "/docker")) || _source.FileExists("/usr/local/bin/docker"),
            DockerDesktop: _source.DirectoryExists("/Applications/Docker.app"),
            OrbStack: _source.DirectoryExists("/Applications/OrbStack.app"));
    }
}

/// <summary>Looks at a Linux machine and reports how far it is from a usable engine.</summary>
public sealed class LinuxSetupReader(IProcessRunner? runner = null, IDockerConfigSource? source = null)
{
    readonly IProcessRunner _runner = runner ?? new SystemProcessRunner();
    readonly IDockerConfigSource _source = source ?? new SystemDockerConfigSource();

    public async Task<LinuxSetupState> ReadAsync(IReadOnlyList<DockerHost> hosts, CancellationToken ct = default)
    {
        bool inGroup;

        try
        {
            // `id -nG` lists the groups this session has, which is what matters: a group added
            // since signing in is not in effect yet, and reporting it as done would be wrong.
            var groups = await _runner.RunAsync("id", ["-nG"], null, ct).ConfigureAwait(false);
            inGroup = groups.ExitCode == 0
                && groups.StandardOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains("docker");
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            inGroup = false;
        }

        return new LinuxSetupState(
            HasEngine: hosts.Any(h => h.State == HostConnectionState.Connected),
            EngineInstalled: _source.FileExists("/usr/bin/dockerd") || _source.FileExists("/usr/local/bin/dockerd"),
            SocketExists: _source.SocketExists("/var/run/docker.sock"),
            InDockerGroup: inGroup,
            Pkexec: _source.FileExists("/usr/bin/pkexec"),
            UserName: Environment.UserName);
    }
}
