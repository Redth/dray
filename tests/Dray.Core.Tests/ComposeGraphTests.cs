using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Reading <c>depends_on</c> out of a compose file, and the start order it implies.
/// <para>
/// This is the one thing about a stack that can only come from the file: compose uses the edges to
/// decide start order and the engine does not keep them afterwards.
/// </para>
/// </summary>
public class ComposeGraphTests
{
    [Fact]
    public void ReadsTheShortListForm()
    {
        const string yaml = """
            services:
              web:
                image: nginx
                depends_on:
                  - db
                  - cache
              db:
                image: postgres
              cache:
                image: redis
            """;

        var web = Assert.Single(ComposeGraph.Parse(yaml));

        Assert.Equal("web", web.Service);
        Assert.Equal(["db", "cache"], web.DependsOn);
    }

    [Fact]
    public void ReadsTheLongMapFormWithConditions()
    {
        // The keys are service names and the values are conditions — a shape that would read as
        // two more services to anything matching on "ends with a colon" alone.
        const string yaml = """
            services:
              web:
                depends_on:
                  db:
                    condition: service_healthy
                  cache:
                    condition: service_started
              db:
                image: postgres
              cache:
                image: redis
            """;

        var web = Assert.Single(ComposeGraph.Parse(yaml));

        Assert.Equal(["db", "cache"], web.DependsOn);
    }

    [Fact]
    public void ReadsTheInlineForm()
    {
        const string yaml = """
            services:
              web:
                depends_on: [db, cache]
              db:
                image: postgres
            """;

        Assert.Equal(["db", "cache"], Assert.Single(ComposeGraph.Parse(yaml)).DependsOn);
    }

    [Fact]
    public void AConditionIsNeverMistakenForAService()
    {
        const string yaml = """
            services:
              web:
                depends_on:
                  db:
                    condition: service_healthy
            """;

        Assert.DoesNotContain("condition", Assert.Single(ComposeGraph.Parse(yaml)).DependsOn);
    }

    [Fact]
    public void KeysOutsideTheServicesBlockAreNotServices()
    {
        // A top-level volumes or networks block has keys that look exactly like service names.
        const string yaml = """
            services:
              web:
                depends_on:
                  - db
              db:
                image: postgres
            volumes:
              pgdata:
            networks:
              default:
            """;

        var edge = Assert.Single(ComposeGraph.Parse(yaml));
        Assert.Equal("web", edge.Service);
    }

    [Fact]
    public void AFileWithNoDependenciesHasNoEdges()
        => Assert.Empty(ComposeGraph.Parse("services:\n  web:\n    image: nginx\n"));

    [Fact]
    public void NothingAtAllIsNotAFailure()
    {
        Assert.Empty(ComposeGraph.Parse(null));
        Assert.Empty(ComposeGraph.Parse(""));
    }

    // ---------------------------------------------------------------- levels

    [Fact]
    public void LevelsPutWhatStartsTogetherOnOneRow()
    {
        var edges = ComposeGraph.Parse("""
            services:
              web:
                depends_on: [api]
              api:
                depends_on: [db, cache]
              db:
                image: postgres
              cache:
                image: redis
            """);

        var levels = ComposeGraph.Levels(["web", "api", "db", "cache"], edges);

        Assert.Equal(3, levels.Count);
        Assert.Equal(["db", "cache"], levels[0]);
        Assert.Equal(["api"], levels[1]);
        Assert.Equal(["web"], levels[2]);
    }

    [Fact]
    public void ServicesWithNoDependenciesAllStartFirst()
    {
        var levels = ComposeGraph.Levels(["a", "b", "c"], []);

        Assert.Equal(["a", "b", "c"], Assert.Single(levels));
    }

    [Fact]
    public void ADependencyOnSomethingNotInTheStackIsIgnored()
    {
        // A compose file can name a service defined in another file of the same project. It is not
        // in this stack's list, so it cannot gate anything here.
        var edges = ComposeGraph.Parse("services:\n  web:\n    depends_on: [elsewhere]\n");

        Assert.Equal(["web"], Assert.Single(ComposeGraph.Levels(["web"], edges)));
    }

    [Fact]
    public void ACycleStillListsEveryService()
    {
        // Compose rejects a cycle, so this file could not run — but the graph is being drawn to
        // explain a file, and silently dropping the services in the cycle would hide the reason.
        var edges = ComposeGraph.Parse("""
            services:
              a:
                depends_on: [b]
              b:
                depends_on: [a]
              c:
                image: nginx
            """);

        var levels = ComposeGraph.Levels(["a", "b", "c"], edges);

        Assert.Equal(["c"], levels[0]);
        Assert.Equal(["a", "b"], levels[^1]);
        Assert.Equal(3, levels.SelectMany(l => l).Distinct().Count());
    }

    // ---------------------------------------------------------------- the full list

    [Fact]
    public void EveryDeclaredServiceIsListedInFileOrder()
    {
        const string yaml = """
            services:
              web:
                image: nginx
                depends_on: [db]
              worker:
                image: app
                environment:
                  QUEUE: redis
              db:
                image: postgres
            volumes:
              pgdata:
            """;

        Assert.Equal(["web", "worker", "db"], ComposeGraph.Services(yaml));
    }

