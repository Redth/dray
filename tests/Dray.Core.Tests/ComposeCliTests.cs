using Dray.Core.Engine;
using Dray.Core.Tests.Fakes;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Pointing compose at the host on screen, and nothing else.
/// <para>
/// Before these, compose ran with no host at all and followed the CLI's current context — so a
/// stack opened under the NAS deployed to whatever the terminal was last switched to. Every
/// assertion here is about where a command lands or what it can see.
/// </para>
/// </summary>
public class ComposeCliTests
{
    static readonly ComposeCommand Plugin = new("docker", ["compose"], "5.4.0");
    static readonly ComposeCommand Standalone = new("docker-compose", [], "5.4.0");

    static DockerHost Host(string id, string raw, HostOrigin origin = HostOrigin.DockerContext, EndpointTls? tls = null)
        => new() { Id = id, Name = id, Endpoint = DockerEndpoint.Parse(raw, tls)!, Origin = origin };

    // ---------------------------------------------------------------- target

    [Fact]
    public void AContextIsNamedOnTheCommandLine()
    {
        var target = ComposeTarget.For(Host("nas", "ssh://me@nas"), Plugin)!;

        Assert.Equal(["--context", "nas"], target.GlobalArguments);
        Assert.Empty(target.Environment);
    }

    [Fact]
    public void AHostWithNoContextIsGivenItsEndpoint()
    {
        var target = ComposeTarget.For(
            Host("discovered:/var/run/docker.sock", "/var/run/docker.sock", HostOrigin.Discovered), Plugin)!;

        Assert.Equal(["-H", "unix:///var/run/docker.sock"], target.GlobalArguments);
    }

    [Fact]
    public void APipeIsSpelledTheWayTheCliWantsIt()
    {
        var target = ComposeTarget.For(
            Host("discovered:pipe", "//./pipe/docker_engine", HostOrigin.Discovered), Plugin)!;

        Assert.Equal(["-H", "npipe://./pipe/docker_engine"], target.GlobalArguments);
    }

    [Fact]
    public void VerifiedTlsCarriesEveryFileItHas()
    {
        var tls = new EndpointTls("/certs/ca.pem", "/certs/cert.pem", "/certs/key.pem", SkipVerify: false);
        var target = ComposeTarget.For(Host("env:DOCKER_HOST", "tcp://10.0.0.4:2376", HostOrigin.Environment, tls), Plugin)!;

        Assert.Equal(
            [
                "-H", "tcp://10.0.0.4:2376", "--tlsverify",
                "--tlscacert", "/certs/ca.pem", "--tlscert", "/certs/cert.pem", "--tlskey", "/certs/key.pem",
            ],
            target.GlobalArguments);
    }

    [Fact]
    public void SkippedVerificationIsNotQuietlyTurnedBackOn()
    {
        // --tlsverify against a host the user told Docker not to verify refuses it outright.
        var tls = new EndpointTls(null, null, null, SkipVerify: true);
        var target = ComposeTarget.For(Host("env:DOCKER_HOST", "tcp://10.0.0.4:2376", HostOrigin.Environment, tls), Plugin)!;

        Assert.Equal(["-H", "tcp://10.0.0.4:2376", "--tls"], target.GlobalArguments);
    }

    [Fact]
    public void TheStandaloneBinaryIsAddressedThroughItsEnvironment()
    {
        // docker-compose has no --context or -H; the variables are the only thing it reads.
        var context = ComposeTarget.For(Host("nas", "ssh://me@nas"), Standalone)!;
        Assert.Empty(context.GlobalArguments);
        Assert.Equal("nas", context.Environment["DOCKER_CONTEXT"]);

        var socket = ComposeTarget.For(Host("discovered:x", "/x.sock", HostOrigin.Discovered), Standalone)!;
        Assert.Equal("unix:///x.sock", socket.Environment["DOCKER_HOST"]);
    }

    [Fact]
    public void StandaloneTlsNeedsTheFilesUnderTheirConventionalNames()
    {
        var conventional = new EndpointTls("/c/ca.pem", "/c/cert.pem", "/c/key.pem", SkipVerify: false);
        var target = ComposeTarget.For(Host("env", "tcp://h:2376", HostOrigin.Environment, conventional), Standalone)!;

        Assert.Equal("1", target.Environment["DOCKER_TLS_VERIFY"]);
        Assert.Equal("/c", target.Environment["DOCKER_CERT_PATH"]);

        // DOCKER_CERT_PATH can only say "this directory, these three names". Anything else has no
        // way to be said, and reaching the host without its certificate is not an option.
        var renamed = new EndpointTls("/c/root.crt", "/c/cert.pem", "/c/key.pem", SkipVerify: false);
        Assert.Null(ComposeTarget.For(Host("env", "tcp://h:2376", HostOrigin.Environment, renamed), Standalone));

        var split = new EndpointTls("/a/ca.pem", "/b/cert.pem", "/b/key.pem", SkipVerify: false);
        Assert.Null(ComposeTarget.For(Host("env", "tcp://h:2376", HostOrigin.Environment, split), Standalone));
    }

    [Fact]
    public void EnginesComposeCannotReachHaveNoTarget()
    {
        Assert.Null(ComposeTarget.For(Host("wsl:Ubuntu", "wsl://Ubuntu", HostOrigin.WslDistro), Plugin));
        var apple = new DockerHost
        {
            Id = "apple:container",
            Name = "Apple",
            Origin = HostOrigin.AppleContainer,
            Endpoint = new DockerEndpoint
            {
                Scheme = EndpointScheme.AppleContainer, Raw = "/usr/local/bin/container", Path = "/usr/local/bin/container",
            },
        };

        Assert.Null(ComposeTarget.For(apple, Plugin));
    }

