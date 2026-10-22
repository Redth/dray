using Dray.Core.Engine;
using Dray.Core.Tests.Fakes;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Creating the first account in a distribution from Dray, and the rule that matters most about
/// it: the password goes to stdin and appears in no argument, no message and no log.
/// </summary>
public class WslAccountTests
{
    const string Secret = "correct horse battery staple";

    static ScriptedRunner Working() => new ScriptedRunner()
        .On(["useradd"], 0)
        .On(["--exec", "sh", "-c"], 0)
        .On(["sh", "-c", "exec chpasswd"], 0)
        .On(["--manage"], 0);

    [Fact]
    public async Task ThePasswordGoesToStdinAndNowhereElse()
    {
        var runner = Working();

        var problem = await new WslAccount(runner).CreateAsync("Ubuntu", "jon", Secret, TestContext.Current.CancellationToken);

        Assert.Null(problem);
        Assert.DoesNotContain(runner.Calls, c => c.Arguments.Any(a => a.Contains(Secret)));

        var chpasswd = runner.Calls.Single(c => c.Arguments.Contains("exec chpasswd"));
        Assert.Equal("jon:" + Secret + "\n", chpasswd.Input);
    }

    /// <summary>The name is a positional argument to the script, never spliced into its text.</summary>
    [Fact]
    public async Task TheNameIsNeverPartOfTheScript()
    {
        var runner = Working();

        await new WslAccount(runner).CreateAsync("Ubuntu", "jon", Secret, TestContext.Current.CancellationToken);

        var create = runner.Calls.First();
        Assert.Equal(["-d", "Ubuntu", "-u", "root", "--exec", "sh", "-c"], create.Arguments.Take(7));
        Assert.DoesNotContain("jon", create.Arguments[7]);
        Assert.Equal("jon", create.Arguments[^1]);
    }

    [Fact]
    public async Task MakesTheAccountTheDefault()
    {
        var runner = Working();

        await new WslAccount(runner).CreateAsync("Ubuntu", "jon", Secret, TestContext.Current.CancellationToken);

        Assert.Equal(["--manage", "Ubuntu", "--set-default-user", "jon"], runner.Calls.Last().Arguments);
    }

    /// <summary>A failure to set the password is reported without echoing anything chpasswd said.</summary>
    [Fact]
    public async Task AFailedPasswordIsNotEchoed()
    {
        var runner = Working().On(["sh", "-c", "exec chpasswd"], 1, stderr: "chpasswd: " + Secret);

        var problem = await new WslAccount(runner).CreateAsync("Ubuntu", "jon", Secret, TestContext.Current.CancellationToken);

        Assert.NotNull(problem);
        Assert.DoesNotContain(Secret, problem);
        Assert.DoesNotContain(runner.Calls, c => c.Arguments.Contains("--manage"));
    }

    [Theory]
    [InlineData("jon", true)]
    [InlineData("_svc", true)]
    [InlineData("jon-dick2", true)]
    [InlineData("Jon", false)]
    [InlineData("2jon", false)]
    [InlineData("jon dick", false)]
    [InlineData("", false)]
    public void AcceptsTheNamesLinuxAccepts(string name, bool valid) => Assert.Equal(valid, WslAccount.IsValidName(name));

    [Theory]
    [InlineData("jondi", "jondi")]
    [InlineData("Jon Dick", "jondick")]
    [InlineData("123abc", "abc")]
    public void SuggestsANameFromTheWindowsOne(string windows, string expected) => Assert.Equal(expected, WslAccount.Suggest(windows));

    [Fact]
    public async Task RefusesAnInvalidNameBeforeRunningAnything()
    {
        var runner = Working();

        Assert.NotNull(await new WslAccount(runner).CreateAsync("Ubuntu", "Not Valid", Secret, TestContext.Current.CancellationToken));
        Assert.Empty(runner.Calls);
    }
}

/// <summary>Reading what is inside a distribution from the probe script's key=value lines.</summary>
public class DistroProbeTests
{
    static readonly WslDistro Ubuntu = new("Ubuntu", 2, true, true);

    [Theory]
    [InlineData("engine=running\nuser=yes\n", DistroEngine.Running, true)]
    [InlineData("engine=stopped\nuser=no\n", DistroEngine.Stopped, false)]
    [InlineData("engine=none\r\nuser=no\r\n", DistroEngine.None, false)]
    [InlineData("", DistroEngine.Unknown, null)]
    public void ReadsTheProbe(string output, DistroEngine engine, bool? hasUser)
        => Assert.Equal(new DistroSetup(Ubuntu, engine, hasUser), WindowsSetupReader.ParseDistroProbe(Ubuntu, output));

    /// <summary>Whatever a profile prints is not an answer — only the key=value lines are read.</summary>
    [Fact]
    public void IgnoresAnythingElseTheDistributionPrints()
        => Assert.Equal(DistroEngine.Running, WindowsSetupReader.ParseDistroProbe(Ubuntu, "Welcome to Ubuntu!\nengine=running\n").Engine);
}
