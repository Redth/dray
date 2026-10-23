using Dray.Core.Engine;
using Xunit;

namespace Dray.Docker.Tests;

public class DockerRawApiTests
{
    [Theory]
    [InlineData("ssh://me@nas")]
    [InlineData("ssh://nas:2222")]
    [InlineData("wsl://Ubuntu")]
    public void RelayedEndpointsGetARawClient(string raw)
    {
        // This threw for every SSH host — "tunnelled to a local socket first", which nothing did —
        // so an SSH host failed to connect at the first raw call. Constructing is the regression;
        // reaching a host is covered by the live check against a real one.
        using var api = new DockerRawApi(DockerEndpoint.Parse(raw)!);
    }
}
