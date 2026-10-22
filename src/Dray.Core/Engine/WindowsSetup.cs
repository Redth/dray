namespace Dray.Core.Engine;

/// <summary>
/// A thing Dray looked for and what it found.
/// <para>
/// Three states rather than a bool, because most of what matters here cannot be established
/// without elevation. <see cref="Unknown"/> is common and is not a failure — it means the answer
/// costs an administrator prompt that nobody asked for, and the step below is safe to run anyway.
/// </para>
/// </summary>
public enum SetupFact
{
    /// <summary>Dray could not tell without asking for privileges it has not been given.</summary>
    Unknown,

    Present,

    Absent,
}

/// <summary>How a setup step is carried out, which decides what the user sees while it runs.</summary>
public enum SetupStepKind
{
    /// <summary>
    /// Run elevated, in its own visible window. Elevation means <c>UseShellExecute</c>, which means
    /// the output cannot be brought into Dray — so the window is where it is shown.
    /// </summary>
    Window,

    /// <summary>
    /// Run by Dray with its output streamed into the page. For everything that does not need Dray
    /// itself to be elevated: installing a distribution, installing an engine inside one as its
    /// root, and installers that raise their own prompt.
    /// </summary>
    InApp,

    /// <summary>Ask for a Linux username and password, then create the account inside the distribution.</summary>
    CreateLinuxUser,

    /// <summary>Start Docker Desktop or switch its engine, through <see cref="EngineManager"/> so the selection follows.</summary>
    DockerDesktop,

    /// <summary>Nothing to install: the engine is there and only needs choosing in the picker.</summary>
    Connect,

    /// <summary>
    /// Run in a new Terminal window on macOS. For an installer that asks for the user's password at
    /// its own prompt — Homebrew's, a cask with a privileged helper — which needs a terminal to ask
    /// in, and which Dray must never be the one to collect.
    /// </summary>
    Terminal,

    /// <summary>Open a download page in the browser. For an installer only its vendor's site provides.</summary>
    OpenUrl,

    /// <summary>
    /// Shown for the user to run themselves, with a copy button. Where Dray has no way to ask for
    /// privileges it would need — a Linux desktop without polkit — and guessing at one would be worse
    /// than saying so.
    /// </summary>
    Manual,
}

/// <summary>
/// One thing Dray can do about something that is missing.
/// <para>
/// The command is <b>shown before it is run</b> and never run without being asked. Showing it also
/// means the user can run it themselves, paste it into a ticket, or decide Dray is wrong about it.
/// </para>
/// </summary>
/// <param name="Id">Stable identity, for tests and for tracking which step is running.</param>
/// <param name="Title">What this does, in the user's terms.</param>
/// <param name="Why">Why it is needed. Never omitted — a setup step with no reason is a demand.</param>
/// <param name="FileName">The program to run.</param>
/// <param name="Arguments">Its arguments, unquoted.</param>
/// <param name="NeedsElevation">Whether a User Account Control prompt will appear, whoever raises it.</param>
/// <param name="NeedsRestart">
/// Whether the machine must restart before the change takes effect. Said up front rather than
/// discovered afterwards, because a step that silently needs a reboot reads as a step that failed.
/// </param>
public sealed record SetupStep(
    string Id,
    string Title,
    string Why,
    string FileName,
    IReadOnlyList<string> Arguments,
    bool NeedsElevation,
    bool NeedsRestart)
{
    public SetupStepKind Kind { get; init; } = SetupStepKind.Window;

    /// <summary>The WSL distribution this step acts on, for the steps that act on one.</summary>
    public string? Distro { get; init; }

    /// <summary>The Docker Desktop engine this step leaves current, for <see cref="SetupStepKind.DockerDesktop"/>.</summary>
    public DesktopEngineMode? DesktopMode { get; init; }

    /// <summary>
    /// A shell script run inside the distribution, when that is what this step is. Shown whole,
    /// because a script that runs as root is exactly the thing a user should be able to read first.
    /// </summary>
    public string? Script { get; init; }

    /// <summary>
    /// Where and as whom <see cref="Script"/> runs, said above it: "As root inside Ubuntu", "In a new
    /// Terminal window". What a script does depends on both, so neither is left to be inferred.
    /// </summary>
    public string? ScriptContext { get; init; }

    /// <summary>
    /// How long it takes, when that is long enough to be worth saying. A step that runs for eight
    /// minutes with three lines of output looks hung unless it was said beforehand.
    /// </summary>
    public string? Duration { get; init; }

    /// <summary>A step that helps but is not needed for an engine to work.</summary>
    public bool Optional { get; init; }

    /// <summary>
    /// What the privilege prompt is, in this platform's words. Defaults to Windows': a Mac's or a
    /// Linux desktop's asks for the user's own password, not for "administrator".
    /// </summary>
    public string ElevationNote { get; init; } = "Asks for administrator.";

    /// <summary>What finishing the change takes. A restart on Windows; signing in again for a Linux group.</summary>
    public string RestartNote { get; init; } = "Needs a restart to take effect.";

    /// <summary>The page to open, for <see cref="SetupStepKind.OpenUrl"/>.</summary>
    public string? Url { get; init; }

    /// <summary>
    /// Variables to set for this step alone. On macOS an app opened from the Finder has a PATH
    /// without Homebrew's directory in it, so <c>colima start</c> — which runs <c>limactl</c> by name —
    /// would fail to find its own dependency. The step carries the PATH it needs rather than Dray
    /// changing its own.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>The command as the user would type it, for the confirmation and for copying.</summary>
    public string CommandLine => Arguments.Count == 0
        ? FileName
        : $"{FileName} {string.Join(' ', Arguments.Select(Quote))}";

    static string Quote(string argument) => argument.Contains(' ') || argument.Contains('\n') ? $"\"{argument}\"" : argument;
}

