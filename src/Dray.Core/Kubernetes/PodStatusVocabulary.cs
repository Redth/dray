using Dray.Core.Model;

namespace Dray.Core.Kubernetes;

/// <summary>
/// A pod's state as a pill: tint + glyph + word, greyscale-legible (DESIGN.md §2.4, docs/KUBERNETES.md
/// §5.1). The same <see cref="ContainerStatus"/> a container's pill is, so a pod and a container in
/// the same state look the same.
/// </summary>
public static class PodStatusVocabulary
{
    public static ContainerStatus Resolve(PodSummary pod)
    {
        if (pod.IsTerminating) return new(StateTone.Warn, "◌", "Terminating");

        // NodeLost: the kubelet stopped reporting. Nothing on the row is current, which is what the
        // dimmed "Unreachable" treatment says for a container on a host that has gone.
        if (pod.Reason == "NodeLost" || pod.Phase == "Unknown") return new(StateTone.Neutral, "⚠", "Node lost");

        if (pod.Reason == "Evicted") return new(StateTone.Danger, "✕", "Evicted", pod.Message);

        // An init container that has not finished holds the pod at the door, and is the reason.
        foreach (var init in pod.InitContainers)
        {
            if (init.State == "terminated" && init.ExitCode == 0) continue;
            if (PullFailure(init) is { } pull) return pull;
            if (CrashLooping(init)) return CrashLoop(init);

            return new(StateTone.Warn, "◐", "Starting", $"init container {init.Name}");
        }

        // Worst container decides. A pull failure first: nothing else can be true of a container
        // whose image never arrived.
        foreach (var c in pod.Containers)
        {
            if (PullFailure(c) is { } pull) return pull;
        }

        foreach (var c in pod.Containers)
        {
            if (CrashLooping(c)) return CrashLoop(c);
        }

        // A container the kubelet cannot start for a reason of its own — a missing ConfigMap, a
        // bad securityContext. Named verbatim: it is the sentence the user needs.
        foreach (var c in pod.Containers)
        {
            if (c.State == "waiting" && c.Reason is { } reason && reason is not ("ContainerCreating" or "PodInitializing"))
                return new(StateTone.Danger, "✕", "Can't start", c.Message ?? reason);
        }

        switch (pod.Phase)
        {
            case "Succeeded":
                return new(StateTone.Neutral, "■", "Completed");

            case "Failed":
            {
                var failed = pod.Containers.FirstOrDefault(c => c.ExitCode is not null and not 0);
                return failed is { ExitCode: { } code }
                    ? new(StateTone.Danger, "■", $"Exited {code}", Explain(failed.Reason, code))
                    : new(StateTone.Danger, "■", "Failed", pod.Message ?? pod.Reason);
            }

            case "Pending" when pod.Node is null:
                // Unscheduled. The scheduler's own reason — "0/1 nodes are available: insufficient
                // memory" — is the answer, so it is the detail.
                return new(StateTone.Neutral, "○", "Pending", pod.UnschedulableMessage);

            case "Pending":
                return new(StateTone.Warn, "◐", "Starting");
        }

        if (pod.Containers.Count > 0 && pod.Containers.All(c => c.Ready) && pod.ConditionReady)
            return new(StateTone.Ok, "●", "Running");

        if (pod.Containers.Any(c => c.State is null or "waiting"))
            return new(StateTone.Warn, "◐", "Starting");

        return new(StateTone.Warn, "◑", $"Not ready {pod.ReadyCount}/{pod.Containers.Count}");
    }

