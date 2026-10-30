using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Dray.Core.Kubernetes;
using k8s;

namespace Dray.Kubernetes;

/// <summary>
/// A pod's port on 127.0.0.1, one WebSocket per connection (docs/KUBERNETES.md §5.4).
/// <para>
/// Measured: ten concurrent HTTP requests through one forward in 139 ms, and one TCP connection held
/// for 30 seconds — twenty round trips with a 20-second idle gap — with nothing lost.
/// </para>
/// <para>
/// Each direction gets its own demuxed stream. <c>StreamDemuxer.GetStream(0, 0)</c> — one stream for
/// both, the obvious call and the shape most examples use — deadlocks: the second write never
/// returns while a read is pending on the same stream. A one-shot HTTP request survives it, which is
/// why a quick test passes; a database client fails on its second query (§9.2). A test holds this.
/// </para>
/// </summary>
sealed class KubePortForward : IPortForward
{
    readonly k8s.Kubernetes _k;
    readonly TcpListener _listener;
    readonly CancellationTokenSource _stop = new();
    int _connections;

    KubePortForward(k8s.Kubernetes k, string ns, string pod, int remotePort, TcpListener listener)
    {
        _k = k;
        Namespace = ns;
        Pod = pod;
        RemotePort = remotePort;
        _listener = listener;
        LocalPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public string Namespace { get; }

    public string Pod { get; }

    public int RemotePort { get; }

    public int LocalPort { get; }

    public int Connections => Volatile.Read(ref _connections);

    public string? Failure { get; private set; }

    /// <summary>
    /// Listen on loopback, on the pod's own port where that is free, the next few after it if not,
    /// and any port at all as the last resort — always saying which, never the network interface.
    /// </summary>
    public static KubePortForward Start(k8s.Kubernetes k, string ns, string pod, int remotePort, int? localPort)
    {
        var preferred = localPort ?? remotePort;
        var candidates = Enumerable.Range(preferred, 10).Where(p => p is > 0 and < 65536).Append(0);

        foreach (var port in candidates)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
                return new KubePortForward(k, ns, pod, remotePort, listener);
            }
            catch (SocketException)
            {
                listener.Dispose();
            }
        }

        throw new ClusterException($"No port on this machine was free to forward {pod}:{remotePort} to.");
    }

    async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            Interlocked.Increment(ref _connections);
            _ = CarryAsync(tcp);
        }
    }

    async Task CarryAsync(TcpClient tcp)
    {
        using var _ = tcp;
        try
        {
            using var socket = await _k.WebSocketNamespacedPodPortForwardAsync(
                Pod, Namespace, [RemotePort], WebSocketProtocol.V4BinaryWebsocketProtocol, cancellationToken: _stop.Token);
            using var demux = new StreamDemuxer(socket, StreamType.PortForward);
            demux.Start();

            // Separate streams per direction. See the class remarks before ever making this one.
            var fromPod = demux.GetStream((byte?)0, null);
            var toPod = demux.GetStream(null, (byte?)0);
            var local = tcp.GetStream();

            // The error channel says why a connection failed inside the pod — nothing listening on
            // the port is the usual one. Kept as the forward's failure only while nothing has
            // succeeded since, so a forward that works is not labelled by one bad connection.
            var errors = ReadErrorsAsync(demux.GetStream((byte?)1, null));

            await Task.WhenAny(local.CopyToAsync(toPod, _stop.Token), fromPod.CopyToAsync(local, _stop.Token));

            if (await Task.WhenAny(errors, Task.Delay(100)) == errors && errors.Result is { Length: > 0 } error)
                Failure = error;
            else
                Failure = null;
        }
        catch (Exception e) when (e is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception e)
        {
            Failure = e.Message;
        }
    }

    static async Task<string> ReadErrorsAsync(Stream stream)
    {
        try
        {
            return (await new StreamReader(stream).ReadToEndAsync()).Trim();
        }
        catch (Exception e) when (e is IOException or WebSocketException or ObjectDisposedException)
        {
            return "";
        }
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        _listener.Dispose();
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }
}
