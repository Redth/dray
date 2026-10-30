using Dray.Core.Engine;

namespace Dray.Core.Kubernetes;

/// <summary>
/// Whether a cluster runs on this machine. Decides whether v1 connects at all (docs/KUBERNETES.md
/// §3), and is measured — never read off a context name, because <c>docker-desktop</c> and
/// <c>kind-*</c> are conventions and a convention is a guess.
/// </summary>
public enum ClusterLocality
{
    /// <summary>The API server resolves to a loopback address, or its nodes are containers on a connected engine.</summary>
    Local,

    /// <summary>Anything else. Listed with the reason, not connectable in v1.</summary>
    Remote,
}

/// <summary>
/// One context from the kubeconfig: a cluster, and how Dray would reach it.
/// <para>
/// Built from the kubeconfig's <c>contexts</c> and <c>clusters</c> sections only. The <c>users</c>
/// section — where client keys and tokens live — is never read here; the client library reads it
/// at connect time, in memory, and nothing of it is kept (docs/CREDENTIALS.md).
/// </para>
/// </summary>
public sealed record KubeContext
{
    /// <summary>The context's name. Stable, and what <c>kubectl --context</c> takes.</summary>
    public required string Name { get; init; }

    public required string ClusterName { get; init; }

    /// <summary>The API server, as the kubeconfig spells it — e.g. <c>https://127.0.0.1:63628</c>.</summary>
    public required string Server { get; init; }

    /// <summary>The namespace the context sets, or null for <c>default</c>.</summary>
    public string? Namespace { get; init; }

    /// <summary>The file that defined it — the first one in <c>KUBECONFIG</c> that does.</summary>
    public required string SourcePath { get; init; }

    /// <summary>True for <c>kubectl</c>'s current-context. Shown; never changed by selecting a cluster.</summary>
    public bool IsCurrent { get; init; }

    public ClusterLocality Locality { get; init; } = ClusterLocality.Remote;

    /// <summary>Why the locality is what it is, in words — the Hosts page shows it.</summary>
    public string LocalityReason { get; init; } = "";

    public HostConnectionState State { get; init; } = HostConnectionState.Disconnected;

    /// <summary>Why it is not connected, in plain language. Null when it is.</summary>
    public string? StateDetail { get; init; }

    public ClusterCapabilities Capabilities { get; init; } = ClusterCapabilities.None;

    /// <summary>The engine whose containers are this cluster's nodes, when that has been measured (§4).</summary>
    public string? PairedEngineId { get; init; }

    public bool IsLocal => Locality == ClusterLocality.Local;
}

/// <summary>
/// What the connected cluster and this user's credentials allow, set at connect from answers the
/// cluster gave (docs/KUBERNETES.md §9.4). A control the user cannot use is hidden rather than
/// refused, and an absent value stays absent — no metrics server is not "0%".
/// </summary>
public sealed record ClusterCapabilities
{
    public static readonly ClusterCapabilities None = new();

    /// <summary><c>/version</c>'s <c>gitVersion</c>, e.g. <c>v1.37.0</c>.</summary>
    public string? ServerVersion { get; init; }

    /// <summary>Pods can be listed across every namespace, so the views need not ask one at a time.</summary>
    public bool CanListAllNamespaces { get; init; }

    public bool CanExec { get; init; }

    public bool CanPortForward { get; init; }

    public bool CanDeletePods { get; init; }

    public bool CanScale { get; init; }

    /// <summary>Patch workloads — what a rollout restart is.</summary>
    public bool CanRestart { get; init; }

    /// <summary>The <c>v1beta1.metrics.k8s.io</c> APIService exists. kind ships none.</summary>
    public bool SupportsMetrics { get; init; }

    /// <summary>Watches can stream their initial state (<c>sendInitialEvents</c>). Measured by trying one.</summary>
    public bool SupportsStreamingLists { get; init; }
}
