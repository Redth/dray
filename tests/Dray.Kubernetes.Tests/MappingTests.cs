using Dray.Core.Kubernetes;
using k8s.Models;
using Xunit;

namespace Dray.Kubernetes.Tests;

/// <summary>The API's shapes, as captured from the spike's pods on kind 1.37, through the mapping.</summary>
public class MappingTests
{
    static V1Pod CrashingPod() => new()
    {
        Metadata = new V1ObjectMeta { Uid = "u1", Name = "crasher", NamespaceProperty = "spike", CreationTimestamp = DateTime.UtcNow },
        Spec = new V1PodSpec { NodeName = "dray-spike-control-plane", Containers = [new V1Container { Name = "main", Image = "busybox:1.37" }] },
        Status = new V1PodStatus
        {
            Phase = "Running",
            Conditions = [new V1PodCondition { Type = "Ready", Status = "False" }, new V1PodCondition { Type = "PodScheduled", Status = "True" }],
            ContainerStatuses =
            [
                new V1ContainerStatus
                {
                    Name = "main", Image = "docker.io/library/busybox:1.37", Ready = false, RestartCount = 3,
                    State = new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = "CrashLoopBackOff", Message = "back-off 40s restarting failed container" } },
                    LastState = new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 3, Reason = "Error" } },
                },
            ],
        },
    };

    [Fact]
    public void ACrashLoopingPodKeepsItsLastExit()
    {
        var pod = KubeMapping.Pod(CrashingPod());

        var main = Assert.Single(pod.Containers);
        Assert.Equal("waiting", main.State);
        Assert.Equal("CrashLoopBackOff", main.Reason);
        Assert.Equal(3, main.LastExitCode);
        Assert.Equal("Error", main.LastReason);
        Assert.Equal(3, pod.Restarts);
        Assert.Equal("docker.io/library/busybox:1.37", main.Image);
    }

    /// <summary>
    /// A replica's owner is its ReplicaSet, which the store follows to the Deployment. The
    /// controller reference is the one that counts when there are several.
    /// </summary>
    [Fact]
    public void TheControllerIsTheOwner()
    {
        var raw = CrashingPod();
        raw.Metadata.OwnerReferences =
        [
            new V1OwnerReference { Kind = "Service", Name = "noise", Uid = "x", ApiVersion = "v1" },
            new V1OwnerReference { Kind = "ReplicaSet", Name = "myapp-84cb689584", Uid = "rs1", Controller = true, ApiVersion = "apps/v1" },
        ];

        Assert.Equal(new OwnerRef(ClusterKind.ReplicaSet, "myapp-84cb689584", "rs1"), KubeMapping.Pod(raw).Owner);
    }

    [Fact]
    public void AnUnschedulablePodCarriesTheSchedulersReason()
    {
        var raw = new V1Pod
        {
            Metadata = new V1ObjectMeta { Uid = "u2", Name = "big", NamespaceProperty = "spike" },
            Spec = new V1PodSpec { Containers = [new V1Container { Name = "main", Image = "x" }] },
            Status = new V1PodStatus
            {
                Phase = "Pending",
                Conditions = [new V1PodCondition { Type = "PodScheduled", Status = "False", Reason = "Unschedulable", Message = "0/1 nodes are available: 1 Insufficient memory." }],
            },
        };

        var pod = KubeMapping.Pod(raw);
        var status = PodStatusVocabulary.Resolve(pod);

        Assert.Equal("Pending", status.Word);
        Assert.Equal("0/1 nodes are available: 1 Insufficient memory.", status.Detail);
    }

    [Fact]
    public void AServicesTargetPortKeepsItsName()
    {
        var raw = new V1Service
        {
            Metadata = new V1ObjectMeta { Uid = "s1", Name = "myapp", NamespaceProperty = "spike" },
            Spec = new V1ServiceSpec
            {
                Type = "ClusterIP",
                ClusterIP = "10.96.12.4",
                Selector = new Dictionary<string, string> { ["app"] = "myapp" },
                Ports = [new V1ServicePort { Port = 80, TargetPort = (IntOrString)"http", Protocol = "TCP" }],
            },
        };

        var service = KubeMapping.Service(raw);
        Assert.Equal(new ServicePort(null, 80, "http", null, "TCP"), Assert.Single(service.Ports));
        Assert.Equal("myapp", service.Selector["app"]);
    }

    /// <summary>Measured on the status channel: a non-zero exit is a Failure with the code in its causes.</summary>
    [Theory]
    [InlineData("{\"metadata\":{},\"status\":\"Success\"}", 0)]
    [InlineData("{\"metadata\":{},\"status\":\"Failure\",\"message\":\"command terminated with non-zero exit code: command terminated with exit code 7\",\"reason\":\"NonZeroExitCode\",\"details\":{\"causes\":[{\"reason\":\"ExitCode\",\"message\":\"7\"}]}}", 7)]
    [InlineData("{\"status\":\"Failure\",\"message\":\"exec: \\\"sh\\\": executable file not found in $PATH\"}", null)]
    [InlineData("", null)]
    public void ExitCodesComeFromTheStatusChannel(string status, int? expected)
        => Assert.Equal(expected, KubeExecSession.ExitCode(status));

    [Fact]
    public void ALogLineSplitsItsTimestampOff()
    {
        var line = KubeLogLine.Parse("2026-10-23T02:10:38.157614335Z fatal: config missing", timestamps: true, 4);

        Assert.Equal("fatal: config missing", line.Text);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 2, 10, 38, TimeSpan.Zero), line.Timestamp!.Value.AddTicks(-(line.Timestamp.Value.Ticks % TimeSpan.TicksPerSecond)));
        Assert.Equal(Dray.Core.Model.LogStream.StdOut, line.Stream);
    }

    [Fact]
    public void ALogLineWithoutATimestampIsKeptWhole()
        => Assert.Equal("no time here", KubeLogLine.Parse("no time here", timestamps: true, 0).Text);
}