/// <summary>
/// One way of getting an engine, as an ordered list of steps.
/// <para>
/// A track can have more than one route — Linux containers come from Docker Desktop or from an
/// engine installed in a WSL distribution — and they are offered side by side rather than merged,
/// because the user is choosing between them and a merged list hides that there is a choice.
/// </para>
/// </summary>
/// <param name="Id">Stable identity.</param>
/// <param name="Title">The route, named as the user would name it.</param>
/// <param name="Summary">What it is and what it costs, in a sentence or two.</param>
/// <param name="Steps">What is left to do on this route. Empty when it needs nothing.</param>
/// <param name="Recommended">The route Dray would take on this machine.</param>
public sealed record SetupRoute(string Id, string Title, string Summary, IReadOnlyList<SetupStep> Steps, bool Recommended);

/// <summary>What a WSL distribution has inside it, as far as running an engine goes.</summary>
public enum DistroEngine
{
    /// <summary>Not looked at — the distribution was not started to find out.</summary>
    Unknown,

    /// <summary>No <c>dockerd</c> installed.</summary>
    None,

    /// <summary>Installed, and not answering.</summary>
    Stopped,

    Running,
}

/// <summary>What Dray found inside one distribution.</summary>
/// <param name="Distro">The distribution.</param>
/// <param name="Engine">Whether it has an engine, and whether that is running.</param>
/// <param name="HasUser">
/// Whether it has an ordinary user account. Null when not looked at. A distribution installed with
/// <c>--no-launch</c> has none until someone creates one, and opening it prompts for one.
/// </param>
public sealed record DistroSetup(WslDistro Distro, DistroEngine Engine, bool? HasUser);

