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
    public static ContainerStatus Resolve(DockerState state, DockerHealth health = DockerHealth.None, int? exitCode = null)
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
            DockerState.Exited => new(StateTone.Danger, "■", $"Exited {exitCode}", ExplainExitCode(exitCode!.Value)),

            _ => new(StateTone.Neutral, "⚠", "Unreachable"),
        };

    /// <summary>
    /// Plain-language expansion of an exit code. PRODUCT.md: Dray says "killed (out of memory)"
    /// where other tools say "Something went wrong".
    /// <para>
    /// Codes above 128 are <c>128 + signal</c> — the shell convention Docker follows for a process
    /// terminated by a signal.
    /// </para>
    /// </summary>
    public static string? ExplainExitCode(int code) => code switch
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
}
