// K0 — the Kubernetes spike behind docs/KUBERNETES.md §10. Each mode measures one claim in that
// document against a running cluster, through the same client Dray.Kubernetes will use.
//
//   kind create cluster --name dray-spike
//   docker build -t myapp:dev .fixtures/kubernetes/app
//   kind load docker-image myapp:dev --name dray-spike
//   kubectl apply -f .fixtures/kubernetes/workloads.yaml
//   dotnet run --project .scratch/k8s-k0 -- <mode>
//
// Modes: status, access, logs, exec [v4|v5], duplex, watch <seconds>, gone, stream,
//        forward, hold, shared, raw

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using k8s;
using k8s.Models;

const string Context = "kind-dray-spike";
const string Ns = "spike";

var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(currentContext: Context);
using var client = new Kubernetes(config);
var mode = args.FirstOrDefault() ?? "status";
Console.WriteLine($"{config.Host} · {mode}");

await (mode switch
{
    "status" => Status(),
    "access" => Access(),
    "logs" => Logs(),
    "exec" => Exec(args.ElementAtOrDefault(1) == "v4" ? WebSocketProtocol.V4BinaryWebsocketProtocol : WebSocketProtocol.V5BinaryWebsocketProtocol),
    "duplex" => ExecDuplex(),
    "watch" => Watch(TimeSpan.FromSeconds(int.Parse(args.ElementAtOrDefault(1) ?? "28"))),
    "gone" => Gone(),
    "stream" => StreamingList(),
    "forward" => ForwardHttp(),
    "hold" => ForwardHeld(shared: false),
    "shared" => ForwardHeld(shared: true),
    "raw" => ForwardRaw(),
    _ => throw new ArgumentException($"unknown mode {mode}"),
});

// §5.1 — kubectl's STATUS, for comparison with `kubectl get pods -n spike`.
async Task Status()
{
    var pods = await client.CoreV1.ListNamespacedPodAsync(Ns);
    foreach (var p in pods.Items) Console.WriteLine($"{p.Metadata.Name,-28} {KubectlStatus(p)}");
}

// §9.4 — capabilities, each answered by the cluster rather than assumed.
async Task Access()
{
    (string Verb, string Group, string Resource, string? Sub, string? Ns)[] checks =
    [
        ("list", "", "pods", null, null),
        ("create", "", "pods", "exec", Ns),
        ("create", "", "pods", "portforward", Ns),
        ("delete", "", "pods", null, Ns),
        ("patch", "apps", "deployments", "scale", Ns),
    ];
    foreach (var c in checks)
    {
        var review = await client.AuthorizationV1.CreateSelfSubjectAccessReviewAsync(new V1SelfSubjectAccessReview
        {
            Spec = new V1SelfSubjectAccessReviewSpec
            {
                ResourceAttributes = new V1ResourceAttributes
                {
                    Verb = c.Verb, Group = c.Group, Resource = c.Resource, Subresource = c.Sub, NamespaceProperty = c.Ns,
                },
            },
        });
        Console.WriteLine($"{c.Verb} {c.Resource}/{c.Sub} in {c.Ns ?? "all"}: {review.Status.Allowed}");
    }
    Console.WriteLine($"version {(await client.Version.GetCodeAsync()).GitVersion}");
    var apis = await client.ApiregistrationV1.ListAPIServiceAsync();
    Console.WriteLine($"metrics: {apis.Items.Any(a => a.Metadata.Name == "v1beta1.metrics.k8s.io")}");
}

