using Docker.DotNet.Models;
using Dray.Core.Model;
using Xunit;

// namespace Dray.Docker.Tests shadows the `Docker` root, so the wire types need naming explicitly.
using Wire = global::Docker.DotNet.Models;

namespace Dray.Docker.Tests;

/// <summary>
/// The inspect response is where two engines disagree most, so these fixtures come from real
/// responses rather than from the API documentation.
/// </summary>
public class DockerInspectTests
{
    static ContainerInspectResponse Response(
        State? state = null,
        ContainerConfig? config = null,
        HostConfig? host = null) => new()
        {
            ID = "0fb73fb252fcc492242a76bb9a3bc84607572e4cdc58b2613",
            Name = "/dray-web",
            Image = "sha256:c961b5309720",
            State = state ?? new State { Running = true, Status = "running" },
            Config = config ?? new ContainerConfig(),
            HostConfig = host,
        };

    // ---------------------------------------------------------------- state and timing

    [Fact]
    public void ARunningContainerHasNotFinished()
    {
        // Captured from podman 6.0.2: a healthy running container still carries a FinishedAt, and
        // it is *earlier* than StartedAt. Shown verbatim it reads as though the container crashed.
        var inspect = DockerInspect.Map(Response(new State
        {
            Running = true,
            Status = "running",
            StartedAt = "2026-08-27T16:20:47.757472346Z",
            FinishedAt = "2026-08-27T16:20:47.567932652Z",
        }), "{}");

        Assert.Null(inspect.FinishedAt);
        Assert.NotNull(inspect.StartedAt);
    }

    [Fact]
    public void AStoppedContainerKeepsItsFinishTime()
    {
        var inspect = DockerInspect.Map(Response(new State
        {
            Running = false,
            Status = "exited",
            StartedAt = "2026-08-27T16:34:38Z",
            FinishedAt = "2026-08-27T17:01:14Z",
        }), "{}");

        Assert.Equal(new DateTimeOffset(2026, 8, 27, 17, 1, 14, TimeSpan.Zero), inspect.FinishedAt);
    }

    [Fact]
    public void TheZeroTimestampIsNotADateInTheYearOne()
    {
        // The engine sends this for an event that never happened.
        var inspect = DockerInspect.Map(Response(new State
        {
            Running = false,
            Status = "created",
            StartedAt = "0001-01-01T00:00:00Z",
            FinishedAt = "0001-01-01T00:00:00Z",
        }), "{}");

        Assert.Null(inspect.StartedAt);
        Assert.Null(inspect.FinishedAt);
    }

    [Fact]
    public void RestartingWinsOverRunning()
    {
        // Both flags are set at once, and the narrower fact is the one worth showing.
        var inspect = DockerInspect.Map(Response(new State
        {
            Running = true,
            Restarting = true,
            Status = "restarting",
        }), "{}");

        Assert.Equal(DockerState.Restarting, inspect.State);
    }

    [Fact]
    public void OomKilledSurvivesBecauseExitCodeAloneCannotSayIt()
    {
        var inspect = DockerInspect.Map(Response(new State
        {
            Running = false,
            Status = "exited",
            ExitCode = 137,
            OOMKilled = true,
        }), "{}");

        Assert.True(inspect.OomKilled);
        Assert.Equal(137, inspect.ExitCode);
    }

    // ---------------------------------------------------------------- environment

    [Fact]
    public void EnvironmentSplitsOnTheFirstEqualsOnly()
    {
        // A value containing '=' is normal — connection strings and base64 both do it.
        var env = DockerInspect.MapEnvironment(["DATABASE_URL=postgres://a=b@host/db"]);

        var entry = Assert.Single(env);
        Assert.Equal("DATABASE_URL", entry.Key);
        Assert.Equal("postgres://a=b@host/db", entry.Value);
    }

    [Fact]
    public void ABareNameIsAnEmptyValueRatherThanADroppedEntry()
    {
        var entry = Assert.Single(DockerInspect.MapEnvironment(["DEBUG"]));

        Assert.Equal("DEBUG", entry.Key);
        Assert.Equal("", entry.Value);
    }

    [Fact]
    public void EnvironmentIsSortedBecauseLayerOrderIsNotAReadingOrder()
    {
        var env = DockerInspect.MapEnvironment(["ZONE=utc", "path=/bin", "APP=dray"]);

        Assert.Equal(["APP", "path", "ZONE"], env.Select(e => e.Key));
    }

    // ---------------------------------------------------------------- ports

    [Fact]
    public void ADeclaredButUnpublishedPortIsKept()
    {
        // The whole point of the Ports tab: this is why the service is unreachable, and a view
        // that only listed published ports would show nothing at all.
        var ports = DockerInspect.MapPorts(
            new Dictionary<string, EmptyStruct> { ["6379/tcp"] = default },
            published: null);

        var port = Assert.Single(ports);
        Assert.Equal(6379, port.ContainerPort);
        Assert.False(port.IsPublished);
    }

    [Fact]
    public void APublishedPortCarriesItsHostBinding()
    {
        var ports = DockerInspect.MapPorts(
            new Dictionary<string, EmptyStruct> { ["80/tcp"] = default },
            new Dictionary<string, IList<Wire.PortBinding>>
            {
                ["80/tcp"] = [new Wire.PortBinding { HostPort = "8080", HostIP = "0.0.0.0" }],
            });

        var port = Assert.Single(ports);
        Assert.True(port.IsPublished);
        Assert.Equal(8080, Assert.Single(port.Bindings).HostPort);
    }

