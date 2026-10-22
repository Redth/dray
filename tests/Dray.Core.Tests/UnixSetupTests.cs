using Dray.Core.Engine;
using Dray.Core.Tests.Fakes;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Install help on a Mac: three routes side by side, the installed one first, and every command run
/// by its full path with Homebrew's directory on PATH — an app opened from the Finder has neither.
/// </summary>
public class MacSetupTests
{
    const string Brew = "/opt/homebrew/bin/brew";

    static MacSetupState Mac(
        bool engine = false, string? brew = Brew, bool colima = false, bool docker = false,
        bool desktop = false, bool orbstack = false)
        => new(engine, brew, colima, docker, desktop, orbstack);

    [Fact]
    public void NothingToSuggestOnceAnEngineAnswers() => Assert.Empty(MacSetupPlan.Routes(Mac(engine: true)));

    /// <summary>With nothing installed, the free route is first and recommended; the apps follow.</summary>
    [Fact]
    public void ColimaIsRecommendedWhenNothingIsInstalled()
    {
        var routes = MacSetupPlan.Routes(Mac());

        Assert.Equal(["mac.colima", "mac.orbstack", "mac.desktop"], routes.Select(r => r.Id));
        Assert.True(routes[0].Recommended);
        Assert.Single(routes, r => r.Recommended);
    }

    [Fact]
    public void AnInstalledAppComesFirstBecauseItIsOneActionAway()
    {
        var routes = MacSetupPlan.Routes(Mac(orbstack: true));

        Assert.Equal("mac.orbstack", routes[0].Id);
        Assert.True(routes[0].Recommended);
        Assert.Equal(["-a", "OrbStack"], Assert.Single(routes[0].Steps).Arguments);
    }

    /// <summary>Homebrew's installer prompts for a password, so it goes to Terminal rather than through Dray.</summary>
    [Fact]
    public void WithoutHomebrewTheFirstStepIsHomebrewInTerminal()
    {
        var step = Assert.Single(MacSetupPlan.Routes(Mac(brew: null))[0].Steps);

        Assert.Equal("brew.install", step.Id);
        Assert.Equal(SetupStepKind.Terminal, step.Kind);
        Assert.Equal(MacSetupPlan.InstallHomebrew, step.Script);
    }

    /// <summary>Without Homebrew the apps are still reachable, from their makers' sites.</summary>
    [Fact]
    public void WithoutHomebrewTheAppsAreDownloads()
    {
        var routes = MacSetupPlan.Routes(Mac(brew: null));

        Assert.All(routes.Where(r => r.Id != "mac.colima"), r =>
        {
            var step = Assert.Single(r.Steps);
            Assert.Equal(SetupStepKind.OpenUrl, step.Kind);
            Assert.StartsWith("https://", step.Url);
        });
    }

    /// <summary>
    /// Run by full path, with Homebrew's directory on PATH: colima starts limactl by name, and a Finder
    /// app's PATH does not include /opt/homebrew/bin.
    /// </summary>
    [Fact]
    public void ColimaRunsByFullPathWithHomebrewOnPath()
    {
        var install = Assert.Single(MacSetupPlan.Routes(Mac())[0].Steps);
        Assert.Equal(Brew, install.FileName);
        Assert.Equal(["install", "colima", "docker"], install.Arguments);
        Assert.StartsWith("/opt/homebrew/bin:", install.Environment!["PATH"]);

        var start = Assert.Single(MacSetupPlan.Routes(Mac(colima: true, docker: true))[0].Steps);
        Assert.Equal("/opt/homebrew/bin/colima", start.FileName);
        Assert.Equal(SetupStepKind.InApp, start.Kind);
    }

    /// <summary>Docker Desktop's cask asks for a password for its helper, so it runs in Terminal too.</summary>
    [Fact]
    public void DockerDesktopsCaskRunsInTerminal()
    {
        var step = Assert.Single(MacSetupPlan.Routes(Mac()).Single(r => r.Id == "mac.desktop").Steps);

        Assert.Equal(SetupStepKind.Terminal, step.Kind);
        Assert.Contains("--cask docker-desktop", step.Script);
    }

