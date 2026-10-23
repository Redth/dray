using Dray.Core.Engine;
using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The guided-setup plan, and the rule that keeps it honest: every command it offers is safe to
/// run when the thing is already done, so an Unknown never has to become a guess.
/// </summary>
public class WindowsSetupTests
{
    static WindowsSetupState State(
        SetupFact wsl = SetupFact.Present,
        IReadOnlyList<WslDistro>? distros = null,
        SetupFact containers = SetupFact.Unknown,
        bool linuxEngine = false,
        bool windowsEngine = false)
        => new(wsl, null, distros ?? [], containers, SetupFact.Present, linuxEngine, windowsEngine);

    /// <summary>
    /// An engine that answered settles the question. A machine where both work must never be shown
    /// a setup checklist, whatever the feature probes inferred.
    /// </summary>
    [Fact]
    public void SuggestsNothingWhenBothEnginesAlreadyAnswer()
    {
        var state = State(wsl: SetupFact.Absent, containers: SetupFact.Absent, linuxEngine: true, windowsEngine: true);

        Assert.True(state.IsComplete);
        Assert.Empty(WindowsSetupPlan.ForLinuxContainers(state));
        Assert.Empty(WindowsSetupPlan.ForWindowsContainers(state));
        Assert.Null(WindowsSetupPlan.EngineAdvice(state));
    }

    /// <summary>
    /// The two tracks are independent, because they are two engines that share nothing. Someone who
    /// only wants Linux containers must not be told to enable a Windows feature.
    /// </summary>
    [Fact]
    public void TheTwoTracksAreIndependent()
    {
        var linuxDone = State(linuxEngine: true, windowsEngine: false);

        Assert.Empty(WindowsSetupPlan.ForLinuxContainers(linuxDone));
        Assert.NotEmpty(WindowsSetupPlan.ForWindowsContainers(linuxDone));

        var windowsDone = State(wsl: SetupFact.Absent, linuxEngine: false, windowsEngine: true);

        Assert.NotEmpty(WindowsSetupPlan.ForLinuxContainers(windowsDone));
        Assert.Empty(WindowsSetupPlan.ForWindowsContainers(windowsDone));
    }

    [Fact]
    public void InstallsWslBeforeADistribution()
    {
        var steps = WindowsSetupPlan.ForLinuxContainers(State(wsl: SetupFact.Absent));

        // Only WSL: suggesting a distribution before WSL exists to hold it would fail on click.
        Assert.Equal("wsl.install", Assert.Single(steps).Id);
    }

    /// <summary>
    /// --no-distribution matters. Without it, `wsl --install` picks Ubuntu for the user, and which
    /// distribution to run is the next step and their decision.
    /// </summary>
    [Fact]
    public void InstallingWslDoesNotSilentlyChooseADistribution()
    {
        var step = Assert.Single(WindowsSetupPlan.ForLinuxContainers(State(wsl: SetupFact.Absent)));

        Assert.Contains("--no-distribution", step.Arguments);
    }

    [Fact]
    public void SuggestsADistributionOnceWslIsThereAndEmpty()
    {
        var step = Assert.Single(WindowsSetupPlan.ForLinuxContainers(State(wsl: SetupFact.Present, distros: [])));

        Assert.Equal("wsl.distro", step.Id);
    }

    /// <summary>
    /// A distribution that belongs to a desktop product is not one the user can put an engine in,
    /// so a machine holding only those still needs a distribution of its own.
    /// </summary>
    [Fact]
    public void ADesktopProductsDistributionDoesNotCountAsOne()
    {
        var state = State(distros: [new WslDistro("docker-desktop", 2, true, false)]);

        Assert.Equal("wsl.distro", Assert.Single(WindowsSetupPlan.ForLinuxContainers(state)).Id);
    }

    /// <summary>
    /// A distribution is not an engine. The old plan stopped here and showed an empty track; the
    /// step after a distribution is putting an engine in it.
    /// </summary>
    [Fact]
    public void OnceThereIsADistributionTheNextStepIsAnEngine()
    {
        var steps = WindowsSetupPlan.ForLinuxContainers(State(distros: [new WslDistro("Ubuntu", 2, true, true)]));

        Assert.DoesNotContain(steps, s => s.Id == "wsl.distro");
        Assert.Equal("engine.install", Assert.Single(steps).Id);
    }