/// <summary>What Dray observed about this machine's container support.</summary>
/// <param name="Wsl">Whether WSL itself is installed.</param>
/// <param name="WslVersion">What <c>wsl --version</c> reported, when it reported anything.</param>
/// <param name="Distros">The distributions WSL knows about, infrastructure ones included.</param>
/// <param name="ContainersFeature">
/// Whether the <c>Containers</c> Windows feature looks enabled. Best-effort: the authoritative
/// check needs elevation, so this is inferred from the Host Compute Service being on disk.
/// </param>
/// <param name="DockerCli">Whether a <c>docker</c> command exists to drive any of this from a terminal.</param>
/// <param name="HasLinuxEngine">Whether an engine running Linux containers was actually reached.</param>
/// <param name="HasWindowsEngine">Whether an engine running Windows containers was actually reached.</param>
public sealed record WindowsSetupState(
    SetupFact Wsl,
    string? WslVersion,
    IReadOnlyList<WslDistro> Distros,
    SetupFact ContainersFeature,
    SetupFact DockerCli,
    bool HasLinuxEngine,
    bool HasWindowsEngine)
{
    /// <summary>What Docker Desktop is doing.</summary>
    public DockerDesktopState Desktop { get; init; } = DockerDesktopState.NotInstalled;

    /// <summary>What was found inside the distribution the WSL route targets. Null when it was not looked at.</summary>
    public DistroSetup? Target { get; init; }

    /// <summary>Windows Home, which cannot run Windows containers at all: no Hyper-V and no Containers feature.</summary>
    public bool IsHomeEdition { get; init; }

    /// <summary>Windows Server, where Windows containers have an engine of their own to install.</summary>
    public bool IsServer { get; init; }

    /// <summary>Distributions the user might actually put an engine in.</summary>
    public IReadOnlyList<WslDistro> UsableDistros => [.. Distros.Where(d => d.CanHostAnEngine)];

    /// <summary>
    /// The distribution the WSL route works in: the default one if it can host an engine, else a
    /// running one, else the first. One rather than all, because the route ends in one engine.
    /// </summary>
    public WslDistro? TargetDistro => UsableDistros
        .OrderByDescending(d => d.IsDefault)
        .ThenByDescending(d => d.IsRunning)
        .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();

    /// <summary>
    /// Nothing to suggest: both kinds of container are already reachable.
    /// <para>
    /// Keyed on engines answering rather than on features being enabled, because that is the only
    /// signal that is not an inference. A machine where everything works must not be shown a
    /// setup checklist.
    /// </para>
    /// </summary>
    public bool IsComplete => HasLinuxEngine && (HasWindowsEngine || IsHomeEdition);
}

/// <summary>
/// Turns what was observed into the routes the user could take, each an ordered list of steps.
/// <para>
/// Two independent tracks, because they are two independent engines that share nothing (see
/// docs/ARCHITECTURE.md section 2.4). Someone who only wants Linux containers must not be shown
/// Windows-container steps, and the reverse.
/// </para>
/// <para>
/// Every command here is <b>idempotent</b> or refuses harmlessly when its work is already done,
/// which is what makes an <see cref="SetupFact.Unknown"/> safe: running DISM against a feature that
/// is already enabled reports that and changes nothing. Dray therefore never has to guess in order
/// to offer a step.
/// </para>
/// </summary>
public static class WindowsSetupPlan
{
    /// <summary>The distribution offered when there is none. The one most container documentation assumes.</summary>
    public const string DefaultDistro = "Ubuntu";

    /// <summary>Docker Desktop's package in winget, the package manager that ships with Windows.</summary>
    const string DockerDesktopPackage = "Docker.DockerDesktop";

    /// <summary>
    /// Installs Docker Engine inside a distribution, as its root user.
    /// <para>
    /// Docker's own convenience script rather than the distribution's package: it installs the
    /// current engine with the compose and buildx plugins, on every distribution Docker supports,
    /// which a distribution's own <c>docker.io</c> does not. It pauses for twenty seconds on WSL to
    /// suggest Docker Desktop, which is why the step says how long it takes.
    /// </para>
    /// <para>
    /// The first ordinary user is added to the <c>docker</c> group, so <c>docker</c> works in their
    /// own shell without <c>sudo</c>. And the engine needs systemd to start with the distribution;
    /// current Ubuntu has it on already, and a distribution without it gets it switched on and is
    /// restarted once — the marker line tells Dray to do that.
    /// </para>
    /// </summary>
    public const string InstallEngineScript =
        "set -e\n"
        + "command -v curl >/dev/null || { apt-get update && apt-get install -y curl; }\n"
        + "curl -fsSL https://get.docker.com -o /tmp/get-docker.sh\n"
        + "sh /tmp/get-docker.sh\n"
        + "u=$(getent passwd | awk -F: '$3>=1000 && $3<60000 {print $1; exit}')\n"
        + "if [ -n \"$u\" ]; then usermod -aG docker \"$u\"; echo \"Added $u to the docker group.\"; fi\n"
        + "if [ \"$(cat /proc/1/comm)\" != systemd ]; then printf '\\n[boot]\\nsystemd=true\\n' >> /etc/wsl.conf; echo " + RestartDistroMarker + "; fi\n";

