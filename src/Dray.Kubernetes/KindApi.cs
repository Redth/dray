using System.Runtime.CompilerServices;
using Dray.Core.Kubernetes;
using k8s;
using k8s.Models;

namespace Dray.Kubernetes;

/// <summary>Watch arguments, in the order every generated <c>WatchList…Async</c> takes them by name.</summary>
delegate IAsyncEnumerable<(WatchEventType, T)> WatchCall<T>(string? resourceVersion, bool? sendInitialEvents, string? resourceVersionMatch, int timeoutSeconds, Action<Exception> onError, CancellationToken ct);

/// <summary>One kind's list and watch, over the client's generated calls, mapped as they arrive.</summary>
sealed class KindApi<T, TList>(
    ClusterKind kind,
    Func<CancellationToken, Task<TList>> list,
    WatchCall<T> watch,
    Func<T, ClusterObject> map) : IKindApi
    where T : IKubernetesObject<V1ObjectMeta>
    where TList : IKubernetesObject<V1ListMeta>, IItems<T>
{
    public ClusterKind Kind => kind;

    public async Task<(IReadOnlyList<ClusterObject> Items, string? ResourceVersion)> ListAsync(CancellationToken ct)
    {
        var result = await list(ct);
        return ([.. result.Items.Select(map)], result.Metadata?.ResourceVersion);
    }

    public async IAsyncEnumerable<WatchItem> WatchAsync(
        string? resourceVersion, bool sendInitialEvents, int timeoutSeconds, Action<WatchFailure> onError,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var events = watch(
            resourceVersion,
            sendInitialEvents ? true : null,

            // Required with sendInitialEvents: the initial state is "at least as new as now".
            sendInitialEvents ? "NotOlderThan" : null,
            timeoutSeconds,
            e => onError(Reflector.Failure(e)),
            ct);

        await foreach (var (type, item) in events.WithCancellation(ct))
        {
            var meta = item.Metadata;
            yield return type switch
            {
                WatchEventType.Bookmark => new WatchItem(
                    WatchEventKind.Bookmark, null, null, meta?.ResourceVersion,
                    meta?.Annotations?.TryGetValue("k8s.io/initial-events-end", out var end) == true && end == "true"),

                WatchEventType.Deleted => new WatchItem(WatchEventKind.Deleted, null, meta?.Uid, meta?.ResourceVersion),
                WatchEventType.Added => new WatchItem(WatchEventKind.Added, map(item), meta?.Uid, meta?.ResourceVersion),
                _ => new WatchItem(WatchEventKind.Modified, map(item), meta?.Uid, meta?.ResourceVersion),
            };
        }
    }
}

