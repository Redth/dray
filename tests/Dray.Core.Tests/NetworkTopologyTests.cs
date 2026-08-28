using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Which containers share which network — the one fact about networking that is never visible from
/// a list, because it is a property of the pairing rather than of either side.
/// </summary>
public class NetworkTopologyTests
{
    static NetworkSummary Network(string name, params (string Id, string Name, string? Address)[] members) => new()
    {
        Id = name + "-id",
        Name = name,
        Driver = "bridge",
        Members = [.. members.Select(m => new NetworkMember(m.Id, m.Name, m.Address, null))],
    };

    [Fact]
    public void EveryContainerGetsOneRowWithACellPerNetwork()
    {
        var topology = NetworkTopology.Build(
        [
            Network("frontend", ("a", "web", "10.0.0.2/16"), ("b", "api", "10.0.0.3/16")),
            Network("backend", ("b", "api", "10.1.0.2/16"), ("c", "db", "10.1.0.3/16")),
        ]);

        Assert.Equal(2, topology.Networks.Count);
        Assert.Equal(3, topology.Rows.Count);

        foreach (var row in topology.Rows) Assert.Equal(2, row.Addresses.Count);
    }

    [Fact]
    public void AContainerOnTwoNetworksIsMarkedAndComesFirst()
    {
        // The row worth looking at: this is how traffic crosses between two networks, and it is
        // invisible on a page that lists networks one at a time.
        var topology = NetworkTopology.Build(
        [
            Network("frontend", ("a", "web", "10.0.0.2"), ("b", "api", "10.0.0.3")),
            Network("backend", ("b", "api", "10.1.0.2"), ("c", "db", "10.1.0.3")),
        ]);

        var first = topology.Rows[0];

        Assert.Equal("api", first.Name);
        Assert.True(first.Bridges);
        Assert.Equal(2, first.Count);
        Assert.Equal(1, topology.Bridges);

        Assert.All(topology.Rows.Skip(1), r => Assert.False(r.Bridges));
    }

    [Fact]
    public void TheAddressShownIsTheOneOnThatNetwork()
    {
        // A container has a different address on each network it is attached to. Showing one of
        // them in both columns would be a plausible-looking lie.
        var topology = NetworkTopology.Build(
        [
            Network("frontend", ("b", "api", "10.0.0.3/16")),
            Network("backend", ("b", "api", "10.1.0.2/16")),
        ]);

        var row = Assert.Single(topology.Rows);
        var columns = topology.Networks.Select(n => n.Name).ToList();

        Assert.Equal("10.0.0.3", row.Addresses[columns.IndexOf("frontend")]);
        Assert.Equal("10.1.0.2", row.Addresses[columns.IndexOf("backend")]);
    }

    [Fact]
    public void TheBusiestNetworkIsTheFirstColumn()
    {
        var topology = NetworkTopology.Build(
        [
            Network("small", ("a", "web", "10.0.0.2")),
            Network("big", ("a", "web", "10.1.0.2"), ("b", "api", "10.1.0.3"), ("c", "db", "10.1.0.4")),
        ]);

        Assert.Equal("big", topology.Networks[0].Name);
        Assert.Equal("small", topology.Networks[1].Name);
    }

    [Fact]
    public void ANetworkNobodyIsOnIsNotAColumn()
    {
        // It would be an empty stripe through the middle of the grid, and the list above already
        // says it exists.
        var topology = NetworkTopology.Build([Network("used", ("a", "web", "10.0.0.2")), Network("empty")]);

        Assert.Equal(["used"], topology.Networks.Select(n => n.Name));
    }

    [Fact]
    public void OnThisNetworkWithNoAddressIsNotTheSameAsNotOnIt()
    {
        // An engine that does not report an address still reported the membership. Treating the
        // two the same would drop the container out of the column it is actually in.
        var topology = NetworkTopology.Build(
        [
            Network("host", ("a", "web", null)),
            Network("bridge", ("b", "api", "10.0.0.2")),
        ]);

        var web = topology.Rows.Single(r => r.Name == "web");

        Assert.Equal("", web.Addresses[topology.Networks.ToList().FindIndex(n => n.Name == "host")]);
        Assert.Equal(1, web.Count);
    }

    [Fact]
    public void ContainersWithTheSamePatternEndUpTogether()
    {
        var topology = NetworkTopology.Build(
        [
            Network("frontend", ("a", "web", "1"), ("c", "proxy", "2")),
            Network("backend", ("b", "db", "3"), ("d", "cache", "4")),
        ]);

        var patterns = topology.Rows.Select(r => string.Concat(r.Addresses.Select(x => x is null ? '0' : '1'))).ToList();

        // Same-pattern rows adjacent: no pattern appears, disappears and comes back.
        Assert.Equal(patterns.Distinct().Count(), patterns.Chunk(1).Select(c => c[0]).Aggregate(
            (Count: 0, Last: (string?)null),
            (state, p) => p == state.Last ? state : (state.Count + 1, p)).Count);
    }

    [Fact]
    public void NothingAtAllIsEmptyRatherThanAFailure()
    {
        Assert.True(NetworkTopology.Build(null).IsEmpty);
        Assert.True(NetworkTopology.Build([]).IsEmpty);
        Assert.True(NetworkTopology.Build([Network("empty")]).IsEmpty);
    }
}
