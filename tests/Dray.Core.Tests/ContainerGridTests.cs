using Dray.Core.Model;
using Dray.Core.Shell;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// What a container's row says, and what it gives up first.
/// <para>
/// These are judgements rather than markup — which fact matters more than which other fact — and
/// they are the kind that get quietly reversed by whoever next adds a column.
/// </para>
/// </summary>
public class ContainerGridTests
{
    static ContainerSummary Container(
        string name = "dray-redis",
        DockerState state = DockerState.Running,
        string? imageId = "sha256:9a56851f1a97e0586f85f8d7f7652e65cb5b9b2409d965c5d6e275dfc2551907",
        string? ip = "10.88.0.38",
        string? stack = null) => new()
    {
        Id = "43faffd4a0e81c250b5d652449e9644d9c38cd14fc1ec55e158ebf9dc3c6789c",
        Name = name,
        Image = "docker.io/library/redis:7-alpine",
        State = state,
        ImageId = imageId,
        IpAddress = ip,
        Compose = stack is null ? null : new ComposeMembership(stack),
    };

    static IReadOnlyList<string> Fields(bool usage = false, bool stacks = false)
        => [.. ContainerGrid.Columns(usage, stacks).Select(c => c.Field)];

    [Fact]
    public void NameAndStateAreNeverDropped()
    {
        // Priority 1: what you came to find, and why you came. Everything else is what you read
        // once you have found it.
        var columns = ContainerGrid.Columns(showUsage: true, stacks: true);

        Assert.Equal(1, columns.Single(c => c.Field == "name").Priority);
        Assert.Equal(1, columns.Single(c => c.Field == "state").Priority);
    }

    [Fact]
    public void TheUsageFiguresGoBeforeAnythingThatIdentifiesTheContainer()
    {
        // The counter-intuitive one, and the reason the order is written down: CPU and memory are
        // the narrowest columns, so a layout left to itself would keep them and drop the image.
        var columns = ContainerGrid.Columns(showUsage: true, stacks: true);

        var identifying = columns.Where(c => c.Field is "name" or "state" or "image").Max(c => c.Priority);
        var usage = columns.Where(c => c.Field is "cpu" or "memory" or "net").Min(c => c.Priority);

        Assert.True(usage > identifying, "usage columns must be dropped before identifying ones");
    }

    [Fact]
    public void TheActionsColumnIsNeverHiddenAndNeverSorted()
    {
        var actions = ContainerGrid.Columns(false, false).Single(c => c.Field == "actions");

        Assert.Equal(0, actions.Priority);
        Assert.False(actions.Sortable);
        Assert.Equal(GridCell.Actions, actions.Cell);
    }

    [Fact]
    public void TheActionsColumnIsLast()
        => Assert.Equal("actions", Fields(usage: true, stacks: true)[^1]);

    [Fact]
    public void UsageColumnsExistOnlyWhenUsageIsOn()
    {
        Assert.DoesNotContain("cpu", Fields(usage: false));
        Assert.Contains("cpu", Fields(usage: true));

        // Absent rather than empty: a column of dashes costs width and says nothing.
        Assert.DoesNotContain("memory", Fields(usage: false));
        Assert.DoesNotContain("net", Fields(usage: false));
    }

    [Fact]
    public void TheStackColumnExistsOnlyWhereThereAreStacks()
    {
        Assert.DoesNotContain("stack", Fields(stacks: false));
        Assert.Contains("stack", Fields(stacks: true));
    }

    [Fact]
    public void HealthIsNotASecondColumn()
    {
        // "Running" and "Unhealthy" are one answer, and a health column beside a state column makes
        // a row say it twice in different words. ContainerStatusVocabulary already folds them.
        Assert.DoesNotContain("health", Fields(usage: true, stacks: true));
    }

    // ---------------------------------------------------------------- the row

    [Fact]
    public void TheRowIsKeyedByTheFullId()
    {
        var row = ContainerGrid.Row(Container());

        Assert.Equal(
            "43faffd4a0e81c250b5d652449e9644d9c38cd14fc1ec55e158ebf9dc3c6789c",
            row[ContainerGrid.KeyField]);
    }

    [Fact]
    public void TheNameCarriesTheShortIdUnderIt()
    {
        var name = Assert.IsType<GridLink>(ContainerGrid.Row(Container())["name"]);

        Assert.Equal("dray-redis", name.Text);
        Assert.Equal("/containers/43faffd4a0e8", name.Href);
        Assert.Equal("43faffd4a0e8", name.Sub);
    }

    [Fact]
    public void TheDigestChipShowsTwelveAndCopiesAllOfIt()
    {
        var chip = Assert.IsType<GridChip>(ContainerGrid.Row(Container())["digest"]);

        Assert.Equal("9a56851f1a97", chip.Text);
        Assert.Equal("sha256:9a56851f1a97e0586f85f8d7f7652e65cb5b9b2409d965c5d6e275dfc2551907", chip.Copy);
    }

    [Fact]
    public void AnUnreportedDigestIsNothingRatherThanADash()
    {
        // The cell is empty because there is no value, not because the value is empty — and a chip
        // reading "—" would invite a click that copied a dash.
        Assert.Null(ContainerGrid.Row(Container(imageId: null))["digest"]);
        Assert.Null(ContainerGrid.Row(Container(imageId: ""))["digest"]);
    }

    [Fact]
    public void AContainerWithNoAddressSaysSo()
    {
        // A stopped container, or one on the host network, genuinely has no address. Showing the
        // last one it had would be the worst of the options.
        Assert.Equal("—", ContainerGrid.Row(Container(ip: null))["ip"]);
    }

    [Fact]
    public void StateSortsWorstFirst()
    {
        // Sorting by state exists to bring the broken ones up, so "worse" has to mean "earlier".
        var running = GridState.From(Container().Status);
        var dead = GridState.From(Container(state: DockerState.Dead).Status);

        Assert.True(dead.Rank < running.Rank);
    }

    [Fact]
    public void EveryColumnHasAValueInEveryRow()
    {
        // A field with no value in the row silently renders an empty cell, which reads as data the
        // engine did not return rather than as a column nobody filled in.
        var row = ContainerGrid.Row(Container(stack: "draydemo"));

        foreach (var column in ContainerGrid.Columns(showUsage: true, stacks: true))
        {
            if (column.Cell == GridCell.Actions) continue;

            Assert.True(row.ContainsKey(column.Field), $"row has no value for '{column.Field}'");
        }
    }
}
