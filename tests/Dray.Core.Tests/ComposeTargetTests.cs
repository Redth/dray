using Dray.Core.Engine;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Compose acts on the engine selected in Dray — not on whichever one the CLI's context names — and
/// for an engine in a WSL distribution it runs inside that distribution, where the stack's paths are.
/// </summary>
public class ComposeTargetTests
{
    static readonly ComposeCommand Plugin = new("docker", ["compose"], "2.40.0");

    static DockerHost Host(string endpoint, HostOrigin origin = HostOrigin.Discovered, EndpointTls? tls = null)
        => new() { Id = "h", Name = "h", Endpoint = DockerEndpoint.Parse(endpoint, tls)!, Origin = origin };

    static ComposeCli.ComposeRun Run(DockerHost host, string? workingDirectory = "/home/jon/app")
        => ComposeCli.Invocation(Plugin, ComposeTarget.For(host, Plugin)!, "app", ["/home/jon/app/compose.yaml"], ["up", "-d"], workingDirectory);

    /// <summary>
    /// The engine on compose's command line, as <c>-H</c> — not <c>DOCKER_HOST</c>, which would be
    /// visible to compose's own interpolation (docs/ARCHITECTURE.md section 2.5).
    /// </summary>
    [Theory]
    [InlineData("//./pipe/dockerDesktopLinuxEngine", "npipe://./pipe/dockerDesktopLinuxEngine")]
    [InlineData("unix:///Users/jon/.colima/default/docker.sock", "unix:///Users/jon/.colima/default/docker.sock")]
    [InlineData("ssh://jon@nas", "ssh://jon@nas")]
    public void PointsComposeAtTheSelectedEngine(string endpoint, string cli)
    {
        var host = Host(endpoint);
        var run = Run(host);

        Assert.Equal("docker", run.Executable);
        Assert.Equal(["-H", cli, "compose", "-p", "app", "-f", "/home/jon/app/compose.yaml", "up", "-d"], run.Arguments);
        Assert.Empty(ComposeTarget.For(host, Plugin)!.Environment);
    }

    /// <summary>
    /// Inside the distribution, in the directory compose recorded, without a login shell — and as the
    /// distribution's default user, so files it creates belong to them rather than root.
    /// </summary>
    [Fact]
    public void RunsComposeInsideTheDistributionForAWslEngine()
    {
        var host = Host("wsl://Ubuntu", HostOrigin.WslDistro);
        var run = Run(host);

        Assert.Equal("wsl.exe", run.Executable);
        Assert.Equal(
            ["-d", "Ubuntu", "--cd", "/home/jon/app", "--exec", "docker", "compose", "-p", "app", "-f", "/home/jon/app/compose.yaml", "up", "-d"],
            run.Arguments);
        Assert.DoesNotContain("-u", run.Arguments);
        Assert.Null(run.WorkingDirectory);

        // wsl.exe writes UTF-16 unless told not to.
        Assert.Equal("1", ComposeTarget.For(host, Plugin)!.Environment["WSL_UTF8"]);
    }

    [Fact]
    public void AWslStackWithNoRecordedDirectoryRunsFromHome()
        => Assert.Contains("~", Run(Host("wsl://Ubuntu", HostOrigin.WslDistro), workingDirectory: null).Arguments);

    /// <summary>TLS the way the CLI reads it: verify unless the context said not to, and every file it has.</summary>
    [Fact]
    public void CarriesTlsMaterialForATcpEngine()
    {
        var host = Host("tcp://10.0.0.4:2376", HostOrigin.Environment, new EndpointTls("/certs/ca.pem", "/certs/cert.pem", "/certs/key.pem", false));

        var run = ComposeCli.Invocation(Plugin, ComposeTarget.For(host, Plugin)!, "app", [], ["ps"], null);

        Assert.Equal(
            ["-H", "tcp://10.0.0.4:2376", "--tlsverify", "--tlscacert", "/certs/ca.pem", "--tlscert", "/certs/cert.pem", "--tlskey", "/certs/key.pem", "compose", "-p", "app", "ps"],
            run.Arguments);
    }
}

/// <summary>A path compose recorded, as this machine can open it.</summary>
public class EnginePathTests
{
    static readonly DockerEndpoint Wsl = DockerEndpoint.Parse("wsl://Ubuntu")!;

    [Theory]
    [InlineData("/home/jon/app/compose.yaml", @"\\wsl.localhost\Ubuntu\home\jon\app\compose.yaml")]
    [InlineData("/mnt/c/Users/jon/app/compose.yaml", @"C:\Users\jon\app\compose.yaml")]
    [InlineData("/mnt/d", @"D:\")]
    public void TranslatesAWslPath(string recorded, string local) => Assert.Equal(local, EnginePaths.ToLocal(Wsl, recorded));

    [Fact]
    public void LeavesEveryOtherEnginesPathsAlone()
        => Assert.Equal("/home/jon/app", EnginePaths.ToLocal(DockerEndpoint.Parse("unix:///var/run/docker.sock"), "/home/jon/app"));

    /// <summary>The directory is found in Linux terms first — Path.GetDirectoryName on Windows would turn the slashes round.</summary>
    [Fact]
    public void FindsTheDirectoryBeforeTranslating()
        => Assert.Equal(@"\\wsl.localhost\Ubuntu\home\jon\app", EnginePaths.DirectoryOf(Wsl, "/home/jon/app/compose.yaml"));
}
