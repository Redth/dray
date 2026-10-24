using System.Net;
using System.Runtime.CompilerServices;
using Dray.Core.Engine;
using Dray.Core.Kubernetes;
using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>Reading the kubeconfig: kubectl's merge, and local measured rather than read off a name.</summary>
public class KubeconfigReaderTests
{
    const string Home = "/home/u";

    const string Kind = """
        apiVersion: v1
        kind: Config
        current-context: kind-dray-spike
        clusters:
        - name: kind-dray-spike
          cluster:
            server: https://127.0.0.1:63628
            certificate-authority-data: AAAA
        - name: staging
          cluster:
            server: https://k8s.staging.example.com:6443
        - name: desktop
          cluster:
            server: https://kubernetes.docker.internal:6443
        contexts:
        - name: kind-dray-spike
          context:
            cluster: kind-dray-spike
            user: kind-dray-spike
        - name: staging
          context:
            cluster: staging
            user: admin
            namespace: web
        - name: docker-desktop
          context:
            cluster: desktop
            user: docker-desktop
        - name: broken
          context:
            cluster: nowhere
        users:
        - name: kind-dray-spike
          user:
            client-key-data: SECRET
        """;

    static IPAddress[] Dns(string host) => host switch
    {
        // A hosts-file entry is what makes this one loopback: resolved, not string-matched.
        "kubernetes.docker.internal" => [IPAddress.Loopback],
        "k8s.staging.example.com" => [IPAddress.Parse("10.4.0.12")],
        _ => throw new System.Net.Sockets.SocketException(),
    };

    [Fact]
    public void LocalIsMeasuredFromTheApiServersAddress()
    {
        var source = new FakeConfigSource().WithFile($"{Home}/.kube/config", Kind);
        var contexts = new KubeconfigReader(source, Home, Dns).Read();

        var kind = contexts.Single(c => c.Name == "kind-dray-spike");
        Assert.True(kind.IsLocal);
        Assert.True(kind.IsCurrent);
        Assert.Contains("127.0.0.1:63628", kind.LocalityReason);

        Assert.True(contexts.Single(c => c.Name == "docker-desktop").IsLocal);

        var staging = contexts.Single(c => c.Name == "staging");
        Assert.False(staging.IsLocal);
        Assert.Equal("web", staging.Namespace);
        Assert.Equal("API server at 10.4.0.12 is not on this machine.", staging.LocalityReason);
    }

    [Fact]
    public void AContextNamingAMissingClusterIsSkipped()
    {
        var source = new FakeConfigSource().WithFile($"{Home}/.kube/config", Kind);
        Assert.DoesNotContain(new KubeconfigReader(source, Home, Dns).Read(), c => c.Name == "broken");
    }

    /// <summary>kubectl's rule: across KUBECONFIG's files, the first to define a name wins, and so does the first current-context.</summary>
    [Fact]
    public void KubeconfigFilesMergeFirstDefinitionWins()
    {
        const string second = """
            current-context: other
            clusters:
            - name: kind-dray-spike
              cluster:
                server: https://10.9.9.9:6443
            - name: other
              cluster:
                server: https://127.0.0.1:7000
            contexts:
            - name: other
              context:
                cluster: other
            """;

        var source = new FakeConfigSource()
            .WithFile("/a/config", Kind)
            .WithFile("/b/config", second)
            .WithEnvironment("KUBECONFIG", "/a/config:/missing:/b/config");

        var reader = new KubeconfigReader(source, Home, Dns);
        var contexts = reader.Read();

        Assert.Equal(["/a/config", "/b/config"], reader.Paths());
        Assert.Equal("https://127.0.0.1:63628", contexts.Single(c => c.Name == "kind-dray-spike").Server);
        Assert.Equal("/b/config", contexts.Single(c => c.Name == "other").SourcePath);
        Assert.True(contexts.Single(c => c.Name == "kind-dray-spike").IsCurrent);
        Assert.False(contexts.Single(c => c.Name == "other").IsCurrent);
    }

    [Fact]
    public void LocalContextsComeFirst()
    {
        var source = new FakeConfigSource().WithFile($"{Home}/.kube/config", Kind);
        var names = new KubeconfigReader(source, Home, Dns).Read().Select(c => c.Name);

        Assert.Equal(["docker-desktop", "kind-dray-spike", "staging"], names);
    }

