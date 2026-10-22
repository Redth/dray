namespace Dray.Core.Model;

/// <summary>
/// The raw lifecycle state Docker reports on a container.
/// </summary>
public enum DockerState
{
    Created,
    Running,
    Paused,
    Restarting,
    Removing,
    Exited,
    Dead,

    /// <summary>Not a Docker state — the host serving this container is unreachable.</summary>
    Unknown,
}

/// <summary>Health as reported by a container's HEALTHCHECK, when it has one.</summary>
public enum DockerHealth
{
    None,
    Starting,
    Healthy,
    Unhealthy,
}

/// <summary>
/// Which of the four pill treatments a state uses. Deliberately not a colour: the UI layer maps
/// this onto <c>--ok-tint</c> / <c>--warn-tint</c> / <c>--danger-tint</c> / <c>--neutral-tint</c>
/// so the vocabulary and the palette stay independently testable.
/// </summary>
public enum StateTone
{
    Neutral,
    Ok,
    Warn,
    Danger,
}

/// <summary>
/// One row of DESIGN.md section 2.4 — the binding container-state vocabulary.
/// <para>
/// Every state is tint + glyph + word. Never colour alone: a greyscale screenshot must stay
/// legible, and roughly 8% of the men who use this app cannot rely on hue. <see cref="Glyph"/> is
/// what makes that true, so it is never optional and never decorative.
/// </para>
/// </summary>
public sealed record ContainerStatus(
    StateTone Tone,
    string Glyph,
    string Word,
    string? Detail = null)
{
    /// <summary>The full label a pill renders, e.g. "Exited 137 · killed (out of memory)".</summary>
    public string Label => Detail is null ? Word : $"{Word} · {Detail}";

    /// <summary>
    /// True when the row itself should be dimmed rather than just the pill — the host is gone, so
    /// every other column is stale rather than wrong.
    /// </summary>
    public bool IsStale => Tone == StateTone.Neutral && Word == "Unreachable";
}

public static class ContainerStatusVocabulary
{
    /// <summary>
    /// Resolve the pill for a container. Order matters: health outranks the lifecycle state for a
    /// running container, because "running but unhealthy" is the case a user most needs to see.
    /// </summary>
    /// <param name="state">Docker's lifecycle state.</param>
    /// <param name="health">Health status, or <see cref="DockerHealth.None"/> when no healthcheck.</param>
    /// <param name="exitCode">Exit code, meaningful only when <paramref name="state"/> is Exited.</param>
    /// <param name="os">
    /// The container's OS. It decides what an exit code means: 137 is a Linux OOM kill and
    /// nothing at all on Windows, where the same field carries an NTSTATUS.
    /// </param>
    public static ContainerStatus Resolve(
        DockerState state,
        DockerHealth health = DockerHealth.None,
        int? exitCode = null,
        ContainerOs os = ContainerOs.Linux)
        => state switch
        {
            DockerState.Running => health switch
            {
                DockerHealth.Starting => new(StateTone.Warn, "◐", "Starting"),
                DockerHealth.Unhealthy => new(StateTone.Danger, "▲", "Unhealthy"),
                _ => new(StateTone.Ok, "●", "Running"),
            },

            DockerState.Restarting => new(StateTone.Warn, "↻", "Restarting"),
            DockerState.Paused => new(StateTone.Warn, "‖", "Paused"),
            DockerState.Created => new(StateTone.Neutral, "○", "Created"),
            DockerState.Removing => new(StateTone.Warn, "◌", "Removing"),
            DockerState.Dead => new(StateTone.Danger, "✕", "Dead"),

            // A clean exit is unremarkable; a non-zero exit is the thing the user came here for.
            DockerState.Exited when exitCode is null or 0 => new(StateTone.Neutral, "■", "Exited"),
            DockerState.Exited => new(StateTone.Danger, "■", $"Exited {exitCode}", ExplainExitCode(exitCode!.Value, os)),

            _ => new(StateTone.Neutral, "⚠", "Unreachable"),
        };

    /// <summary>
    /// Plain-language expansion of an exit code. PRODUCT.md: Dray says "killed (out of memory)"
    /// where other tools say "Something went wrong".
    /// <para>
    /// Which vocabulary applies is decided by the container's OS, not by a range check. The two
    /// overlap: 3 is a plain failure on Linux and <c>STATUS_CONTROL_C_EXIT</c>'s neighbour on
    /// Windows, and 139 is a segfault on Linux and nothing in particular on Windows. Reading a
    /// Windows code through the Linux table produces a confident sentence about a signal that
    /// does not exist on that platform, which is worse than saying nothing.
    /// </para>
    /// </summary>
    public static string? ExplainExitCode(int code, ContainerOs os = ContainerOs.Linux)
        => os == ContainerOs.Windows ? ExplainWindowsExitCode(code) : ExplainLinuxExitCode(code);

    /// <summary>
    /// Codes above 128 are <c>128 + signal</c> — the shell convention Docker follows for a process
    /// terminated by a signal.
    /// </summary>
    static string? ExplainLinuxExitCode(int code) => code switch
    {
        0 => null,
        1 => "general error",
        2 => "misuse of a shell builtin",
        125 => "the docker run command itself failed",
        126 => "the command could not be invoked",
        127 => "command not found",

        // 128 + signal
        130 => "interrupted (SIGINT)",
        137 => "killed (out of memory)",
        139 => "segmentation fault",
        143 => "stopped (SIGTERM)",
        > 128 and < 165 => $"killed by signal {code - 128}",

        _ => null,
    };

    /// <summary>
    /// Windows has no signals, so nothing here is <c>128 + n</c>. A process killed by the runtime
    /// exits with an NTSTATUS, which the engine reports as a signed 32-bit integer — so
    /// <c>0xC0000005</c> arrives as -1073741819 through some paths and as 3221225477 through
    /// others depending on how it was widened. Both spellings are matched, because a user looking
    /// at "Exited -1073741819" and a user looking at "Exited 3221225477" have the same problem.
    /// </summary>
    static string? ExplainWindowsExitCode(int code) => unchecked((uint)code) switch
    {
        0 => null,
        1 => "general error",

        // Docker's own codes, which are the daemon's rather than the container's and so mean the
        // same thing on both platforms.
        125 => "the docker run command itself failed",
        126 => "the command could not be invoked",
        127 => "command not found",

        // The runtime stopping a container it was asked to stop. Windows containers are ended by
        // a control event rather than a signal, and this is what a process that took it reports.
        0x40010004 => "ended by the runtime (shutdown)",
        0xC000013A => "interrupted (Ctrl+C)",

        0xC0000005 => "access violation",
        0xC0000017 => "out of memory",
        0xC000007B => "a DLL was the wrong architecture",
        0xC0000135 => "a required DLL was not found",
        0xC0000142 => "a DLL failed to initialise",
        0xC0000409 => "stack buffer overrun",
        0xC00000FD => "stack overflow",
        0xC000041D => "an unhandled exception in a callback",
        0xE0434352 => "an unhandled .NET exception",
        0xE0434F4D => "an unhandled .NET exception",

        // Everything in the NTSTATUS error range is a crash of some kind. Naming the class is
        // worth more than silence, and far more than a signal number that cannot exist here.
        >= 0xC0000000 => "terminated by Windows",

        _ => null,
    };
}
