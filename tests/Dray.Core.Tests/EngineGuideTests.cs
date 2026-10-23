using Dray.Core.Engine;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The add-a-host chooser says what it measured on this machine. These hold it to evidence: a tile
/// claims an engine is installed, listening or connected only when something on disk or in the
/// host list says so.
/// </summary>
public class EngineGuideTests
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static EngineGuide Guide(string id, GuidePlatform platform = GuidePlatform.MacOS)
        => EngineGuides.For(platform, AppleEngineAvailability.Available).Single(g => g.Id == id);

    static DockerHost Host(string id, string endpoint, HostConnectionState state = HostConnectionState.Disconnected,
        HostOrigin origin = HostOrigin.DockerContext) => new()
    {
        Id = id,
        Name = id,
        Endpoint = DockerEndpoint.Parse(endpoint)!,
        Origin = origin,
        State = state,
    };

    [Fact]
    public void NothingFoundIsAbsent()
    {
        var probe = new EngineGuideProbe(new FakeConfigSource());

        Assert.Equal(GuideState.Absent, probe.Check(Guide("colima"), []).State);
    }

    [Fact]
    public void AnExecutableOnPathIsInstalled()
    {
        // In this machine's own PATH syntax: the probe reads PATH the way the OS it runs on does.
        var source = new FakeConfigSource()
            .WithEnvironment("PATH", string.Join(Path.PathSeparator, "/usr/bin", "/opt/homebrew/bin"))
            .WithExecutable(Path.Combine("/opt/homebrew/bin", "colima"));

        Assert.Equal(GuideState.Installed, new EngineGuideProbe(source).Check(Guide("colima"), []).State);
    }

    [Fact]
    public void ALiveSocketWithNoHostIsListening()
    {
        var source = new FakeConfigSource().WithLiveSocket($"{Home}/.colima/default/docker.sock");

        Assert.Equal(GuideState.Listening, new EngineGuideProbe(source).Check(Guide("colima"), []).State);
    }

    [Fact]
    public void AHostOnTheEnginesSocketIsThatEngine()
    {
        var host = Host("colima", $"unix://{Home}/.colima/default/docker.sock", HostConnectionState.Connected);

        var status = new EngineGuideProbe(new FakeConfigSource()).Check(Guide("colima"), [host]);

        Assert.Equal(GuideState.Connected, status.State);
        Assert.Same(host, status.Host);
    }

    [Fact]
    public void AHostReachedThroughASymlinkIsStillThatEngine()
    {
        // Podman symlinks /var/run/docker.sock to its machine socket; a host on the link is podman.
        var target = $"{Home}/.local/share/containers/podman/machine/podman.sock";
        var source = new FakeConfigSource().WithSocketSymlink("/var/run/docker.sock", target);
        var host = Host("discovered:/var/run/docker.sock", "unix:///var/run/docker.sock", HostConnectionState.Unreachable);

        var status = new EngineGuideProbe(source).Check(Guide("podman"), [host]);

        Assert.Equal(GuideState.NotAnswering, status.State);
    }

    [Fact]
    public void AnUnrelatedHostDoesNotMatch()
    {
        var host = Host("orbstack", $"unix://{Home}/.orbstack/run/docker.sock", HostConnectionState.Connected);

        Assert.Equal(GuideState.Absent, new EngineGuideProbe(new FakeConfigSource()).Check(Guide("colima"), [host]).State);
    }

    [Fact]
    public void AppleIsMatchedByOrigin()
    {
        var host = Host("apple:container", "/usr/local/bin/container", origin: HostOrigin.AppleContainer);

        Assert.Equal(GuideState.Listed, new EngineGuideProbe(new FakeConfigSource()).Check(Guide("apple-container"), [host]).State);
    }

    [Fact]
    public void PipesMatchWhateverWayTheyAreSpelled()
    {
        var host = Host("discovered", @"\\.\pipe\podman-machine-default", HostConnectionState.Connected);

        var status = new EngineGuideProbe(new FakeConfigSource()).Check(Guide("podman", GuidePlatform.Windows), [host]);

        Assert.Equal(GuideState.Connected, status.State);
    }

    [Fact]
    public void LinuxPodmanSocketIsAddableOnlyWhenTheRuntimeDirIsKnown()
    {
        var guide = Guide("podman", GuidePlatform.Linux);

        Assert.Null(new EngineGuideProbe(new FakeConfigSource()).AddableSocketPath(guide));

        var source = new FakeConfigSource()
            .WithEnvironment("XDG_RUNTIME_DIR", "/run/user/1000")
            .WithLiveSocket("/run/user/1000/podman/podman.sock");

        Assert.Equal("/run/user/1000/podman/podman.sock", new EngineGuideProbe(source).AddableSocketPath(guide));
    }

    [Fact]
    public void AppleIsOnlyOfferedWhereItCouldRun()
    {
        Assert.DoesNotContain(EngineGuides.For(GuidePlatform.MacOS, AppleEngineAvailability.Unsupported), g => g.Id == "apple-container");
        Assert.DoesNotContain(EngineGuides.For(GuidePlatform.Windows, AppleEngineAvailability.Available), g => g.Id == "apple-container");

        var old = EngineGuides.For(GuidePlatform.MacOS, AppleEngineAvailability.NeedsNewerMacOS).Single(g => g.Id == "apple-container");
        Assert.NotNull(old.Unavailable);
    }

    [Theory]
    [InlineData(GuidePlatform.MacOS)]
    [InlineData(GuidePlatform.Windows)]
    [InlineData(GuidePlatform.Linux)]
    public void EveryLocalGuideHasStepsAndEveryStepSaysWhy(GuidePlatform platform)
    {
        foreach (var guide in EngineGuides.For(platform, AppleEngineAvailability.Available).Where(EngineGuides.IsLocal))
        {
            Assert.NotEmpty(guide.Steps);
            Assert.All(guide.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s.Body)));
        }
    }
}

