using Dray.Core.Model;
using Dray.Core.Shell;

namespace Dray.Core.Kubernetes;

/// <summary>Where each object's page is. One place, so a link and a palette entry cannot disagree.</summary>
public static class KubeRoutes
{
    public static string Pod(PodSummary pod) => $"/kubernetes/pods/{Uri.EscapeDataString(pod.Namespace)}/{Uri.EscapeDataString(pod.Name)}";

    public static string Workload(WorkloadSummary w)
        => $"/kubernetes/workloads/{w.Kind.ToString().ToLowerInvariant()}/{Uri.EscapeDataString(w.Namespace)}/{Uri.EscapeDataString(w.Name)}";

    public static string Workload(OwnerRef owner, string ns)
        => $"/kubernetes/workloads/{owner.Kind.ToString().ToLowerInvariant()}/{Uri.EscapeDataString(ns)}/{Uri.EscapeDataString(owner.Name)}";

    public static ClusterKind? KindFromRoute(string segment) => segment switch
    {
        "deployment" => ClusterKind.Deployment,
        "statefulset" => ClusterKind.StatefulSet,
        "daemonset" => ClusterKind.DaemonSet,
        "job" => ClusterKind.Job,
        _ => null,
    };
}

/// <summary>A workload's state: its worst pod's, since a Deployment with one crash-looping replica of three is not healthy.</summary>
public static class WorkloadStatus
{
    public static ContainerStatus Resolve(WorkloadSummary workload, IReadOnlyList<PodSummary> pods)
    {
        if (workload.Kind == ClusterKind.Job)
        {
            if (workload.Succeeded >= workload.Desired) return new(StateTone.Neutral, "■", "Completed");
            if (workload.Failed > 0 && pods.All(p => p.Phase == "Failed")) return new(StateTone.Danger, "■", "Failed", $"{workload.Failed} failed");
        }

        if (workload.Desired == 0 && pods.Count == 0) return new(StateTone.Neutral, "○", "Scaled to zero");

        var worst = pods
            .Select(PodStatusVocabulary.Resolve)
            .OrderBy(s => s.Tone switch { StateTone.Danger => 0, StateTone.Warn => 1, StateTone.Neutral => 2, _ => 3 })
            .FirstOrDefault();

        if (worst is { Tone: StateTone.Danger or StateTone.Warn }) return worst;
        if (workload.Ready < workload.Desired) return new(StateTone.Warn, "◐", "Starting", $"{workload.Ready} of {workload.Desired} ready");

        return worst ?? new(StateTone.Ok, "●", "Running");
    }

    public static string KindLabel(ClusterKind kind) => kind switch
    {
        ClusterKind.Deployment => "Deployment",
        ClusterKind.StatefulSet => "StatefulSet",
        ClusterKind.DaemonSet => "DaemonSet",
        ClusterKind.Job => "Job",
        _ => kind.ToString(),
    };
}

public static class PodGrid
{
    /// <summary>
    /// Five columns and the row's controls. A replica's name is its workload's plus two hashes, and
    /// the hashes are what tell two replicas apart, so the name gets the width to show them: the
    /// workload and the namespace ride underneath it rather than taking columns of their own. The
    /// node is on the pod's page — a local cluster usually has one.
    /// </summary>
    public static IReadOnlyList<GridColumn> Columns(bool manyNamespaces) =>
    [
        new("name", "Pod", GridCell.Link, Priority: 1, MinWidth: 280),

        // The question the page exists to answer, next to the name.
        new("state", "State", GridCell.State, Priority: 1, MinWidth: 200),
        new("ready", "Ready", GridCell.Text, Priority: 2, MinWidth: 72, Grow: false),
        new("restarts", "Restarts", GridCell.Text, Priority: 2, Numeric: true, MinWidth: 110, Grow: false),
        new("age", "Age", GridCell.Since, Priority: 4, Numeric: true, MinWidth: 72, Grow: false),
        new("actions", "", GridCell.Actions, Priority: 0, Sortable: false, MinWidth: 104),
    ];

    public static IReadOnlyDictionary<string, object?> Row(PodSummary pod, OwnerRef? owner, DateTimeOffset? now = null, bool showNamespace = true)
    {
        var last = pod.Containers
            .Select(c => c.LastFinishedAt)
            .Where(t => t is not null)
            .Max();

        var under = string.Join(" · ", new[] { owner?.Name, showNamespace ? pod.Namespace : null }.Where(s => !string.IsNullOrEmpty(s)));

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = new GridLink(pod.Name, KubeRoutes.Pod(pod), under.Length > 0 ? under : null),
            ["state"] = GridState.From(PodStatusVocabulary.Resolve(pod)),
            ["ready"] = $"{pod.ReadyCount}/{pod.Containers.Count}",

            // When it last restarted is what makes a count mean something: four restarts last
            // month is a different pod from four in the last minute.
            ["restarts"] = GridValue.Number(pod.Restarts, pod.Restarts == 0
                ? "0"
                : last is { } at ? $"{pod.Restarts} · {Humanize.Since(at, now)}" : pod.Restarts.ToString()),
            ["age"] = GridValue.When(pod.Created, pod.Created is { } c ? Humanize.Since(c, now) : "—"),
        };
    }
}

public static class WorkloadGrid
{
    public static IReadOnlyList<GridColumn> Columns(bool manyNamespaces) =>
    [
        new("name", "Workload", GridCell.Link, Priority: 1, MinWidth: 220),
        new("state", "State", GridCell.State, Priority: 1, MinWidth: 200),
        new("ready", "Ready", GridCell.Text, Priority: 2, MinWidth: 96, Grow: false),
        new("images", "Images", GridCell.Mono, Priority: 4, MinWidth: 140),
        new("age", "Age", GridCell.Since, Priority: 4, Numeric: true, MinWidth: 72, Grow: false),
        new("actions", "", GridCell.Actions, Priority: 0, Sortable: false, MinWidth: 104),
    ];