    [Fact]
    public void APortBoundOnBothStacksIsNotListedTwice()
    {
        // Publishing on IPv4 and IPv6 produces two bindings with the same host port.
        var ports = DockerInspect.MapPorts(
            new Dictionary<string, EmptyStruct> { ["80/tcp"] = default },
            new Dictionary<string, IList<Wire.PortBinding>>
            {
                ["80/tcp"] =
                [
                    new Wire.PortBinding { HostPort = "8080", HostIP = "0.0.0.0" },
                    new Wire.PortBinding { HostPort = "8080", HostIP = "::" },
                ],
            });

        Assert.Single(Assert.Single(ports).Bindings);
    }

    [Fact]
    public void ABindingWithNoHostPortMeansDeclaredNotMapped()
    {
        var ports = DockerInspect.MapPorts(
            new Dictionary<string, EmptyStruct> { ["443/tcp"] = default },
            new Dictionary<string, IList<Wire.PortBinding>>
            {
                ["443/tcp"] = [new Wire.PortBinding { HostPort = "" }],
            });

        Assert.False(Assert.Single(ports).IsPublished);
    }

    [Fact]
    public void APublishedPortMissingFromExposedIsStillShown()
    {
        // `-p` without `EXPOSE` in the image produces exactly this, and dropping it would hide a
        // port that genuinely reaches the host.
        var ports = DockerInspect.MapPorts(
            exposed: null,
            new Dictionary<string, IList<Wire.PortBinding>>
            {
                ["9000/tcp"] = [new Wire.PortBinding { HostPort = "9000" }],
            });

        Assert.True(Assert.Single(ports).IsPublished);
    }

    [Theory]
    [InlineData("8080/tcp", 8080, "tcp")]
    [InlineData("53/udp", 53, "udp")]
    [InlineData("80", 80, "tcp")]
    [InlineData("80/TCP", 80, "tcp")]
    public void PortKeysParse(string key, int port, string protocol)
    {
        var parsed = DockerInspect.ParsePortKey(key);

        Assert.NotNull(parsed);
        Assert.Equal((port, protocol), parsed.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/tcp")]
    [InlineData("http/tcp")]
    [InlineData("0/tcp")]
    public void UnparseablePortKeysAreSkippedRatherThanThrowing(string key)
        => Assert.Null(DockerInspect.ParsePortKey(key));

    // ---------------------------------------------------------------- mounts

    [Fact]
    public void ANamedVolumeIsIdentifiedByNameAndIsBrowsable()
    {
        var mounts = DockerInspect.MapMounts(
        [
            new Wire.MountPoint
            {
                Type = "volume",
                Name = "dray-data",
                Source = "/var/lib/containers/storage/volumes/dray-data/_data",
                Destination = "/data",
                RW = true,
            },
        ]);

        var mount = Assert.Single(mounts);
        Assert.Equal(MountKind.Volume, mount.Kind);
        Assert.Equal("dray-data", mount.Source);
        Assert.True(mount.IsBrowsable);
    }

    [Fact]
    public void ABindKeepsItsHostPathAndIsNotBrowsable()
    {
        // The engine cannot serve a host path, so offering to open it would be a broken promise.
        var mount = Assert.Single(DockerInspect.MapMounts(
        [
            new Wire.MountPoint
            {
                Type = "bind",
                Source = "/Users/redth/code",
                Destination = "/src",
                RW = false,
            },
        ]));

        Assert.Equal(MountKind.Bind, mount.Kind);
        Assert.Equal("/Users/redth/code", mount.Source);
        Assert.True(mount.ReadOnly);
        Assert.False(mount.IsBrowsable);
    }

    // ---------------------------------------------------------------- command line

    [Fact]
    public void EntrypointAndCommandReadAsOneLine()
    {
        var inspect = DockerInspect.Map(Response(config: new ContainerConfig
        {
            Entrypoint = ["/docker-entrypoint.sh"],
            Cmd = ["nginx", "-g", "daemon off;"],
        }), "{}");

        Assert.Equal("/docker-entrypoint.sh nginx -g \"daemon off;\"", inspect.CommandLine);
    }

    // ---------------------------------------------------------------- restart policy

    [Theory]
    [InlineData(RestartPolicyKind.Always, 0, "always")]
    [InlineData(RestartPolicyKind.UnlessStopped, 0, "unless-stopped")]
    [InlineData(RestartPolicyKind.OnFailure, 5, "on-failure:5")]
    [InlineData(RestartPolicyKind.OnFailure, 0, "on-failure")]
    public void RestartPolicyReadsAsTheUserWouldHaveWrittenIt(RestartPolicyKind kind, int retries, string expected)
    {
        var inspect = DockerInspect.Map(Response(host: new HostConfig
        {
            RestartPolicy = new RestartPolicy { Name = kind, MaximumRetryCount = retries },
        }), "{}");

        Assert.Equal(expected, inspect.RestartPolicy);
    }

    [Fact]
    public void NoRestartPolicyIsNullRatherThanTheWordNo()
    {
        var inspect = DockerInspect.Map(Response(host: new HostConfig
        {
            RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No },
        }), "{}");

        Assert.Null(inspect.RestartPolicy);
    }
}
