using Docker.DotNet.Models;

using Xunit;

namespace Dray.Docker.Tests;

public class DockerVolumesTests
{
    [Fact]
    public void UsageIsJoinedInBecauseTheVolumesEndpointDoesNotCarryIt()
    {
        var volume = DockerVolumes.Map(
            new VolumeResponse { Name = "dray-data", Driver = "local" },
            ["dray-web", "dray-worker"]);

        Assert.True(volume.IsInUse);
        Assert.Equal(["dray-web", "dray-worker"], volume.UsedBy);
    }

    [Fact]
    public void SizeIsNullWhenTheEngineDidNotMeasureIt()
    {
        // A plain list never carries usage data — only `system df` does. Reporting zero would read
        // as "empty volume".
        var volume = DockerVolumes.Map(new VolumeResponse { Name = "dray-data" }, []);

        Assert.Null(volume.SizeBytes);
    }

    [Fact]
    public void AMissingDriverFallsBackToLocalRatherThanEmpty()
        => Assert.Equal("local", DockerVolumes.Map(new VolumeResponse { Name = "v" }, []).Driver);

    [Fact]
    public void CreatedAtParsesTheEnginesStringTimestamp()
    {
        var volume = DockerVolumes.Map(
            new VolumeResponse { Name = "v", CreatedAt = "2026-08-27T17:01:14Z" }, []);

        Assert.Equal(new DateTimeOffset(2026, 8, 27, 17, 1, 14, TimeSpan.Zero), volume.Created);
    }

    [Fact]
    public void AnUnparseableTimestampIsNullRatherThanThrowing()
        => Assert.Null(DockerVolumes.Map(new VolumeResponse { Name = "v", CreatedAt = "whenever" }, []).Created);
}

/// <summary>
/// The helper container mounts the volume at a fixed path, and nothing above the session should
/// ever see it — a path that leaked would send the user browsing into the helper's own filesystem.
/// </summary>
public class VolumePathMappingTests
{
    [Theory]
    [InlineData("/", "/dray-volume")]
    [InlineData("/conf", "/dray-volume/conf")]
    [InlineData("conf", "/dray-volume/conf")]
    [InlineData("/conf/app.conf", "/dray-volume/conf/app.conf")]
    [InlineData("//conf//", "/dray-volume/conf")]
    public void VolumePathsMapOntoTheMountPoint(string input, string expected)
        => Assert.Equal(expected, DockerVolumeSession.ToHelperPath(input));

    [Theory]
    [InlineData("/dray-volume", "/")]
    [InlineData("/dray-volume/conf", "/conf")]
    [InlineData("/dray-volume/conf/app.conf", "/conf/app.conf")]
    public void HelperPathsMapBack(string input, string expected)
        => Assert.Equal(expected, DockerVolumeSession.ToVolumePath(input));

    [Fact]
    public void MappingRoundTripsForEveryShape()
    {
        foreach (var path in new[] { "/", "/a", "/a/b", "/a/b/c.txt" })
            Assert.Equal(path, DockerVolumeSession.ToVolumePath(DockerVolumeSession.ToHelperPath(path)));
    }

    [Fact]
    public void APathThatIsNotUnderTheMountPointIsLeftAlone()
    {
        // Defensive: rewriting an unexpected path would be worse than passing it through, because
        // it would silently point somewhere real.
        Assert.Equal("/etc/hosts", DockerVolumeSession.ToVolumePath("/etc/hosts"));
    }

    [Fact]
    public void AVolumeDirectoryNamedLikeTheMountPointIsNotConfusedWithIt()
    {
        // "/dray-volumes" shares a prefix with "/dray-volume" but is not inside it.
        Assert.Equal("/dray-volumes", DockerVolumeSession.ToVolumePath("/dray-volumes"));
    }
}
