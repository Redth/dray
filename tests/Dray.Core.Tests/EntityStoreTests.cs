using Dray.Core.Engine;
using Dray.Core.Model;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Dray.Core.Tests;

public class EntityStoreTests
{
    static ContainerSummary Container(string id, string name, DockerState state = DockerState.Running)
        => new() { Id = id, Name = name, Image = "nginx:1", State = state };

    static RuntimeEvent Event(string action, string id, params (string Key, string Value)[] attributes)
        => new(RuntimeEntity.Container, action, id, attributes.ToDictionary(a => a.Key, a => a.Value), DateTimeOffset.UnixEpoch);

    [Fact]
    public void ResetSeedsTheStoreAndRaisesOnce()
    {
        var store = new EntityStore();
        var changes = new List<StoreChange>();
        store.Changed += changes.Add;

        store.Reset([Container("a", "web"), Container("b", "api")]);

        Assert.Equal(2, store.Count);
        Assert.Equal(StoreChangeKind.Reset, Assert.Single(changes).Kind);
    }

    [Fact]
    public void RunningContainersSortFirstThenByName()
    {
        // The list must not reshuffle under the user, and what they came for is at the top.
        var store = new EntityStore();
        store.Reset([
            Container("a", "zulu"),
            Container("b", "alpha", DockerState.Exited),
            Container("c", "bravo"),
        ]);

        Assert.Equal(["bravo", "zulu", "alpha"], store.Containers.Select(c => c.Name));
    }

    // ---------------------------------------------------------------- events

    [Fact]
    public void StartEventFlipsStateWithoutAFetch()
    {
        var store = new EntityStore();
        store.Reset([Container("a", "web", DockerState.Exited)]);

        var needsFetch = store.Apply(Event("start", "a"));

        Assert.False(needsFetch);
        Assert.Equal(DockerState.Running, store.Find("a")!.State);
    }

    [Fact]
    public void DieEventCarriesTheExitCode()
    {
        var store = new EntityStore();
        store.Reset([Container("a", "worker")]);

        store.Apply(Event("die", "a", ("exitCode", "137")));

        var c = store.Find("a")!;
        Assert.Equal(DockerState.Exited, c.State);
        Assert.Equal(137, c.ExitCode);
        Assert.Equal("Exited 137 · killed (out of memory)", c.Status.Label);
    }

    [Fact]
    public void KillIsIgnoredBecauseDieFollowsWithTheExitCode()
    {
        // Acting on kill would flip the row to a codeless Exited and then immediately again to
        // Exited 137 — a visible flicker on every OOM.
        var store = new EntityStore();
        store.Reset([Container("a", "worker")]);

        store.Apply(Event("kill", "a"));

        Assert.Equal(DockerState.Running, store.Find("a")!.State);
    }

    [Fact]
    public void StartClearsAStaleExitCode()
    {
        var store = new EntityStore();
        store.Reset([Container("a", "web", DockerState.Exited) with { ExitCode = 137 }]);

        store.Apply(Event("start", "a"));

        var c = store.Find("a")!;
        Assert.Equal(DockerState.Running, c.State);
        Assert.Null(c.ExitCode);
    }

    [Fact]
    public void RestartAsksForAFetchRatherThanGuessing()
    {
        // Observed against podman: a restart emits `restart`, `start`, and then the `die`
        // belonging to the instance that was replaced. Applying that die would leave a running
        // container reading "Exited 137", so the one ambiguous sequence goes to the API.
        var store = new EntityStore();
        store.Reset([Container("a", "web")]);

        Assert.True(store.Apply(Event("restart", "a")));
    }

    [Theory]
    [InlineData("healthy", DockerHealth.Healthy)]
    [InlineData("unhealthy", DockerHealth.Unhealthy)]
    [InlineData("starting", DockerHealth.Starting)]
    public void HealthStatusFromAnAttribute(string value, DockerHealth expected)
    {
        var store = new EntityStore();
        store.Reset([Container("a", "api")]);

        store.Apply(Event("health_status", "a", ("health_status", value)));

        Assert.Equal(expected, store.Find("a")!.Health);
    }

