using System.Net;
using System.Net.Sockets;
using Dray.Core.Engine;
using YamlDotNet.RepresentationModel;

namespace Dray.Core.Kubernetes;

/// <summary>
/// The contexts in the user's kubeconfig, merged the way <c>kubectl</c> merges them, each classified
/// as local or remote by measurement.
/// <para>
/// <c>KUBECONFIG</c> is a list of files, separated as <c>PATH</c> is on the platform; the first file
/// to define a context, a cluster or the current-context wins, as it does for <c>kubectl</c>. Without
/// it, <c>~/.kube/config</c>.
/// </para>
/// <para>
/// Only <c>contexts</c>, <c>clusters</c> and <c>current-context</c> are read. The <c>users</c>
/// section is where the client key and tokens are, and this class never touches it.
/// </para>
/// </summary>
public sealed class KubeconfigReader(IDockerConfigSource source, string? home = null, Func<string, IPAddress[]>? resolve = null)
{
    readonly string _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    readonly Func<string, IPAddress[]> _resolve = resolve ?? Resolve;

    /// <summary>The kubeconfig files in effect, in precedence order. Missing ones are skipped.</summary>
    public IReadOnlyList<string> Paths()
    {
        var env = source.GetEnvironmentVariable("KUBECONFIG");
        var separator = source.IsWindows ? ';' : ':';

        IEnumerable<string> candidates = string.IsNullOrWhiteSpace(env)
            ? [Path.Combine(_home, ".kube", "config")]
            : env.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return [.. candidates.Distinct(StringComparer.Ordinal).Where(source.FileExists)];
    }

    public IReadOnlyList<KubeContext> Read()
    {
        var contexts = new Dictionary<string, (string Cluster, string? Namespace, string Path)>(StringComparer.Ordinal);
        var clusters = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;

        foreach (var path in Paths())
        {
            YamlMappingNode? root;
            try
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(source.ReadAllText(path)));
                root = stream.Documents.FirstOrDefault()?.RootNode as YamlMappingNode;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
            {
                // One unreadable file does not hide the contexts in the others — kubectl refuses
                // the lot, but a GUI that shows nothing because of one stray file is worse.
                continue;
            }

            if (root is null) continue;

            current ??= Scalar(root, "current-context") is { Length: > 0 } c ? c : null;

            foreach (var entry in Sequence(root, "clusters"))
            {
                if (Scalar(entry, "name") is not { } name || clusters.ContainsKey(name)) continue;
                if (Child(entry, "cluster") is YamlMappingNode cluster && Scalar(cluster, "server") is { } server)
                    clusters[name] = server;
            }

            foreach (var entry in Sequence(root, "contexts"))
            {
                if (Scalar(entry, "name") is not { } name || contexts.ContainsKey(name)) continue;
                if (Child(entry, "context") is not YamlMappingNode context) continue;
                if (Scalar(context, "cluster") is not { } cluster) continue;

                contexts[name] = (cluster, Scalar(context, "namespace"), path);
            }
        }

        var result = new List<KubeContext>();
        foreach (var (name, (cluster, ns, path)) in contexts)
        {
            // A context naming a cluster no file defines is broken in kubectl too; it is skipped
            // rather than offered as a cluster with no address.
            if (!clusters.TryGetValue(cluster, out var server)) continue;

            var (locality, reason) = Classify(server);
            result.Add(new KubeContext
            {
                Name = name,
                ClusterName = cluster,
                Server = server,
                Namespace = string.IsNullOrEmpty(ns) ? null : ns,
                SourcePath = path,
                IsCurrent = name == current,
                Locality = locality,
                LocalityReason = reason,
            });
        }

        return [.. result.OrderByDescending(c => c.IsLocal).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Local when the API server's host resolves to loopback. Resolved rather than matched as a
    /// string: <c>kubernetes.docker.internal</c> is loopback only because a hosts-file entry says
    /// so, and a hostname that happens to say "localhost" in its middle is not.
    /// </summary>
    internal (ClusterLocality, string) Classify(string server)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return (ClusterLocality.Remote, $"The API server address “{server}” could not be read.");

        var host = uri.Host.Trim('[', ']');
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal) ? [literal] : _resolve(host);
        }
        catch (SocketException)
        {
            return (ClusterLocality.Remote, $"{host} does not resolve from this machine.");
        }

        if (addresses.Length > 0 && addresses.All(IPAddress.IsLoopback))
            return (ClusterLocality.Local, $"API server on this machine ({uri.Authority}).");

        var shown = addresses.FirstOrDefault()?.ToString() ?? host;
        return (ClusterLocality.Remote, $"API server at {shown} is not on this machine.");
    }

    static IPAddress[] Resolve(string host) => Dns.GetHostAddresses(host);

    static YamlNode? Child(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    static string? Scalar(YamlMappingNode node, string key) => (Child(node, key) as YamlScalarNode)?.Value;

    static IEnumerable<YamlMappingNode> Sequence(YamlMappingNode node, string key)
        => (Child(node, key) as YamlSequenceNode)?.Children.OfType<YamlMappingNode>() ?? [];
}
