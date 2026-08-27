using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Which actions apply to which state. An action offered in the wrong state is either a no-op the
/// user does not understand or an error the engine rejects, and both teach them the UI is guessing.
/// </summary>
public class ContainerActionTests
{
    [Theory]
    [InlineData(DockerState.Exited)]
    [InlineData(DockerState.Created)]
    [InlineData(DockerState.Dead)]
    public void StartAppliesOnlyToAStoppedContainer(DockerState state)
        => Assert.True(ContainerActions.AppliesTo(ContainerAction.Start, state));

    [Theory]
    [InlineData(DockerState.Running)]
    [InlineData(DockerState.Paused)]
    public void StartIsNotOfferedWhenAlreadyUp(DockerState state)
        => Assert.False(ContainerActions.AppliesTo(ContainerAction.Start, state));

    [Fact]
    public void StopAppliesToARunningContainerOnly()
    {
        Assert.True(ContainerActions.AppliesTo(ContainerAction.Stop, DockerState.Running));
        Assert.False(ContainerActions.AppliesTo(ContainerAction.Stop, DockerState.Exited));
    }

    [Fact]
    public void PausedContainersCanBeKilledButNotStopped()
    {
        // A paused container cannot receive a graceful shutdown, so Stop would hang and Kill is
        // the only way out.
        Assert.False(ContainerActions.AppliesTo(ContainerAction.Stop, DockerState.Paused));
        Assert.True(ContainerActions.AppliesTo(ContainerAction.Kill, DockerState.Paused));
    }

    [Fact]
    public void ResumeAppliesOnlyToAPausedContainer()
    {
        Assert.True(ContainerActions.AppliesTo(ContainerAction.Unpause, DockerState.Paused));
        Assert.False(ContainerActions.AppliesTo(ContainerAction.Unpause, DockerState.Running));
    }

    [Fact]
    public void RemoveIsNotOfferedWhileRunning()
    {
        // Removing a running container needs force, which is a different question with a different
        // confirmation. The user reaches Stop first.
        Assert.False(ContainerActions.AppliesTo(ContainerAction.Remove, DockerState.Running));
        Assert.True(ContainerActions.AppliesTo(ContainerAction.Remove, DockerState.Exited));
    }

    [Fact]
    public void AnUnreachableContainerOffersNothing()
    {
        // The host is gone, so every action would fail. Offering them would be a lie about what
        // Dray can currently do.
        Assert.Empty(ContainerActions.For(DockerState.Unknown));
    }

    [Fact]
    public void EveryStateOffersSomethingExceptUnreachable()
    {
        foreach (var state in Enum.GetValues<DockerState>())
        {
            if (state is DockerState.Unknown or DockerState.Removing) continue;
            Assert.NotEmpty(ContainerActions.For(state));
        }
    }

    // ---------------------------------------------------------------- destructiveness

    [Fact]
    public void RemoveAndKillAreDestructiveAndStopIsNot()
    {
        Assert.True(ContainerActions.IsDestructive(ContainerAction.Remove));

        // Kill skips the graceful shutdown, so a process mid-write can lose data.
        Assert.True(ContainerActions.IsDestructive(ContainerAction.Kill));

        // Stop is the ordinary way to stop a container. Confirming it would train the user to
        // click through confirmations, which is how a real confirmation stops working.
        Assert.False(ContainerActions.IsDestructive(ContainerAction.Stop));
        Assert.False(ContainerActions.IsDestructive(ContainerAction.Restart));
    }

    [Fact]
    public void DestructiveConfirmationsNameWhatIsLost()
    {
        var (removeTitle, removeBody) = ContainerActions.Confirmation(ContainerAction.Remove, "web");
        Assert.Contains("web", removeTitle, StringComparison.Ordinal);
        Assert.Contains("lost", removeBody, StringComparison.OrdinalIgnoreCase);

        var (_, killBody) = ContainerActions.Confirmation(ContainerAction.Kill, "db");
        Assert.Contains("SIGKILL", killBody, StringComparison.Ordinal);
    }

    [Fact]
    public void NoConfirmationAsksWhetherTheUserIsSure()
    {
        // PRODUCT.md: say what will be destroyed, never "Are you sure?".
        foreach (var action in ContainerActions.All)
        {
            var (title, body) = ContainerActions.Confirmation(action, "web");
            Assert.DoesNotContain("Are you sure", title, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Are you sure", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- labels

    [Fact]
    public void LabelsAreSentenceCaseVerbs()
    {
        foreach (var action in ContainerActions.All)
        {
            var label = ContainerActions.Label(action);
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.Equal(char.ToUpperInvariant(label[0]), label[0]);
            Assert.DoesNotContain(label[1..], char.IsUpper);
        }
    }

    [Fact]
    public void KillIsCalledForceStopBecauseNobodyThinksInSignals()
        => Assert.Equal("Force stop", ContainerActions.Label(ContainerAction.Kill));

    [Fact]
    public void PendingLabelsArePresentContinuous()
    {
        foreach (var action in ContainerActions.All)
            Assert.EndsWith("ing", ContainerActions.PendingLabel(action), StringComparison.Ordinal);
    }
}