    /// <summary>Starts an installed engine that is not running.</summary>
    public const string StartEngineScript = "systemctl start docker 2>/dev/null || service docker start";

    /// <summary>
    /// What a script prints when the distribution must be restarted for its change to apply. A
    /// line rather than an exit code, because the script succeeded.
    /// </summary>
    public const string RestartDistroMarker = "dray:restart-distro";

    /// <summary>The routes to Linux containers, the one Dray would take first. Empty when an engine is already reachable.</summary>
    public static IReadOnlyList<SetupRoute> LinuxRoutes(WindowsSetupState state)
    {
        if (state.HasLinuxEngine) return [];

        // Docker Desktop first when it is already installed: it is one action away. Otherwise the
        // engine in WSL, which needs no licence and no extra application.
        var desktopFirst = state.Desktop.Installed;

        var wsl = new SetupRoute(
            "linux.wsl",
            "Docker Engine in WSL",
            "Docker's open-source engine, installed inside a Linux distribution of your own. Free for any use, "
            + "and nothing else runs in the background. Dray connects to it directly.",
            ForLinuxContainers(state),
            Recommended: !desktopFirst);

        var desktop = new SetupRoute(
            "linux.desktop",
            "Docker Desktop",
            "Docker's application, which runs its own WSL virtual machine and switches between Linux and Windows "
            + "containers. Free for personal use, education and small businesses; larger organisations need a paid "
            + "subscription.",
            DesktopSteps(state, DesktopEngineMode.Linux),
            Recommended: desktopFirst);

        return desktopFirst ? [desktop, wsl] : [wsl, desktop];
    }

    /// <summary>The routes to Windows containers. Empty when an engine is reachable, or on Windows Home, where there is none.</summary>
    public static IReadOnlyList<SetupRoute> WindowsRoutes(WindowsSetupState state)
    {
        if (state.HasWindowsEngine || state.IsHomeEdition) return [];

        return
        [
            new SetupRoute(
                "windows.engine",
                state.IsServer ? "Docker Engine for Windows Server" : "Docker Desktop in Windows-container mode",
                state.IsServer
                    ? "Microsoft's script enables the Containers feature and installs Docker's engine as a Windows service."
                    : "Windows containers run on this machine's own kernel. On Windows 11 the engine for them comes with Docker Desktop.",
                ForWindowsContainers(state),
                Recommended: true),
        ];
    }

