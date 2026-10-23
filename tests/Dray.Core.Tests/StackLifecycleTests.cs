using Dray.Core.Engine;
using Dray.Core.Model;
using Dray.Core.Tests.Fakes;
using Xunit;

namespace Dray.Core.Tests;

public class StackCommandsTests
{
    static ContainerSummary C(string service, DockerState state = DockerState.Running) => new()
    {
        Id = service + state, Name = "p-" + service + "-1", Image = "x", State = state,
        Compose = new ComposeMembership("p", service),
    };

    static StackSummary Stack(params ContainerSummary[] containers)
        => containers.Length == 0
            ? new StackSummary { Name = "p", Services = [new StackService("web", [])] }
            : StackDiscovery.From(containers).Single();

    [Fact]
    public void TheDeployPresetsAreTheThreeRedeploysPeopleMean()
    {
        var stack = Stack(C("web"));

        Assert.Equal(["up", "-d", "--remove-orphans"], StackCommands.For(StackVerb.Deploy, stack, DeployOptions.OnlyChanged).Single());
        Assert.Equal(["up", "-d", "--remove-orphans", "--force-recreate"], StackCommands.For(StackVerb.Deploy, stack, DeployOptions.RecreateAll).Single());
        Assert.Equal(["up", "-d", "--remove-orphans", "--pull", "always"], StackCommands.For(StackVerb.Deploy, stack, DeployOptions.PullAndApply).Single());
        Assert.Equal(["up", "-d", "--remove-orphans", "--build"], StackCommands.For(StackVerb.Deploy, stack, new DeployOptions(Build: true)).Single());
    }

    [Fact]
    public void StartKeepsWhatExistsAndUpCreatesWhatDoesNot()
    {
        Assert.Equal(["start"], StackCommands.For(StackVerb.Start, Stack(C("web", DockerState.Exited))).Single());
        Assert.Equal(["up", "-d", "--remove-orphans"], StackCommands.For(StackVerb.Start, Stack()).Single());
    }

    [Fact]
    public void UpdateOnlyBringsUpAStackThatWasUp()
    {
        Assert.Equal([["pull"], ["up", "-d", "--remove-orphans"]], StackCommands.For(StackVerb.Update, Stack(C("web"))));
        Assert.Equal([["pull"]], StackCommands.For(StackVerb.Update, Stack(C("web", DockerState.Exited))));
    }

    [Fact]
    public void OneServiceNeverTakesItsDependenciesWithIt()
    {
        var stack = Stack(C("web"), C("db"));

        Assert.Equal(["up", "-d", "--no-deps", "--force-recreate", "web"], StackCommands.For(StackVerb.Recreate, stack, service: "web").Single());
        Assert.Equal(["restart", "web"], StackCommands.For(StackVerb.Restart, stack, service: "web").Single());
        Assert.Equal([["pull", "web"], ["up", "-d", "--no-deps", "web"]], StackCommands.For(StackVerb.Update, stack, service: "web"));
    }

    [Fact]
    public void OnlyDownWithVolumesTakesTheVolumes()
    {
        var stack = Stack(C("web"));

        Assert.DoesNotContain("--volumes", StackCommands.For(StackVerb.Down, stack).Single());
        Assert.Contains("--volumes", StackCommands.For(StackVerb.DownWithVolumes, stack).Single());
    }

    [Fact]
    public void WhatWorksWithoutFilesIsWhatComposeDoesFromLabels()
    {
        var running = Stack(C("web"));

        Assert.False(StackCommands.NeedsFiles(StackVerb.Stop, running));
        Assert.False(StackCommands.NeedsFiles(StackVerb.Down, running));
        Assert.False(StackCommands.NeedsFiles(StackVerb.Start, running));
        Assert.True(StackCommands.NeedsFiles(StackVerb.Pull, running));
        Assert.True(StackCommands.NeedsFiles(StackVerb.Deploy, running));
        Assert.True(StackCommands.NeedsFiles(StackVerb.Start, Stack()));
    }
}

public class StackCatalogTests
{
    static readonly Func<IReadOnlyList<string>, IReadOnlyList<ComposeServiceEntry>> Declares = _ =>
    [
        new ComposeServiceEntry("web", new YamlRange(1, 1, 1, 2), [], false),
        new ComposeServiceEntry("worker", new YamlRange(1, 1, 1, 2), [], false) { Builds = true, Profiles = ["jobs"] },
    ];

    static StackSummary Observed(string name, string? file = null, DockerState state = DockerState.Running) => StackDiscovery.From(
    [
        new ContainerSummary
        {
            Id = name, Name = name + "-web-1", Image = "x", State = state,
            Compose = new ComposeMembership(name, "web", file is null ? [] : [file], file is null ? null : Path.GetDirectoryName(file)),
        },
    ]).Single();

