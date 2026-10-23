using Dray.Core.Engine;
using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>Stacks whose files live on an SSH host, and compose run there.</summary>
public class RemoteStackTests
{
    static SshShell Shell(string raw) => new(DockerEndpoint.Parse(raw)!);

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("/srv/my stack/compose.yaml", "'/srv/my stack/compose.yaml'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("$(rm -rf /)", "'$(rm -rf /)'")]
    [InlineData("", "''")]
    public void EverythingTheRemoteShellSeesIsQuoted(string value, string quoted)
        => Assert.Equal(quoted, SshShell.Quote(value));

    [Fact]
    public void TheSshArgumentsMatchTheEnginesOwnTransport()
    {
        // The same shape the API relay uses, so a host that answers for the engine answers here.
        Assert.Equal(
            ["-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-p", "2222", "me@nas", "--", "true"],
            Shell("ssh://me@nas:2222").Arguments("true"));
    }

    [Fact]
    public void ComposeRunsInTheProjectDirectoryOnTheHost()
    {
        var command = RemoteCompose.Command("web", ["/srv/stacks/web/compose.yaml"], ["up", "-d"], "/srv/stacks/web");

        Assert.Equal(
            "cd /srv/stacks/web && env -u COMPOSE_FILE -u COMPOSE_PROJECT_NAME -u COMPOSE_PROFILES docker compose -p web -f /srv/stacks/web/compose.yaml up -d",
            command);
    }

    [Fact]
    public void ARemoteCommandIsShownAsTheSshItIs()
        => Assert.Equal(
            "ssh nas -- 'cd /srv/x && env -u COMPOSE_FILE -u COMPOSE_PROJECT_NAME -u COMPOSE_PROFILES docker compose -p x ps'",
            new RemoteCompose(Shell("ssh://nas")).Display("x", [], ["ps"], "/srv/x"));

    static readonly Func<IReadOnlyList<string>, IReadOnlyList<ComposeServiceEntry>> None = _ => [];

    [Fact]
    public void FilesFoundOnTheHostMakeTheStackTheHosts()
    {
        var observed = StackDiscovery.From(
        [
            new ContainerSummary
            {
                Id = "a", Name = "web-app-1", Image = "x", State = DockerState.Running,
                Compose = new ComposeMembership("web", "app", ["/srv/stacks/web/compose.yaml"], "/srv/stacks/web"),
            },
        ]);

        // Not on this machine: observed, unless the host said the files are there.
        Assert.Equal(StackProvenance.Observed, StackCatalog.Merge(observed, [], [], null, None, _ => false).Single().Provenance);
        Assert.Equal(StackProvenance.Remote, StackCatalog.Merge(observed, [], [], null, None, _ => false, new HashSet<string> { "web" }).Single().Provenance);
    }

    [Fact]
    public void ARemoteStackThatIsDownIsStillTheHosts()
    {
        var stack = StackCatalog.Merge(
            [], [], [new StackRecord("nas", "web", "/srv/stacks/web", ["/srv/stacks/web/compose.yaml"], OnHost: true)], null, None, _ => false).Single();

        Assert.Equal(StackProvenance.Remote, stack.Provenance);
        Assert.Equal(StackState.Down, stack.State);
    }
}

public class RemoteComposeProbeTests
{
    [Fact]
    public async Task ACancelledFirstCallerDoesNotPoisonTheProbe()
    {
        // The bug: the first check on a host was cancelled by a save, the shared probe with it, and
        // every later compose run on that host "cancelled" before starting.
        var runner = new Fakes.RecordingProcessRunner().Returns("docker compose version --short", "5.6.1\n");
        var remote = new RemoteCompose(new SshShell(DockerEndpoint.Parse("ssh://nas")!, runner));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // The cancelled caller may get its answer or its cancellation; either way it is its own.
        try
        {
            await remote.DetectAsync(cancelled.Token);
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Equal("5.6.1", await remote.DetectAsync(TestContext.Current.CancellationToken));
    }
}