/// <summary>The kinds Dray watches, each wired to its calls. Cluster-wide: v1 connects to local clusters, where the user is the admin.</summary>
static class KindApis
{
    public static IKindApi For(IKubernetes k, ClusterKind kind) => kind switch
    {
        ClusterKind.Namespace => new KindApi<V1Namespace, V1NamespaceList>(kind,
            ct => k.CoreV1.ListNamespaceAsync(cancellationToken: ct),
            (rv, initial, match, timeout, onError, ct) => k.CoreV1.WatchListNamespaceAsync(allowWatchBookmarks: true, resourceVersion: rv, sendInitialEvents: initial, resourceVersionMatch: match, timeoutSeconds: timeout, onError: onError, cancellationToken: ct),
            KubeMapping.Namespace),

        ClusterKind.Node => new KindApi<V1Node, V1NodeList>(kind,
            ct => k.CoreV1.ListNodeAsync(cancellationToken: ct),
            (rv, initial, match, timeout, onError, ct) => k.CoreV1.WatchListNodeAsync(allowWatchBookmarks: true, resourceVersion: rv, sendInitialEvents: initial, resourceVersionMatch: match, timeoutSeconds: timeout, onError: onError, cancellationToken: ct),
            KubeMapping.Node),

        ClusterKind.Pod => new KindApi<V1Pod, V1PodList>(kind,
            ct => k.CoreV1.ListPodForAllNamespacesAsync(cancellationToken: ct),
            (rv, initial, match, timeout, onError, ct) => k.CoreV1.WatchListPodForAllNamespacesAsync(allowWatchBookmarks: true, resourceVersion: rv, sendInitialEvents: initial, resourceVersionMatch: match, timeoutSeconds: timeout, onError: onError, cancellationToken: ct),
            KubeMapping.Pod),

        ClusterKind.ReplicaSet => new KindApi<V1ReplicaSet, V1ReplicaSetList>(kind,
            ct => k.AppsV1.ListReplicaSetForAllNamespacesAsync(cancellationToken: ct),
            (rv, initial, match, timeout, onError, ct) => k.AppsV1.WatchListReplicaSetForAllNamespacesAsync(allowWatchBookmarks: true, resourceVersion: rv, sendInitialEvents: initial, resourceVersionMatch: match, timeoutSeconds: timeout, onError: onError, cancellationToken: ct),
            KubeMapping.ReplicaSet),

        ClusterKind.Deployment => new KindApi<V1Deployment, V1DeploymentList>(kind,
            ct => k.AppsV1.ListDeploymentForAllNamespacesAsync(cancellationToken: ct),
            (rv, initial, match, timeout, onError, ct) => k.AppsV1.WatchListDeploymentForAllNamespacesAsync(allowWatchBookmarks: true, resourceVersion: rv, sendInitialEvents: initial, resourceVersionMatch: match, timeoutSeconds: timeout, onError: onError, cancellationToken: ct),
            KubeMapping.Deployment),

        ClusterKind.StatefulSet => new KindApi<V1StatefulSet, V1StatefulSetList>(kind,
            ct => k.AppsV1.ListStatefulSetForAllNamespacesAsync(cancellationToken: ct),
            (rv, initial, match, timeout, onError, ct) => k.AppsV1.WatchListStatefulSetForAllNamespacesAsync(allowWatchBookmarks: true, resourceVersion: rv, sendInitialEvents: initial, resourceVersionMatch: match, timeoutSeconds: timeout, onError: onError, cancellationToken: ct),
            KubeMapping.StatefulSet),

        ClusterKind.DaemonSet => new KindApi<V1DaemonSet, V1DaemonSetList>(kind,
            ct => k.AppsV1.ListDaemonSetForAllNamespacesAsync(cancellationToken: ct),
            (rv, initial, match, timeout, onError, ct) => k.AppsV1.WatchListDaemonSetForAllNamespacesAsync(allowWatchBookmarks: true, resourceVersion: rv, sendInitialEvents: initial, resourceVersionMatch: match, timeoutSeconds: timeout, onError: onError, cancellationToken: ct),
            KubeMapping.DaemonSet),

        ClusterKind.Job => new KindApi<V1Job, V1JobList>(kind,
            ct => k.BatchV1.ListJobForAllNamespacesAsync(cancellationToken: ct),
            (rv, initial, match, timeout, onError, ct) => k.BatchV1.WatchListJobForAllNamespacesAsync(allowWatchBookmarks: true, resourceVersion: rv, sendInitialEvents: initial, resourceVersionMatch: match, timeoutSeconds: timeout, onError: onError, cancellationToken: ct),
            KubeMapping.Job),

        ClusterKind.Service => new KindApi<V1Service, V1ServiceList>(kind,
            ct => k.CoreV1.ListServiceForAllNamespacesAsync(cancellationToken: ct),
            (rv, initial, match, timeout, onError, ct) => k.CoreV1.WatchListServiceForAllNamespacesAsync(allowWatchBookmarks: true, resourceVersion: rv, sendInitialEvents: initial, resourceVersionMatch: match, timeoutSeconds: timeout, onError: onError, cancellationToken: ct),
            KubeMapping.Service),

        ClusterKind.Event => new KindApi<Corev1Event, Corev1EventList>(kind,
            ct => k.CoreV1.ListEventForAllNamespacesAsync(cancellationToken: ct),
            (rv, initial, match, timeout, onError, ct) => k.CoreV1.WatchListEventForAllNamespacesAsync(allowWatchBookmarks: true, resourceVersion: rv, sendInitialEvents: initial, resourceVersionMatch: match, timeoutSeconds: timeout, onError: onError, cancellationToken: ct),
            KubeMapping.Event),

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a watched kind."),
    };
}