// §5.2 — during back-off the plain log and previous=true return the same instance.
async Task Logs()
{
    foreach (var (pod, previous) in new[] { ("crasher", false), ("crasher", true), ("oomer", true), ("sleeper", false) })
    {
        try
        {
            using var s = await client.CoreV1.ReadNamespacedPodLogAsync(pod, Ns, container: "main", previous: previous, timestamps: true, tailLines: 3);
            var text = await new StreamReader(s).ReadToEndAsync();
            Console.WriteLine($"{pod} previous={previous}:\n  {text.Trim().Replace("\n", "\n  ")}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"{pod} previous={previous}: {e.GetType().Name} {e.Message}");
        }
    }

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var follow = await client.CoreV1.ReadNamespacedPodLogAsync("sleeper", Ns, container: "main", follow: true, tailLines: 0, cancellationToken: cts.Token);
    var reader = new StreamReader(follow);
    var lines = 0;
    try { while (await reader.ReadLineAsync(cts.Token) is not null) lines++; } catch (OperationCanceledException) { }
    Console.WriteLine($"follow: {lines} lines in 5s");
}

// §5.3 — stdin close and the exit code. Under v4 a command reading to EOF never finishes.
async Task Exec(string protocol)
{
    using var ws = await client.WebSocketNamespacedPodExecAsync("sleeper", Ns,
        ["sh", "-c", "echo out:$(cat); echo err >&2; exit 7"], "main",
        stdin: true, stdout: true, stderr: true, tty: false, webSocketSubProtocol: protocol);
    Console.WriteLine($"negotiated {ws.SubProtocol}");
    using var demux = new StreamDemuxer(ws);
    demux.Start();
    var stdin = demux.GetStream(null, ChannelIndex.StdIn);
    var stdout = new StreamReader(demux.GetStream(ChannelIndex.StdOut, null)).ReadToEndAsync();
    var stderr = new StreamReader(demux.GetStream(ChannelIndex.StdErr, null)).ReadToEndAsync();
    var status = new StreamReader(demux.GetStream(ChannelIndex.Error, null)).ReadToEndAsync();

    await stdin.WriteAsync(Encoding.UTF8.GetBytes("abc"));
    if (protocol == WebSocketProtocol.V5BinaryWebsocketProtocol)
    {
        // v5's close frame: channel 255, then the channel being closed.
        await ws.SendAsync(new byte[] { 255, (byte)ChannelIndex.StdIn }, WebSocketMessageType.Binary, true, default);
    }

    var all = Task.WhenAll(stdout, stderr, status);
    if (await Task.WhenAny(all, Task.Delay(5000)) != all)
    {
        Console.WriteLine("hung: stdin was never closed");
        return;
    }
    Console.WriteLine($"stdout: {stdout.Result.Trim()}\nstderr: {stderr.Result.Trim()}\nstatus: {status.Result.Trim()}");
}

// §5.3 — a write issued while a read is pending, as a terminal always has.
async Task ExecDuplex()
{
    using var ws = await client.WebSocketNamespacedPodExecAsync("sleeper", Ns, ["cat"], "main",
        stdin: true, stdout: true, stderr: true, tty: false, webSocketSubProtocol: WebSocketProtocol.V5BinaryWebsocketProtocol);
    using var demux = new StreamDemuxer(ws);
    demux.Start();
    var stdin = demux.GetStream(null, ChannelIndex.StdIn);
    var reader = new StreamReader(demux.GetStream(ChannelIndex.StdOut, null));
    for (var i = 0; i < 3; i++)
    {
        var read = reader.ReadLineAsync();
        var write = stdin.WriteAsync(Encoding.UTF8.GetBytes($"line {i}\n")).AsTask();
        if (await Task.WhenAny(write, Task.Delay(3000)) != write)
        {
            Console.WriteLine($"write {i} hung");
            return;
        }
        Console.WriteLine($"echoed: {await read.WaitAsync(TimeSpan.FromSeconds(3))}");
    }
}

// §9.3 — run alongside `kubectl delete pod -n spike -l app=myapp`. The stream ends on the server's
// timeout with no exception and no event; bookmarks carry a resourceVersion and nothing else.
async Task Watch(TimeSpan duration)
{
    var sw = Stopwatch.StartNew();
    var list = await client.CoreV1.ListPodForAllNamespacesAsync();
    Console.WriteLine($"list: {list.Items.Count} pods at rv {list.Metadata.ResourceVersion} in {sw.ElapsedMilliseconds}ms");
    using var cts = new CancellationTokenSource(duration);
    try
    {
        var events = client.CoreV1.WatchListPodForAllNamespacesAsync(
            resourceVersion: list.Metadata.ResourceVersion, allowWatchBookmarks: true, timeoutSeconds: 20,
            onError: OnError, cancellationToken: cts.Token);
        await foreach (var (type, pod) in events)
        {
            Console.WriteLine($"{sw.Elapsed.TotalSeconds,6:F1}s {type,-8} {pod.Metadata.NamespaceProperty}/{pod.Metadata.Name} rv {pod.Metadata.ResourceVersion}");
        }
        Console.WriteLine($"ended at {sw.Elapsed.TotalSeconds:F1}s without an exception");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("cancelled");
    }
}

// §9.3 — a compacted cursor. Needs churn and a few minutes of cluster age first:
//   for i in $(seq 150); do kubectl create configmap -n default churn-$i; done; kubectl delete cm -n default -l '!x'
async Task Gone()
{
    foreach (var rv in new[] { "1", "100" })
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var stream = client.CoreV1.WatchListNamespacedConfigMapAsync("default", resourceVersion: rv, onError: OnError, cancellationToken: cts.Token);
            var events = 0;
            await foreach (var _ in stream) events++;
            Console.WriteLine($"rv {rv}: ended normally after {events} events");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"rv {rv}: still open after 5s — not compacted yet");
        }
    }
}

