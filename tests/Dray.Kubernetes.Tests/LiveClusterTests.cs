using System.Net.Sockets;
using System.Text;
using Dray.Core.Engine;
using Dray.Core.Kubernetes;
using Dray.Core.Model;
using Xunit;

namespace Dray.Kubernetes.Tests;

/// <summary>
/// Against a real cluster: the kind cluster the K0 spike stood up, with .fixtures/kubernetes
/// applied. Skipped where there is none, like the Docker integration tests are without an engine.
/// Each test is a claim docs/KUBERNETES.md makes, held to what the cluster actually does.
/// </summary>
public sealed class LiveClusterTests : IAsyncLifetime
{
    const string Context = "kind-dray-spike";
    const string Ns = "spike";

    IClusterClient? _client;

    public async ValueTask InitializeAsync()
    {
        var reader = new KubeconfigReader(new SystemDockerConfigSource());
        if (reader.Read().FirstOrDefault(c => c.Name == Context) is not { IsLocal: true } context) return;

        var client = new KubeClusterClientFactory(reader).Create(context);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(timeout.Token);
            _client = client;
        }
        catch (Exception)
        {
            await client.DisposeAsync();
        }
    }

    IClusterClient Client
    {
        get
        {
            if (_client is null) Assert.Skip($"No reachable {Context} cluster. Create it with: kind create cluster --name dray-spike");
            return _client;
        }
    }

    async Task<PodSummary> PodAsync(string name)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var change in Client.WatchAsync(ClusterKind.Pod, cts.Token))
        {
            if (change is ClusterSynced synced)
            {
                return synced.Items.OfType<PodSummary>().FirstOrDefault(p => p.Namespace == Ns && p.Name.StartsWith(name, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"No pod {name} in {Ns}. Apply .fixtures/kubernetes/workloads.yaml.");
            }
        }

        throw new TimeoutException();
    }

    [Fact]
    public async Task ConnectingMeasuresWhatTheClusterAllows()
    {
        var caps = await Client.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith("v1.", caps.ServerVersion);
        Assert.True(caps.CanExec);
        Assert.True(caps.CanPortForward);

        // kind ships no metrics server; 1.37 streams initial state.
        Assert.False(caps.SupportsMetrics);
        Assert.True(caps.SupportsStreamingLists);
    }

    [Fact]
    public async Task ACrashLoopingPodReadsAsOneSteadyState()
    {
        var crasher = await PodAsync("crasher");
        Assert.Equal("Crash loop", PodStatusVocabulary.Resolve(crasher).Word);

        var oomer = await PodAsync("oomer");
        var status = PodStatusVocabulary.Resolve(oomer);
        Assert.Equal("Crash loop", status.Word);
        Assert.Contains("out of memory", status.Detail);
    }

    /// <summary>
    /// The deadlock the spike found: a second write while a read is pending. Five round trips over
    /// one connection through the forward Dray makes; the shared-stream shape stalls on the second.
    /// </summary>
    [Fact]
    public async Task APortForwardCarriesAConversation()
    {
        await using var forward = await Client.ForwardAsync(Ns, "echo", 9000, ct: TestContext.Current.CancellationToken);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(System.Net.IPAddress.Loopback, forward.LocalPort, TestContext.Current.CancellationToken);
        var stream = tcp.GetStream();
        var reader = new StreamReader(stream);

        for (var i = 0; i < 5; i++)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"ping {i}\n"), TestContext.Current.CancellationToken);
            Assert.Equal($"ping {i}", await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, forward.Connections);
    }

    [Fact]
    public async Task ExecReportsTheExitCodeFromTheStatusChannel()
    {
        await using var session = await Client.StartExecAsync(Ns, "sleeper", "main",
            new ExecOptions(["sh", "-c", "echo out; exit 7"], Tty: false), TestContext.Current.CancellationToken);

        var output = new StringBuilder();
        await foreach (var chunk in session.ReadAsync(TestContext.Current.CancellationToken)) output.Append(chunk);

        Assert.Equal("out", output.ToString().Trim());
        Assert.Equal(7, await session.GetExitCodeAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AShellIsFoundRatherThanAssumed()
    {
        await using var session = await Client.StartExecAsync(Ns, "sleeper", "main", ExecOptions.InteractiveShell, TestContext.Current.CancellationToken);
        Assert.EndsWith("sh", session.Command);
    }

    /// <summary>Measured: during back-off the plain log is the dead instance's — the reason it died is already there.</summary>
    [Fact]
    public async Task ACrashLoopingPodsLogSaysWhy()
    {
        var lines = new List<string>();
        await foreach (var line in Client.StreamLogsAsync(Ns, "crasher", "main", new PodLogOptions(Follow: false, Tail: 10), TestContext.Current.CancellationToken))
            lines.Add(line.Text);

        Assert.Contains("fatal: config missing", lines);
    }

    [Fact]
    public async Task InspectIsTheServersOwnJson()
    {
        var raw = await Client.GetRawAsync(ClusterKind.Pod, Ns, "sleeper", TestContext.Current.CancellationToken);
        using var doc = System.Text.Json.JsonDocument.Parse(raw);

        Assert.Equal("Pod", doc.RootElement.GetProperty("kind").GetString());
        Assert.True(doc.RootElement.GetProperty("metadata").TryGetProperty("managedFields", out _));
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }
}
