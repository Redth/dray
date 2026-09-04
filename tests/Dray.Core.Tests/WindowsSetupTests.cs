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

    [Fact]
    public void SuggestsNoDistributionWhenThereIsAUsableOne()
    {
        var state = State(distros: [new WslDistro("Ubuntu", 2, true, true)]);

        Assert.Empty(WindowsSetupPlan.ForLinuxContainers(state));
    }

    /// <summary>
    /// The Containers feature probe cannot be sure — Hyper-V installs the same binary — so it
    /// answers Unknown, and the step must still be offered. A plan that only acted on certainty
    /// would show nothing on the machines that need it most.
    /// </summary>
    [Fact]
    public void OffersTheFeatureStepEvenWhenItCannotBeSure()
    {
        Assert.NotEmpty(WindowsSetupPlan.ForWindowsContainers(State(containers: SetupFact.Unknown)));
        Assert.NotEmpty(WindowsSetupPlan.ForWindowsContainers(State(containers: SetupFact.Absent)));
        Assert.Empty(WindowsSetupPlan.ForWindowsContainers(State(containers: SetupFact.Present)));
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
        var step = Assert.Single(WindowsSetupPlan.ForWindowsContainers(State()));

        Assert.Equal(
            "dism.exe /online /enable-feature /featurename:Containers /all /norestart",
            step.CommandLine);
    }

    /// <summary>Dray does not install an engine, and says so rather than leaving a gap in the list.</summary>
    [Fact]
    public void ExplainsThatItWillNotInstallAnEngine()
    {
        var advice = WindowsSetupPlan.EngineAdvice(State());

        Assert.NotNull(advice);
        Assert.Contains("does not install one", advice);
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
