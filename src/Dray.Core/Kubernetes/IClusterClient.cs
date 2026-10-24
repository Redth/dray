using Dray.Core.Model;

namespace Dray.Core.Kubernetes;

/// <summary>
/// One cluster, as Dray drives it. The sibling of <c>IContainerRuntime</c>, not another
/// implementation of it: a cluster has no image store, volumes or networks in Docker's sense, and
/// pods have owners and desired state that containers do not. Forcing it through the container
/// interface would put Kubernetes conditionals on every Docker page (docs/KUBERNETES.md §9.1).
/// </summary>
public interface IClusterClient : IAsyncDisposable
{
    /// <summary>Reach the cluster and measure what it and these credentials allow.</summary>
    Task<ClusterCapabilities> ConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// One kind's state, then every change to it, for as long as the token holds.
    /// <para>
    /// The list-then-watch loop, the resourceVersion cursor, bookmarks, the server ending a watch on
    /// its timeout and the relist after 410 Gone all happen behind this (docs/KUBERNETES.md §9.3).
    /// What comes out is what the store needs: a full state whenever one is (re)established, and
    /// single changes between.
    /// </para>
    /// </summary>
    IAsyncEnumerable<ClusterChange> WatchAsync(ClusterKind kind, CancellationToken ct = default);

    /// <summary>
    /// A container's log. Measured: stdout and stderr arrive merged with no marker, so every line is
    /// <see cref="LogStream.StdOut"/> — reported as what it is, not guessed apart.
    /// </summary>
    IAsyncEnumerable<LogLine> StreamLogsAsync(string ns, string pod, string container, PodLogOptions options, CancellationToken ct = default);

    Task<IExecSession> StartExecAsync(string ns, string pod, string container, ExecOptions options, CancellationToken ct = default);

    /// <summary>
    /// Forward a pod's port to this machine. Bound to 127.0.0.1 only — a forward is a tunnel into
    /// the cluster, and exposing it to the network is not something a click should do (§5.4).
    /// </summary>
    Task<IPortForward> ForwardAsync(string ns, string pod, int remotePort, int? localPort = null, CancellationToken ct = default);

    Task DeleteAsync(ClusterKind kind, string ns, string name, CancellationToken ct = default);

    Task ScaleAsync(ClusterKind kind, string ns, string name, int replicas, CancellationToken ct = default);

    /// <summary>What <c>kubectl rollout restart</c> does: a new pod template annotation, so the controller rolls.</summary>
    Task RestartAsync(ClusterKind kind, string ns, string name, CancellationToken ct = default);

    /// <summary>
    /// The object as the API server returns it, as JSON text — for the Inspect tab. Not re-serialised
    /// from Dray's model, for the reason docs/ARCHITECTURE.md §2.7 gives: the fields worth seeing
    /// when something is strange are the ones a model never heard of.
    /// </summary>
    Task<string> GetRawAsync(ClusterKind kind, string ns, string name, CancellationToken ct = default);
}

/// <summary>Makes a client for a context. Implemented in <c>Dray.Kubernetes</c>; faked in tests.</summary>
public interface IClusterClientFactory
{
    IClusterClient Create(KubeContext context);
}

/// <param name="Previous">
/// The instance before the current one. Measured: while a pod is in back-off the plain log already
/// is the dead instance's, so this is only offered once a replacement is running (§5.2).
/// </param>
public sealed record PodLogOptions(bool Follow = true, int? Tail = 500, bool Previous = false, bool Timestamps = true);

/// <summary>What one kind's watch says happened.</summary>
public abstract record ClusterChange(ClusterKind Kind);

/// <summary>
/// The kind's whole state, as of now: the first list, and every relist after a 410. The store
/// diffs it against what it holds rather than resetting, so a relist that changes nothing repaints
/// nothing (docs/ARCHITECTURE.md §3.1).
/// </summary>
public sealed record ClusterSynced(ClusterKind Kind, IReadOnlyList<ClusterObject> Items) : ClusterChange(Kind);

public sealed record ClusterUpserted(ClusterObject Item) : ClusterChange(Item.Kind);

public sealed record ClusterDeleted(ClusterKind Kind, string Uid) : ClusterChange(Kind);

/// <summary>
/// The watch could not continue — a permission refused, the server gone. Said, so the page can,
/// rather than leaving a list that silently stopped updating.
/// </summary>
public sealed record ClusterInterrupted(ClusterKind Kind, string Reason, bool Forbidden = false) : ClusterChange(Kind);

/// <summary>A live port forward. Disposing it closes the listener and every connection through it.</summary>
public interface IPortForward : IAsyncDisposable
{
    string Namespace { get; }

    string Pod { get; }

    int RemotePort { get; }

    /// <summary>The port on 127.0.0.1. The remote one when it was free, the next free one otherwise.</summary>
    int LocalPort { get; }

    /// <summary>Connections carried so far — so the list of forwards can say one is in use.</summary>
    int Connections { get; }

    /// <summary>Why it stopped, when it has. Null while it runs.</summary>
    string? Failure { get; }
}
