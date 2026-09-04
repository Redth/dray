using Docker.DotNet.Handler.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Client;

namespace Dray.Docker.Transport;

/// <summary>Transport options for <see cref="StdioRelayHandlerFactory"/>.</summary>
/// <param name="Command">The relay to start for each connection.</param>
public sealed record StdioRelayTransportOptions(RelayCommand Command)
{
    /// <summary>Hook for tests and diagnostics, matching the shape of every other transport here.</summary>
    public Action<ManagedHandler>? ConfigureHandler { get; init; }
}

/// <summary>
/// Speaks the Docker Engine API over a relay process's standard input and output.
/// <para>
/// The three transports that ship with the client cover a socket, a pipe and a TCP address. This
/// covers the case where the engine has no address on this machine at all: an engine inside a WSL2
/// distribution, and an engine on the far end of an SSH connection. Both are reached by running a
/// program that has the socket and copying bytes through it.
/// </para>
/// <para>
/// Built on <see cref="ManagedHandler"/> — the same hand-rolled HTTP/1.1 client the Unix-socket and
/// named-pipe transports use — rather than on <c>SocketsHttpHandler</c>. That is what makes exec
/// and attach work: those endpoints upgrade the connection and then hand the raw stream to the
/// caller, which a pooling handler will not do.
/// </para>
/// <para>
/// <b>One process per connection.</b> That is the real cost of this transport and it is worth
/// stating plainly: the handler does not pool, so every request that is not already on an open
/// connection starts a relay. It is the same bargain the Docker CLI makes for <c>ssh://</c>, where
/// each command is its own <c>ssh</c>. It matters less than it sounds like it should, because the
/// expensive things here are the long-lived ones — the event stream, a log follow, a stats stream —
/// and each of those is a single connection held open for as long as it is being watched.
/// </para>
/// </summary>
public sealed class StdioRelayHandlerFactory : IDockerHandlerFactory<StdioRelayTransportOptions>
{
    public static readonly IDockerHandlerFactory<StdioRelayTransportOptions> Instance = new StdioRelayHandlerFactory();

    StdioRelayHandlerFactory()
    {
    }

    /// <summary>
    /// The authority every request is addressed to.
    /// <para>
    /// A relayed connection has no host and no port — it is already at the engine by the time the
    /// first byte is written. The name exists so the client can build a syntactically valid
    /// request line and <c>Host</c> header, which the daemon does not read. Every other transport
    /// in this client does the same thing with the same name.
    /// </para>
    /// </summary>
    static readonly Uri Authority = new("http://localhost");

    public ResolvedTransport CreateHandler(ResolvedClientOptions clientOptions, ILogger logger)
        => throw new InvalidOperationException(
            "A relay transport cannot be resolved from an endpoint URI alone; it needs the command to run. "
            + "Use the overload that takes StdioRelayTransportOptions.");

    public ResolvedTransport CreateHandler(
        StdioRelayTransportOptions transportOptions,
        ResolvedClientOptions clientOptions,
        ILogger logger)
    {
        var command = transportOptions.Command;

        var handler = new ManagedHandler(
            (_, _, _) => Task.FromResult<Stream>(ProcessRelayStream.Start(command)),
            logger);

        transportOptions.ConfigureHandler?.Invoke(handler);

        return new ResolvedTransport(handler, Authority);
    }

    /// <summary>
    /// Hand the caller the raw connection after an upgrade, for exec and attach.
    /// <para>
    /// The response content is the handler's own type and already owns the stream; this only takes
    /// it out. Identical to what every transport in the client does, because the hijack belongs to
    /// <see cref="ManagedHandler"/> rather than to the thing underneath it.
    /// </para>
    /// </summary>
    public Task<WriteClosableStream> HijackStreamAsync(HttpContent content)
        => content is HttpConnectionResponseContent hijackable
            ? Task.FromResult(hijackable.HijackStream())
            : throw new InvalidOperationException(
                $"This response cannot be hijacked: expected {nameof(HttpConnectionResponseContent)}, got {content.GetType().Name}.");
}
