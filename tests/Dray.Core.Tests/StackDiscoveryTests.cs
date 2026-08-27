using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

public class StackDiscoveryTests
{
    static ContainerSummary Container(
        string name,
        DockerState state = DockerState.Running,
        string? project = null,
        string? service = null,
        int replica = 1) => new()
        {
            Id = name,
            Name = name,
            Image = "alpine",
            State = state,
            Compose = project is null ? null : new ComposeMembership(project, service, Replica: replica),
        };

    [Fact]
    public void ContainersWithNoComposeLabelsAreNotInAStack()
    {
        // Dray does not invent a project for a container someone started by hand.
        Assert.Empty(StackDiscovery.From([Container("standalone")]));
    }

    [Fact]
    public void ContainersAreGroupedByProject()
    {
        var stacks = StackDiscovery.From([
            Container("a-web-1", project: "a", service: "web"),
            Container("b-web-1", project: "b", service: "web"),
            Container("a-db-1", project: "a", service: "db"),
        ]);

        Assert.Equal(["a", "b"], stacks.Select(s => s.Name));
        Assert.Equal(["db", "web"], stacks[0].Services.Select(s => s.Name));
    }

    [Fact]
    public void ReplicasOfOneServiceStayTogether()
    {
        var stack = StackDiscovery.From([
            Container("web-1", project: "p", service: "web", replica: 1),
            Container("web-2", project: "p", service: "web", replica: 2),
        ]).Single();

        var service = Assert.Single(stack.Services);

        Assert.True(service.IsScaled);
        Assert.Equal(2, service.Replicas.Count);
    }

    [Fact]
    public void ReplicasAreOrderedByComposesNumberNotByName()
    {
        // Ordered by name, "web-10" sorts before "web-2".
        var stack = StackDiscovery.From([
            Container("p-web-10", project: "p", service: "web", replica: 10),
            Container("p-web-2", project: "p", service: "web", replica: 2),
        ]).Single();

        Assert.Equal(["p-web-2", "p-web-10"], stack.Services[0].Replicas.Select(r => r.Name));
    }

    [Fact]
    public void AServiceNameIsInferredWhenComposeOnlyLabelledTheProject()
    {
        // Real case, seen on this project's own containers: the project label is present and the
        // service label is not. "dray-web" under project "dray" is the "web" service.
        var stack = StackDiscovery.From([Container("dray-web", project: "dray")]).Single();

        Assert.Equal("web", stack.Services[0].Name);
    }

    [Fact]
    public void AnInferredServiceNameDropsATrailingReplicaNumber()
        => Assert.Equal("web", StackDiscovery.FallbackServiceName("p", Container("p-web-3")));

    [Fact]
    public void AContainerNotNamedAfterItsProjectKeepsItsOwnName()
    {
        // Guessing harder would produce a wrong name; the container's own is at least true.
        Assert.Equal("something-else", StackDiscovery.FallbackServiceName("p", Container("something-else")));
    }

    // ---------------------------------------------------------------- status

    [Fact]
    public void AServiceTakesTheWorstOfItsReplicas()
    {
        // Three containers where one has crashed is not a healthy service, and showing the first
        // would hide exactly the container worth looking at.
        var stack = StackDiscovery.From([
            Container("p-web-1", project: "p", service: "web"),
            Container("p-web-2", DockerState.Exited, "p", "web", 2),
        ]).Single();

        Assert.Equal(StateTone.Neutral, stack.Services[0].Status.Tone);
        Assert.Equal(1, stack.Services[0].RunningCount);
    }

    [Fact]
    public void AStackTakesTheWorstOfItsServices()
    {
        var stack = StackDiscovery.From([
            Container("p-web-1", project: "p", service: "web"),
            Container("p-db-1", DockerState.Exited, "p", "db"),
        ]).Single();

        Assert.Equal(StateTone.Neutral, stack.Status.Tone);
        Assert.Equal(1, stack.RunningCount);
        Assert.Equal(2, stack.ContainerCount);
    }

    [Fact]
    public void AStackWithEveryServiceRunningIsRunning()
    {
        var stack = StackDiscovery.From([
            Container("p-web-1", project: "p", service: "web"),
            Container("p-db-1", project: "p", service: "db"),
        ]).Single();

        Assert.Equal(StateTone.Ok, stack.Status.Tone);
    }

    // ---------------------------------------------------------------- membership

    [Fact]
    public void MembershipIsNullWhenComposeMadeNoneOfIt()
        => Assert.Null(ComposeMembership.From(new Dictionary<string, string> { ["other"] = "x" }));

    [Fact]
    public void MembershipReadsEveryLabelComposeWrites()
    {
        var membership = ComposeMembership.From(new Dictionary<string, string>
        {
            [ComposeLabels.Project] = "draydemo",
            [ComposeLabels.Service] = "web",
            [ComposeLabels.ConfigFiles] = "/a/compose.yaml,/a/compose.override.yaml",
            [ComposeLabels.WorkingDirectory] = "/a",
            [ComposeLabels.ContainerNumber] = "3",
        });

        Assert.NotNull(membership);
        Assert.Equal("draydemo", membership.Project);
        Assert.Equal("web", membership.Service);
        Assert.Equal(3, membership.Replica);
        Assert.Equal(["/a/compose.yaml", "/a/compose.override.yaml"], membership.Files);
        Assert.Equal("/a", membership.WorkingDirectory);
    }

    [Fact]
    public void AnUnnumberedContainerIsTheFirstReplica()
    {
        var membership = ComposeMembership.From(new Dictionary<string, string>
        {
            [ComposeLabels.Project] = "p",
        });

        Assert.Equal(1, membership!.Replica);
    }
}
