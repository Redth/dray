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

/// <summary>
/// What Dray can do about something that is missing.
/// <para>
/// The command is <b>shown before it is run</b> and never run without being asked. Dray is a
/// client (PRODUCT.md): it does not own an engine, and a setup step is help getting one rather
/// than Dray taking responsibility for the machine. Showing the command also means the user can
/// run it themselves, paste it into a ticket, or decide Dray is wrong about it.
/// </para>
/// </summary>
/// <param name="Id">Stable identity, for tests and for tracking which step is running.</param>
/// <param name="Title">What this does, in the user's terms.</param>
/// <param name="Why">Why it is needed. Never omitted — a setup step with no reason is a demand.</param>
/// <param name="FileName">The program to run.</param>
/// <param name="Arguments">Its arguments, unquoted.</param>
/// <param name="NeedsElevation">Whether this raises a User Account Control prompt.</param>
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
    /// <summary>The command as the user would type it, for the confirmation and for copying.</summary>
    public string CommandLine => Arguments.Count == 0
        ? FileName
        : $"{FileName} {string.Join(' ', Arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}";
}

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
    /// <summary>Distributions the user might actually put an engine in.</summary>
    public IReadOnlyList<WslDistro> UsableDistros => [.. Distros.Where(d => d.CanHostAnEngine)];

    /// <summary>
    /// Nothing to suggest: both kinds of container are already reachable.
    /// <para>
    /// Keyed on engines answering rather than on features being enabled, because that is the only
    /// signal that is not an inference. A machine where everything works must not be shown a
    /// setup checklist.
    /// </para>
    /// </summary>
    public bool IsComplete => HasLinuxEngine && HasWindowsEngine;
}

/// <summary>
/// Turns what was observed into an ordered list of things the user could do about it.
/// <para>
/// Two independent tracks, because they are two independent engines that share nothing (see
/// docs/ARCHITECTURE.md section 2.4). Someone who only wants Linux containers must not be shown
/// Windows-container steps, and the reverse.
/// </para>
/// <para>
/// Every command here is <b>idempotent</b>, which is what makes an <see cref="SetupFact.Unknown"/>
/// safe: running DISM against a feature that is already enabled reports that it is already enabled
/// and changes nothing. Dray therefore never has to guess in order to offer a step.
/// </para>
/// </summary>
public static class WindowsSetupPlan
{
    /// <summary>
    /// What to do to run Linux containers, in order. Empty when one is already reachable.
    /// </summary>
    public static IReadOnlyList<SetupStep> ForLinuxContainers(WindowsSetupState state)
    {
        if (state.HasLinuxEngine) return [];

        List<SetupStep> steps = [];

        if (state.Wsl != SetupFact.Present)
        {
            steps.Add(new SetupStep(
                "wsl.install",
                "Install WSL",
                "Linux containers need a Linux kernel, so they run inside a virtual machine. WSL2 is "
                + "the one every container product on Windows uses for this.",
                "wsl.exe",

                // --no-distribution because choosing one is the next step and a separate decision.
                // Without it this installs Ubuntu whether or not that is what the user wanted.
                ["--install", "--no-distribution"],
                NeedsElevation: true,
                NeedsRestart: true));
        }

        // A distribution is only worth suggesting once WSL exists to hold it. Where WSL is already
        // installed and has nothing in it, this is the whole of what is missing.
        if (state.Wsl == SetupFact.Present && state.UsableDistros.Count == 0)
        {
            steps.Add(new SetupStep(
                "wsl.distro",
                "Install a Linux distribution",
                "WSL is installed but has no distribution to run anything in. Ubuntu is the usual "
                + "choice and the one most container documentation assumes.",
                "wsl.exe",
                ["--install", "-d", "Ubuntu"],
                NeedsElevation: true,
                NeedsRestart: false));
        }

        return steps;
    }

    /// <summary>What to do to run Windows containers, in order. Empty when one is already reachable.</summary>
    public static IReadOnlyList<SetupStep> ForWindowsContainers(WindowsSetupState state)
    {
        if (state.HasWindowsEngine) return [];

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

        return steps;
    }

    /// <summary>
    /// What Dray will not do for the user, and why — shown next to the steps it will.
    /// <para>
    /// Installing an engine is where the line is. Dray does not ship one and does not install one:
    /// which engine to run is a licensing decision as much as a technical one, the installers are
    /// large and interactive, and a half-finished engine install is a much worse state to be left
    /// in than no engine at all.
    /// </para>
    /// </summary>
    public static string? EngineAdvice(WindowsSetupState state)
    {
        if (state.HasWindowsEngine) return null;

        return "Windows ships the container platform but no engine to drive it, so one still has to "
            + "be installed. On Windows 11 that means Docker Desktop switched into Windows-container "
            + "mode, which needs Pro or Enterprise with Hyper-V enabled. On Windows Server it means "
            + "Microsoft's install-docker-ce.ps1 script, which enables the feature and installs the "
            + "engine together. Dray drives whichever you choose; it does not install one.";
    }
}
