using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dray.Core.Model;
using k8s;

namespace Dray.Kubernetes;

/// <summary>
/// A shell or command in a pod's container, over the exec WebSocket.
/// <para>
/// <c>v5.channel.k8s.io</c>, measured against 1.37. Its one addition is the reason to insist on it:
/// a client can close stdin. Under <c>v4</c> anything that reads to the end of its input hangs for
/// ever (docs/KUBERNETES.md §5.3). The exit code arrives on the status channel as a Status object
/// and is read from its <c>causes</c>, not from its message.
/// </para>
/// <para>
/// stdin and stdout are separate demuxed streams, which is what makes full duplex work — the shared
/// two-way stream deadlocks, as port forwarding found (§9.2).
/// </para>
/// </summary>
sealed class KubeExecSession : IExecSession
{
    /// <summary>
    /// The same order Docker's shells are tried in: bash for line editing and history, ash on
    /// Alpine, sh because almost everything has one.
    /// </summary>
    const string FindShell = "for s in /bin/bash /usr/bin/bash /bin/ash /bin/sh /usr/bin/sh; do if [ -x \"$s\" ]; then echo \"$s\"; exit 0; fi; done; exit 1";

    readonly WebSocket _socket;
    readonly StreamDemuxer _demux;
    readonly Stream _stdin;
    readonly Stream? _resize;
    readonly Task<string> _status;
    readonly Channel<string> _output = Channel.CreateUnbounded<string>();

    KubeExecSession(WebSocket socket, string command, bool tty)
    {
        _socket = socket;
        Command = command;
        HasPseudoTerminal = tty;

        _demux = new StreamDemuxer(socket);
        _demux.Start();

        _stdin = _demux.GetStream(null, ChannelIndex.StdIn);
        _resize = tty ? _demux.GetStream(null, ChannelIndex.Resize) : null;
        _status = new StreamReader(_demux.GetStream(ChannelIndex.Error, null)).ReadToEndAsync();

        var pumps = new List<Task> { Pump(_demux.GetStream(ChannelIndex.StdOut, null)) };
        if (!tty) pumps.Add(Pump(_demux.GetStream(ChannelIndex.StdErr, null)));
        _ = Task.WhenAll(pumps).ContinueWith(_ => _output.Writer.TryComplete(), TaskScheduler.Default);
    }

    public string Command { get; }

    public bool HasPseudoTerminal { get; }

    public static async Task<KubeExecSession> StartAsync(k8s.Kubernetes k, string ns, string pod, string container, ExecOptions options, CancellationToken ct)
    {
        IReadOnlyList<string> command = options.Command ?? [await FindShellAsync(k, ns, pod, container, ct)];

        var socket = await k.WebSocketNamespacedPodExecAsync(
            pod, ns, command, container,
            stdin: true, stdout: true, stderr: !options.Tty, tty: options.Tty,
            webSocketSubProtocol: WebSocketProtocol.V5BinaryWebsocketProtocol,
            cancellationToken: ct);

        return new KubeExecSession(socket, string.Join(' ', command), options.Tty);
    }

    /// <summary>
    /// Which shell the container has, asked with one short exec. Probed rather than assumed: a shell
    /// that does not exist starts, at the API, and then the terminal opens blank and closes — the
    /// least diagnosable failure there is.
    /// </summary>
    static async Task<string> FindShellAsync(k8s.Kubernetes k, string ns, string pod, string container, CancellationToken ct)
    {
        using var socket = await k.WebSocketNamespacedPodExecAsync(
            pod, ns, ["sh", "-c", FindShell], container,
            stdin: false, stdout: true, stderr: false, tty: false,
            webSocketSubProtocol: WebSocketProtocol.V5BinaryWebsocketProtocol,
            cancellationToken: ct);

        using var demux = new StreamDemuxer(socket);
        demux.Start();

        var stdout = new StreamReader(demux.GetStream(ChannelIndex.StdOut, null)).ReadToEndAsync(ct);
        var status = new StreamReader(demux.GetStream(ChannelIndex.Error, null)).ReadToEndAsync(ct);
        await Task.WhenAll(stdout, status).WaitAsync(TimeSpan.FromSeconds(10), ct);

        if (stdout.Result.Trim() is { Length: > 0 } shell && ExitCode(status.Result) == 0) return shell;

        // No sh at all — a distroless image — reports "executable file not found" on the status
        // channel. Either way there is no shell to open, and the terminal says so.
        throw new NoShellException("This container has no shell. Images built FROM scratch or on distroless have none, so there is nothing to open a terminal into.");
    }

    async Task Pump(Stream stream)
    {
        var decoder = new Utf8StreamDecoder();
        var buffer = new byte[8192];

        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                var text = decoder.Decode(buffer.AsSpan(0, read));
                if (text.Length > 0) await _output.Writer.WriteAsync(text);
            }
        }
        catch (Exception e) when (e is IOException or WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
        }

        if (decoder.Flush() is { Length: > 0 } rest) await _output.Writer.WriteAsync(rest);
    }

    public IAsyncEnumerable<string> ReadAsync(CancellationToken ct = default) => _output.Reader.ReadAllAsync(ct);

    public async Task WriteAsync(string text, CancellationToken ct = default)
    {
        await _stdin.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await _stdin.FlushAsync(ct);
    }

    public async Task ResizeAsync(int columns, int rows, CancellationToken ct = default)
    {
        if (_resize is null) return;
        await _resize.WriteAsync(Encoding.UTF8.GetBytes($"{{\"Width\":{columns},\"Height\":{rows}}}"), ct);
        await _resize.FlushAsync(ct);
    }

    public async Task<int?> GetExitCodeAsync(CancellationToken ct = default)
    {
        try
        {
            return ExitCode(await _status.WaitAsync(TimeSpan.FromSeconds(5), ct));
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>{"status":"Success"}</c> is 0. A failure carries the code in <c>details.causes</c> under
    /// reason <c>ExitCode</c> — measured: <c>{"reason":"ExitCode","message":"7"}</c>. Anything else
    /// is no exit code, which is not the same as zero.
    /// </summary>
    internal static int? ExitCode(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;

        try
        {
            using var doc = JsonDocument.Parse(status);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var s) && s.GetString() == "Success") return 0;

            if (root.TryGetProperty("details", out var details) && details.TryGetProperty("causes", out var causes))
            {
                foreach (var cause in causes.EnumerateArray())
                {
                    if (cause.TryGetProperty("reason", out var r) && r.GetString() == "ExitCode"
                        && cause.TryGetProperty("message", out var m) && int.TryParse(m.GetString(), out var code))
                        return code;
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        // v5's close frame for stdin, so a command reading its input sees the end of it.
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.SendAsync(new byte[] { 255, (byte)ChannelIndex.StdIn }, WebSocketMessageType.Binary, true, default);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException)
        {
        }

        _demux.Dispose();
        _socket.Dispose();
        _output.Writer.TryComplete();
    }
}
