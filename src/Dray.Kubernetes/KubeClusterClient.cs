using System.Net;
using System.Runtime.CompilerServices;
using Dray.Core.Kubernetes;
using Dray.Core.Model;
using k8s;
using k8s.Autorest;
using k8s.KubeConfigModels;
using k8s.Models;

namespace Dray.Kubernetes;

/// <summary>Makes a client for a context, from the same kubeconfig files the reader found it in.</summary>
public sealed class KubeClusterClientFactory(KubeconfigReader reader) : IClusterClientFactory
{
    public IClusterClient Create(KubeContext context)
    {
        // Every file in KUBECONFIG, merged the way kubectl merges them, then this context. The
        // client reads the users section — the key, the token, an exec plugin — in memory, here and
        // nowhere else, and nothing of it is kept (docs/KUBERNETES.md §9.5).
        var config = KubernetesClientConfiguration.BuildConfigFromConfigObject(Merge(reader.Paths()), context.Name);

        return new KubeClusterClient(new k8s.Kubernetes(config));
    }

    /// <summary>
    /// The files in <c>KUBECONFIG</c> as one configuration, first definition of each name winning —
    /// kubectl's rule. The client merges this way only for its own default configuration, with no
    /// way to name a context, so the merge is done here. Each file is loaded on its own first, so
    /// a relative certificate path resolves against the file that names it.
    /// </summary>
    static K8SConfiguration Merge(IReadOnlyList<string> paths)
    {
        var configs = paths.Select(p => KubernetesClientConfiguration.LoadKubeConfig(new FileInfo(p), useRelativePaths: true)).ToList();
        if (configs.Count == 1) return configs[0];

        return new K8SConfiguration
        {
            CurrentContext = configs.Select(c => c.CurrentContext).FirstOrDefault(c => !string.IsNullOrEmpty(c)),
            Contexts = [.. configs.SelectMany(c => c.Contexts ?? []).DistinctBy(c => c.Name)],
            Clusters = [.. configs.SelectMany(c => c.Clusters ?? []).DistinctBy(c => c.Name)],
            Users = [.. configs.SelectMany(c => c.Users ?? []).DistinctBy(u => u.Name)],
        };
    }
}

/// <summary><see cref="IClusterClient"/> over the official C# client (docs/KUBERNETES.md §9.2).</summary>
public sealed class KubeClusterClient : IClusterClient
{
    readonly k8s.Kubernetes _k;
    bool _streaming;

    internal KubeClusterClient(k8s.Kubernetes client) => _k = client;

    public async Task<ClusterCapabilities> ConnectAsync(CancellationToken ct = default)
    {
        var version = await Call(() => _k.Version.GetCodeAsync(ct));

        // What these credentials allow, asked rather than discovered by failing. On a local
        // cluster the answer is almost always "everything", and the answers cost five small calls.
        var listAll = await Can("list", "", "pods", null, null, ct);
        var exec = await Can("create", "", "pods", "exec", null, ct);
        var forward = await Can("create", "", "pods", "portforward", null, ct);
        var delete = await Can("delete", "", "pods", null, null, ct);
        var scale = await Can("patch", "apps", "deployments", "scale", null, ct);
        var restart = await Can("patch", "apps", "deployments", null, null, ct);

        _streaming = await ProbeStreamingAsync(ct);

        return new ClusterCapabilities
        {
            ServerVersion = version.GitVersion,
            CanListAllNamespaces = listAll,
            CanExec = exec,
            CanPortForward = forward,
            CanDeletePods = delete,
            CanScale = scale,
            CanRestart = restart,
            SupportsMetrics = await HasMetricsAsync(ct),
            SupportsStreamingLists = _streaming,
        };
    }

    async Task<bool> Can(string verb, string group, string resource, string? subresource, string? ns, CancellationToken ct)
    {
        try
        {
            var review = await _k.AuthorizationV1.CreateSelfSubjectAccessReviewAsync(new V1SelfSubjectAccessReview
            {
                Spec = new V1SelfSubjectAccessReviewSpec
                {
                    ResourceAttributes = new V1ResourceAttributes
                    {
                        Verb = verb, Group = group, Resource = resource, Subresource = subresource, NamespaceProperty = ns,
                    },
                },
            }, cancellationToken: ct);

            return review.Status?.Allowed == true;
        }
        catch (HttpOperationException)
        {
            // A cluster that will not answer the question has not said yes.
            return false;
        }
    }