    /// <summary>
    /// The steps to an engine in WSL, in order. Empty when an engine is already reachable.
    /// <para>
    /// Each step appears only once the one before it is done, because each needs what the last one
    /// made: a distribution needs WSL, an engine needs a distribution.
    /// </para>
    /// </summary>
    public static IReadOnlyList<SetupStep> ForLinuxContainers(WindowsSetupState state)
    {
        if (state.HasLinuxEngine) return [];

        if (state.Wsl != SetupFact.Present)
        {
            return
            [
                new SetupStep(
                    "wsl.install",
                    "Install WSL",
                    "Linux containers need a Linux kernel, so they run inside a virtual machine. WSL2 is "
                    + "the one every container product on Windows uses for this.",
                    "wsl.exe",

                    // --no-distribution because choosing one is the next step and a separate decision.
                    // Without it this installs Ubuntu whether or not that is what the user wanted.
                    ["--install", "--no-distribution"],
                    NeedsElevation: true,
                    NeedsRestart: true),
            ];
        }

        if (state.TargetDistro is not { } distro)
        {
            return
            [
                new SetupStep(
                    "wsl.distro",
                    $"Install {DefaultDistro}",
                    "WSL is installed but has no distribution to run an engine in. Ubuntu is the usual "
                    + "choice and the one most container documentation assumes.",
                    "wsl.exe",

                    // --no-launch so nothing opens a console asking for a username: Dray asks for one
                    // in the next step, in its own window. Not elevated, and must not be: a
                    // distribution is registered to the user who installs it, and an administrator
                    // prompt answered with another account would install it for that account.
                    ["--install", DefaultDistro, "--no-launch"],
                    NeedsElevation: false,
                    NeedsRestart: false)
                {
                    Kind = SetupStepKind.InApp,
                    Distro = DefaultDistro,
                    Duration = "Downloads Ubuntu from Microsoft and can take several minutes, with little to show until it is done.",
                },
            ];
        }

        var found = state.Target is { } target && target.Distro.Name == distro.Name ? target : new DistroSetup(distro, DistroEngine.Unknown, null);

        List<SetupStep> steps = [];

        if (found.HasUser == false)
        {
            steps.Add(new SetupStep(
                "linux.user",
                $"Create your account in {distro.Name}",
                "A new distribution has only its root account until someone creates one. Dray can make it "
                + "here, with the same groups Ubuntu gives its first user, so opening the distribution "
                + "later does not stop to ask. Your password goes straight to the distribution and Dray "
                + "keeps no copy.",
                "wsl.exe",
                ["-d", distro.Name, "-u", "root", "--exec", "useradd", "-m", "-s", "/bin/bash", "<name>"],
                NeedsElevation: false,
                NeedsRestart: false)
            {
                Kind = SetupStepKind.CreateLinuxUser,
                Distro = distro.Name,
                Optional = true,
            });
        }

        switch (found.Engine)
        {
            case DistroEngine.Running:
                steps.Add(new SetupStep(
                    "engine.connect",
                    $"Use the engine in {distro.Name}",
                    "Docker is installed and running in this distribution. Choosing it connects Dray to it.",
                    "wsl.exe",
                    ["-d", distro.Name, "docker", "ps"],
                    NeedsElevation: false,
                    NeedsRestart: false)
                {
                    Kind = SetupStepKind.Connect,
                    Distro = distro.Name,
                });
                break;

            case DistroEngine.Stopped:
                steps.Add(new SetupStep(
                    "engine.start",
                    $"Start Docker in {distro.Name}",
                    "Docker is installed in this distribution but its engine is not running.",
                    "wsl.exe",
                    ["-d", distro.Name, "-u", "root", "--exec", "sh", "-c", StartEngineScript],
                    NeedsElevation: false,
                    NeedsRestart: false)
                {
                    Kind = SetupStepKind.InApp,
                    Distro = distro.Name,
                    Script = StartEngineScript,
                    ScriptContext = $"As root inside {distro.Name}:",
                });
                break;

            // Unknown is offered the install too. The script is Docker's own and re-running it on a
            // distribution that already has the engine upgrades it in place rather than breaking it.
            default:
                steps.Add(new SetupStep(
                    "engine.install",
                    $"Install Docker Engine in {distro.Name}",
                    "Docker's own install script, run as the distribution's root user. It installs the "
                    + "engine with compose and buildx, and starts it with the distribution from then on. "
                    + "If Docker Desktop's WSL integration is turned on for this distribution, turn it off "
                    + "first — two engines in one distribution both want the docker command.",
                    "wsl.exe",
                    ["-d", distro.Name, "-u", "root", "--exec", "sh", "-c", InstallEngineScript],
                    NeedsElevation: false,
                    NeedsRestart: false)
                {
                    Kind = SetupStepKind.InApp,
                    Distro = distro.Name,
                    Script = InstallEngineScript,
                    ScriptContext = $"As root inside {distro.Name}:",
                    Duration = "Takes a minute or two, including a twenty-second pause in which Docker's script suggests Docker Desktop. It carries on by itself.",
                });
                break;
        }

        return steps;
    }