    /// <summary>What kind of workload, and where, under its name: two facts read with the name, not instead of it.</summary>
    public static IReadOnlyDictionary<string, object?> Row(WorkloadSummary w, IReadOnlyList<PodSummary> pods, DateTimeOffset? now = null, bool showNamespace = true)
        => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = new GridLink(w.Name, KubeRoutes.Workload(w), showNamespace ? $"{WorkloadStatus.KindLabel(w.Kind)} · {w.Namespace}" : WorkloadStatus.KindLabel(w.Kind)),
            ["state"] = GridState.From(WorkloadStatus.Resolve(w, pods)),
            ["ready"] = w.Kind == ClusterKind.Job ? $"{w.Succeeded}/{w.Desired} done" : $"{w.Ready}/{w.Desired}",
            ["images"] = string.Join(", ", w.Images),
            ["age"] = GridValue.When(w.Created, w.Created is { } c ? Humanize.Since(c, now) : "—"),
        };
}

public static class ServiceGrid
{
    public static IReadOnlyList<GridColumn> Columns(bool manyNamespaces) =>
    [
        new("name", "Service", GridCell.Text, Priority: 1, MinWidth: 180),
        new("ports", "Ports", GridCell.Mono, Priority: 1, MinWidth: 150, Grow: false),

        // "How do I reach it from here" — the reason anyone opens this page on a laptop.
        new("reach", "Reach it at", GridCell.Text, Priority: 1, MinWidth: 160),
        new("type", "Type", GridCell.Muted, Priority: 3, MinWidth: 110, Grow: false),
        .. manyNamespaces ? new[] { new GridColumn("namespace", "Namespace", GridCell.Muted, Priority: 3) } : [],
        new("ip", "Cluster IP", GridCell.Mono, Priority: 4, MinWidth: 130, Grow: false),
        new("pods", "Pods", GridCell.Text, Priority: 3, MinWidth: 80, Grow: false),
        new("actions", "", GridCell.Actions, Priority: 0, Sortable: false, MinWidth: 104),
    ];

    public static IReadOnlyDictionary<string, object?> Row(ServiceSummary s, int pods, IReadOnlyList<IPortForward> forwards)
        => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = s.Name,
            ["ports"] = s.Ports.Count == 0 ? "—" : string.Join(", ", s.Ports.Select(Port)),
            ["reach"] = Reach(s, forwards),
            ["type"] = s.Type,
            ["namespace"] = s.Namespace,
            ["ip"] = s.ClusterIp ?? "—",

            // No selector means someone else manages its endpoints; "0 pods" would be a claim.
            ["pods"] = s.Selector.Count == 0 ? "—" : pods == 1 ? "1 pod" : $"{pods} pods",
        };

    static string Port(ServicePort p) => p.TargetPort == p.Port.ToString() ? $"{p.Port}/{p.Protocol}" : $"{p.Port}→{p.TargetPort}/{p.Protocol}";

    /// <summary>
    /// A forward when there is one; a load balancer's address when it has one; otherwise how to make
    /// one. A NodePort is not claimed to be on localhost: whether it is depends on the engine, and
    /// kind does not publish them without extra port mappings (§5.4).
    /// </summary>
    static string Reach(ServiceSummary s, IReadOnlyList<IPortForward> forwards)
    {
        var mine = forwards.Where(f => f.Namespace == s.Namespace && f.Failure is null).ToList();
        if (mine.Count > 0) return string.Join(", ", mine.Select(f => $"localhost:{f.LocalPort}"));
        if (s.Ingress.Count > 0) return string.Join(", ", s.Ingress);
        return s.Type == "ExternalName" ? "—" : "Forward a port";
    }
}

public static class EventGrid
{
    public static IReadOnlyList<GridColumn> Columns() =>
    [
        new("type", "", GridCell.State, Priority: 1, MinWidth: 112, Grow: false),
        new("object", "Object", GridCell.Text, Priority: 1, MinWidth: 220),
        new("reason", "Reason", GridCell.Text, Priority: 2, MinWidth: 150, Grow: false),

        // The cluster's sentence is the point of the page, so it takes the width that is left.
        new("message", "Message", GridCell.Muted, Priority: 1, MinWidth: 260),
        new("count", "Count", GridCell.Text, Priority: 4, Numeric: true, MinWidth: 72, Grow: false),
        new("seen", "Last seen", GridCell.Since, Priority: 2, Numeric: true, MinWidth: 96, Grow: false),
    ];

    /// <summary>Warnings are what the page is for, so they get the warning tint; Normal is neutral, never green.</summary>
    public static ContainerStatus Status(ClusterEvent e) => e.IsWarning
        ? new(StateTone.Warn, "▲", "Warning")
        : new(StateTone.Neutral, "●", "Normal");

    public static IReadOnlyDictionary<string, object?> Row(ClusterEvent e, DateTimeOffset? now = null)
        => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = GridState.From(Status(e)),
            ["object"] = $"{e.InvolvedKind?.ToString() ?? "Object"} {e.InvolvedName}",
            ["reason"] = e.Reason,
            ["message"] = e.Message,
            ["count"] = GridValue.Number(e.Count, e.Count.ToString()),
            ["seen"] = GridValue.When(e.LastSeen, e.LastSeen is { } t ? Humanize.Since(t, now) : "—"),
        };
}