// §9.3 — WatchList: Added for each object, then a bookmark marking the end of the initial state.
async Task StreamingList()
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var sw = Stopwatch.StartNew();
    try
    {
        var events = client.CoreV1.WatchListPodForAllNamespacesAsync(
            sendInitialEvents: true, resourceVersionMatch: "NotOlderThan", allowWatchBookmarks: true,
            onError: OnError, cancellationToken: cts.Token);
        await foreach (var (type, pod) in events)
        {
            var end = pod.Metadata.Annotations is { } a && a.TryGetValue("k8s.io/initial-events-end", out var v) ? $" initial-events-end={v}" : "";
            Console.WriteLine($"{sw.ElapsedMilliseconds,5}ms {type,-8} {pod.Metadata.Name} rv {pod.Metadata.ResourceVersion}{end}");
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("(5s)");
    }
}

// §5.4 — ten concurrent one-shot requests. Passes even with the shared stream, which is why a
// quick HTTP test does not find the deadlock.
async Task ForwardHttp()
{
    var pods = await client.CoreV1.ListNamespacedPodAsync(Ns, labelSelector: "app=myapp");
    var pod = pods.Items.First(p => p.Status.Phase == "Running").Metadata.Name;
    var (port, connections) = StartForward(pod, 8080, shared: false);
    using var http = new HttpClient();
    var sw = Stopwatch.StartNew();
    var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/");
            request.Headers.ConnectionClose = true;
            using var response = await http.SendAsync(request);
            return $"{(int)response.StatusCode} {(await response.Content.ReadAsStringAsync()).Trim()}";
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    }));
    Console.WriteLine($"10 requests in {sw.ElapsedMilliseconds}ms over {connections()} connections");
    foreach (var g in results.GroupBy(r => r)) Console.WriteLine($"  {g.Count()}x {g.Key}");
}

// §9.2 — one connection, twenty round trips, a 20-second idle gap. `shared` reproduces the deadlock.
async Task ForwardHeld(bool shared)
{
    var (port, _) = StartForward("echo", 9000, shared);
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(IPAddress.Loopback, port);
    var stream = tcp.GetStream();
    var reader = new StreamReader(stream);
    var sw = Stopwatch.StartNew();
    var echoed = 0;
    for (var i = 0; i < 20; i++)
    {
        if (i == 10) await Task.Delay(20_000);
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"ping {i}\n"));
        try
        {
            if (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == $"ping {i}") echoed++;
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"round trip {i} timed out — the write never reached the pod");
            break;
        }
        await Task.Delay(500);
    }
    Console.WriteLine($"{echoed}/20 echoed over one connection in {sw.Elapsed.TotalSeconds:F0}s ({(shared ? "shared" : "separate")} streams)");
}

