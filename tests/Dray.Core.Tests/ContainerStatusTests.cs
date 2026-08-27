using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Guards DESIGN.md section 2.4 — the binding container-state vocabulary. These are design
/// decisions with teeth, not incidental mappings.
/// </summary>
public class ContainerStatusTests
{
    [Fact]
    public void RunningWithoutHealthcheckIsOk()
    {
        var s = ContainerStatusVocabulary.Resolve(DockerState.Running);
        Assert.Equal(StateTone.Ok, s.Tone);
        Assert.Equal("Running", s.Word);
    }

    [Fact]
    public void HealthOutranksLifecycleForARunningContainer()
    {
        // "Running but unhealthy" is the case a user most needs to see. Reporting it as plain
        // Running would hide the exact thing they opened Dray to find.
        var s = ContainerStatusVocabulary.Resolve(DockerState.Running, DockerHealth.Unhealthy);
        Assert.Equal(StateTone.Danger, s.Tone);
        Assert.Equal("Unhealthy", s.Word);
    }

    [Fact]
    public void StartingHealthIsWarnNotOk()
    {
        var s = ContainerStatusVocabulary.Resolve(DockerState.Running, DockerHealth.Starting);
        Assert.Equal(StateTone.Warn, s.Tone);
        Assert.Equal("Starting", s.Word);
    }

    [Fact]
    public void CleanExitIsNeutralButFailedExitIsDanger()
    {
        var clean = ContainerStatusVocabulary.Resolve(DockerState.Exited, exitCode: 0);
        Assert.Equal(StateTone.Neutral, clean.Tone);
        Assert.Equal("Exited", clean.Word);

        var failed = ContainerStatusVocabulary.Resolve(DockerState.Exited, exitCode: 1);
        Assert.Equal(StateTone.Danger, failed.Tone);
    }

    [Fact]
    public void OomExitReadsInPlainLanguage()
    {
        var s = ContainerStatusVocabulary.Resolve(DockerState.Exited, exitCode: 137);
        Assert.Equal("Exited 137 · killed (out of memory)", s.Label);
    }

    [Theory]
    [InlineData(139, "segmentation fault")]
    [InlineData(143, "stopped (SIGTERM)")]
    [InlineData(130, "interrupted (SIGINT)")]
    [InlineData(127, "command not found")]
    [InlineData(125, "the docker run command itself failed")]
    public void KnownExitCodesAreExplained(int code, string expected)
        => Assert.Equal(expected, ContainerStatusVocabulary.ExplainExitCode(code));

    [Fact]
    public void UnmappedSignalExitStillNamesTheSignal()
    {
        // 128 + signal is the convention Docker follows. A signal we have not spelled out by name
        // should still say something true rather than leaving the user with a bare number.
        Assert.Equal("killed by signal 6", ContainerStatusVocabulary.ExplainExitCode(134));
        Assert.Equal("killed by signal 4", ContainerStatusVocabulary.ExplainExitCode(132));
    }

    [Fact]
    public void ExitCodeZeroHasNoExplanation()
        => Assert.Null(ContainerStatusVocabulary.ExplainExitCode(0));

    [Fact]
    public void UnreachableHostDimsTheRow()
    {
        var s = ContainerStatusVocabulary.Resolve(DockerState.Unknown);
        Assert.True(s.IsStale);
        Assert.Equal("Unreachable", s.Word);
    }

    [Fact]
    public void RunningIsNotStale()
        => Assert.False(ContainerStatusVocabulary.Resolve(DockerState.Running).IsStale);

    /// <summary>
    /// The rule that makes DESIGN.md section 2.4 accessible: never colour alone. Every state
    /// carries a distinct glyph and a word, so a greyscale screenshot stays legible.
    /// </summary>
    [Fact]
    public void EveryStateHasAGlyphAndAWord()
    {
        foreach (var status in AllStatuses())
        {
            Assert.False(string.IsNullOrWhiteSpace(status.Glyph), $"{status.Word} has no glyph");
            Assert.False(string.IsNullOrWhiteSpace(status.Word), "a status has no word");
        }
    }

    /// <summary>
    /// Two states sharing a tone must not also share a glyph, or they become indistinguishable
    /// in greyscale — which is precisely the failure the glyph exists to prevent.
    /// </summary>
    [Fact]
    public void StatesSharingAToneHaveDistinctGlyphs()
    {
        var collisions = AllStatuses()
            .GroupBy(s => (s.Tone, s.Glyph))
            .Where(g => g.Select(s => s.Word).Distinct().Count() > 1)
            .Select(g => $"{g.Key.Tone}/{g.Key.Glyph}: {string.Join(", ", g.Select(s => s.Word).Distinct())}")
            .ToList();

        Assert.True(collisions.Count == 0, "Indistinguishable in greyscale: " + string.Join(" | ", collisions));
    }

    static List<ContainerStatus> AllStatuses() =>
    [
        ContainerStatusVocabulary.Resolve(DockerState.Running),
        ContainerStatusVocabulary.Resolve(DockerState.Running, DockerHealth.Starting),
        ContainerStatusVocabulary.Resolve(DockerState.Running, DockerHealth.Unhealthy),
        ContainerStatusVocabulary.Resolve(DockerState.Restarting),
        ContainerStatusVocabulary.Resolve(DockerState.Paused),
        ContainerStatusVocabulary.Resolve(DockerState.Created),
        ContainerStatusVocabulary.Resolve(DockerState.Removing),
        ContainerStatusVocabulary.Resolve(DockerState.Dead),
        ContainerStatusVocabulary.Resolve(DockerState.Exited, exitCode: 0),
        ContainerStatusVocabulary.Resolve(DockerState.Exited, exitCode: 137),
        ContainerStatusVocabulary.Resolve(DockerState.Unknown),
    ];
}
