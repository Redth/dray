using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The text behind "Copy summary".
/// <para>
/// It exists to be pasted somewhere a person will read it while working out what happened, which
/// makes the rule simple: everything the engine said, nothing it did not.
/// </para>
/// </summary>
public class ContainerInspectSummaryTests
{
    static ContainerInspect Inspect(
        bool oomKilled = false,
        string? error = null,
        IReadOnlyList<NetworkAttachment>? networks = null,
        IReadOnlyList<MountPoint>? mounts = null) => new()
    {
        Id = "43faffd4a0e81c250b5d652449e9644d9c38cd14fc1ec55e158ebf9dc3c6789c",
        Name = "dray-redis",
        Image = "docker.io/library/redis:7-alpine",
        State = DockerState.Running,
        Entrypoint = ["docker-entrypoint.sh"],
        Command = ["redis-server"],
        OomKilled = oomKilled,
        Error = error,
        Networks = networks ?? [],
        Mounts = mounts ?? [],
    };

    [Fact]
    public void TheIdIsFullBecauseItMayBePastedIntoACommand()
    {
        var text = ContainerInspectSummary.ToText(Inspect());

        Assert.Contains("43faffd4a0e81c250b5d652449e9644d9c38cd14fc1ec55e158ebf9dc3c6789c", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFieldsThatAlwaysExistAreAlwaysThere()
    {
        var lines = ContainerInspectSummary.ToText(Inspect()).Split('\n');

        Assert.Equal(5, lines.Length);
        Assert.Collection(lines,
            l => Assert.StartsWith("name:", l, StringComparison.Ordinal),
            l => Assert.StartsWith("id:", l, StringComparison.Ordinal),
            l => Assert.StartsWith("image:", l, StringComparison.Ordinal),
            l => Assert.StartsWith("status:", l, StringComparison.Ordinal),
            l => Assert.StartsWith("command:", l, StringComparison.Ordinal));
    }

    [Fact]
    public void AnOomKillIsStatedOnlyWhenItHappened()
    {
        // "killed: no" is noise in something being skimmed for the reason a container stopped.
        Assert.DoesNotContain("killed:", ContainerInspectSummary.ToText(Inspect()), StringComparison.Ordinal);
        Assert.Contains("killed:   out of memory", ContainerInspectSummary.ToText(Inspect(oomKilled: true)), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyErrorIsNotAnError()
    {
        // The engine returns "" for a container that has not failed, and a blank "error:" line
        // reads as one that has.
        Assert.DoesNotContain("error:", ContainerInspectSummary.ToText(Inspect(error: "")), StringComparison.Ordinal);
        Assert.Contains("error:    no such file", ContainerInspectSummary.ToText(Inspect(error: "no such file")), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryNetworkAndEveryMountIsListed()
    {
        var text = ContainerInspectSummary.ToText(Inspect(
            networks: [new NetworkAttachment("podman", "10.88.0.38", null, null, []), new NetworkAttachment("backend", "10.1.0.2", null, null, [])],
            mounts: [new MountPoint(MountKind.Volume, "vol-a", "/data", false)]));

        Assert.Contains("network:  podman 10.88.0.38", text, StringComparison.Ordinal);
        Assert.Contains("network:  backend 10.1.0.2", text, StringComparison.Ordinal);
        Assert.Contains("mount:    vol-a -> /data", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFieldNamesLineUp()
    {
        // It is read as a column. A field whose value starts one character off is the kind of
        // thing nobody reports and everybody notices.
        var text = ContainerInspectSummary.ToText(Inspect(
            oomKilled: true,
            error: "boom",
            networks: [new NetworkAttachment("podman", "10.88.0.38", null, null, [])],
            mounts: [new MountPoint(MountKind.Volume, "vol-a", "/data", false)]));

        foreach (var line in text.Split('\n'))
        {
            var value = line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..];

            Assert.Equal(10, line.Length - value.TrimStart().Length);
        }
    }
}