public class ContextSpecTests
{
    [Theory]
    [InlineData("nas", null)]
    [InlineData("home-server.1", null)]
    [InlineData("", "Give the host a name.")]
    [InlineData("-nas", "A host's name has to start with a letter or a digit.")]
    [InlineData("default", "\"default\" is reserved by Docker for the engine it finds without a context.")]
    public void NamesFollowDockersRule(string name, string? expected)
        => Assert.Equal(expected, ContextSpec.ValidateName(name));

    [Fact]
    public void ANameWithASpaceIsRefused() => Assert.NotNull(ContextSpec.ValidateName("my nas"));

    [Theory]
    [InlineData("me@nas.local", "nas")]
    [InlineData("10.0.0.4", "10-0-0-4")]
    [InlineData("Build_Box", "build-box")]
    [InlineData("", "remote")]
    public void SuggestsAShortName(string host, string expected)
        => Assert.Equal(expected, ContextSpec.SuggestName(host));

    [Theory]
    [InlineData("nas")]
    [InlineData("me@nas.local")]
    [InlineData("prod-alias")]
    public void AcceptsWhatYouWouldTypeAfterSsh(string target) => Assert.Null(ContextSpec.ValidateSshTarget(target));

    [Theory]
    [InlineData("")]
    [InlineData("ssh://nas")]
    [InlineData("me@")]
    [InlineData("nas,host=tcp://evil")]
    [InlineData("-oProxyCommand=x")]
    public void RefusesWhatWouldNotWorkOrWouldSplitTheSpec(string target)
        => Assert.NotNull(ContextSpec.ValidateSshTarget(target));

    [Fact]
    public void PortsAreBounded()
    {
        Assert.Null(ContextSpec.ValidatePort("", out var none));
        Assert.Null(none);
        Assert.Null(ContextSpec.ValidatePort("2222", out var port));
        Assert.Equal(2222, port);
        Assert.NotNull(ContextSpec.ValidatePort("70000", out _));
        Assert.NotNull(ContextSpec.ValidatePort("ssh", out _));
    }

