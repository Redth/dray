using Dray.Core.Engine;
using Dray.Core.Model;
using Xunit;

namespace Dray.Apple.Tests;

/// <summary>
/// The second engine behind the seam.
/// <para>
/// Most of these guard against the same temptation: filling a gap Apple's runtime genuinely has.
/// An exit code of 0 for a container that crashed, a memory limit invented from the host, a shell
/// that pretends to work — each would be a nicer-looking screen and a lie. The tests assert the
/// absence, so the gap has to be closed deliberately rather than by accident.
/// </para>
/// </summary>
public class AppleRuntimeTests
{
    /// <summary>The running test's token, which every call below threads through.</summary>
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static ScriptedProcessRunner Connected() => new ScriptedProcessRunner()
        .Returns("--version", AppleFixtures.Version)
        .Returns("system status", AppleFixtures.SystemStatusRunning);

    static async Task<(AppleRuntime Runtime, ScriptedProcessRunner Runner)> ConnectAsync(ScriptedProcessRunner? runner = null)
    {
        runner ??= Connected();
        var runtime = new AppleRuntime(runner);
        await runtime.ConnectAsync(Ct);
        return (runtime, runner);
    }

    // ---------------------------------------------------------------- connecting

    [Fact]
    public async Task ConnectingReportsTheEngineHonestly()
    {
        var (runtime, _) = await ConnectAsync();

        Assert.Equal(EngineFlavor.Apple, runtime.Capabilities.Flavor);
        Assert.Equal("1.3.0", runtime.Capabilities.EngineVersion);

        // The whole reason RuntimeEventPump grew a polling path. There is no `events` subcommand
        // at all, so claiming otherwise would leave the pump waiting on a stream that never speaks.
        Assert.False(runtime.Capabilities.SupportsEvents);

        // Compose and BuildKit are Docker's, not this engine's.
        Assert.False(runtime.Capabilities.SupportsCompose);
        Assert.False(runtime.Capabilities.SupportsBuildKit);

        // Each of these hides a control that would otherwise render and fail. Asserted together
        // because they are one decision: report what the engine cannot do, do not catch it later.
        Assert.False(runtime.Capabilities.SupportsPause);
        Assert.False(runtime.Capabilities.SupportsRename);
        Assert.False(runtime.Capabilities.SupportsNetworks);
        Assert.False(runtime.Capabilities.SupportsLogMetadata);

        // These two were reported unsupported on the strength of a glance at the subcommand list,
        // and testing them showed otherwise: `exec -i` streams in both directions and
        // `container volume` manages volumes properly. Asserted so the claim cannot regress to the
        // guess it started as.
        Assert.True(runtime.Capabilities.SupportsShell);
        Assert.True(runtime.Capabilities.SupportsVolumes);

        // This one was checked rather than assumed: `cp` and `exec` both refuse a container that
        // is not running.
        Assert.False(runtime.Capabilities.SupportsStoppedFileAccess);

        // Stats do work here, at a slower cadence. Reporting them absent would empty two columns
        // that have real numbers behind them.
        Assert.True(runtime.Capabilities.SupportsStats);
    }

    [Fact]
    public async Task ThereAreNoNetworksToManageRatherThanASyntheticOne()
    {
        var (runtime, _) = await ConnectAsync(Connected().Returns("ls", AppleFixtures.ListJson));

        // Containers do report the network they joined, so a "default" row could be derived. It
        // would be a row the user cannot inspect, connect to, disconnect from or remove.
        Assert.Empty(await runtime.ListNetworksAsync(Ct));
    }

    // ---------------------------------------------------------------- volumes

