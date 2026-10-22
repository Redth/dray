using Dray.Core.Engine;
using Xunit;

namespace Dray.Core.Tests;

public class MacSearchPathTests
{
    const string Launchd = "/usr/bin:/bin:/usr/sbin:/sbin";
    const string Home = "/Users/jon";

    static readonly string[] SystemPaths = ["/usr/local/bin", "/System/Cryptexes/App/usr/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"];

    [Fact]
    public void AnAppOpenedFromFinderGetsTheEnginesCommands()
    {
        var present = new HashSet<string> { "/usr/local/bin", "/opt/homebrew/bin", "/Users/jon/.docker/bin", "/System/Cryptexes/App/usr/bin" };

        var path = MacSearchPath.Augment(Launchd, SystemPaths, Home, present.Contains);

        Assert.Equal(
            "/usr/bin:/bin:/usr/sbin:/sbin:/usr/local/bin:/System/Cryptexes/App/usr/bin:/opt/homebrew/bin:/Users/jon/.docker/bin",
            path);
    }

    [Fact]
    public void ATerminalsPathKeepsItsOrder()
    {
        // Launched from a shell whose PATH puts Homebrew first: that choice stands, and nothing
        // already there is added again.
        const string shell = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin";

        var path = MacSearchPath.Augment(shell, SystemPaths, Home, _ => true);

        Assert.StartsWith(shell + ":", path);
        Assert.Single(path.Split(':'), d => d == "/opt/homebrew/bin");
        Assert.Single(path.Split(':'), d => d == "/usr/local/bin");
    }

    [Fact]
    public void ADirectoryThatIsNotThereIsLeftOut()
    {
        var path = MacSearchPath.Augment(Launchd, [], Home, _ => false);

        Assert.Equal(Launchd, path);
    }

    [Fact]
    public void ATildeMeansHome()
    {
        // The .NET SDK's paths.d entry is written this way.
        var path = MacSearchPath.Augment(Launchd, ["~/.dotnet/tools"], Home, d => d == "/Users/jon/.dotnet/tools");

        Assert.EndsWith(":/Users/jon/.dotnet/tools", path);
    }

    [Fact]
    public void BlankLinesCommentsAndTrailingSlashesAreNotNewDirectories()
    {
        var path = MacSearchPath.Augment("/usr/bin/", ["", "  ", "# comment", "/usr/bin"], Home, _ => true);

        Assert.DoesNotContain("#", path);
        Assert.Single(path.Split(':'), d => d.TrimEnd('/') == "/usr/bin");
    }

    [Fact]
    public void NoPathAtAllStillGetsOne()
    {
        var path = MacSearchPath.Augment(null, ["/usr/bin"], Home, d => d == "/usr/bin");

        Assert.Equal("/usr/bin", path);
    }
}