    /// <summary>
    /// A container restarting after failing, in whichever half of the cycle it is caught.
    /// <para>
    /// kubectl's STATUS alternates — measured on the spike's pods within one minute: <c>Error</c> or
    /// <c>OOMKilled</c> in the moment after each exit, <c>CrashLoopBackOff</c> during the back-off.
    /// Following it would flip a pill between three states on every restart and fire the change
    /// highlight each time, so a crash loop is one steady state here, with the last reason as its
    /// detail. The one deliberate departure from kubectl's column (docs/KUBERNETES.md §5.1).
    /// </para>
    /// </summary>
    static bool CrashLooping(PodContainer c)
    {
        if (c.State == "waiting" && c.Reason == "CrashLoopBackOff") return true;
        if (c.RestartCount == 0) return false;

        // Between the exit and the back-off.
        if (c.State == "terminated" && (c.ExitCode is not null and not 0 || c.Reason is "Error" or "OOMKilled")) return true;

        // Restarted, running again, not yet ready, and the last run failed: still in the loop.
        return c.State == "running" && !c.Ready && (c.LastExitCode is not null and not 0);
    }

    static ContainerStatus CrashLoop(PodContainer c)
    {
        var reason = c.State == "terminated" ? c.Reason : c.LastReason;
        var code = c.State == "terminated" ? c.ExitCode : c.LastExitCode;

        return new(StateTone.Danger, "↯", "Crash loop", code is { } exit ? $"last exit: {Explain(reason, exit) ?? $"code {exit}"}" : null);
    }

    static ContainerStatus? PullFailure(PodContainer c)
        => c.State == "waiting" && c.Reason is "ImagePullBackOff" or "ErrImagePull" or "InvalidImageName" or "ErrImageNeverPull"
            ? new ContainerStatus(StateTone.Danger, "⤓", "Can't pull image", c.Image)
            : null;

    /// <summary>
    /// Kubernetes names the reason, so the reason is used: <c>OOMKilled</c> is "out of memory"
    /// because the cluster said so, not because 137 usually means it.
    /// </summary>
    static string? Explain(string? reason, int code) => reason switch
    {
        "OOMKilled" => "out of memory",
        "Completed" => null,
        _ => ContainerStatusVocabulary.ExplainExitCode(code) is { } words ? $"{code} · {words}" : null,
    };

    /// <summary>
    /// The STATUS column of <c>kubectl get pods</c>, for parity with what users cross-check against
    /// (docs/KUBERNETES.md §5.1). A port of <c>printPod</c> as far as the fields Dray keeps allow.
    /// Dray's pill departs from it only for crash loops; this exists so a test can say so precisely.
    /// </summary>
    public static string KubectlStatus(PodSummary pod)
    {
        var reason = string.IsNullOrEmpty(pod.Reason) ? pod.Phase : pod.Reason;

        var initializing = false;
        for (var i = 0; i < pod.InitContainers.Count; i++)
        {
            var c = pod.InitContainers[i];
            if (c.State == "terminated" && c.ExitCode == 0) continue;

            if (c.State == "terminated")
                reason = !string.IsNullOrEmpty(c.Reason) ? "Init:" + c.Reason : $"Init:ExitCode:{c.ExitCode}";
            else if (c.State == "waiting" && !string.IsNullOrEmpty(c.Reason) && c.Reason != "PodInitializing")
                reason = "Init:" + c.Reason;
            else
                reason = $"Init:{i}/{pod.InitContainers.Count}";

            initializing = true;
            break;
        }

        if (!initializing)
        {
            var hasRunning = false;
            foreach (var c in pod.Containers.Reverse())
            {
                if (c.State == "waiting" && !string.IsNullOrEmpty(c.Reason)) reason = c.Reason;
                else if (c.State == "terminated") reason = !string.IsNullOrEmpty(c.Reason) ? c.Reason : $"ExitCode:{c.ExitCode}";
                else if (c.Ready && c.State == "running") hasRunning = true;
            }

            if (reason == "Completed" && hasRunning) reason = pod.ConditionReady ? "Running" : "NotReady";
        }

        if (pod.IsTerminating) reason = pod.Reason == "NodeLost" ? "Unknown" : "Terminating";
        return reason;
    }
}