// §9.2 — the wire format with no demuxer: a channel byte per frame, and a two-byte little-endian
// port on each channel's first frame.
async Task ForwardRaw()
{
    using var ws = await client.WebSocketNamespacedPodPortForwardAsync("echo", Ns, [9000], WebSocketProtocol.V4BinaryWebsocketProtocol);
    var sender = Task.Run(async () =>
    {
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(500);
            await ws.SendAsync((byte[])[0, .. Encoding.UTF8.GetBytes($"ping {i}\n")], WebSocketMessageType.Binary, true, default);
        }
    });
    var buffer = new byte[4096];
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    try
    {
        while (true)
        {
            var frame = await ws.ReceiveAsync(buffer, cts.Token);
            Console.WriteLine($"frame {frame.Count}B: [{string.Join(",", buffer.Take(Math.Min(frame.Count, 12)))}]");
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("(4s)");
    }
    await sender;
}

// A loopback listener that opens one port-forward WebSocket per accepted connection — the shape
// Dray's forwards take. `shared` uses one demuxed stream for both directions, which deadlocks.
(int Port, Func<int> Connections) StartForward(string pod, int remotePort, bool shared)
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var connections = 0;
    _ = Task.Run(async () =>
    {
        while (true)
        {
            var tcp = await listener.AcceptTcpClientAsync();
            Interlocked.Increment(ref connections);
            _ = Task.Run(async () =>
            {
                using var _ = tcp;
                using var ws = await client.WebSocketNamespacedPodPortForwardAsync(pod, Ns, [remotePort], WebSocketProtocol.V4BinaryWebsocketProtocol);
                using var demux = new StreamDemuxer(ws, StreamType.PortForward);
                demux.Start();
                Stream fromPod, toPod;
                if (shared)
                {
                    fromPod = toPod = demux.GetStream((byte?)0, (byte?)0);
                }
                else
                {
                    fromPod = demux.GetStream((byte?)0, null);
                    toPod = demux.GetStream(null, (byte?)0);
                }
                var local = tcp.GetStream();
                await Task.WhenAny(local.CopyToAsync(toPod), fromPod.CopyToAsync(local));
            });
        }
    });
    return (((IPEndPoint)listener.LocalEndpoint).Port, () => connections);
}

static void OnError(Exception e)
{
    var status = (e as KubernetesException)?.Status;
    Console.WriteLine($"onError: {e.GetType().Name}{(status is null ? "" : $" code {status.Code} {status.Reason}")}: {e.Message}");
}

// kubectl's printPod (k8s.io/kubernetes/pkg/printers/internalversion/printers.go), as far as the
// spike's pods exercise it. Dray's own version departs for crash loops — see §5.1.
static string KubectlStatus(V1Pod pod)
{
    var reason = string.IsNullOrEmpty(pod.Status.Reason) ? pod.Status.Phase : pod.Status.Reason;

    var initializing = false;
    var inits = pod.Status.InitContainerStatuses ?? [];
    for (var i = 0; i < inits.Count; i++)
    {
        var c = inits[i];
        if (c.State.Terminated is { ExitCode: 0 }) continue;
        if (c.State.Terminated is { } t)
        {
            reason = !string.IsNullOrEmpty(t.Reason) ? "Init:" + t.Reason
                : t.Signal is { } s and not 0 ? $"Init:Signal:{s}" : $"Init:ExitCode:{t.ExitCode}";
        }
        else if (c.State.Waiting is { Reason: { Length: > 0 } w } && w != "PodInitializing")
        {
            reason = "Init:" + w;
        }
        else
        {
            reason = $"Init:{i}/{pod.Spec.InitContainers?.Count ?? 0}";
        }
        initializing = true;
        break;
    }

    if (!initializing)
    {
        var hasRunning = false;
        foreach (var c in (pod.Status.ContainerStatuses ?? []).Reverse())
        {
            if (c.State.Waiting is { Reason: { Length: > 0 } w }) reason = w;
            else if (c.State.Terminated is { } t)
            {
                reason = !string.IsNullOrEmpty(t.Reason) ? t.Reason
                    : t.Signal is { } s and not 0 ? $"Signal:{s}" : $"ExitCode:{t.ExitCode}";
            }
            else if (c.Ready && c.State.Running is not null) hasRunning = true;
        }
        if (reason == "Completed" && hasRunning)
        {
            reason = pod.Status.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true ? "Running" : "NotReady";
        }
    }

    if (pod.Metadata.DeletionTimestamp is not null)
        reason = pod.Status.Reason == "NodeLost" ? "Unknown" : "Terminating";
    return reason ?? "";
}