    [Fact]
    public void ADownStackStaysListed()
    {
        // The exit criterion Phase 5 missed: no containers, so no labels — and still on the list.
        var stacks = StackCatalog.Merge(
            [], [], [new StackRecord("h", "web", "/s/web", ["/s/web/compose.yaml"])], "/s", Declares, _ => true);

        var stack = Assert.Single(stacks);
        Assert.Equal(StackState.Down, stack.State);
        Assert.Equal(StackProvenance.Managed, stack.Provenance);
        Assert.Equal(["web", "worker"], stack.Services.Select(s => s.Name));

        // What the files say about each service travels with the stack.
        Assert.True(stack.Builds);
        Assert.Equal(["jobs"], stack.Profiles);
    }

    [Fact]
    public void AFolderNeverDeployedHereIsADraft()
    {
        var stacks = StackCatalog.Merge([], [new StackDefinition("api", "/s/api", ["/s/api/compose.yaml"])], [], "/s", Declares, _ => true);

        Assert.Equal(StackState.Draft, Assert.Single(stacks).State);
    }

    [Fact]
    public void WhatIsRunningWinsItsNameAndGainsTheServicesNotStarted()
    {
        var stacks = StackCatalog.Merge(
            [Observed("web", "/s/web/compose.yaml")],
            [new StackDefinition("web", "/s/web", ["/s/web/compose.yaml"])],
            [], "/s", Declares, _ => true);

        var stack = Assert.Single(stacks);
        Assert.Equal(["web", "worker"], stack.Services.Select(s => s.Name));

        // Listed, but not counted against the stack: a service with no containers is as often
        // behind a profile, or scaled to zero, as it is missing — state is what the containers say.
        Assert.Equal(StackState.Running, stack.State);
        Assert.Empty(stack.Services.Single(s => s.Name == "worker").Replicas);
    }

    [Fact]
    public void ProvenanceComesFromWhereTheFilesAre()
    {
        var stacks = StackCatalog.Merge(
            [Observed("inside", "/s/inside/compose.yaml"), Observed("outside", "/code/app/compose.yaml"), Observed("remote", "/srv/x/compose.yaml")],
            [], [], "/s", Declares, path => !path.StartsWith("/srv", StringComparison.Ordinal));

        Assert.Equal(StackProvenance.Managed, stacks.Single(s => s.Name == "inside").Provenance);
        Assert.Equal(StackProvenance.Adopted, stacks.Single(s => s.Name == "outside").Provenance);

        // Files on another machine: nothing to read here, so nothing invented from them either.
        var remote = stacks.Single(s => s.Name == "remote");
        Assert.Equal(StackProvenance.Observed, remote.Provenance);
        Assert.Equal(["web"], remote.Services.Select(s => s.Name));
    }

    [Fact]
    public void TheStacksFolderItselfIsNotInsideItself()
    {
        Assert.True(StackCatalog.IsUnder("/s/web", "/s"));
        Assert.False(StackCatalog.IsUnder("/s", "/s"));
        Assert.False(StackCatalog.IsUnder("/stacks-old/web", "/s"));
    }

    [Theory]
    [InlineData("/s/web/compose.yaml", true)]
    [InlineData("/s/web/.env", true)]
    [InlineData("/s/newstack", true)]
    [InlineData("/s/web/data/pg/base/16384", false)]
    [InlineData("/s/web/data/log.txt", false)]
    [InlineData("/s/.DS_Store", false)]
    public void OnlyChangesThatCanMoveTheListAreWatched(string path, bool matters)
        => Assert.Equal(matters, StackCatalog.Matters("/s", path));
}