    [Fact]
    public void ReadsWhatIsInstalledFromTheFilesystemAlone()
    {
        var source = new FakeConfigSource()
            .WithExecutable(Brew)
            .WithExecutable("/opt/homebrew/bin/colima")
            .WithDirectory("/Applications/OrbStack.app");

        var state = new MacSetupReader(source).Read([]);

        Assert.Equal(Mac(colima: true, orbstack: true), state);
    }
}

/// <summary>
/// Install help on Linux: privileges from polkit where it exists, and the command shown for sudo
/// where it does not — Dray never asks for a password itself.
/// </summary>
public class LinuxSetupTests
{
    static LinuxSetupState Linux(
        bool engine = false, bool installed = false, bool socket = false, bool group = false, bool pkexec = true)
        => new(engine, installed, socket, group, pkexec, "jon");

    [Theory]
    [InlineData(false, false, false, "engine.install")]
    [InlineData(true, false, false, "engine.start")]
    [InlineData(true, true, false, "engine.group")]
    public void EachStepFollowsTheLast(bool installed, bool socket, bool group, string expected)
        => Assert.Equal(expected, Assert.Single(LinuxSetupPlan.Routes(Linux(installed: installed, socket: socket, group: group))[0].Steps).Id);

    [Fact]
    public void NothingToSuggestOnceAnEngineAnswers() => Assert.Empty(LinuxSetupPlan.Routes(Linux(engine: true)));

    /// <summary>Through polkit, in Dray, with the user name as an argument rather than script text.</summary>
    [Fact]
    public void InstallsThroughPolkit()
    {
        var step = Assert.Single(LinuxSetupPlan.Routes(Linux())[0].Steps);

        Assert.Equal("/usr/bin/pkexec", step.FileName);
        Assert.Equal(SetupStepKind.InApp, step.Kind);
        Assert.Equal(["sh", "-c", LinuxSetupPlan.InstallScript, "sh", "jon"], step.Arguments);
        Assert.DoesNotContain("jon", LinuxSetupPlan.InstallScript);
    }

    /// <summary>A Linux prompt is for the user's password, and a group takes effect at sign-in — not "administrator", not a restart.</summary>
    [Fact]
    public void SpeaksLinuxsLanguageAboutPrivileges()
    {
        var step = Assert.Single(LinuxSetupPlan.Routes(Linux())[0].Steps);

        Assert.Contains("password", step.ElevationNote);
        Assert.Equal("Takes effect when you next sign in.", step.RestartNote);
    }

    /// <summary>Without polkit, Dray says so and shows the sudo command rather than guessing at a way to elevate.</summary>
    [Fact]
    public void WithoutPolkitTheCommandIsShownForSudo()
    {
        var step = Assert.Single(LinuxSetupPlan.Routes(Linux(pkexec: false))[0].Steps);

        Assert.Equal(SetupStepKind.Manual, step.Kind);
        Assert.StartsWith("sudo ", step.Script);
        Assert.Contains("usermod -aG docker jon", step.Script);
    }

    /// <summary>A group added since signing in is not in effect, so `id -nG` — this session's groups — is what is asked.</summary>
    [Fact]
    public async Task ReadsGroupMembershipFromThisSession()
    {
        var runner = new ScriptedRunner().On(["-nG"], 0, "jon adm sudo docker\n");
        var source = new FakeConfigSource().WithExecutable("/usr/bin/dockerd").WithExecutable("/usr/bin/pkexec").WithLiveSocket("/var/run/docker.sock");

        var state = await new LinuxSetupReader(runner, source).ReadAsync([], TestContext.Current.CancellationToken);

        Assert.True(state.EngineInstalled);
        Assert.True(state.SocketExists);
        Assert.True(state.InDockerGroup);
        Assert.True(state.Pkexec);
    }
}
