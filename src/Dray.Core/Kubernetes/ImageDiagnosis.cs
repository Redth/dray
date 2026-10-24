namespace Dray.Core.Kubernetes;

/// <summary>Which local-cluster tool made a cluster, read off its nodes — not off the context's name.</summary>
public enum ClusterTool
{
    Unknown,
    Kind,
    K3d,
    Minikube,
}

/// <summary>Why a pod could not pull its image, as far as Dray can see from here.</summary>
public enum PullCause
{
    /// <summary>Nothing more is known than the runtime's own sentence.</summary>
    Unknown,

    /// <summary>The image is on the engine, and the cluster has a store of its own that it was never loaded into.</summary>
    NotLoaded,

    /// <summary>The image is not on this engine either: a registry problem, a typo, or a missing login.</summary>
    NotHereEither,
}

/// <param name="Command">The command that fixes it, shown before it is run (docs/KUBERNETES.md §8).</param>
public sealed record PullDiagnosis(PullCause Cause, string Image, string? Command = null);

/// <summary>
/// The local-cluster failure Dray can answer precisely, because it sees the engine as well as the
/// cluster: <c>ImagePullBackOff</c> on an image built here (docs/KUBERNETES.md §8).
/// <para>
/// Measured end to end on kind: the pull error names <c>docker.io/library/myapp:dev</c> where Docker
/// says <c>myapp:dev</c>, so references are compared normalised; digests do not match across the
/// two stores, so nothing is claimed about whether a build is the latest one; and a node's
/// <c>status.images</c> lags a load by about fifty seconds, so it is not used as proof of anything.
/// </para>
/// </summary>
public static class ImageDiagnosis
{
    /// <summary>
    /// The tool, from a node's <c>spec.providerID</c> — measured for kind as
    /// <c>kind://docker/dray-spike/dray-spike-control-plane</c>. A name, from the same place.
    /// </summary>
    public static (ClusterTool Tool, string? Cluster) Identify(IReadOnlyList<NodeSummary> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.ProviderId is not { } id) continue;

            if (id.StartsWith("kind://", StringComparison.Ordinal))
            {
                // kind://<provider>/<cluster>/<node>
                var parts = id["kind://".Length..].Split('/');
                return (ClusterTool.Kind, parts.Length >= 2 ? parts[1] : null);
            }

            if (id.StartsWith("k3s://", StringComparison.Ordinal) && node.Name.StartsWith("k3d-", StringComparison.Ordinal))
            {
                // k3d names its nodes k3d-<cluster>-server-0 / -agent-N.
                var rest = node.Name["k3d-".Length..];
                var cut = rest.LastIndexOf("-server-", StringComparison.Ordinal) is var s and >= 0 ? s : rest.LastIndexOf("-agent-", StringComparison.Ordinal);
                return (ClusterTool.K3d, cut > 0 ? rest[..cut] : null);
            }
        }

        // minikube sets no providerID; its node is called minikube, or <profile>-m02 and so on.
        return nodes.Count > 0 && nodes.All(n => n.Name == "minikube" || n.Name.StartsWith("minikube-", StringComparison.Ordinal))
            ? (ClusterTool.Minikube, "minikube")
            : (ClusterTool.Unknown, null);
    }

    public static PullDiagnosis Diagnose(string image, IReadOnlyList<NodeSummary> nodes, IEnumerable<string> engineImages)
    {
        var wanted = Normalize(image);
        var onEngine = engineImages.Any(i => Normalize(i) == wanted);
        if (!onEngine) return new PullDiagnosis(PullCause.NotHereEither, image);

        var (tool, cluster) = Identify(nodes);
        var command = tool switch
        {
            ClusterTool.Kind => $"kind load docker-image {image}{(cluster is null or "kind" ? "" : $" --name {cluster}")}",
            ClusterTool.K3d => $"k3d image import {image}{(cluster is null ? "" : $" --cluster {cluster}")}",
            ClusterTool.Minikube => $"minikube image load {image}",
            _ => null,
        };

        return new PullDiagnosis(command is null ? PullCause.Unknown : PullCause.NotLoaded, image, command);
    }

    /// <summary>
    /// A reference as a runtime spells it in full: <c>myapp:dev</c> is
    /// <c>docker.io/library/myapp:dev</c>, <c>user/app</c> is <c>docker.io/user/app:latest</c>. A
    /// first component with a dot or a port, or <c>localhost</c>, is a registry and is kept.
    /// </summary>
    public static string Normalize(string reference)
    {
        var name = reference.Trim();
        string? digest = null;

        var at = name.IndexOf('@');
        if (at >= 0)
        {
            digest = name[at..];
            name = name[..at];
        }

        var slash = name.IndexOf('/');
        var first = slash < 0 ? null : name[..slash];
        var hasRegistry = first is not null && (first.Contains('.') || first.Contains(':') || first == "localhost");

        if (!hasRegistry) name = slash < 0 ? $"docker.io/library/{name}" : $"docker.io/{name}";

        // A tag is a colon after the last slash; one before it is a registry's port.
        var lastSlash = name.LastIndexOf('/');
        if (digest is null && name.IndexOf(':', lastSlash + 1) < 0) name += ":latest";

        return (name + digest).ToLowerInvariant();
    }
}