    /// <summary>Whether the metrics API is served. Measured absent on kind.</summary>
    async Task<bool> HasMetricsAsync(CancellationToken ct)
    {
        try
        {
            var service = await _k.ApiregistrationV1.ReadAPIServiceAsync("v1beta1.metrics.k8s.io", cancellationToken: ct);
            return service.Status?.Conditions?.Any(c => c.Type == "Available" && c.Status == "True") == true;
        }
        catch (HttpOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a watch can stream its initial state, found out by trying one on the smallest kind.
    /// Measured present on 1.37; beta on the 1.32–1.35 clusters local tools ship, so asked rather
    /// than assumed from the version.
    /// </summary>
    async Task<bool> ProbeStreamingAsync(CancellationToken ct)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probe.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await foreach (var (type, ns) in _k.CoreV1.WatchListNamespaceAsync(
                allowWatchBookmarks: true, sendInitialEvents: true, resourceVersionMatch: "NotOlderThan",
                timeoutSeconds: 5, onError: _ => { }, cancellationToken: probe.Token))
            {
                if (type == WatchEventType.Bookmark && ns.Metadata?.Annotations?.ContainsKey("k8s.io/initial-events-end") == true)
                    return true;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or HttpOperationException or HttpRequestException)
        {
        }

        return false;
    }

    public IAsyncEnumerable<ClusterChange> WatchAsync(ClusterKind kind, CancellationToken ct = default)
        => new Reflector(KindApis.For(_k, kind), _streaming).RunAsync(ct);

    public async IAsyncEnumerable<LogLine> StreamLogsAsync(
        string ns, string pod, string container, PodLogOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var stream = await Call(() => _k.CoreV1.ReadNamespacedPodLogAsync(
            pod, ns, container: container, follow: options.Follow, previous: options.Previous,
            tailLines: options.Tail, timestamps: options.Timestamps, cancellationToken: ct));

        await using var _ = stream;
        using var reader = new StreamReader(stream);
        long sequence = 0;

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (IOException) when (ct.IsCancellationRequested)
            {
                yield break;
            }

            if (line is null) yield break;
            yield return KubeLogLine.Parse(line, options.Timestamps, sequence++);
        }
    }

    public async Task<IExecSession> StartExecAsync(string ns, string pod, string container, ExecOptions options, CancellationToken ct = default)
        => await KubeExecSession.StartAsync(_k, ns, pod, container, options, ct);

    public async Task<IPortForward> ForwardAsync(string ns, string pod, int remotePort, int? localPort = null, CancellationToken ct = default)
        => await Task.FromResult<IPortForward>(KubePortForward.Start(_k, ns, pod, remotePort, localPort));

    public async Task DeleteAsync(ClusterKind kind, string ns, string name, CancellationToken ct = default)
    {
        // Background: the controller's dependents go too, as kubectl delete does by default.
        var options = new V1DeleteOptions { PropagationPolicy = "Background" };

        Func<Task> call = kind switch
        {
            ClusterKind.Pod => () => _k.CoreV1.DeleteNamespacedPodAsync(name, ns, options, cancellationToken: ct),
            ClusterKind.Deployment => () => _k.AppsV1.DeleteNamespacedDeploymentAsync(name, ns, options, cancellationToken: ct),
            ClusterKind.StatefulSet => () => _k.AppsV1.DeleteNamespacedStatefulSetAsync(name, ns, options, cancellationToken: ct),
            ClusterKind.DaemonSet => () => _k.AppsV1.DeleteNamespacedDaemonSetAsync(name, ns, options, cancellationToken: ct),
            ClusterKind.Job => () => _k.BatchV1.DeleteNamespacedJobAsync(name, ns, options, cancellationToken: ct),
            ClusterKind.Service => () => _k.CoreV1.DeleteNamespacedServiceAsync(name, ns, options, cancellationToken: ct),
            _ => throw new ClusterException($"Dray does not delete a {kind}."),
        };

        await Call(call);
    }