    /// <summary>
    /// A distribution is registered to the user who installs it. Elevating would install it for
    /// whichever administrator answered the prompt — on a machine with a separate admin account,
    /// the wrong person.
    /// </summary>
    [Fact]
    public void InstallsADistributionWithoutElevatingAndWithoutAConsole()
    {
        var step = Assert.Single(WindowsSetupPlan.ForLinuxContainers(State(distros: [])));

        Assert.False(step.NeedsElevation);
        Assert.Equal(SetupStepKind.InApp, step.Kind);
        Assert.Contains("--no-launch", step.Arguments);
        Assert.NotNull(step.Duration);
    }

    [Fact]
    public void OffersToCreateAnAccountInADistributionThatHasNone()
    {
        var ubuntu = new WslDistro("Ubuntu", 2, false, true);
        var state = State(distros: [ubuntu]) with { Target = new DistroSetup(ubuntu, DistroEngine.None, HasUser: false) };

        var steps = WindowsSetupPlan.ForLinuxContainers(state);

        var account = steps.Single(s => s.Id == "linux.user");
        Assert.Equal(SetupStepKind.CreateLinuxUser, account.Kind);
        Assert.True(account.Optional);
        Assert.Contains(steps, s => s.Id == "engine.install");
    }

    [Theory]
    [InlineData(DistroEngine.None, "engine.install")]
    [InlineData(DistroEngine.Unknown, "engine.install")]
    [InlineData(DistroEngine.Stopped, "engine.start")]
    [InlineData(DistroEngine.Running, "engine.connect")]
    public void TheEngineStepFollowsWhatIsInTheDistribution(DistroEngine engine, string expected)
    {
        var ubuntu = new WslDistro("Ubuntu", 2, true, true);
        var state = State(distros: [ubuntu]) with { Target = new DistroSetup(ubuntu, engine, HasUser: true) };

        Assert.Equal(expected, Assert.Single(WindowsSetupPlan.ForLinuxContainers(state)).Id);
    }

    /// <summary>The engine install runs as root inside the distribution, so the script is shown whole.</summary>
    [Fact]
    public void TheEngineInstallShowsTheScriptItRunsAsRoot()
    {
        var step = Assert.Single(WindowsSetupPlan.ForLinuxContainers(State(distros: [new WslDistro("Ubuntu", 2, true, true)])));

        Assert.Equal(WindowsSetupPlan.InstallEngineScript, step.Script);
        Assert.Equal(["-d", "Ubuntu", "-u", "root", "--exec", "sh", "-c", WindowsSetupPlan.InstallEngineScript], step.Arguments);
        Assert.Contains("get.docker.com", step.Script);
    }

    /// <summary>The route works in the default distribution, else a running one — not whichever sorts first.</summary>
    [Fact]
    public void TargetsTheDefaultDistribution()
    {
        var state = State(distros:
        [
            new WslDistro("Alpine", 2, true, false),
            new WslDistro("Ubuntu", 2, false, true),
        ]);

        Assert.Equal("Ubuntu", state.TargetDistro!.Name);
    }

    /// <summary>
    /// Docker Desktop installed is one action from Linux containers, so its route comes first — but
    /// the engine in WSL is still offered beside it, because that is a real choice.
    /// </summary>
    [Fact]
    public void DockerDesktopInTheOtherModeIsOneSwitchAway()
    {
        var state = State() with { Desktop = new DockerDesktopState(true, true, DesktopEngineMode.Windows) };

        var routes = WindowsSetupPlan.LinuxRoutes(state);

        Assert.Equal(["linux.desktop", "linux.wsl"], routes.Select(r => r.Id));
        Assert.True(routes[0].Recommended);

        var step = Assert.Single(routes[0].Steps);
        Assert.Equal("desktop.linux", step.Id);
        Assert.Equal(DesktopEngineMode.Linux, step.DesktopMode);
    }

