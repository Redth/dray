using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

public class ContainerSummaryTests
{
    static ContainerSummary Row(params PortBinding[] ports)
        => new() { Id = "a", Name = "web", Image = "nginx:1", State = DockerState.Running, Ports = ports };

    [Fact]
    public void TwoIdenticalRowsBuiltSeparatelyAreTheSameContainer()
    {
        // The trap this method exists for: Ports is a list, so record equality compares it by
        // reference and two summaries deserialized from identical engine responses are never ==.
        // A poll loop using == would find that everything changed on every tick.
        Assert.False(Row(new PortBinding(8080, 80)) == Row(new PortBinding(8080, 80)));
        Assert.True(Row(new PortBinding(8080, 80)).SameAs(Row(new PortBinding(8080, 80))));
    }

    [Fact]
    public void AChangedPortIsAChange()
    {
        Assert.False(Row(new PortBinding(8080, 80)).SameAs(Row(new PortBinding(9090, 80))));
        Assert.False(Row(new PortBinding(8080, 80)).SameAs(Row()));
    }

    [Theory]
    [InlineData(DockerState.Exited)]
    [InlineData(DockerState.Paused)]
    public void AChangedStateIsAChange(DockerState state)
        => Assert.False(Row().SameAs(Row() with { State = state }));

    [Fact]
    public void ARenameIsAChange()
        => Assert.False(Row().SameAs(Row() with { Name = "api" }));

    [Fact]
    public void AnExitCodeAppearingIsAChange()
        => Assert.False(Row().SameAs(Row() with { ExitCode = 137 }));

    [Fact]
    public void MovingBetweenStacksIsAChange()
    {
        var loose = Row();
        var stacked = Row() with { Compose = new ComposeMembership("shop", "web") };

        Assert.False(loose.SameAs(stacked));
        Assert.True(stacked.SameAs(Row() with { Compose = new ComposeMembership("shop", "web") }));
    }

    [Fact]
    public void TheSameMembershipReadFromDifferentLabelsIsNotAChange()
    {
        // ComposeMembership carries a config-file list, which has the same reference-equality
        // problem one level down. Two containers in the same stack must not look different just
        // because their file lists are separate arrays.
        var a = Row() with { Compose = new ComposeMembership("shop", "web", ["/a/compose.yml"]) };
        var b = Row() with { Compose = new ComposeMembership("shop", "web", ["/a/compose.yml"]) };

        Assert.True(a.SameAs(b));
    }
}
