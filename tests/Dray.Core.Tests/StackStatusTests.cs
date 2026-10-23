using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The stack-level state vocabulary. The two rules here are the ones Dockge's most-upvoted bug and
/// Dockhand's fix are about: a finished one-shot is not a failure, and health is not state.
/// </summary>
public class StackStatusTests
{
    static ContainerSummary C(DockerState state, int? exit = null, DockerHealth health = DockerHealth.None)
        => new() { Id = Guid.NewGuid().ToString("N"), Name = "c", Image = "x", State = state, ExitCode = exit, Health = health };

    [Fact]
    public void AMigrationThatFinishedDoesNotTurnTheStackRed()
    {
        var state = StackStatusVocabulary.StateOf([C(DockerState.Running), C(DockerState.Exited, 0)], wasDeployed: true);

        Assert.Equal(StackState.Running, state);
    }

    [Fact]
    public void ACrashedServiceMakesTheStackPartial()
    {
        var status = StackStatusVocabulary.Resolve([C(DockerState.Running), C(DockerState.Exited, 1)], wasDeployed: true);

        Assert.Equal("Partly running · 1 of 2 running", status.Label);
        Assert.Equal(StateTone.Warn, status.Tone);
        Assert.Equal("◑", status.Glyph);
    }

    [Fact]
    public void StoppedIsOrdinaryUnlessSomethingFailed()
    {
        var clean = StackStatusVocabulary.Resolve([C(DockerState.Exited, 0), C(DockerState.Created)], true);
        Assert.Equal((StateTone.Neutral, "Stopped"), (clean.Tone, clean.Label));

        var failed = StackStatusVocabulary.Resolve([C(DockerState.Exited, 0), C(DockerState.Exited, 137)], true);
        Assert.Equal((StateTone.Danger, "Stopped · 1 exited with an error"), (failed.Tone, failed.Label));
    }

    [Fact]
    public void ACrashLoopIsRestartingNotRunning()
    {
        // Every live container restarting: Start would do nothing, Stop is the useful action.
        Assert.Equal(StackState.Restarting, StackStatusVocabulary.StateOf([C(DockerState.Restarting), C(DockerState.Exited, 0)], true));
    }

    [Fact]
    public void NoContainersIsDownOrDraftDependingOnHistory()
    {
        Assert.Equal(StackState.Down, StackStatusVocabulary.StateOf([], wasDeployed: true));
        Assert.Equal(StackState.Draft, StackStatusVocabulary.StateOf([], wasDeployed: false));
        Assert.Equal("Draft · never deployed here", StackStatusVocabulary.Resolve([], false).Label);
    }

    [Fact]
    public void HealthIsReportedBesideStateNotInsteadOfIt()
    {
        var containers = new[] { C(DockerState.Running, health: DockerHealth.Healthy), C(DockerState.Running, health: DockerHealth.Unhealthy) };

        Assert.Equal(StackState.Running, StackStatusVocabulary.StateOf(containers, true));
        Assert.Equal(DockerHealth.Unhealthy, StackStatusVocabulary.HealthOf(containers));
    }

    [Fact]
    public void NoHealthcheckIsNoHealth()
        => Assert.Equal(DockerHealth.None, StackStatusVocabulary.HealthOf([C(DockerState.Running)]));

    [Fact]
    public void EveryStateHasItsOwnGlyph()
    {
        // Greyscale must stay legible: no two stack states may share a glyph and a word.
        var labels = new[]
        {
            StackStatusVocabulary.Resolve([C(DockerState.Running)], true),
            StackStatusVocabulary.Resolve([C(DockerState.Running), C(DockerState.Created)], true),
            StackStatusVocabulary.Resolve([C(DockerState.Restarting)], true),
            StackStatusVocabulary.Resolve([C(DockerState.Paused)], true),
            StackStatusVocabulary.Resolve([C(DockerState.Created)], true),
            StackStatusVocabulary.Resolve([], true),
            StackStatusVocabulary.Resolve([], false),
        }.Select(s => s.Glyph).ToList();

        Assert.Equal(labels.Count, labels.Distinct().Count());
    }
}