    [Fact]
    public void AnUnresolvableServerIsRemoteAndSaysWhy()
    {
        var (locality, reason) = new KubeconfigReader(new FakeConfigSource(), Home, Dns).Classify("https://nowhere.invalid:6443");
        Assert.Equal(ClusterLocality.Remote, locality);
        Assert.Equal("nowhere.invalid does not resolve from this machine.", reason);
    }
}

/// <summary>The pod pill, from the states the spike's pods were measured in (docs/KUBERNETES.md §5.1).</summary>
public class PodStatusTests
{
    static PodSummary Pod(string phase, params PodContainer[] containers) => new()
    {
        Uid = "u", Name = "p", Namespace = "spike", Phase = phase, Node = "n",
        ConditionReady = containers.Length > 0 && containers.All(c => c.Ready),
        Containers = containers,
    };

    static PodContainer C(string? state, string? reason = null, int restarts = 0, int? exit = null, string? lastReason = null, int? lastExit = null, bool ready = false)
        => new() { Name = "main", Image = "busybox:1.37", State = state, Reason = reason, RestartCount = restarts, ExitCode = exit, LastReason = lastReason, LastExitCode = lastExit, Ready = ready };

    [Fact]
    public void Running() => Assert.Equal("Running", PodStatusVocabulary.Resolve(Pod("Running", C("running", ready: true))).Word);

    /// <summary>
    /// The departure from kubectl. Measured within one minute on the same pod: kubectl said Error,
    /// then CrashLoopBackOff, then Error. Dray says Crash loop throughout, with the last exit.
    /// </summary>
    [Theory]
    [InlineData("terminated", "Error", 3, "Error")]
    [InlineData("waiting", "CrashLoopBackOff", null, "CrashLoopBackOff")]
    public void ACrashLoopIsOneStateInEitherHalfOfTheCycle(string state, string reason, int? exit, string kubectl)
    {
        var pod = Pod("Running", C(state, reason, restarts: 4, exit: exit, lastReason: "Error", lastExit: 3));

        Assert.Equal(kubectl, PodStatusVocabulary.KubectlStatus(pod));

        var status = PodStatusVocabulary.Resolve(pod);
        Assert.Equal("Crash loop", status.Word);
        Assert.Equal("↯", status.Glyph);
        Assert.Equal(StateTone.Danger, status.Tone);
        Assert.Equal("last exit: code 3", status.Detail);
    }

    /// <summary>The cluster names the reason, so the reason is used: not read back out of 137.</summary>
    [Fact]
    public void OutOfMemoryIsTheClustersWordNotTheExitCodes()
    {
        var pod = Pod("Running", C("terminated", "OOMKilled", restarts: 3, exit: 137));

        Assert.Equal("OOMKilled", PodStatusVocabulary.KubectlStatus(pod));
        Assert.Equal("last exit: out of memory", PodStatusVocabulary.Resolve(pod).Detail);
    }

    [Fact]
    public void AFailedPullNamesTheImage()
    {
        var pod = Pod("Pending", C("waiting", "ErrImagePull") with { Image = "myapp:dev" });
        var status = PodStatusVocabulary.Resolve(pod);

        Assert.Equal("Can't pull image", status.Word);
        Assert.Equal("⤓", status.Glyph);
        Assert.Equal("myapp:dev", status.Detail);
    }

    [Fact]
    public void CompletedIsNeutral()
    {
        var status = PodStatusVocabulary.Resolve(Pod("Succeeded", C("terminated", "Completed", exit: 0)));
        Assert.Equal(("Completed", StateTone.Neutral), (status.Word, status.Tone));
    }

    [Fact]
    public void TerminatingOutranksEverything()
        => Assert.Equal("Terminating", PodStatusVocabulary.Resolve(Pod("Running", C("running", ready: true)) with { IsTerminating = true }).Word);

    [Fact]
    public void NotReadyCountsItsContainers()
    {
        var pod = Pod("Running", C("running", ready: true), C("running") with { Name = "sidecar" }) with { ConditionReady = false };
        var status = PodStatusVocabulary.Resolve(pod);

        Assert.Equal("Not ready 1/2", status.Word);
        Assert.Equal("◑", status.Glyph);
    }