    [Fact]
    public void BuildsSshAndTcpEndpoints()
    {
        Assert.Equal("ssh://me@nas:2222", ContextSpec.SshEndpoint("me@nas", 2222));
        Assert.Equal("ssh://nas", ContextSpec.SshEndpoint(" nas ", null));
        Assert.Equal("tcp://10.0.0.4:2376", ContextSpec.TcpEndpoint("10.0.0.4", 2376));
        Assert.Equal("tcp://[::1]:2375", ContextSpec.TcpEndpoint("::1", 2375));
    }

    [Fact]
    public void TheSpecCarriesOnlyWhatWasGiven()
    {
        Assert.Equal("host=ssh://nas", ContextSpec.Docker("ssh://nas"));
        Assert.Equal(
            "host=tcp://h:2376,ca=/c/ca.pem,cert=/c/cert.pem,key=/c/key.pem",
            ContextSpec.Docker("tcp://h:2376", "/c/ca.pem", "/c/cert.pem", "/c/key.pem"));
        Assert.Equal("host=tcp://h:2376,skip-tls-verify=true", ContextSpec.Docker("tcp://h:2376", skipVerify: true));
    }

    [Fact]
    public void APathThatWouldSplitTheSpecIsRefused()
    {
        Assert.Null(ContextSpec.ValidatePath("/Users/me/certs/ca.pem"));
        Assert.NotNull(ContextSpec.ValidatePath("/Users/me/a,b/ca.pem"));
    }

    [Fact]
    public void ReadsMinikubesPlainOutput()
    {
        const string output = """
            DOCKER_TLS_VERIFY=1
            DOCKER_HOST=tcp://127.0.0.1:58123
            DOCKER_CERT_PATH=/Users/me/.minikube/certs
            MINIKUBE_ACTIVE_DOCKERD=minikube
            """;

        Assert.Equal(
            "host=tcp://127.0.0.1:58123,ca=/Users/me/.minikube/certs/ca.pem,cert=/Users/me/.minikube/certs/cert.pem,key=/Users/me/.minikube/certs/key.pem",
            MinikubeDockerEnv.Parse(output));
    }

    [Fact]
    public void ReadsMinikubesShellOutputToo()
    {
        const string output = """
            export DOCKER_HOST="tcp://192.168.49.2:2376"
            export DOCKER_CERT_PATH="/home/me/.minikube/certs"
            """;

        Assert.StartsWith("host=tcp://192.168.49.2:2376,ca=/home/me/.minikube/certs/ca.pem", MinikubeDockerEnv.Parse(output));
    }

    [Fact]
    public void AWindowsCertPathKeepsItsOwnSeparators()
    {
        const string output = """
            DOCKER_HOST=tcp://127.0.0.1:58123
            DOCKER_CERT_PATH=C:\Users\me\.minikube\certs
            """;

        Assert.Equal(
            @"host=tcp://127.0.0.1:58123,ca=C:\Users\me\.minikube\certs\ca.pem,cert=C:\Users\me\.minikube\certs\cert.pem,key=C:\Users\me\.minikube\certs\key.pem",
            MinikubeDockerEnv.Parse(output));
    }

    [Fact]
    public void NoEngineInTheOutputIsNoSpec()
        => Assert.Null(MinikubeDockerEnv.Parse("MINIKUBE_ACTIVE_DOCKERD=minikube"));

    [Fact]
    public void OnlyHostsDrayAddedCanBeRemoved()
    {
        DockerHost Context(string id, string? description) => new()
        {
            Id = id,
            Name = id,
            Endpoint = DockerEndpoint.Parse("ssh://nas")!,
            Description = description,
        };

        Assert.True(DockerCliContext.CanRemove(Context("nas", DockerCliContext.AddedBy + " — ssh://nas")));
        Assert.True(DockerCliContext.CanRemove(Context("dray-minikube", null)));
        Assert.False(DockerCliContext.CanRemove(Context("desktop-linux", "Docker Desktop")));
        Assert.False(DockerCliContext.CanRemove(Context("nas", null) with { Origin = HostOrigin.Discovered }));
    }
}
