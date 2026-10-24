namespace Dray.Core.Kubernetes;

/// <summary>The kinds Dray watches. Each has its own reflector and its own slice of the store.</summary>
public enum ClusterKind
{
    Namespace,
    Node,
    Pod,
    ReplicaSet,
    Deployment,
    StatefulSet,
    DaemonSet,
    Job,
    Service,
    Event,
}

/// <summary>
/// Anything from the cluster that the store holds. Keyed by <see cref="Uid"/>, which survives a
/// rename-free lifetime and is unique across namespaces — names are not, and a pod deleted and
/// recreated under the same name is a different pod.
/// </summary>
public abstract record ClusterObject
{
    public required string Uid { get; init; }

    public required string Name { get; init; }

    /// <summary>Empty for cluster-scoped kinds — nodes and namespaces.</summary>
    public string Namespace { get; init; } = "";

    public DateTimeOffset? Created { get; init; }

    public abstract ClusterKind Kind { get; }

    /// <summary>
    /// Whether two snapshots of the same object would render the same. Compared on what a row shows,
    /// never by record equality: the collections in here are lists, and two lists with the same
    /// items compare unequal by reference — which would make every relist rewrite every row
    /// (docs/ARCHITECTURE.md §3.1).
    /// </summary>
    public abstract bool SameAs(ClusterObject other);
}

public sealed record NamespaceSummary : ClusterObject
{
    public override ClusterKind Kind => ClusterKind.Namespace;

    public string Phase { get; init; } = "Active";

    /// <summary><c>kube-*</c> is reserved for the system by Kubernetes itself, so this is a rule, not a guess.</summary>
    public bool IsSystem => Name.StartsWith("kube-", StringComparison.Ordinal);

    public override bool SameAs(ClusterObject other) => other is NamespaceSummary n && n.Name == Name && n.Phase == Phase;
}

public sealed record NodeSummary : ClusterObject
{
    public override ClusterKind Kind => ClusterKind.Node;

    /// <summary>e.g. <c>kind://docker/dray-spike/dray-spike-control-plane</c>. Measured: kind sets it so.</summary>
    public string? ProviderId { get; init; }

    public bool Ready { get; init; }

    public string? KubeletVersion { get; init; }

    /// <summary><c>status.nodeInfo.operatingSystem</c> — what a shell in a pod on it should be.</summary>
    public string? OperatingSystem { get; init; }

    public string? Architecture { get; init; }

    public string? ContainerRuntime { get; init; }

    /// <summary>
    /// Every name the node's runtime reports for each image it holds. Lags a load by the kubelet's
    /// node-status interval — measured at about fifty seconds — so it is a hint, never the proof
    /// (docs/KUBERNETES.md §8).
    /// </summary>
    public IReadOnlyList<string> Images { get; init; } = [];

    public override bool SameAs(ClusterObject other) => other is NodeSummary n
        && n.Name == Name && n.Ready == Ready && n.KubeletVersion == KubeletVersion && n.Images.Count == Images.Count;
}

/// <summary>Who made an object: the controller in its <c>ownerReferences</c>.</summary>
public sealed record OwnerRef(ClusterKind Kind, string Name, string Uid);

/// <summary>A container in a pod, as its status reports it.</summary>
public sealed record PodContainer
{
    public required string Name { get; init; }

    public required string Image { get; init; }

    public bool Ready { get; init; }

    public int RestartCount { get; init; }

    public bool IsInit { get; init; }

    /// <summary><c>running</c>, <c>waiting</c> or <c>terminated</c>; null when the kubelet has not reported yet.</summary>
    public string? State { get; init; }

    /// <summary>The waiting or terminated reason — <c>CrashLoopBackOff</c>, <c>ErrImagePull</c>, <c>OOMKilled</c>.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The waiting message. For a failed pull it is the runtime's own sentence — measured: "failed
    /// to pull and unpack image "docker.io/library/myapp:dev": … pull access denied" — and is shown
    /// verbatim.
    /// </summary>
    public string? Message { get; init; }

    public int? ExitCode { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>The previous instance's end, for a container that has restarted.</summary>
    public string? LastReason { get; init; }

    public int? LastExitCode { get; init; }

    public DateTimeOffset? LastFinishedAt { get; init; }

    public IReadOnlyList<int> Ports { get; init; } = [];
}

public sealed record PodSummary : ClusterObject
{
    public override ClusterKind Kind => ClusterKind.Pod;

    public string Phase { get; init; } = "Pending";

    /// <summary><c>status.reason</c> — <c>Evicted</c>, <c>NodeLost</c>.</summary>
    public string? Reason { get; init; }

    public string? Message { get; init; }

    public string? Node { get; init; }

    public string? PodIp { get; init; }

    public bool IsTerminating { get; init; }

    /// <summary>The <c>Ready</c> condition is true.</summary>
    public bool ConditionReady { get; init; }

    /// <summary>The scheduler's reason for a pod it could not place, when it gave one.</summary>
    public string? UnschedulableMessage { get; init; }

    public OwnerRef? Owner { get; init; }