public sealed class StackRegistryTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("dray-registry-").FullName;

    string PathOf => System.IO.Path.Combine(_dir, "stacks.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void WhatIsRememberedSurvivesARestart()
    {
        var registry = new StackRegistry(PathOf);
        registry.SetStacksFolder(_dir);
        Assert.True(registry.Remember(new StackRecord("h", "web", "/s/web", ["/s/web/compose.yaml"])));

        var reopened = new StackRegistry(PathOf);

        Assert.Equal(System.IO.Path.GetFullPath(_dir), reopened.StacksFolder);
        Assert.Equal("web", Assert.Single(reopened.KnownOn("h")).Project);
        Assert.Empty(reopened.KnownOn("other"));
    }

    [Fact]
    public void RememberingTheSameThingTwiceWritesOnce()
    {
        var registry = new StackRegistry(PathOf);
        var record = new StackRecord("h", "web", "/s/web", ["/s/web/compose.yaml"]);
        var changes = 0;
        registry.Changed += () => changes++;

        registry.Remember(record);
        Assert.False(registry.Remember(record with { Files = ["/s/web/compose.yaml"] }));

        Assert.Equal(1, changes);
    }

    [Fact]
    public void ForgettingIsRememberedUntilTheStackIsDeployedAgain()
    {
        var registry = new StackRegistry(PathOf);
        registry.Remember(new StackRecord("h", "web", "/s/web", []));

        registry.Forget("h", "web", folder: null);

        Assert.Empty(registry.KnownOn("h"));
        Assert.True(new StackRegistry(PathOf).IsForgotten("h", "web"));

        registry.Unforget("h", "web");
        Assert.False(registry.IsForgotten("h", "web"));
    }

    [Fact]
    public void AFileDrayCannotReadStartsEmptyRatherThanFailing()
    {
        File.WriteAllText(PathOf, "{ not json");

        var registry = new StackRegistry(PathOf);

        Assert.Null(registry.StacksFolder);
        Assert.Empty(registry.AdoptedFolders);
    }

    [Fact]
    public void NoValueIsEverWritten()
    {
        // The registry holds where a stack is, never what is in it.
        var registry = new StackRegistry(PathOf);
        registry.Remember(new StackRecord("h", "web", "/s/web", ["/s/web/compose.yaml"]));

        var json = File.ReadAllText(PathOf);
        Assert.DoesNotContain("environment", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }
}

public class StackOperationsTests
{
    static async Task<(EngineManager Manager, RecordingProcessRunner Runner)> ManagerAsync()
    {
        var source = new FakeConfigSource().WithContext("alpha", "unix:///a.sock").WithConfig("alpha");
        var runner = new RecordingProcessRunner().Returns("compose version --short", "5.4.0\n");
        var manager = new EngineManager(new DockerContextReader(source), new FakeRuntimeFactory(), new ComposeCli(runner));
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        return (manager, runner);
    }

    static async Task<StackOperation> Finished(StackOperation operation)
    {
        for (var i = 0; i < 200 && operation.IsRunning; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        return operation;
    }

    [Fact]
    public async Task EveryStepRunsInOrderAndAnnouncesItself()
    {
        var (manager, runner) = await ManagerAsync();
        await using var _ = manager;

        var operation = new StackOperations(manager).Start(
            new StackSummary { Name = "web" }, "Update", [["pull"], ["up", "-d"]])!;

        await Finished(operation);

        Assert.Equal(OperationState.Succeeded, operation.State);
        Assert.Equal(["pull", "up"], runner.Streams.Select(s => s.Arguments[5]));
        Assert.StartsWith("$ docker --context alpha compose -p web pull", operation.Lines[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedStepStopsTheRest()
    {
        // An up after a pull that failed would deploy the images the pull failed to replace.
        var (manager, runner) = await ManagerAsync();
        await using var _ = manager;
        runner.ExitCode = 1;

        var operation = await Finished(new StackOperations(manager).Start(
            new StackSummary { Name = "web" }, "Update", [["pull"], ["up", "-d"]])!);

        Assert.Equal(OperationState.Failed, operation.State);
        Assert.Equal(1, operation.ExitCode);
        Assert.Single(runner.Streams);
    }

    [Fact]
    public async Task OneOperationAtATimePerStack()
    {
        var (manager, runner) = await ManagerAsync();
        await using var __ = manager;
        var operations = new StackOperations(manager);

        // The fake's compose does not exit until released: the first deploy is genuinely in flight.
        var release = new TaskCompletionSource();
        runner.Hold = release.Task;

        var first = operations.Start(new StackSummary { Name = "web" }, "Deploy", [["up", "-d"]]);

        // Refused while the first is in flight; another stack is not affected.
        Assert.Null(operations.Start(new StackSummary { Name = "web" }, "Down", [["down"]]));
        Assert.NotNull(operations.Start(new StackSummary { Name = "api" }, "Down", [["down"]]));

        release.SetResult();
        await Finished(first!);
        Assert.Same(first, operations.For("web"));
    }
}

public class StackDriftTests
{
    // Captured from compose 5.4.0: `config --hash '*'` before and after editing worker's command.
    const string Before = "web bf6121f23746cbb8aa6e190dbdd370ed3928bc89066e458132ea9a62bcbc8edd\nworker b9e27cb1fb16e9fb483e685400e257fa56f11aa94948ba27d83c99fc1671d5a3\n";
    const string After = "web bf6121f23746cbb8aa6e190dbdd370ed3928bc89066e458132ea9a62bcbc8edd\nworker 110bd8d8f056a5bf557d71f9553f96e6976f2503f733958234641e754d4a8bb2\n";

    static StackSummary Running(params (string Service, string? Hash)[] services) => StackDiscovery.From(
    [
        .. services.Select(s => new ContainerSummary
        {
            Id = s.Service, Name = "p-" + s.Service + "-1", Image = "x", State = DockerState.Running,
            Compose = new ComposeMembership("p", s.Service, ConfigHash: s.Hash),
        }),
    ]).Single();

    static readonly IReadOnlyDictionary<string, string> Deployed = StackDrift.ParseHashes(Before);

    [Fact]
    public void AnUneditedFileIsUnchanged()
    {
        var drift = StackDrift.Compare(Running(("web", Deployed["web"]), ("worker", Deployed["worker"])), Deployed);

        Assert.All(drift.Values, d => Assert.Equal(ServiceDrift.Unchanged, d));
    }

    [Fact]
    public void OnlyTheEditedServiceHasChanged()
    {
        var drift = StackDrift.Compare(Running(("web", Deployed["web"]), ("worker", Deployed["worker"])), StackDrift.ParseHashes(After));

        Assert.Equal(ServiceDrift.Unchanged, drift["web"]);
        Assert.Equal(ServiceDrift.Changed, drift["worker"]);
    }

    [Fact]
    public void NewAndRemovedServicesAreNamedAsSuch()
    {
        var drift = StackDrift.Compare(
            Running(("web", Deployed["web"]), ("legacy", "0000000000000000000000000000000000000000000000000000000000000000")),
            Deployed);

        Assert.Equal(ServiceDrift.NotCreated, drift["worker"]);
        Assert.Equal(ServiceDrift.Removed, drift["legacy"]);
    }

    [Fact]
    public void NoHashOnTheContainerIsUnknownNotChanged()
        => Assert.Equal(ServiceDrift.Unknown, StackDrift.Compare(Running(("web", null)), Deployed)["web"]);

    [Fact]
    public void WarningsAndBlankLinesAreNotServices()
        => Assert.Equal(["web"], StackDrift.ParseHashes("\nweb bf6121f23746cbb8aa6e190dbdd370ed3928bc89066e458132ea9a62bcbc8edd\nWARN something odd\n").Keys);
}

public sealed class StackHistoryTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("dray-history-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static OperationRecord Op(string stack, int minute, OperationState state = OperationState.Succeeded)
        => new("h", stack, "Deploy", ["docker compose -p " + stack + " up -d"], DateTimeOffset.UnixEpoch.AddMinutes(minute), DateTimeOffset.UnixEpoch.AddMinutes(minute + 1), state, 0);

    [Fact]
    public void HistorySurvivesARestartNewestFirst()
    {
        var path = Path.Combine(_dir, "history.json");
        var history = new StackHistory(path);
        history.Add(Op("web", 1));
        history.Add(Op("web", 2, OperationState.Failed));
        history.Add(Op("api", 3));

        var reopened = new StackHistory(path).For("h", "web");

        Assert.Equal([OperationState.Failed, OperationState.Succeeded], reopened.Select(r => r.State));
        Assert.Equal(TimeSpan.FromMinutes(1), reopened[0].Duration);
    }

    [Fact]
    public void OnlyTheLatestAreKeptPerStack()
    {
        var history = new StackHistory(Path.Combine(_dir, "history.json"));
        for (var i = 0; i < StackHistory.Kept + 5; i++) history.Add(Op("web", i));
        history.Add(Op("api", 0));

        Assert.Equal(StackHistory.Kept, history.For("h", "web").Count);
        Assert.Single(history.For("h", "api"));
    }

    [Fact]
    public void AMountedFileEditIsTheOneThingComposeCannotSee()
    {
        var stack = Path.Combine(_dir, "web");
        Directory.CreateDirectory(Path.Combine(stack, "conf"));
        File.WriteAllText(Path.Combine(stack, "conf", "nginx.conf"), "worker_processes 1;");
        File.WriteAllText(Path.Combine(stack, "compose.yaml"),
            "services:\n  web:\n    image: nginx:1\n    volumes:\n      - ./conf/nginx.conf:/etc/nginx/nginx.conf:ro\n      - ./data:/data\n      - cache:/cache\n");
        Directory.CreateDirectory(Path.Combine(stack, "data"));

        var files = new[] { Path.Combine(stack, "compose.yaml") };
        var deployed = StackMounts.Snapshot(files, stack);

        // One file hashed: not the directory, not the named volume.
        Assert.Equal(["nginx.conf"], deployed.Services["web"].Keys.Select(Path.GetFileName));
        Assert.Empty(StackMounts.Changed(StackMounts.Snapshot(files, stack), deployed));

        File.WriteAllText(Path.Combine(stack, "conf", "nginx.conf"), "worker_processes 4;");

        Assert.Equal(["nginx.conf"], StackMounts.Changed(StackMounts.Snapshot(files, stack), deployed)["web"]);

        // Never deployed by Dray: nothing to compare with, so nothing is claimed.
        Assert.Empty(StackMounts.Changed(StackMounts.Snapshot(files, stack), null));
    }
}
