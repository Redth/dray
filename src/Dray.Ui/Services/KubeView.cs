using Dray.Core.Kubernetes;
using Dray.Core.Shell;

namespace Dray.Ui.Services;

/// <summary>
/// What the Kubernetes pages are looking at: which cluster, and which namespaces. Shared, so moving
/// from Pods to Workloads keeps the namespace the user chose.
/// <para>
/// The cluster picker is a menu in these pages' toolbar rather than a second control in the window's
/// corner: the engine scopes every page, but a cluster scopes only these four, and the corner above
/// the macOS sidebar has room for one control, not two (docs/KUBERNETES.md §1). The namespace is a
/// filter, never a mode — no page goes blank because the wrong one is selected (§5).
/// </para>
/// </summary>
public sealed class KubeView(ClusterManager clusters)
{
    const string AllId = "*";
    const string AllWithSystemId = "**";

    /// <summary>One namespace, or null for all of them.</summary>
    public string? Namespace { get; private set; }

    /// <summary>
    /// Whether "all" includes <c>kube-*</c>. Off by default: the system's own pods are four or five
    /// rows of noise to someone looking for theirs. The prefix is reserved by Kubernetes itself, so
    /// hiding it is a rule, not a guess.
    /// </summary>
    public bool ShowSystem { get; private set; }

    public event Action? Changed;

    public bool Matches(string ns)
        => Namespace is { } chosen ? ns == chosen : ShowSystem || !ns.StartsWith("kube-", StringComparison.Ordinal);

    /// <summary>Whether rows from more than one namespace can be on screen, so a Namespace column earns its width.</summary>
    public bool ManyNamespaces => Namespace is null;

    public ChromeFilter ClusterFilter
    {
        get
        {
            var local = clusters.LocalContexts;
            var selected = Math.Max(0, local.ToList().FindIndex(c => c.Name == clusters.Selected?.Name));
            return new ChromeFilter("cluster", "Cluster", [.. local.Select(c => new ChromeFilterOption(c.Name, IconRef.Cluster))], selected);
        }
    }

    public ChromeFilter NamespaceFilter
    {
        get
        {
            var options = NamespaceIds();
            var index = Namespace is { } ns ? options.IndexOf(ns) : ShowSystem ? 1 : 0;
            return new ChromeFilter("namespace", "Namespace", [.. options.Select(o => new ChromeFilterOption(Label(o)))], Math.Max(0, index));
        }
    }

    /// <summary>Both filters, for a page whose cluster is connected — and only the cluster's when it is not.</summary>
    public IReadOnlyList<ChromeFilter> Filters => clusters.Selected is { State: Core.Engine.HostConnectionState.Connected }
        ? [ClusterFilter, NamespaceFilter]
        : clusters.LocalContexts.Count > 1 ? [ClusterFilter] : [];

    List<string> NamespaceIds() =>
    [
        AllId,
        AllWithSystemId,
        .. clusters.Store.Namespaces
            .OrderBy(n => n.IsSystem)
            .ThenBy(n => n.Name, StringComparer.Ordinal)
            .Select(n => n.Name),
    ];

    static string Label(string id) => id switch
    {
        AllId => "All namespaces",
        AllWithSystemId => "All, with system",
        _ => id,
    };

    /// <summary>A chrome filter changed. Null when applied; otherwise why not, for the page to say.</summary>
    public async Task<string?> ApplyAsync(string filterId, int index)
    {
        switch (filterId)
        {
            case "cluster" when index >= 0 && index < clusters.LocalContexts.Count:
                var name = clusters.LocalContexts[index].Name;
                if (name == clusters.Selected?.Name) return null;

                Namespace = null;
                Changed?.Invoke();
                return await clusters.SelectAsync(name);

            case "namespace":
                var ids = NamespaceIds();
                if (index < 0 || index >= ids.Count) return null;

                (Namespace, ShowSystem) = ids[index] switch
                {
                    AllId => ((string?)null, false),
                    AllWithSystemId => (null, true),
                    var ns => (ns, ShowSystem),
                };

                Changed?.Invoke();
                return null;
        }

        return null;
    }
}