    public IReadOnlyList<PodContainer> Containers { get; init; } = [];

    public IReadOnlyList<PodContainer> InitContainers { get; init; } = [];

    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    public int ReadyCount => Containers.Count(c => c.Ready);

    public int Restarts => Containers.Sum(c => c.RestartCount);

    public override bool SameAs(ClusterObject other) => other is PodSummary p
        && p.Name == Name
        && p.Phase == Phase
        && p.Reason == Reason
        && p.Node == Node
        && p.IsTerminating == IsTerminating
        && p.ConditionReady == ConditionReady
        && p.UnschedulableMessage == UnschedulableMessage
        && p.Containers.Count == Containers.Count
        && p.Containers.SequenceEqual(Containers, ContainerComparer.Instance)
        && p.InitContainers.SequenceEqual(InitContainers, ContainerComparer.Instance);

    sealed class ContainerComparer : IEqualityComparer<PodContainer>
    {
        public static readonly ContainerComparer Instance = new();

        public bool Equals(PodContainer? a, PodContainer? b) => a is not null && b is not null
            && a.Name == b.Name && a.Image == b.Image && a.Ready == b.Ready && a.RestartCount == b.RestartCount
            && a.State == b.State && a.Reason == b.Reason && a.Message == b.Message && a.ExitCode == b.ExitCode
            && a.LastReason == b.LastReason && a.LastExitCode == b.LastExitCode;

        public int GetHashCode(PodContainer c) => HashCode.Combine(c.Name, c.State, c.Reason);
    }
}

/// <summary>A ReplicaSet — kept only to find the Deployment a pod belongs to. Never listed.</summary>
public sealed record ReplicaSetSummary : ClusterObject
{
    public override ClusterKind Kind => ClusterKind.ReplicaSet;

    public OwnerRef? Owner { get; init; }

    public override bool SameAs(ClusterObject other) => other is ReplicaSetSummary r && r.Owner == Owner;
}

/// <summary>A Deployment, StatefulSet, DaemonSet or Job: the unit someone thinks in.</summary>
public sealed record WorkloadSummary : ClusterObject
{
    public required ClusterKind WorkloadKind { get; init; }

    public override ClusterKind Kind => WorkloadKind;

    /// <summary>Replicas asked for. A DaemonSet's is how many nodes it should run on; a Job's, its completions.</summary>
    public int Desired { get; init; }

    public int Ready { get; init; }

    /// <summary>A Job's successful completions.</summary>
    public int Succeeded { get; init; }

    public int Failed { get; init; }

    public bool CanScale => WorkloadKind is ClusterKind.Deployment or ClusterKind.StatefulSet;

    public bool CanRestart => WorkloadKind is ClusterKind.Deployment or ClusterKind.StatefulSet or ClusterKind.DaemonSet;

    public IReadOnlyList<string> Images { get; init; } = [];

    public override bool SameAs(ClusterObject other) => other is WorkloadSummary w
        && w.Name == Name && w.Desired == Desired && w.Ready == Ready && w.Succeeded == Succeeded
        && w.Failed == Failed && w.Images.SequenceEqual(Images);
}

public sealed record ServicePort(string? Name, int Port, string TargetPort, int? NodePort, string Protocol);

public sealed record ServiceSummary : ClusterObject
{
    public override ClusterKind Kind => ClusterKind.Service;

    /// <summary><c>ClusterIP</c>, <c>NodePort</c>, <c>LoadBalancer</c>, <c>ExternalName</c>.</summary>
    public string Type { get; init; } = "ClusterIP";

    public string? ClusterIp { get; init; }

    public IReadOnlyList<ServicePort> Ports { get; init; } = [];

    public IReadOnlyDictionary<string, string> Selector { get; init; } = new Dictionary<string, string>();

    /// <summary>A LoadBalancer's addresses, when one has been assigned.</summary>
    public IReadOnlyList<string> Ingress { get; init; } = [];

    public override bool SameAs(ClusterObject other) => other is ServiceSummary s
        && s.Name == Name && s.Type == Type && s.ClusterIp == ClusterIp
        && s.Ports.SequenceEqual(Ports) && s.Ingress.SequenceEqual(Ingress);
}

/// <summary>A Kubernetes Event: the cluster saying why something happened to an object.</summary>
public sealed record ClusterEvent : ClusterObject
{
    public override ClusterKind Kind => ClusterKind.Event;

    /// <summary><c>Normal</c> or <c>Warning</c>.</summary>
    public string Type { get; init; } = "Normal";

    public string Reason { get; init; } = "";

    public string Message { get; init; } = "";

    public required ClusterKind? InvolvedKind { get; init; }

    public string InvolvedName { get; init; } = "";

    public string? InvolvedUid { get; init; }

    public int Count { get; init; } = 1;

    public DateTimeOffset? LastSeen { get; init; }

    public bool IsWarning => Type == "Warning";

    public override bool SameAs(ClusterObject other) => other is ClusterEvent e
        && e.Count == Count && e.LastSeen == LastSeen && e.Message == Message;
}