    /// <summary>Every state its own glyph, so the narrow-width pill — glyph and tint, word hidden — stays legible in greyscale (DESIGN.md §2.4).</summary>
    [Fact]
    public void EveryStateHasAGlyphOfItsOwn()
    {
        PodSummary[] pods =
        [
            Pod("Running", C("running", ready: true)),
            Pod("Running", C("running", ready: true), C("running") with { Name = "b" }) with { ConditionReady = false },
            Pod("Pending") with { Node = null },
            Pod("Pending", C("waiting", "ContainerCreating")),
            Pod("Running", C("waiting", "CrashLoopBackOff", restarts: 2)),
            Pod("Pending", C("waiting", "ImagePullBackOff")),
            Pod("Running", C("running", ready: true)) with { IsTerminating = true },
            Pod("Failed") with { Reason = "Evicted" },
            Pod("Unknown"),
        ];

        var glyphs = pods.Select(p => PodStatusVocabulary.Resolve(p)).Select(s => (s.Word.Split(' ')[0], s.Glyph)).Distinct().ToList();
        Assert.Equal(glyphs.Count, glyphs.Select(g => g.Glyph).Distinct().Count());
    }
}

/// <summary>The store: diffed relists, and a pod found under the workload the user made.</summary>
public class ClusterStoreTests
{
    static PodSummary Pod(string uid, string phase = "Running", OwnerRef? owner = null) => new()
    {
        Uid = uid, Name = uid, Namespace = "spike", Phase = phase, Owner = owner,
        Containers = [new PodContainer { Name = "main", Image = "x", State = "running", Ready = true }],
    };

    /// <summary>
    /// A relist after 410 that finds nothing new writes nothing — fresh instances of unchanged data,
    /// with lists inside them, included. Record equality would have rewritten every row.
    /// </summary>
    [Fact]
    public void ARelistOfUnchangedObjectsWritesNothing()
    {
        var store = new ClusterStore();
        store.Apply(new ClusterSynced(ClusterKind.Pod, [Pod("a"), Pod("b")]));
        var writes = store.Writes;

        var changed = 0;
        store.Changed += _ => changed++;
        Assert.False(store.Apply(new ClusterSynced(ClusterKind.Pod, [Pod("a"), Pod("b")])));

        Assert.Equal(writes, store.Writes);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void ARelistRemovesWhatVanishedAndReplacesWhatChanged()
    {
        var store = new ClusterStore();
        store.Apply(new ClusterSynced(ClusterKind.Pod, [Pod("a"), Pod("b")]));

        store.Apply(new ClusterSynced(ClusterKind.Pod, [Pod("a", "Succeeded")]));

        var only = Assert.Single(store.Pods);
        Assert.Equal("Succeeded", only.Phase);
    }

    [Fact]
    public void AnEmptyFirstListIsSyncedNotPending()
    {
        var store = new ClusterStore();
        Assert.False(store.IsSynced(ClusterKind.Service));

        Assert.True(store.Apply(new ClusterSynced(ClusterKind.Service, [])));
        Assert.True(store.IsSynced(ClusterKind.Service));
    }

    /// <summary>A Deployment's pods are owned by a ReplicaSet the user never made; the store follows the ReplicaSet's own owner.</summary>
    [Fact]
    public void APodIsFoundUnderItsDeploymentThroughItsReplicaSet()
    {
        var store = new ClusterStore();
        var deployment = new WorkloadSummary { Uid = "d1", Name = "myapp", Namespace = "spike", WorkloadKind = ClusterKind.Deployment, Desired = 2 };

        store.Apply(new ClusterUpserted(deployment));
        store.Apply(new ClusterUpserted(new ReplicaSetSummary { Uid = "rs1", Name = "myapp-84cb689584", Namespace = "spike", Owner = new OwnerRef(ClusterKind.Deployment, "myapp", "d1") }));
        store.Apply(new ClusterSynced(ClusterKind.Pod, [Pod("p1", owner: new OwnerRef(ClusterKind.ReplicaSet, "myapp-84cb689584", "rs1")), Pod("p2")]));

        Assert.Equal(["p1"], store.PodsOf(deployment).Select(p => p.Name));
        Assert.Equal(new OwnerRef(ClusterKind.Deployment, "myapp", "d1"), store.TopOwner(store.Pods.Single(p => p.Name == "p1")));
    }

    [Fact]
    public void AServiceFindsItsPodsBySelector()
    {
        var store = new ClusterStore();
        store.Apply(new ClusterSynced(ClusterKind.Pod,
        [
            Pod("web") with { Labels = new Dictionary<string, string> { ["app"] = "myapp", ["tier"] = "web" } },
            Pod("other") with { Labels = new Dictionary<string, string> { ["app"] = "other" } },
        ]));

        var service = new ServiceSummary { Uid = "s", Name = "myapp", Namespace = "spike", Selector = new Dictionary<string, string> { ["app"] = "myapp" } };
        Assert.Equal(["web"], store.PodsFor(service).Select(p => p.Name));
    }
}

/// <summary>Choosing a cluster: local only, never kubectl's current-context, and a dead one said plainly.</summary>
public class ClusterManagerTests
{
    const string Home = "/home/u";

