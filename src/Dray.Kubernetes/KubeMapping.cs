using Dray.Core.Kubernetes;
using k8s.Models;

namespace Dray.Kubernetes;

/// <summary>
/// The API's types, as Dray's. One direction only: nothing here builds a request.
/// </summary>
static class KubeMapping
{
    public static NamespaceSummary Namespace(V1Namespace ns) => new()
    {
        Uid = ns.Metadata.Uid,
        Name = ns.Metadata.Name,
        Created = Created(ns.Metadata),
        Phase = ns.Status?.Phase ?? "Active",
    };

    public static NodeSummary Node(V1Node node) => new()
    {
        Uid = node.Metadata.Uid,
        Name = node.Metadata.Name,
        Created = Created(node.Metadata),
        ProviderId = node.Spec?.ProviderID,
        Ready = node.Status?.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true,
        KubeletVersion = node.Status?.NodeInfo?.KubeletVersion,
        OperatingSystem = node.Status?.NodeInfo?.OperatingSystem,
        Architecture = node.Status?.NodeInfo?.Architecture,
        ContainerRuntime = node.Status?.NodeInfo?.ContainerRuntimeVersion,
        Images = [.. (node.Status?.Images ?? []).SelectMany(i => i.Names ?? [])],
    };

    public static PodSummary Pod(V1Pod pod)
    {
        var status = pod.Status;
        var scheduled = status?.Conditions?.FirstOrDefault(c => c.Type == "PodScheduled");

        return new PodSummary
        {
            Uid = pod.Metadata.Uid,
            Name = pod.Metadata.Name,
            Namespace = pod.Metadata.NamespaceProperty ?? "",
            Created = Created(pod.Metadata),
            Phase = status?.Phase ?? "Pending",
            Reason = status?.Reason,
            Message = status?.Message,
            Node = pod.Spec?.NodeName,
            PodIp = status?.PodIP,
            IsTerminating = pod.Metadata.DeletionTimestamp is not null,
            ConditionReady = status?.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true,
            UnschedulableMessage = scheduled is { Status: "False" } ? scheduled.Message : null,
            Owner = Owner(pod.Metadata),
            Labels = pod.Metadata.Labels is { } labels ? new Dictionary<string, string>(labels) : new Dictionary<string, string>(),
            Containers = Containers(pod.Spec?.Containers, status?.ContainerStatuses, init: false),
            InitContainers = Containers(pod.Spec?.InitContainers, status?.InitContainerStatuses, init: true),
        };
    }

    /// <summary>
    /// The spec's containers, each with its status where the kubelet has reported one. Spec order,
    /// because that is the order the user wrote them in and the order kubectl lists them.
    /// </summary>
    static IReadOnlyList<PodContainer> Containers(IList<V1Container>? spec, IList<V1ContainerStatus>? statuses, bool init)
    {
        if (spec is null) return [];

        return
        [
            .. spec.Select(c =>
            {
                var s = statuses?.FirstOrDefault(x => x.Name == c.Name);
                var state = s?.State;
                var last = s?.LastState?.Terminated;

                return new PodContainer
                {
                    Name = c.Name,
                    Image = s?.Image is { Length: > 0 } reported && !reported.StartsWith("sha256:", StringComparison.Ordinal) ? reported : c.Image,
                    IsInit = init,
                    Ready = s?.Ready ?? false,
                    RestartCount = s?.RestartCount ?? 0,
                    State = state?.Running is not null ? "running" : state?.Terminated is not null ? "terminated" : state?.Waiting is not null ? "waiting" : null,
                    Reason = state?.Waiting?.Reason ?? state?.Terminated?.Reason,
                    Message = state?.Waiting?.Message ?? state?.Terminated?.Message,
                    ExitCode = state?.Terminated?.ExitCode,
                    StartedAt = ToOffset(state?.Running?.StartedAt ?? state?.Terminated?.StartedAt),
                    LastReason = last?.Reason,
                    LastExitCode = last?.ExitCode,
                    LastFinishedAt = ToOffset(last?.FinishedAt),
                    Ports = [.. (c.Ports ?? []).Select(p => p.ContainerPort)],
                    NamedPorts = (c.Ports ?? []).Where(p => !string.IsNullOrEmpty(p.Name)).DistinctBy(p => p.Name).ToDictionary(p => p.Name, p => p.ContainerPort),
                };
            }),
        ];
    }

    public static ReplicaSetSummary ReplicaSet(V1ReplicaSet rs) => new()
    {
        Uid = rs.Metadata.Uid,
        Name = rs.Metadata.Name,
        Namespace = rs.Metadata.NamespaceProperty ?? "",
        Created = Created(rs.Metadata),
        Owner = Owner(rs.Metadata),
    };

    public static WorkloadSummary Deployment(V1Deployment d) => new()
    {
        Uid = d.Metadata.Uid,
        Name = d.Metadata.Name,
        Namespace = d.Metadata.NamespaceProperty ?? "",
        Created = Created(d.Metadata),
        WorkloadKind = ClusterKind.Deployment,
        Desired = d.Spec?.Replicas ?? 1,
        Ready = d.Status?.ReadyReplicas ?? 0,
        Images = Images(d.Spec?.Template),
    };

