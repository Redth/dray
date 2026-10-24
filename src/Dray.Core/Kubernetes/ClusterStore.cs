namespace Dray.Core.Kubernetes;

/// <summary>
/// The selected cluster's objects, kept current from its watches. The Kubernetes counterpart of
/// <c>EntityStore</c>, and separate from it: that store is container-shaped and stays that way
/// (docs/KUBERNETES.md §9.1).
/// <para>
/// Nothing writes here except a watch. A relist is diffed against what is held — only objects that
/// render differently are replaced, and only vanished ones removed — so a 410 relist that finds
/// nothing new changes nothing and repaints nothing.
/// </para>
/// </summary>
public sealed class ClusterStore
{
    readonly Lock _gate = new();
    readonly Dictionary<ClusterKind, Dictionary<string, ClusterObject>> _byKind = [];
    readonly HashSet<ClusterKind> _synced = [];

    /// <summary>Raised after a change that altered what is held, with the kind it altered.</summary>
    public event Action<ClusterKind>? Changed;

    /// <summary>Every write that changed something. Tests assert on it; a relist of unchanged data adds zero.</summary>
    public int Writes { get; private set; }

    /// <summary>Whether a kind has had its first full state, so an empty list means "none" rather than "not yet".</summary>
    public bool IsSynced(ClusterKind kind)
    {
        lock (_gate) return _synced.Contains(kind);
    }

    public bool Apply(ClusterChange change)
    {
        bool changed;
        lock (_gate)
        {
            changed = change switch
            {
                ClusterSynced synced => Sync(synced),
                ClusterUpserted upserted => Upsert(upserted.Item),
                ClusterDeleted deleted => Bucket(deleted.Kind).Remove(deleted.Uid),
                _ => false,
            };

            if (changed) Writes++;
        }

        if (changed) Changed?.Invoke(change.Kind);
        return changed;
    }

    /// <summary>Drop everything: the cluster changed, or went away.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _byKind.Clear();
            _synced.Clear();
        }

        foreach (var kind in Enum.GetValues<ClusterKind>()) Changed?.Invoke(kind);
    }

    bool Sync(ClusterSynced synced)
    {
        var bucket = Bucket(synced.Kind);
        var incoming = synced.Items.ToDictionary(i => i.Uid, StringComparer.Ordinal);
        var changed = _synced.Add(synced.Kind);

        foreach (var uid in bucket.Keys.Where(k => !incoming.ContainsKey(k)).ToList())
        {
            bucket.Remove(uid);
            changed = true;
        }

        foreach (var item in incoming.Values) changed |= Upsert(item);
        return changed;
    }

    bool Upsert(ClusterObject item)
    {
        var bucket = Bucket(item.Kind);
        if (bucket.TryGetValue(item.Uid, out var held) && held.SameAs(item)) return false;

        bucket[item.Uid] = item;
        return true;
    }

    Dictionary<string, ClusterObject> Bucket(ClusterKind kind)
    {
        if (!_byKind.TryGetValue(kind, out var bucket)) _byKind[kind] = bucket = new(StringComparer.Ordinal);
        return bucket;
    }

    IReadOnlyList<T> All<T>(ClusterKind kind) where T : ClusterObject
    {
        lock (_gate)
        {
            return _byKind.TryGetValue(kind, out var bucket) ? [.. bucket.Values.OfType<T>()] : [];
        }
    }

    public IReadOnlyList<PodSummary> Pods => All<PodSummary>(ClusterKind.Pod);

    public IReadOnlyList<ServiceSummary> Services => All<ServiceSummary>(ClusterKind.Service);

    public IReadOnlyList<ClusterEvent> Events => All<ClusterEvent>(ClusterKind.Event);

    public IReadOnlyList<NodeSummary> Nodes => All<NodeSummary>(ClusterKind.Node);

    public IReadOnlyList<NamespaceSummary> Namespaces => All<NamespaceSummary>(ClusterKind.Namespace);

    public IReadOnlyList<WorkloadSummary> Workloads =>
    [
        .. All<WorkloadSummary>(ClusterKind.Deployment),
        .. All<WorkloadSummary>(ClusterKind.StatefulSet),
        .. All<WorkloadSummary>(ClusterKind.DaemonSet),
        .. All<WorkloadSummary>(ClusterKind.Job),
    ];

    public T? Find<T>(ClusterKind kind, string uid) where T : ClusterObject
    {
        lock (_gate)
        {
            return _byKind.TryGetValue(kind, out var bucket) && bucket.TryGetValue(uid, out var item) ? item as T : null;
        }
    }

    public PodSummary? FindPod(string ns, string name) => Pods.FirstOrDefault(p => p.Namespace == ns && p.Name == name);

    public WorkloadSummary? FindWorkload(ClusterKind kind, string ns, string name)
        => All<WorkloadSummary>(kind).FirstOrDefault(w => w.Namespace == ns && w.Name == name);

    /// <summary>
    /// The workload a pod belongs to, as the user thinks of it. A Deployment's pods are owned by a
    /// ReplicaSet the user never made, so the chain is followed one step further — by the
    /// ReplicaSet's own owner reference, which the cluster reports, rather than by trimming the
    /// pod-template hash off its name.
    /// </summary>
    public OwnerRef? TopOwner(PodSummary pod)
    {
        if (pod.Owner is not { } owner) return null;
        if (owner.Kind != ClusterKind.ReplicaSet) return owner;

        return Find<ReplicaSetSummary>(ClusterKind.ReplicaSet, owner.Uid)?.Owner ?? owner;
    }

    public IReadOnlyList<PodSummary> PodsOf(WorkloadSummary workload)
        => [.. Pods.Where(p => TopOwner(p)?.Uid == workload.Uid)];

    /// <summary>What the cluster has said about one object: warnings first, then newest — the warning is the why.</summary>
    public IReadOnlyList<ClusterEvent> EventsFor(ClusterObject item)
        => [.. Events.Where(e => e.InvolvedUid == item.Uid || (e.InvolvedKind == item.Kind && e.InvolvedName == item.Name && e.Namespace == item.Namespace))
                .OrderByDescending(e => e.IsWarning)
                .ThenByDescending(e => e.LastSeen)];

    /// <summary>The pods a service sends traffic to: every label in its selector, matched.</summary>
    public IReadOnlyList<PodSummary> PodsFor(ServiceSummary service)
        => service.Selector.Count == 0
            ? []
            : [.. Pods.Where(p => p.Namespace == service.Namespace && service.Selector.All(kv => p.Labels.TryGetValue(kv.Key, out var v) && v == kv.Value))];
}