    /// <summary>The steps to Windows containers, in order. Empty when one is already reachable.</summary>
    public static IReadOnlyList<SetupStep> ForWindowsContainers(WindowsSetupState state)
    {
        if (state.HasWindowsEngine || state.IsHomeEdition) return [];

        List<SetupStep> steps = [];

        if (state.ContainersFeature != SetupFact.Present)
        {
            steps.Add(new SetupStep(
                "feature.containers",
                "Enable the Containers feature",
                "Windows containers run on this machine's own kernel, and this is the Windows feature "
                + "that makes that possible. It is not an engine — that is the next step.",
                "dism.exe",
                ["/online", "/enable-feature", "/featurename:Containers", "/all", "/norestart"],
                NeedsElevation: true,
                NeedsRestart: true));
        }

        if (state.IsServer && !state.Desktop.Installed)
        {
            steps.Add(new SetupStep(
                "engine.windows",
                "Install Docker Engine for Windows",
                "Microsoft's install-docker-ce.ps1, the route Microsoft documents for Windows Server. It "
                + "installs Docker's engine as a Windows service.",
                "powershell.exe",
                [
                    "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
                    "Invoke-WebRequest -UseBasicParsing https://raw.githubusercontent.com/microsoft/Windows-Containers/Main/helpful_tools/Install-DockerCE/install-docker-ce.ps1 -OutFile $env:TEMP\\install-docker-ce.ps1; & $env:TEMP\\install-docker-ce.ps1",
                ],
                NeedsElevation: true,
                NeedsRestart: true));

            return steps;
        }

        steps.AddRange(DesktopSteps(state, DesktopEngineMode.Windows));
        return steps;
    }

    /// <summary>What Docker Desktop needs before it is running <paramref name="mode"/>.</summary>
    static IReadOnlyList<SetupStep> DesktopSteps(WindowsSetupState state, DesktopEngineMode mode)
    {
        var desktop = state.Desktop;

        if (!desktop.Installed)
        {
            return
            [
                new SetupStep(
                    "desktop.install",
                    "Install Docker Desktop",
                    "Installed with winget, the package manager that comes with Windows, from Docker's own "
                    + "package. Its installer asks for administrator, and may ask you to sign out and back "
                    + "in when it finishes.",
                    "winget.exe",
                    ["install", "--exact", "--id", DockerDesktopPackage, "--accept-package-agreements", "--accept-source-agreements"],
                    NeedsElevation: true,
                    NeedsRestart: false)
                {
                    Kind = SetupStepKind.InApp,
                    Duration = "Downloads about half a gigabyte, so it can take several minutes.",
                },
            ];
        }

        if (!desktop.Running)
        {
            return
            [
                new SetupStep(
                    "desktop.start",
                    "Start Docker Desktop",
                    "Docker Desktop is installed but not running.",
                    "docker",
                    ["desktop", "start"],
                    NeedsElevation: false,
                    NeedsRestart: false)
                {
                    Kind = SetupStepKind.DockerDesktop,
                    DesktopMode = mode,
                },
            ];
        }

        if (desktop.NeedsSwitchTo(mode))
        {
            return
            [
                new SetupStep(
                    mode == DesktopEngineMode.Linux ? "desktop.linux" : "desktop.windows",
                    $"Switch Docker Desktop to {DockerDesktop.Label(mode)}",
                    $"Docker Desktop runs one engine at a time and is running {DockerDesktop.Label(desktop.Mode!.Value)}. "
                    + "Switching stops that engine and starts this one; containers on the other engine stop with it.",
                    "docker",
                    ["desktop", "engine", "use", DockerDesktop.Word(mode)],
                    NeedsElevation: false,
                    NeedsRestart: false)
                {
                    Kind = SetupStepKind.DockerDesktop,
                    DesktopMode = mode,
                },
            ];
        }

        return [];
    }

    /// <summary>
    /// What to say about Windows containers when there is no step that would help.
    /// </summary>
    public static string? EngineAdvice(WindowsSetupState state)
    {
        if (state.HasWindowsEngine) return null;

        if (state.IsHomeEdition)
        {
            return "Windows containers need Windows 11 Pro, Enterprise or Education. Home has neither Hyper-V "
                + "nor the Containers feature they run on, so there is nothing to install here — Linux "
                + "containers work on every edition.";
        }

        return null;
    }
}