    static KubeconfigReader Reader() => new(new FakeConfigSource().WithFile($"{Home}/.kube/config", """
        current-context: staging
        clusters:
        - name: kind
          cluster: { server: "https://127.0.0.1:6443" }
        - name: staging
          cluster: { server: "https://10.4.0.12:6443" }
        contexts:
        - name: kind-dev
          context: { cluster: kind }
        - name: staging
          context: { cluster: staging }
        """), Home, _ => throw new System.Net.Sockets.SocketException());

    /// <summary>kubectl points at a remote cluster; Dray opens the local one and leaves kubectl alone.</summary>
    [Fact]
    public async Task OpensOnALocalClusterEvenWhenKubectlPointsAtARemoteOne()
    {
        var factory = new FakeFactory();
        await using var manager = new ClusterManager(Reader(), factory);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("kind-dev", manager.Selected?.Name);
        Assert.Equal(HostConnectionState.Connected, manager.Selected?.State);
        Assert.Equal(["kind-dev"], factory.Created);
    }

    [Fact]
    public async Task ARemoteClusterIsRefusedWithItsReason()
    {
        var factory = new FakeFactory();
        await using var manager = new ClusterManager(Reader(), factory);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        var problem = await manager.SelectAsync("staging", TestContext.Current.CancellationToken);

        Assert.NotNull(problem);
        Assert.Contains("10.4.0.12", problem);
        Assert.Equal(["kind-dev"], factory.Created);
    }

    [Fact]
    public async Task AClusterThatDoesNotAnswerIsUnreachableAndSaysWhy()
    {
        var factory = new FakeFactory { Fail = new HttpRequestException("Connection refused", new System.Net.Sockets.SocketException(61)) };
        await using var manager = new ClusterManager(Reader(), factory);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HostConnectionState.Unreachable, manager.Selected?.State);
        Assert.StartsWith("Could not reach the cluster", manager.Selected?.StateDetail);
    }

    [Fact]
    public async Task WatchesFillTheStore()
    {
        var factory = new FakeFactory();
        await using var manager = new ClusterManager(Reader(), factory);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (manager.Store.Pods.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(5, TestContext.Current.CancellationToken);

        Assert.Equal("web", Assert.Single(manager.Store.Pods).Name);
    }

    sealed class FakeFactory : IClusterClientFactory
    {
        public List<string> Created { get; } = [];

        public Exception? Fail { get; init; }

        public IClusterClient Create(KubeContext context)
        {
            Created.Add(context.Name);
            return new FakeClient(Fail);
        }
    }

    sealed class FakeClient(Exception? fail) : IClusterClient
    {
        public Task<ClusterCapabilities> ConnectAsync(CancellationToken ct = default)
            => fail is null ? Task.FromResult(new ClusterCapabilities { ServerVersion = "v1.37.0" }) : Task.FromException<ClusterCapabilities>(fail);

        public async IAsyncEnumerable<ClusterChange> WatchAsync(ClusterKind kind, [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return kind == ClusterKind.Pod
                ? new ClusterSynced(kind, [new PodSummary { Uid = "p", Name = "web", Namespace = "default" }])
                : new ClusterSynced(kind, []);

            await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
        }

        public IAsyncEnumerable<LogLine> StreamLogsAsync(string ns, string pod, string container, PodLogOptions options, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IExecSession> StartExecAsync(string ns, string pod, string container, ExecOptions options, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IPortForward> ForwardAsync(string ns, string pod, int remotePort, int? localPort = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteAsync(ClusterKind kind, string ns, string name, CancellationToken ct = default) => Task.CompletedTask;

        public Task ScaleAsync(ClusterKind kind, string ns, string name, int replicas, CancellationToken ct = default) => Task.CompletedTask;

        public Task RestartAsync(ClusterKind kind, string ns, string name, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetRawAsync(ClusterKind kind, string ns, string name, CancellationToken ct = default) => Task.FromResult("{}");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