    [Fact]
    public void AServiceScaledToZeroIsStillInTheFile()
    {
        // The engine only reports services that have containers, so this is the case the file list
        // exists for: `db` is declared and not running, and the graph still has to draw it.
        const string yaml = """
            services:
              web:
                depends_on:
                  db:
                    condition: service_healthy
              db:
                image: postgres
            """;

        Assert.Equal(["web", "db"], ComposeGraph.Services(yaml));
    }

    [Fact]
    public void KeysUnderAServiceAreNotServices()
    {
        const string yaml = """
            services:
              web:
                build:
                  context: .
                deploy:
                  resources:
                    limits:
                      cpus: "1"
                labels:
                  a: b
            """;

        Assert.Equal(["web"], ComposeGraph.Services(yaml));
    }

    [Fact]
    public void TheProjectsOwnFixtureParses()
    {
        var path = "/Users/redth/code/dray/.fixtures/stack/compose.yaml";
        if (!File.Exists(path)) return;

        var yaml = File.ReadAllText(path);

        Assert.Equal(["web", "cache", "worker"], ComposeGraph.Services(yaml));
        Assert.Equal(2, ComposeGraph.Parse(yaml).Count);
    }

    // ---------------------------------------------------------------- arrangement

    [Fact]
    public void ArrangingUncrossesTheEdgesItCan()
    {
        // Two independent pairs, declared interleaved. In file order the two edges cross; nothing
        // about the stack requires that, so the picture should not show it.
        var edges = ComposeGraph.Parse("""
            services:
              a1:
                depends_on: [a0]
              b1:
                depends_on: [b0]
              a0:
                image: x
              b0:
                image: x
            """);

        IReadOnlyList<IReadOnlyList<string>> tangled = [["a0", "b0"], ["b1", "a1"]];
        Assert.Equal(1, Crossings(tangled, edges));

        Assert.Equal(0, Crossings(ComposeGraph.Arrange(tangled, edges), edges));
    }

    [Fact]
    public void ArrangingNeverLosesOrInventsAService()
    {
        var edges = ComposeGraph.Parse("""
            services:
              proxy:
                depends_on: [web, api]
              web:
                depends_on: [api]
              api:
                depends_on: [db, cache]
              migrate:
                depends_on: [db]
              db:
                image: postgres
              cache:
                image: redis
            """);

        var names = ComposeGraph.Services("""
            services:
              proxy:
              web:
              api:
              migrate:
              db:
              cache:
            """);

        var levels = ComposeGraph.Levels(names, edges);
        var arranged = ComposeGraph.Arrange(levels, edges);

        Assert.Equal(levels.Count, arranged.Count);

        for (var i = 0; i < levels.Count; i++)
            Assert.Equal([.. levels[i].Order()], [.. arranged[i].Order()]);

        Assert.True(Crossings(arranged, edges) <= Crossings(levels, edges));
    }

    [Fact]
    public void AServiceWithNoEdgesKeepsThePlaceTheFileGaveIt()
    {
        // Nothing pulls `lonely` anywhere, so moving it would be churn: the file's order is the
        // only information available about where it belongs.
        var edges = ComposeGraph.Parse("services:\n  web:\n    depends_on: [db]\n");

        IReadOnlyList<IReadOnlyList<string>> levels = [["lonely", "db"], ["web"]];

        Assert.Equal(["lonely", "db"], ComposeGraph.Arrange(levels, edges)[0]);
    }

    [Fact]
    public void ASingleLevelIsLeftAlone()
    {
        IReadOnlyList<IReadOnlyList<string>> levels = [["a", "b", "c"]];

        Assert.Equal(["a", "b", "c"], Assert.Single(ComposeGraph.Arrange(levels, [])));
    }

    /// <summary>
    /// Count the pairs of edges between two adjacent levels that cross — the thing arranging is
    /// meant to reduce. Two edges cross when their endpoints are in the opposite vertical order at
    /// each end.
    /// </summary>
    static int Crossings(IReadOnlyList<IReadOnlyList<string>> levels, IReadOnlyList<ServiceDependency> edges)
    {
        var row = new Dictionary<string, (int Level, int Row)>(StringComparer.Ordinal);

        for (var l = 0; l < levels.Count; l++)
            for (var r = 0; r < levels[l].Count; r++) row[levels[l][r]] = (l, r);

        var drawn = edges
            .SelectMany(e => e.DependsOn.Select(d => (From: d, To: e.Service)))
            .Where(e => row.ContainsKey(e.From) && row.ContainsKey(e.To))
            .Select(e => (A: row[e.From], B: row[e.To]))
            .Where(e => e.B.Level == e.A.Level + 1)
            .ToList();

        var crossings = 0;

        for (var i = 0; i < drawn.Count; i++)
            for (var j = i + 1; j < drawn.Count; j++)
            {
                var (a, b) = (drawn[i], drawn[j]);
                if (a.A.Level != b.A.Level) continue;

                if ((a.A.Row - b.A.Row) * (a.B.Row - b.B.Row) < 0) crossings++;
            }

        return crossings;
    }
}
