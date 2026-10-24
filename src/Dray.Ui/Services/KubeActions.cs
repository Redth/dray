using Dray.Core.Kubernetes;
using Dray.Core.Shell;
using Microsoft.AspNetCore.Components;

namespace Dray.Ui.Services;

/// <summary>What an action did, for the page's banner. Null from an action means it was cancelled or needs no word.</summary>
public sealed record KubeOutcome(string Message, bool Ok, string? OpenUrl = null);

/// <summary>
/// What can be done to pods and workloads, and what each needs to ask first — one place, so the list
/// row, the detail page and the palette offer the same actions under the same rules.
/// <para>
/// Destructive, by what is lost (docs/KUBERNETES.md §5.5): deleting a pod a controller owns is a
/// restart in practice and gets the ordinary confirmation; deleting a bare pod, a workload, or
/// scaling to zero destroys something nothing will recreate, and is typed.
/// </para>
/// </summary>
public sealed class KubeActions(ClusterManager clusters, IShellBridge shell, NavigationManager nav)
{
    ClusterCapabilities Caps => clusters.Selected?.Capabilities ?? ClusterCapabilities.None;

    public IReadOnlyList<RowAction> ForPod(PodSummary pod) =>
    [
        new("logs", "Logs", IconRef.Logs),
        .. Caps.CanExec && pod.Containers.Any(c => c.State == "running") ? new[] { new RowAction("shell", "Shell", IconRef.Terminal) } : [],
        .. Caps.CanPortForward && pod.Phase == "Running"
            ? pod.Containers.SelectMany(c => c.Ports).Distinct().Select(p => new RowAction($"forward:{p}", $"Forward port {p}", IconRef.Port))
            : [],
        .. Caps.CanDeletePods && !pod.IsTerminating ? new[] { new RowAction("delete", "Delete", IconRef.Trash, Danger: true) } : [],
    ];

    public IReadOnlyList<RowAction> ForWorkload(WorkloadSummary w) =>
    [
        .. Caps.CanRestart && w.CanRestart ? new[] { new RowAction("restart", "Restart", IconRef.Restart, "Roll every pod, as kubectl rollout restart does") } : [],
        .. Caps.CanDeletePods ? new[] { new RowAction("delete", "Delete", IconRef.Trash, Danger: true) } : [],
    ];

    public IReadOnlyList<RowAction> ForService(ServiceSummary s) =>
        Caps.CanPortForward && s.Type != "ExternalName" && s.Selector.Count > 0
            ? [.. s.Ports.Select(p => new RowAction($"forward:{p.Port}", $"Forward port {p.Port}", IconRef.Port))]
            : [];

    public async Task<KubeOutcome?> RunAsync(PodSummary pod, string action)
    {
        switch (action)
        {
            case "logs":
                nav.NavigateTo(KubeRoutes.Pod(pod) + "?tab=Logs");
                return null;

            case "shell":
                nav.NavigateTo(KubeRoutes.Pod(pod) + "?tab=Terminal");
                return null;

            case "delete":
                return await DeletePodAsync(pod);

            case var forward when forward.StartsWith("forward:", StringComparison.Ordinal)
                                  && int.TryParse(forward["forward:".Length..], out var port):
                return await ForwardAsync(pod, port);
        }

        return null;
    }

    public async Task<KubeOutcome?> RunAsync(WorkloadSummary w, string action) => action switch
    {
        "restart" => await RestartAsync(w),
        "delete" => await DeleteWorkloadAsync(w),
        _ => null,
    };