    // ---------------------------------------------------------------- arguments

    [Fact]
    public void GlobalFlagsComeBeforeTheSubcommand()
    {
        // `docker compose --context nas` is an unknown-flag error; the flag belongs to docker.
        var target = ComposeTarget.For(Host("nas", "ssh://me@nas"), Plugin)!;

        var args = ComposeCli.Arguments(Plugin, target, "web", ["/stacks/web/compose.yaml"], ["up", "-d"]);

        Assert.Equal(
            ["--context", "nas", "compose", "-p", "web", "-f", "/stacks/web/compose.yaml", "up", "-d"],
            args);
    }

    [Fact]
    public void TheStandaloneBinaryTakesNoLeadingSubcommand()
    {
        var target = ComposeTarget.For(Host("nas", "ssh://me@nas"), Standalone)!;

        Assert.Equal(["-p", "web", "down"], ComposeCli.Arguments(Standalone, target, "web", [], ["down"]));
    }

    // ---------------------------------------------------------------- environment

    [Fact]
    public void TheShellIsNotPassedThrough()
    {
        // Each of these, inherited, changes what compose does: COMPOSE_FILE points it at another
        // file, DOCKER_HOST at another engine, and TAG overrides the .env the user is looking at.
        var shell = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/local/bin:/usr/bin",
            ["HOME"] = "/Users/me",
            ["SSH_AUTH_SOCK"] = "/tmp/agent.sock",
            ["COMPOSE_FILE"] = "/elsewhere/compose.yaml",
            ["COMPOSE_PROJECT_NAME"] = "other",
            ["DOCKER_HOST"] = "tcp://wrong:2375",
            ["DOCKER_CONTEXT"] = "wrong",
            ["TAG"] = "from-the-shell",
        };

        var target = ComposeTarget.For(Host("nas", "ssh://me@nas"), Plugin)!;
        var environment = ComposeEnvironment.For(shell, target);

        Assert.Equal(
            ["HOME", "PATH", "SSH_AUTH_SOCK"],
            environment.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheTargetsOwnVariablesAreAdded()
    {
        var target = ComposeTarget.For(Host("nas", "ssh://me@nas"), Standalone)!;
        var environment = ComposeEnvironment.For(new Dictionary<string, string> { ["PATH"] = "/bin" }, target);

        Assert.Equal("nas", environment["DOCKER_CONTEXT"]);
        Assert.Equal("/bin", environment["PATH"]);
    }

    [Fact]
    public async Task RunningHandsTheRunnerTheWholeInvocation()
    {
        var runner = new RecordingProcessRunner();
        var compose = new ComposeCli(runner, () => new Dictionary<string, string> { ["PATH"] = "/bin", ["TAG"] = "x" });
        var target = ComposeTarget.For(Host("nas", "ssh://me@nas"), Plugin)!;

        await foreach (var _ in compose.RunAsync(Plugin, target, "web", [], ["up", "-d"], "/stacks/web", TestContext.Current.CancellationToken))
        {
        }

        var run = Assert.Single(runner.Streams);
        Assert.Equal("docker", run.Executable);
        Assert.Equal(["--context", "nas", "compose", "-p", "web", "up", "-d"], run.Arguments);
        Assert.Equal("/stacks/web", run.WorkingDirectory);
        Assert.Equal(["PATH"], run.Environment!.Keys);
    }

    // ---------------------------------------------------------------- the real process

    [Fact]
    public async Task TheExitCodeArrivesLastAndAfterEveryLine()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses /bin/sh.");

        var lines = new List<ComposeOutput>();

        await foreach (var line in new SystemProcessRunner().StreamAsync(
            "/bin/sh", ["-c", "echo one; echo two >&2; exit 3"], null,
            new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" },
            TestContext.Current.CancellationToken))
        {
            lines.Add(line);
        }

        Assert.Equal(3, lines[^1].ExitCode);
        Assert.Equal(["one", "two"], lines.SkipLast(1).Select(l => l.Text).Order());
        Assert.All(lines.SkipLast(1), l => Assert.False(l.IsExit));
    }

    [Fact]
    public async Task TheEnvironmentIsExactlyWhatWasGiven()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses /bin/sh.");

        var name = "DRAY_LEAK_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "leaked");

        try
        {
            var lines = new List<string>();

            await foreach (var line in new SystemProcessRunner().StreamAsync(
                "/bin/sh", ["-c", $"echo \"${{{name}-absent}}\""], null,
                new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" },
                TestContext.Current.CancellationToken))
            {
                if (!line.IsExit) lines.Add(line.Text);
            }

            Assert.Equal(["absent"], lines);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task TheOlderStreamReportsNoExitCode()
    {
        // Callers that predate the exit code — Apple's logs, buildx — must not suddenly receive an
        // empty line at the end of every stream.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses /bin/sh.");

        var lines = new List<ComposeOutput>();

        await foreach (var line in new SystemProcessRunner().StreamAsync(
            "/bin/sh", ["-c", "echo one"], null, TestContext.Current.CancellationToken))
        {
            lines.Add(line);
        }

        Assert.Equal("one", Assert.Single(lines).Text);
    }
}