    public static WorkloadSummary StatefulSet(V1StatefulSet s) => new()
    {
        Uid = s.Metadata.Uid,
        Name = s.Metadata.Name,
        Namespace = s.Metadata.NamespaceProperty ?? "",
        Created = Created(s.Metadata),
        WorkloadKind = ClusterKind.StatefulSet,
        Desired = s.Spec?.Replicas ?? 1,
        Ready = s.Status?.ReadyReplicas ?? 0,
        Images = Images(s.Spec?.Template),
    };

    public static WorkloadSummary DaemonSet(V1DaemonSet d) => new()
    {
        Uid = d.Metadata.Uid,
        Name = d.Metadata.Name,
        Namespace = d.Metadata.NamespaceProperty ?? "",
        Created = Created(d.Metadata),
        WorkloadKind = ClusterKind.DaemonSet,
        Desired = d.Status?.DesiredNumberScheduled ?? 0,
        Ready = d.Status?.NumberReady ?? 0,
        Images = Images(d.Spec?.Template),
    };

    public static WorkloadSummary Job(V1Job j) => new()
    {
        Uid = j.Metadata.Uid,
        Name = j.Metadata.Name,
        Namespace = j.Metadata.NamespaceProperty ?? "",
        Created = Created(j.Metadata),
        WorkloadKind = ClusterKind.Job,
        Desired = j.Spec?.Completions ?? 1,
        Ready = j.Status?.Ready ?? 0,
        Succeeded = j.Status?.Succeeded ?? 0,
        Failed = j.Status?.Failed ?? 0,
        Images = Images(j.Spec?.Template),
    };

    public static ServiceSummary Service(V1Service s) => new()
    {
        Uid = s.Metadata.Uid,
        Name = s.Metadata.Name,
        Namespace = s.Metadata.NamespaceProperty ?? "",
        Created = Created(s.Metadata),
        Type = s.Spec?.Type ?? "ClusterIP",
        ClusterIp = s.Spec?.ClusterIP is { Length: > 0 } ip && ip != "None" ? ip : null,
        Ports = [.. (s.Spec?.Ports ?? []).Select(p => new ServicePort(p.Name, p.Port, p.TargetPort?.Value ?? p.Port.ToString(), p.NodePort, p.Protocol ?? "TCP"))],
        Selector = s.Spec?.Selector is { } selector ? new Dictionary<string, string>(selector) : new Dictionary<string, string>(),
        Ingress = [.. (s.Status?.LoadBalancer?.Ingress ?? []).Select(i => i.Ip ?? i.Hostname).OfType<string>()],
    };

    public static ClusterEvent Event(Corev1Event e) => new()
    {
        Uid = e.Metadata.Uid,
        Name = e.Metadata.Name,
        Namespace = e.Metadata.NamespaceProperty ?? "",
        Created = Created(e.Metadata),
        Type = e.Type ?? "Normal",
        Reason = e.Reason ?? "",
        Message = e.Message ?? "",
        InvolvedKind = KindOf(e.InvolvedObject?.Kind),
        InvolvedName = e.InvolvedObject?.Name ?? "",
        InvolvedUid = e.InvolvedObject?.Uid,
        Count = e.Count ?? e.Series?.Count ?? 1,

        // Three clocks, newest first. Events from the events.k8s.io writers set only eventTime;
        // the core writers set lastTimestamp. Whichever is there is when it last happened.
        LastSeen = ToOffset(e.Series?.LastObservedTime ?? e.LastTimestamp ?? e.EventTime ?? e.FirstTimestamp ?? e.Metadata.CreationTimestamp),
    };

    static IReadOnlyList<string> Images(V1PodTemplateSpec? template)
        => [.. (template?.Spec?.Containers ?? []).Select(c => c.Image).Distinct()];

    static OwnerRef? Owner(V1ObjectMeta meta)
    {
        var owner = meta.OwnerReferences?.FirstOrDefault(o => o.Controller == true) ?? meta.OwnerReferences?.FirstOrDefault();
        return owner is not null && KindOf(owner.Kind) is { } kind ? new OwnerRef(kind, owner.Name, owner.Uid) : null;
    }

    public static ClusterKind? KindOf(string? kind) => kind switch
    {
        "Namespace" => ClusterKind.Namespace,
        "Node" => ClusterKind.Node,
        "Pod" => ClusterKind.Pod,
        "ReplicaSet" => ClusterKind.ReplicaSet,
        "Deployment" => ClusterKind.Deployment,
        "StatefulSet" => ClusterKind.StatefulSet,
        "DaemonSet" => ClusterKind.DaemonSet,
        "Job" => ClusterKind.Job,
        "Service" => ClusterKind.Service,
        _ => null,
    };

    static DateTimeOffset? Created(V1ObjectMeta meta) => ToOffset(meta.CreationTimestamp);

    /// <summary>The client hands times back as UTC <see cref="DateTime"/>s; Dray's models carry offsets.</summary>
    static DateTimeOffset? ToOffset(DateTime? time) => time is { } t
        ? new DateTimeOffset(DateTime.SpecifyKind(t, t.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : t.Kind))
        : null;
}