    public async Task ScaleAsync(ClusterKind kind, string ns, string name, int replicas, CancellationToken ct = default)
    {
        var patch = new V1Patch($"{{\"spec\":{{\"replicas\":{replicas}}}}}", V1Patch.PatchType.MergePatch);

        Func<Task> call = kind switch
        {
            ClusterKind.Deployment => () => _k.AppsV1.PatchNamespacedDeploymentScaleAsync(patch, name, ns, cancellationToken: ct),
            ClusterKind.StatefulSet => () => _k.AppsV1.PatchNamespacedStatefulSetScaleAsync(patch, name, ns, cancellationToken: ct),
            _ => throw new ClusterException($"A {kind} cannot be scaled."),
        };

        await Call(call);
    }

    public async Task RestartAsync(ClusterKind kind, string ns, string name, CancellationToken ct = default)
    {
        // Exactly kubectl rollout restart's patch, so the history reads the same whoever did it.
        var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        var patch = new V1Patch(
            $"{{\"spec\":{{\"template\":{{\"metadata\":{{\"annotations\":{{\"kubectl.kubernetes.io/restartedAt\":\"{stamp}\"}}}}}}}}}}",
            V1Patch.PatchType.MergePatch);

        Func<Task> call = kind switch
        {
            ClusterKind.Deployment => () => _k.AppsV1.PatchNamespacedDeploymentAsync(patch, name, ns, cancellationToken: ct),
            ClusterKind.StatefulSet => () => _k.AppsV1.PatchNamespacedStatefulSetAsync(patch, name, ns, cancellationToken: ct),
            ClusterKind.DaemonSet => () => _k.AppsV1.PatchNamespacedDaemonSetAsync(patch, name, ns, cancellationToken: ct),
            _ => throw new ClusterException($"A {kind} cannot be restarted."),
        };

        await Call(call);
    }

    public async Task<string> GetRawAsync(ClusterKind kind, string ns, string name, CancellationToken ct = default)
    {
        var path = kind switch
        {
            ClusterKind.Pod => $"api/v1/namespaces/{ns}/pods/{name}",
            ClusterKind.Service => $"api/v1/namespaces/{ns}/services/{name}",
            ClusterKind.Node => $"api/v1/nodes/{name}",
            ClusterKind.Namespace => $"api/v1/namespaces/{name}",
            ClusterKind.Deployment => $"apis/apps/v1/namespaces/{ns}/deployments/{name}",
            ClusterKind.StatefulSet => $"apis/apps/v1/namespaces/{ns}/statefulsets/{name}",
            ClusterKind.DaemonSet => $"apis/apps/v1/namespaces/{ns}/daemonsets/{name}",
            ClusterKind.ReplicaSet => $"apis/apps/v1/namespaces/{ns}/replicasets/{name}",
            ClusterKind.Job => $"apis/batch/v1/namespaces/{ns}/jobs/{name}",
            _ => throw new ClusterException($"Dray does not read a {kind}."),
        };

        // Raw, through the client's own HTTP stack and credentials: the server's JSON, every field
        // of it, not the client's model re-serialised.
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_k.BaseUri, path));
        if (_k.Credentials is { } credentials) await credentials.ProcessHttpRequestAsync(request, ct);

        using var response = await _k.HttpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new ClusterException(StatusText(body) ?? $"The cluster answered {(int)response.StatusCode}.", (int)response.StatusCode);

        return body;
    }

    /// <summary>
    /// Run a call and turn the cluster's refusal into its own sentence. A request failure carries
    /// the request with it; only the Status message is kept, never a header (§9.5).
    /// </summary>
    static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (HttpOperationException e)
        {
            throw new ClusterException(StatusText(e.Response?.Content) ?? $"The cluster answered {(int?)e.Response?.StatusCode}.", (int?)e.Response?.StatusCode, e);
        }
    }

    static async Task Call(Func<Task> call) => await Call(async () =>
    {
        await call();
        return true;
    });

    static string? StatusText(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _k.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A log line as the API sends it with <c>timestamps=true</c>: an RFC 3339 time, a space, the text.</summary>
static class KubeLogLine
{
    public static LogLine Parse(string line, bool timestamps, long sequence)
    {
        if (timestamps)
        {
            var space = line.IndexOf(' ');
            if (space > 0 && DateTimeOffset.TryParse(line.AsSpan(0, space), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
            {
                return new LogLine(at, LogStream.StdOut, line[(space + 1)..], sequence);
            }
        }

        // Measured: the API merges stdout and stderr with no marker, so every line is StdOut — the
        // same as Apple's engine, and said as plainly (docs/KUBERNETES.md §5.2).
        return new LogLine(null, LogStream.StdOut, line, sequence);
    }
}