    [Fact]
    public void AStoppedDockerDesktopIsStartedRatherThanSwitched()
    {
        var state = State() with { Desktop = new DockerDesktopState(true, false, null) };

        Assert.Equal("desktop.start", Assert.Single(WindowsSetupPlan.LinuxRoutes(state)[0].Steps).Id);
    }

    /// <summary>Without Docker Desktop, the free route is the recommended one and Docker Desktop is the alternative.</summary>
    [Fact]
    public void WithoutDockerDesktopTheEngineInWslComesFirst()
    {
        var routes = WindowsSetupPlan.LinuxRoutes(State());

        Assert.Equal(["linux.wsl", "linux.desktop"], routes.Select(r => r.Id));
        Assert.True(routes[0].Recommended);
        Assert.Equal("desktop.install", Assert.Single(routes[1].Steps).Id);
    }

    /// <summary>
    /// The Containers feature probe cannot be sure — Hyper-V installs the same binary — so it
    /// answers Unknown, and the step must still be offered. A plan that only acted on certainty
    /// would show nothing on the machines that need it most.
    /// </summary>
    [Fact]
    public void OffersTheFeatureStepEvenWhenItCannotBeSure()
    {
        Assert.Contains(WindowsSetupPlan.ForWindowsContainers(State(containers: SetupFact.Unknown)), s => s.Id == "feature.containers");
        Assert.Contains(WindowsSetupPlan.ForWindowsContainers(State(containers: SetupFact.Absent)), s => s.Id == "feature.containers");
        Assert.DoesNotContain(WindowsSetupPlan.ForWindowsContainers(State(containers: SetupFact.Present)), s => s.Id == "feature.containers");
    }

    /// <summary>
    /// Every step says whether it raises a UAC prompt and whether it needs a reboot, before it is
    /// run. A step that silently needs a restart reads as a step that failed.
    /// </summary>
    [Fact]
    public void EveryStepDeclaresItsCostUpFront()
    {
        var all = WindowsSetupPlan.ForLinuxContainers(State(wsl: SetupFact.Absent))
            .Concat(WindowsSetupPlan.ForWindowsContainers(State()))
            .ToList();

        Assert.NotEmpty(all);

        foreach (var step in all)
        {
            Assert.True(step.NeedsElevation);
            Assert.NotEmpty(step.Why);
            Assert.NotEmpty(step.CommandLine);
        }
    }

    /// <summary>The command is shown before it is run, so it has to render as something typeable.</summary>
    [Fact]
    public void RendersTheCommandTheUserWouldType()
    {
        var step = WindowsSetupPlan.ForWindowsContainers(State()).Single(s => s.Id == "feature.containers");

        Assert.Equal(
            "dism.exe /online /enable-feature /featurename:Containers /all /norestart",
            step.CommandLine);
    }

    /// <summary>
    /// Dray installs the engine as well as the feature now (PRODUCT.md): a feature with no engine to
    /// drive it is not a working setup.
    /// </summary>
    [Fact]
    public void OffersAnEngineForWindowsContainersAfterTheFeature()
    {
        var steps = WindowsSetupPlan.ForWindowsContainers(State());

        Assert.Equal(["feature.containers", "desktop.install"], steps.Select(s => s.Id));
    }

    [Fact]
    public void DockerDesktopInLinuxModeIsSwitchedForWindowsContainers()
    {
        var state = State(containers: SetupFact.Present) with { Desktop = new DockerDesktopState(true, true, DesktopEngineMode.Linux) };

        Assert.Equal("desktop.windows", Assert.Single(WindowsSetupPlan.ForWindowsContainers(state)).Id);
    }

    /// <summary>Windows Server has Microsoft's own route, and does not need Docker Desktop for it.</summary>
    [Fact]
    public void WindowsServerGetsMicrosoftsEngineScript()
    {
        var steps = WindowsSetupPlan.ForWindowsContainers(State() with { IsServer = true });

        var engine = steps.Single(s => s.Id == "engine.windows");
        Assert.Contains(engine.Arguments, a => a.Contains("install-docker-ce.ps1"));
        Assert.DoesNotContain(steps, s => s.Id == "desktop.install");
    }