    [Fact]
    public async Task VolumesAreListedWithTheirRealMountPoint()
    {
        var (runtime, _) = await ConnectAsync(Connected()
            .Returns("volume ls", AppleFixtures.VolumesJson)
            .Returns("ls --all", AppleFixtures.ListJson));

        var volume = Assert.Single(await runtime.ListVolumesAsync(Ct));

        Assert.Equal("dray-vol-check", volume.Name);
        Assert.Equal("local", volume.Driver);

        // Unusually for a container engine this is a path on the user's own machine, not one
        // inside a VM they cannot reach.
        Assert.Contains("Application Support", volume.Mountpoint!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVolumesSizeIsUnknownRatherThanTheHalfTerabyteTheImageClaims()
    {
        var (runtime, _) = await ConnectAsync(Connected()
            .Returns("volume ls", AppleFixtures.VolumesJson)
            .Returns("ls --all", AppleFixtures.ListJson));

        // sizeInBytes is the disk image's allocated size — 549755813888 for an empty volume,
        // because the file is sparse. Reporting it would say every volume is 512 GB.
        Assert.Null(Assert.Single(await runtime.ListVolumesAsync(Ct)).SizeBytes);
    }

    [Fact]
    public async Task AVolumeKnowsWhichContainersHoldIt()
    {
        var (runtime, _) = await ConnectAsync(Connected()
            .Returns("volume ls", AppleFixtures.VolumesJson)
            .Returns("ls --all", AppleFixtures.ContainerWithVolumeJson));

        // The engine does not report users, so this is derived from the containers' own mounts —
        // and "nothing is holding this" is the question worth answering before a delete.
        Assert.Equal(["shop-db-1"], Assert.Single(await runtime.ListVolumesAsync(Ct)).UsedBy);
    }

    [Fact]
    public async Task AHostPathMountIsNotMistakenForAVolume()
    {
        var (runtime, _) = await ConnectAsync(Connected()
            .Returns("volume ls", AppleFixtures.VolumesJson)
            .Returns("ls --all", AppleFixtures.ComposeContainerJson));

        // The compose fixture binds /Users/redth/site. A leading slash means a host path, and
        // counting it as a volume would attribute someone's folder to a named volume.
        Assert.Empty(Assert.Single(await runtime.ListVolumesAsync(Ct)).UsedBy);
    }

    // ---------------------------------------------------------------- writing

    [Fact]
    public async Task WritingAFileGoesThroughTheContainerRatherThanThroughCopy()
    {
        var runner = Connected().Returns("exec", "");
        var (runtime, _) = await ConnectAsync(runner);

        byte[] content = [0x61, 0x00, 0x62];
        await runtime.WriteFileAsync("web", "/etc/nginx/nginx.conf", content, Ct);

        // `container cp` into a path inside a mounted volume returns exit code 0 and writes
        // nothing — verified on 1.3.0. A file editor that silently discards a save is the worst
        // failure in this app, so the write goes through the container's own filesystem view.
        var invocation = Assert.Single(runner.Invocations, i => i.StartsWith("exec", StringComparison.Ordinal));
        Assert.Contains("cat > '/etc/nginx/nginx.conf'", invocation, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Invocations, i => i.StartsWith("cp ", StringComparison.Ordinal));

        // Raw bytes: a file being edited here may be a certificate, and encoding it through a
        // string would corrupt anything that is not valid UTF-16.
        Assert.Equal(content, Assert.Single(runner.Written));
    }

    [Fact]
    public async Task APathWithAnApostropheIsQuotedRatherThanSplit()
    {
        var runner = Connected().Returns("exec", "");
        var (runtime, _) = await ConnectAsync(runner);

        await runtime.WriteFileAsync("web", "/tmp/it's here.txt", [1], Ct);

        var invocation = Assert.Single(runner.Invocations, i => i.StartsWith("exec", StringComparison.Ordinal));

        // The shell must see one argument, not three. A single-quoted string cannot contain a
        // single quote, so the apostrophe closes it, escapes one, and reopens: '…it'\''s here…'
        Assert.Contains("""'/tmp/it'\''s here.txt'""", invocation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInstalledButStoppedServiceSaysHowToStartIt()
    {
        var runner = new ScriptedProcessRunner()
            .Returns("--version", AppleFixtures.Version)
            .Returns("system status", "", exitCode: 1, stderr: AppleFixtures.SystemStatusStopped);

        var error = await Assert.ThrowsAsync<RuntimeConnectionException>(
            () => new AppleRuntime(runner).ConnectAsync(Ct));

        // "Could not connect" would leave the user nowhere. The fix is one command and it is in
        // the message.
        Assert.Contains("container system start", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVersionIsReadOutOfTheCliBanner()
    {
        Assert.Equal("1.3.0", AppleRuntime.ParseVersion(AppleFixtures.Version));
        Assert.Null(AppleRuntime.ParseVersion("command not found"));
    }

    // ---------------------------------------------------------------- listing

    [Fact]
    public async Task AContainersIdIsItsName()
    {
        var (runtime, _) = await ConnectAsync(Connected().Returns("ls", AppleFixtures.ListJson));

        var running = (await runtime.ListContainersAsync(ct: Ct)).First(c => c.State == DockerState.Running);

        // Apple has no 64-hex id. Inventing one, or leaving Id empty and Name populated, would
        // break every lookup that addresses a container by id.
        Assert.Equal("dray-apple-test", running.Id);
        Assert.Equal("dray-apple-test", running.Name);
        Assert.Equal("docker.io/library/alpine:latest", running.Image);
    }

    [Fact]
    public async Task AStoppedContainerHasNoExitCodeBecauseTheEngineDoesNotReportOne()
    {
        var (runtime, _) = await ConnectAsync(Connected().Returns("ls", AppleFixtures.ListJson));

        var exited = (await runtime.ListContainersAsync(ct: Ct)).First(c => c.Id == "dray-apple-exit");

        Assert.Equal(DockerState.Exited, exited.State);

        // The container in this fixture ran `exit 7`. Nothing in the engine's output says so —
        // not in `ls`, not in `inspect`. A zero here would read as "finished cleanly" and would
        // be wrong for exactly the containers a user is investigating.
        Assert.Null(exited.ExitCode);
    }

    [Fact]
    public async Task HealthIsAbsentBecauseTheConceptDoesNotExistHere()
    {
        var (runtime, _) = await ConnectAsync(Connected().Returns("ls", AppleFixtures.ListJson));

        Assert.All(await runtime.ListContainersAsync(ct: Ct), c => Assert.Equal(DockerHealth.None, c.Health));
    }

    [Fact]
    public async Task PublishedPortsAndComposeMembershipSurvive()
    {
        var (runtime, _) = await ConnectAsync(Connected().Returns("ls", AppleFixtures.ComposeContainerJson));

        var web = Assert.Single(await runtime.ListContainersAsync(ct: Ct));

        Assert.Equal([8080, 8443], web.Ports.Select(p => p.HostPort));
        Assert.Equal("shop", web.Stack);
        Assert.Equal("web", web.Compose?.Service);
    }

    [Fact]
    public async Task ListingWithoutStoppedContainersDoesNotAskForThem()
    {
        var (runtime, runner) = await ConnectAsync(Connected().Returns("ls", AppleFixtures.ListJson));

        await runtime.ListContainersAsync(includeStopped: false, Ct);

        Assert.DoesNotContain(runner.Invocations, i => i.Contains("--all", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GarbageFromTheCliBecomesAnEmptyListRatherThanACrash()
    {
        var (runtime, _) = await ConnectAsync(Connected().Returns("ls", "Downloading image...\nnot json"));

        Assert.Empty(await runtime.ListContainersAsync(ct: Ct));
    }

    // ---------------------------------------------------------------- inspect

    [Fact]
    public async Task InspectCarriesTheCommandUserAndAddress()
    {
        var (runtime, _) = await ConnectAsync(Connected().Returns("inspect", AppleFixtures.ComposeContainerJson));

        var inspect = await runtime.InspectContainerAsync("shop-web-1", Ct);

        Assert.Equal(["nginx"], inspect.Entrypoint);
        Assert.Equal("101:0", inspect.User);

        // The engine reports the address with its prefix. A UI showing "192.168.64.3/24" as an
        // address is showing a subnet.
        Assert.Equal("192.168.64.3", inspect.Networks.Single().IpAddress);
    }

    [Fact]
    public async Task InspectMasksASecretItRecognisesAndLeavesTheRest()
    {
        var (runtime, _) = await ConnectAsync(Connected().Returns("inspect", AppleFixtures.ComposeContainerJson));

        var env = (await runtime.InspectContainerAsync("shop-web-1", Ct)).Environment;

        Assert.True(env.Single(e => e.Key == "DB_PASSWORD").IsSecret);
        Assert.False(env.Single(e => e.Key == "TZ").IsSecret);
    }

    [Fact]
    public async Task InspectKeepsTheRawJsonForWhatDrayDidNotModel()
    {
        var (runtime, _) = await ConnectAsync(Connected().Returns("inspect", AppleFixtures.ListJson));

        var inspect = await runtime.InspectContainerAsync("dray-apple-test", Ct);

        // Half of this engine's record has no Docker equivalent — rosetta, runtimeHandler,
        // virtualization. The Inspect tab is where a user goes to find it, so none of it may be
        // dropped just because Dray has no field for it.
        Assert.Contains("runtimeHandler", inspect.RawJson, StringComparison.Ordinal);
        Assert.Contains("rosetta", inspect.RawJson, StringComparison.Ordinal);

        // Unwrapped from the CLI's single-element array and indented: the model's contract is
        // readable JSON, and 2 KB on one line is worse than the terminal this is meant to replace.
        Assert.StartsWith("{", inspect.RawJson, StringComparison.Ordinal);
        Assert.Contains('\n', inspect.RawJson);
    }

    [Fact]
    public async Task InspectingSomethingGoneSaysSoRatherThanReturningAnEmptyRecord()
    {
        var (runtime, _) = await ConnectAsync(Connected().Returns("inspect", "[]"));

        await Assert.ThrowsAsync<RuntimeConnectionException>(() => runtime.InspectContainerAsync("ghost", Ct));
    }

    [Fact]
    public async Task AStoppedContainerHasNoFinishTimeEither()
    {
        var (runtime, _) = await ConnectAsync(Connected().Returns("inspect", AppleFixtures.ListJson));

        var inspect = await runtime.InspectContainerAsync("dray-apple-test", Ct);

        // The engine records when a container started and whether it is still going, and nothing
        // about how it ended. Reporting "now" would put a finish time on a running container,
        // which is the podman bug this project already fixed once.
        Assert.Null(inspect.FinishedAt);
    }

    // ---------------------------------------------------------------- actions

    [Fact]
    public async Task RestartIsAStopFollowedByAStartBecauseThereIsNoRestartCommand()
    {
        var runner = Connected().Returns("stop", "").Returns("start", "");
        var (runtime, _) = await ConnectAsync(runner);

        await runtime.PerformAsync("dray-apple-test", ContainerAction.Restart, Ct);

        var lifecycle = runner.Invocations.Where(i => i.StartsWith("stop", StringComparison.Ordinal)
                                                   || i.StartsWith("start", StringComparison.Ordinal)).ToList();

        Assert.Equal(["stop dray-apple-test", "start dray-apple-test"], lifecycle);
    }

    [Theory]
    [InlineData(ContainerAction.Pause)]
    [InlineData(ContainerAction.Unpause)]
    public async Task PausingSaysItCannotRatherThanSilentlyDoingNothing(ContainerAction action)
    {
        var (runtime, runner) = await ConnectAsync();

        var error = await Assert.ThrowsAsync<RuntimeConnectionException>(
            () => runtime.PerformAsync("dray-apple-test", action, Ct));

        Assert.Contains("cannot pause", error.Message, StringComparison.OrdinalIgnoreCase);

        // A no-op that runs a command and ignores the failure would leave the row spinning.
        Assert.DoesNotContain(runner.Invocations, i => i.Contains("pause", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenamingExplainsWhyTheNameCannotChange()
    {
        var (runtime, _) = await ConnectAsync();

        var error = await Assert.ThrowsAsync<RuntimeConnectionException>(
            () => runtime.RenameAsync("dray-apple-test", "something-else", Ct));

        // The id is the name, so a rename would mean recreating the container — a different
        // operation with different consequences.
        Assert.Contains("Recreate it", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedActionCarriesTheEnginesOwnReason()
    {
        var runner = Connected().Returns("stop", "", exitCode: 1, stderr: "Error: container is not running");
        var (runtime, _) = await ConnectAsync(runner);

        var error = await Assert.ThrowsAsync<RuntimeConnectionException>(
            () => runtime.PerformAsync("dray-apple-test", ContainerAction.Stop, Ct));

        Assert.Contains("container is not running", error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- logs

    [Fact]
    public async Task TheCliesOwnDiagnosticsAreNotPassedOffAsContainerOutput()
    {
        var (runtime, runner) = await ConnectAsync();
        runner.StreamedLines.AddRange(["hello from stdout", "hello from stderr", "!container: warning about something"]);

        var lines = new List<LogLine>();
        await foreach (var line in runtime.StreamLogsAsync("dray-apple-test", new LogOptions(Follow: false, Tail: 10), Ct))
            lines.Add(line);

        // `container logs` merges the container's two streams into its own stdout — verified
        // against the real CLI. Its stderr therefore carries the CLI's problems, not the
        // container's, and labelling those as container output would blame the wrong program.
        Assert.Equal(["hello from stdout", "hello from stderr"], lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.Equal(LogStream.StdOut, l.Stream));

        // No timestamps flag exists; stamping the moment Dray read the line would be a different
        // fact from when it was written.
        Assert.All(lines, l => Assert.Null(l.Timestamp));
        Assert.Equal([0, 1], lines.Select(l => l.Sequence));
    }

    [Fact]
    public async Task ATailIsAskedForWithTheFlagTheCliActuallyHas()
    {
        var (runtime, runner) = await ConnectAsync();

        await foreach (var _ in runtime.StreamLogsAsync("dray-apple-test", new LogOptions(Follow: true, Tail: 50), Ct)) { }

        var invocation = runner.Invocations.Single(i => i.StartsWith("logs", StringComparison.Ordinal));
        Assert.Equal("logs --follow -n 50 dray-apple-test", invocation);
    }

    // ---------------------------------------------------------------- files

    [Fact]
    public async Task AStoppedContainersFilesystemIsUnreachableAndSaysSo()
    {
        var (runtime, runner) = await ConnectAsync();

        var listing = await runtime.ListDirectoryAsync("dray-apple-exit", "/etc", containerIsRunning: false, Ct);

        Assert.Empty(listing.Entries);
        Assert.Contains("running container", listing.Note, StringComparison.Ordinal);

        // Docker's archive route works on a stopped container; this engine has no equivalent, so
        // the answer is an explanation rather than a failed exec.
        Assert.DoesNotContain(runner.Invocations, i => i.StartsWith("exec", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADirectoryListingComesBackParsed()
    {
        var runner = Connected().Returns("ls -la", """
            total 12
            drwxr-xr-x    2 root     root          4096 Aug 27 20:59 apk
            -rw-r--r--    1 root     root            13 Aug 27 20:59 hostname
            lrwxrwxrwx    1 root     root            12 Aug 27 20:59 mtab -> /proc/mounts
            """);

        var (runtime, _) = await ConnectAsync(runner);

        var listing = await runtime.ListDirectoryAsync("dray-apple-test", "/etc", containerIsRunning: true, Ct);

        Assert.Equal(ListingMethod.Exec, listing.Method);
        Assert.Equal(["apk", "hostname", "mtab"], listing.Sorted.Select(e => e.Name));
        Assert.True(listing.Sorted[0].IsDirectory);
        Assert.Equal("/proc/mounts", listing.Sorted[2].LinkTarget);
    }

    [Fact]
    public async Task AnUnreadableDirectoryReturnsTheReasonRatherThanThrowing()
    {
        var runner = Connected().Returns("ls -la", "", exitCode: 1, stderr: "ls: /root: Permission denied");
        var (runtime, _) = await ConnectAsync(runner);

        var listing = await runtime.ListDirectoryAsync("dray-apple-test", "/root", containerIsRunning: true, Ct);

        Assert.Empty(listing.Entries);
        Assert.Equal("ls: /root: Permission denied", listing.Note);
    }

    [Fact]
    public async Task OpeningAShellInAStoppedContainerSaysToStartItFirst()
    {
        var (runtime, _) = await ConnectAsync(Connected().Returns("ls", AppleFixtures.ListJson));

        // Checked up front rather than inferred from an exit code: "that container is not running"
        // is a sentence, and a non-zero status is not.
        var error = await Assert.ThrowsAsync<NoShellException>(
            () => runtime.StartExecAsync("dray-apple-exit", new ExecOptions(), Ct));

        Assert.Contains("not running", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpeningAShellInAContainerThatIsGoneSaysSo()
    {
        var (runtime, _) = await ConnectAsync(Connected().Returns("ls", AppleFixtures.ListJson));

        var error = await Assert.ThrowsAsync<NoShellException>(
            () => runtime.StartExecAsync("ghost", new ExecOptions(), Ct));

        Assert.Contains("no longer exists", error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- images and totals

    [Fact]
    public async Task AnImagesSizeIsUnknownRatherThanItsManifestsSize()
    {
        var (runtime, _) = await ConnectAsync(Connected().Returns("image ls", AppleFixtures.ImagesJson));

        var image = Assert.Single(await runtime.ListImagesAsync(ct: Ct));

        Assert.Equal("docker.io/library/alpine:latest", image.Tags.Single().Display);

        // The descriptor's 9218 bytes is the manifest, not the image. Reporting it would tell the
        // user alpine weighs nine kilobytes.
        Assert.Equal(0, image.SizeBytes);

        // The engine does not say how many containers use an image, and -1 is how the model says
        // "did not say" rather than "none".
        Assert.Null(image.IsInUse);
    }

    [Fact]
    public async Task DiskUsageIsUnknownRatherThanZero()
    {
        var (runtime, _) = await ConnectAsync();

        var usage = await runtime.GetDiskUsageAsync(Ct);

        // "0 B reclaimable" invites the user to stop looking. "Unknown" invites them to look
        // elsewhere, which is the true statement here.
        Assert.False(usage.IsKnown);
    }

    [Fact]
    public async Task SystemTotalsAreCountedFromWhatCanBeListed()
    {
        var (runtime, _) = await ConnectAsync(Connected()
            .Returns("ls --all", AppleFixtures.ListJson)
            .Returns("image ls", AppleFixtures.ImagesJson));

        var info = await runtime.GetSystemInfoAsync(Ct);

        Assert.Equal(1, info.ContainersRunning);
        Assert.Equal(1, info.ContainersStopped);
        Assert.Equal(1, info.Images);
        Assert.Equal("1.3.0", info.ServerVersion);
    }

    [Fact]
    public async Task WatchingEventsNeverYieldsAndEndsWithTheToken()
    {
        var (runtime, _) = await ConnectAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            // There is no stream. The pump reads SupportsEvents and polls instead; this exists so
            // a caller that ignores the capability blocks rather than seeing a silent empty stream
            // it would mistake for a quiet engine.
            await foreach (var _ in runtime.WatchEventsAsync(cts.Token)) { }
        });
    }

    [Fact]
    public void TheDiscoveredPathIsWhatGetsRun()
    {
        // Two installs are two engines. Falling back to bare PATH resolution would silently run
        // whichever one came first, which is not the host the user picked.
        var runtime = new AppleRuntime(
            new ScriptedProcessRunner(), executable: "/opt/homebrew/bin/container");

        Assert.Equal("/opt/homebrew/bin/container", runtime.Executable);
        Assert.Equal("container", new AppleRuntime().Executable);
    }

    [Fact]
    public void TheFactoryClaimsOnlyAppleEndpoints()
    {
        var factory = new AppleRuntimeFactory();

        var apple = new DockerEndpoint
        {
            Scheme = EndpointScheme.AppleContainer,
            Raw = "/opt/homebrew/bin/container",
            Path = "/opt/homebrew/bin/container",
        };

        Assert.True(factory.Handles(apple));
        Assert.False(factory.Handles(new DockerEndpoint { Scheme = EndpointScheme.Unix, Raw = "/var/run/docker.sock" }));

        // The endpoint's path reaches the runtime, rather than being discovered and then discarded.
        Assert.Equal("/opt/homebrew/bin/container", Assert.IsType<AppleRuntime>(factory.Create(apple)).Executable);
    }
}
