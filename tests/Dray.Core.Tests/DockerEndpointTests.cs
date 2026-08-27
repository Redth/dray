using Dray.Core.Engine;
using Xunit;

namespace Dray.Core.Tests;

public class DockerEndpointTests
{
    [Theory]
    [InlineData("unix:///var/run/docker.sock", "/var/run/docker.sock")]
    [InlineData("unix:///Users/x/.docker/run/docker.sock", "/Users/x/.docker/run/docker.sock")]
    [InlineData("/var/run/docker.sock", "/var/run/docker.sock")]
    public void ParsesUnixSockets(string raw, string expectedPath)
    {
        var endpoint = DockerEndpoint.Parse(raw);
        Assert.NotNull(endpoint);
        Assert.Equal(EndpointScheme.Unix, endpoint.Scheme);
        Assert.Equal(expectedPath, endpoint.Path);
        Assert.False(endpoint.IsRemote);
    }

    [Theory]
    [InlineData("//./pipe/dockerDesktopLinuxEngine")]
    [InlineData(@"\\.\pipe\docker_engine")]
    [InlineData("npipe:////./pipe/docker_engine")]
    public void ParsesNamedPipes(string raw)
    {
        var endpoint = DockerEndpoint.Parse(raw);
        Assert.NotNull(endpoint);
        Assert.Equal(EndpointScheme.NamedPipe, endpoint.Scheme);
        Assert.False(endpoint.IsRemote);
    }

    [Fact]
    public void TcpDefaultsToThePlaintextPort()
    {
        var endpoint = DockerEndpoint.Parse("tcp://10.0.0.4");
        Assert.NotNull(endpoint);
        Assert.Equal(2375, endpoint.Port);
        Assert.True(endpoint.IsRemote);
    }

    [Fact]
    public void TcpWithTlsDefaultsToTheTlsPort()
    {
        var endpoint = DockerEndpoint.Parse("tcp://10.0.0.4", new EndpointTls("ca", "cert", "key", false));
        Assert.NotNull(endpoint);
        Assert.Equal(2376, endpoint.Port);
        Assert.True(endpoint.Tls!.HasClientCertificate);
    }

    [Fact]
    public void ExplicitPortWins()
        => Assert.Equal(9999, DockerEndpoint.Parse("tcp://10.0.0.4:9999")!.Port);

    [Fact]
    public void ParsesSshWithUserAndPort()
    {
        var endpoint = DockerEndpoint.Parse("ssh://redth@nas.local:2222");
        Assert.NotNull(endpoint);
        Assert.Equal(EndpointScheme.Ssh, endpoint.Scheme);
        Assert.Equal("redth", endpoint.User);
        Assert.Equal("nas.local", endpoint.Host);
        Assert.Equal(2222, endpoint.Port);
        Assert.True(endpoint.IsRemote);
    }

    [Fact]
    public void SshWithoutUserOrPortLeavesThemToTheSshConfig()
    {
        // Dray must not invent a user or port: ~/.ssh/config may specify both, and
        // reimplementing that resolution is explicitly out of scope.
        var endpoint = DockerEndpoint.Parse("ssh://nas");
        Assert.NotNull(endpoint);
        Assert.Null(endpoint.User);
        Assert.Null(endpoint.Port);
    }

    [Theory]
    [InlineData("tcp://[::1]:2375", "::1", 2375)]
    [InlineData("tcp://[fe80::1]", "fe80::1", 2375)]
    public void ParsesBracketedIpv6(string raw, string host, int port)
    {
        var endpoint = DockerEndpoint.Parse(raw);
        Assert.NotNull(endpoint);
        Assert.Equal(host, endpoint.Host);
        Assert.Equal(port, endpoint.Port);
    }

    [Fact]
    public void UnbracketedIpv6IsNotMistakenForAPort()
    {
        // The last colon looks like a port separator but is part of the address.
        var endpoint = DockerEndpoint.Parse("tcp://fe80::1");
        Assert.NotNull(endpoint);
        Assert.Equal("fe80::1", endpoint.Host);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("ftp://example.com")]
    [InlineData("unix://")]
    public void UnparseableInputReturnsNullRatherThanThrowing(string? raw)
    {
        // A malformed context must degrade that one host, not take down discovery.
        Assert.Null(DockerEndpoint.Parse(raw));
    }

    [Fact]
    public void DisplayIdentifiesTheEndpointInTheHostPicker()
    {
        Assert.Equal("ssh://redth@nas:22", DockerEndpoint.Parse("ssh://redth@nas:22")!.Display);
        Assert.Equal("tcp://10.0.0.4:2375", DockerEndpoint.Parse("tcp://10.0.0.4")!.Display);
        Assert.Equal("/var/run/docker.sock", DockerEndpoint.Parse("unix:///var/run/docker.sock")!.Display);
    }

    [Fact]
    public void UnixSocketDisplayKeepsEnoughPathToTellEnginesApart()
    {
        // Docker Desktop, OrbStack and Colima all call their socket docker.sock, so the filename
        // alone identifies nothing. The path is what distinguishes them.
        var orb = DockerEndpoint.Parse("unix:///Users/x/.orbstack/run/docker.sock")!.Display;
        var desktop = DockerEndpoint.Parse("unix:///Users/x/.docker/run/docker.sock")!.Display;

        Assert.NotEqual(orb, desktop);
        Assert.Contains("orbstack", orb, StringComparison.Ordinal);
    }

    [Fact]
    public void HomeIsShortenedTheWayAShellShowsIt()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.SkipWhen(string.IsNullOrEmpty(home), "no home directory in this environment");

        var display = DockerEndpoint.Parse($"unix://{home}/.colima/default/docker.sock")!.Display;
        Assert.Equal("~/.colima/default/docker.sock", display);
    }
}