    /// <summary>
    /// Home has no Hyper-V and no Containers feature, so there is nothing to install — and a machine
    /// whose Linux containers work is set up, not missing half.
    /// </summary>
    [Fact]
    public void WindowsHomeIsExplainedRatherThanOfferedSteps()
    {
        var home = State(linuxEngine: true) with { IsHomeEdition = true };

        Assert.Empty(WindowsSetupPlan.WindowsRoutes(home));
        Assert.Contains("Home", WindowsSetupPlan.EngineAdvice(home));
        Assert.True(home.IsComplete);
    }
}

/// <summary>
/// Pointing the docker CLI at a host. A context rather than DOCKER_HOST, because compose, buildx
/// and everything else read a context and only the exporting shell reads a variable.
/// </summary>
public class DockerCliContextTests
{
    /// <summary>
    /// A relayed endpoint has no address the CLI can dial — that is the whole reason Dray relays
    /// into it. Offering a button that cannot work is the failure ContainerAction.cs names.
    /// </summary>
    [Theory]
    [InlineData("//./pipe/docker_engine", true)]
    [InlineData("unix:///var/run/docker.sock", true)]
    [InlineData("tcp://10.0.0.4:2375", true)]

    // The CLI has its own ssh:// support, so a context works even though Dray reaches it its own way.
    [InlineData("ssh://nas", true)]

    // No Windows-side address exists for this at all.
    [InlineData("wsl://Ubuntu", false)]
    public void KnowsWhichHostsTheCliCanBePointedAt(string raw, bool expected)
        => Assert.Equal(expected, DockerCliContext.CanTarget(DockerEndpoint.Parse(raw)!));

    /// <summary>
    /// One pipe is spelled at least three ways. Treating them as different would make Dray create a
    /// duplicate context every single time the button was pressed.
    /// </summary>
    [Theory]
    [InlineData("npipe:////./pipe/dockerDesktopLinuxEngine")]
    [InlineData("npipe://./pipe/dockerDesktopLinuxEngine")]
    [InlineData("//./pipe/dockerDesktopLinuxEngine")]
    [InlineData(@"\\.\pipe\dockerDesktopLinuxEngine")]
    public void RecognisesEverySpellingOfOnePipe(string contextEndpoint)
        => Assert.True(DockerCliContext.SameEndpoint(
            contextEndpoint,
            DockerEndpoint.Parse("//./pipe/dockerDesktopLinuxEngine")!));

    [Fact]
    public void DoesNotConfuseTwoDifferentPipes()
        => Assert.False(DockerCliContext.SameEndpoint(
            "npipe:////./pipe/dockerDesktopWindowsEngine",
            DockerEndpoint.Parse("//./pipe/dockerDesktopLinuxEngine")!));

    /// <summary>Docker restricts what a context may be called, and host names are prose.</summary>
    [Theory]
    [InlineData("Docker Desktop (Linux containers)", "dray-docker-desktop-linux-containers")]
    [InlineData("Ubuntu-24.04", "dray-ubuntu-24-04")]
    [InlineData("!!!", "dray-host")]
    public void MakesALegalContextNameFromAHostName(string hostName, string expected)
    {
        var host = new DockerHost
        {
            Id = "x",
            Name = hostName,
            Endpoint = DockerEndpoint.Parse("//./pipe/docker_engine")!,
        };

        Assert.Equal(expected, DockerCliContext.NameFor(host));
    }

    /// <summary>
    /// Dray's contexts are prefixed so it is obvious in `docker context ls` which are its own, and
    /// so it never adopts or overwrites one the user made.
    /// </summary>
    [Fact]
    public void NamesItsOwnContextsRecognisably()
    {
        var host = new DockerHost
        {
            Id = "x",
            Name = "Docker Engine",
            Endpoint = DockerEndpoint.Parse("//./pipe/docker_engine")!,
        };

        Assert.StartsWith(DockerCliContext.ContextPrefix, DockerCliContext.NameFor(host));
    }

    /// <summary>The CLI writes a pipe with a scheme; discovery stores the bare form it also finds.</summary>
    [Fact]
    public void WritesAPipeTheWayTheCliExpects()
        => Assert.Equal(
            "npipe://./pipe/docker_engine",
            DockerCliContext.CliEndpoint(DockerEndpoint.Parse("//./pipe/docker_engine")!));
}