    [Fact]
    public void HealthStatusFromTheActionVerb()
    {
        // Older API versions report it as `health_status: unhealthy` in the action itself.
        var store = new EntityStore();
        store.Reset([Container("a", "api")]);

        store.Apply(Event("health_status: unhealthy", "a"));

        Assert.Equal(DockerHealth.Unhealthy, store.Find("a")!.Health);
    }

    [Fact]
    public void RenameUpdatesTheName()
    {
        var store = new EntityStore();
        store.Reset([Container("a", "old")]);

        store.Apply(Event("rename", "a", ("name", "new")));

        Assert.Equal("new", store.Find("a")!.Name);
    }

    [Fact]
    public void DestroyRemovesTheContainer()
    {
        var store = new EntityStore();
        store.Reset([Container("a", "web")]);

        var needsFetch = store.Apply(Event("destroy", "a"));

        Assert.False(needsFetch);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void AnEventForAnUnknownContainerAsksForAFetch()
    {
        // An event cannot describe a new container — it carries no image and no ports.
        var store = new EntityStore();

        Assert.True(store.Apply(Event("start", "brand-new")));
    }

    [Fact]
    public void NonContainerEventsAreIgnored()
    {
        var store = new EntityStore();
        store.Reset([Container("a", "web")]);

        var e = new RuntimeEvent(RuntimeEntity.Image, "pull", "nginx", new Dictionary<string, string>(), DateTimeOffset.UnixEpoch);

        Assert.False(store.Apply(e));
        Assert.Equal(DockerState.Running, store.Find("a")!.State);
    }

    [Fact]
    public void UninterestingEventsDoNotRaiseAChange()
    {
        // exec_start fires constantly on a busy engine. Re-rendering for it would defeat the
        // point of event-driven updates.
        var store = new EntityStore();
        store.Reset([Container("a", "web")]);

        var changes = 0;
        store.Changed += _ => changes++;
        store.Apply(Event("exec_start", "a"));

        Assert.Equal(0, changes);
    }

    [Fact]
    public void AChangeIdentifiesTheSingleRowAffected()
    {
        // What lets a 400-row table re-render one row instead of all of them.
        var store = new EntityStore();
        store.Reset([Container("a", "web"), Container("b", "api")]);

        var changes = new List<StoreChange>();
        store.Changed += changes.Add;
        store.Apply(Event("die", "a", ("exitCode", "0")));

        var change = Assert.Single(changes);
        Assert.Equal(StoreChangeKind.Updated, change.Kind);
        Assert.Equal("a", change.ContainerId);
    }

    // ---------------------------------------------------------------- staleness

    [Fact]
    public void MarkAllStaleKeepsRowsButFlagsThemUnreachable()
    {
        // The containers still exist; Dray simply cannot see them. Emptying the list would claim
        // they were gone, which is a different and false statement.
        var store = new EntityStore();
        store.Reset([Container("a", "web"), Container("b", "api")]);

        store.MarkAllStale();

        Assert.Equal(2, store.Count);
        Assert.All(store.Containers, c => Assert.True(c.Status.IsStale));
    }

    // ---------------------------------------------------------------- change pulse

    [Fact]
    public void ARowIsRecentlyChangedForTheHighlightWindowOnly()
    {
        var time = new FakeTimeProvider();
        var store = new EntityStore(time) { ChangeHighlightWindow = TimeSpan.FromMilliseconds(600) };
        store.Reset([Container("a", "web")]);

        store.Apply(Event("die", "a", ("exitCode", "1")));
        Assert.True(store.WasRecentlyChanged("a"));

        time.Advance(TimeSpan.FromMilliseconds(599));
        Assert.True(store.WasRecentlyChanged("a"));

        time.Advance(TimeSpan.FromMilliseconds(2));
        Assert.False(store.WasRecentlyChanged("a"));
    }

    [Fact]
    public void AnUntouchedRowIsNeverRecentlyChanged()
    {
        var store = new EntityStore();
        store.Reset([Container("a", "web")]);

        Assert.False(store.WasRecentlyChanged("a"));
    }
}