    public async Task<KubeOutcome?> RunAsync(ServiceSummary s, string action)
    {
        if (!action.StartsWith("forward:", StringComparison.Ordinal) || !int.TryParse(action["forward:".Length..], out var servicePort)) return null;

        // A forward goes to a pod: the Service is a name for a set of them. A ready one, so the
        // forward reaches something that answers.
        var port = s.Ports.FirstOrDefault(p => p.Port == servicePort);
        var pods = clusters.Store.PodsFor(s);
        var pod = pods.FirstOrDefault(p => p.ConditionReady) ?? pods.FirstOrDefault(p => p.Phase == "Running");
        if (pod is null || port is null) return new KubeOutcome($"{s.Name} has no running pod to forward to.", Ok: false);

        // targetPort is a number or the name of a container port.
        var target = int.TryParse(port.TargetPort, out var number)
            ? number
            : pod.Containers.Select(c => c.NamedPorts.GetValueOrDefault(port.TargetPort)).FirstOrDefault(p => p > 0);

        return target > 0
            ? await ForwardAsync(pod, target)
            : new KubeOutcome($"No container in {pod.Name} has a port named {port.TargetPort}.", Ok: false);
    }

    public async Task<KubeOutcome> ForwardAsync(PodSummary pod, int port)
    {
        var (forward, problem) = await clusters.ForwardAsync(pod, port);
        if (forward is null) return new KubeOutcome(problem ?? "The forward could not start.", Ok: false);

        // Which port, always: the pod's own when it was free, the next free one when it was not.
        var url = $"http://localhost:{forward.LocalPort}";
        var moved = forward.LocalPort == port ? "" : $" ({port} was in use here)";
        return new KubeOutcome($"Forwarding localhost:{forward.LocalPort} to {pod.Name}:{port}{moved}.", Ok: true, url);
    }

    async Task<KubeOutcome?> DeletePodAsync(PodSummary pod)
    {
        var owner = clusters.Store.TopOwner(pod);

        var answer = await shell.ConfirmDestructiveAsync(owner is not null
            ? new DestructiveConfirm(
                $"Delete {pod.Name}?",
                $"{owner.Name} will start a replacement, so in practice this restarts the pod. Its logs go with it.",
                "Delete")
            : new DestructiveConfirm(
                $"Delete {pod.Name}?",
                "Nothing owns this pod, so nothing will recreate it. Its logs and anything it kept outside a volume are lost.",
                "Delete",
                TypeToConfirm: pod.Name));

        if (answer != ConfirmResult.Confirm) return null;

        return await clusters.DeleteAsync(pod) is { } failure
            ? new KubeOutcome(failure, Ok: false)
            : new KubeOutcome($"Deleting {pod.Name}.", Ok: true);
    }

    async Task<KubeOutcome?> DeleteWorkloadAsync(WorkloadSummary w)
    {
        var pods = clusters.Store.PodsOf(w).Count;
        var answer = await shell.ConfirmDestructiveAsync(new DestructiveConfirm(
            $"Delete {w.Name}?",
            $"The {WorkloadStatus.KindLabel(w.Kind)} and its {pods} pod{(pods == 1 ? "" : "s")} are deleted, and nothing recreates them. Re-apply its manifest to bring it back.",
            "Delete",
            TypeToConfirm: w.Name));

        if (answer != ConfirmResult.Confirm) return null;

        return await clusters.DeleteAsync(w) is { } failure
            ? new KubeOutcome(failure, Ok: false)
            : new KubeOutcome($"Deleting {w.Name}.", Ok: true);
    }

    async Task<KubeOutcome?> RestartAsync(WorkloadSummary w)
        => await clusters.RestartAsync(w) is { } failure
            ? new KubeOutcome(failure, Ok: false)
            : new KubeOutcome($"Restarting {w.Name}: its pods are replaced one by one.", Ok: true);

    /// <summary>
    /// Scale, asking first only for zero — the one count that stops everything, and the one that is
    /// typed. Any other count is a stepper's ordinary click.
    /// </summary>
    public async Task<KubeOutcome?> ScaleAsync(WorkloadSummary w, int replicas)
    {
        if (replicas < 0) return null;

        if (replicas == 0)
        {
            var answer = await shell.ConfirmDestructiveAsync(new DestructiveConfirm(
                $"Scale {w.Name} to zero?",
                "Every pod stops, and the workload serves nothing until it is scaled up again.",
                "Scale to zero",
                TypeToConfirm: w.Name));

            if (answer != ConfirmResult.Confirm) return null;
        }

        return await clusters.ScaleAsync(w, replicas) is { } failure ? new KubeOutcome(failure, Ok: false) : null;
    }
}
