namespace Dray.Core.Engine;

/// <summary>
/// Keeps one host's <see cref="EntityStore"/> current from the engine's event stream, and owns
/// that host's connection state machine.
/// <para>
/// The rule this enforces is PRODUCT.md's: a cold list seeds the store once, and after that only
/// events mutate it. A dropped stream reconnects with backoff and the host shows as Degraded while
/// it does — data on screen is real but going stale, which is a different statement from gone.
/// </para>
/// </summary>
public sealed class RuntimeEventPump : IAsyncDisposable
{
    readonly IContainerRuntime _runtime;
    readonly EntityStore _store;
    readonly TimeProvider _time;

    CancellationTokenSource? _cts;
    Task? _loop;

    public RuntimeEventPump(IContainerRuntime runtime, EntityStore store, TimeProvider? time = null)
    {
        _runtime = runtime;
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Backoff between reconnect attempts, capped. Exposed so tests do not wait in real time.</summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often to re-list when the engine has no event stream.
    /// <para>
    /// Two seconds is the compromise: a lifecycle change feels immediate at this rate, and the
    /// cost is one list call — which on Apple's runtime is a process launch, not a socket read.
    /// Only used when <see cref="RuntimeCapabilities.SupportsEvents"/> is false.
    /// </para>
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public HostConnectionState State { get; private set; } = HostConnectionState.Disconnected;

    /// <summary>Plain-language reason for the current state, or null when connected.</summary>
    public string? StateDetail { get; private set; }

    public event Action<HostConnectionState, string?>? StateChanged;

    /// <summary>Number of completed reconnect attempts. Surfaced for diagnostics and tests.</summary>
    public int ReconnectCount { get; private set; }

    public void Start()
    {
        if (_loop is not null) return;

        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
    }

    async Task RunAsync(CancellationToken ct)
    {
        var delay = InitialRetryDelay;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetState(HostConnectionState.Connecting, null);
                await _runtime.ConnectAsync(ct).ConfigureAwait(false);

                // Seed once per connection. Every later change arrives as an event.
                var containers = await _runtime.ListContainersAsync(ct: ct).ConfigureAwait(false);
                _store.Reset(containers);

                SetState(HostConnectionState.Connected, null);
                delay = InitialRetryDelay;

                // Not every engine has an event stream. Apple's `container` runtime has no
                // `events` subcommand at all, so there is nothing to subscribe to and the only
                // way to notice a change is to look again.
                if (_runtime.Capabilities.SupportsEvents)
                {
                    await ConsumeAsync(ct).ConfigureAwait(false);

                    // The stream ended without an error and without cancellation. The engine went
                    // away cleanly; treat it as a drop and reconnect.
                    if (!ct.IsCancellationRequested)
                        SetState(HostConnectionState.Degraded, "The event stream ended. Reconnecting…");
                }
                else
                {
                    // Runs until cancelled or until a list call throws, which the outer catch
                    // turns into the same reconnect the stream path takes.
                    await PollAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Containers already on screen stay, marked stale: they still exist, Dray just
                // cannot see them. Emptying the list would claim they were gone.
                _store.MarkAllStale();

                var reason = Describe(ex);
                SetState(
                    State == HostConnectionState.Connected ? HostConnectionState.Degraded : HostConnectionState.Unreachable,
                    reason);
            }

            if (ct.IsCancellationRequested) break;

            ReconnectCount++;

            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = delay < MaxRetryDelay
                ? TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks))
                : MaxRetryDelay;
        }

        if (!ct.IsCancellationRequested) return;

        SetState(HostConnectionState.Disconnected, null);
    }

    /// <summary>
    /// The fallback for an engine with no event stream: list, diff, repeat.
    /// <para>
    /// Deliberately a diff rather than a <see cref="EntityStore.Reset"/>. Resetting twice a second
    /// would clear every pending action and re-announce every row as new, so the list would flash
    /// and the change highlights — which exist to show what just happened — would fire constantly
    /// and mean nothing. Only rows that actually differ are written, so an idle engine produces no
    /// store changes at all and the UI does not repaint.
    /// </para>
    /// <para>
    /// This is worse than an event stream and is meant to look it: a change is noticed up to one
    /// interval late, and a container that is created and destroyed between two polls is never
    /// seen. <see cref="RuntimeCapabilities.SupportsEvents"/> is what the UI reads to say so.
    /// </para>
    /// </summary>
    async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, _time, ct).ConfigureAwait(false);

            var containers = await _runtime.ListContainersAsync(ct: ct).ConfigureAwait(false);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var container in containers)
            {
                seen.Add(container.Id);

                var existing = _store.Find(container.Id);

                // Field comparison, not record equality: Ports is a list, so two identical
                // responses are never == and every row would be rewritten on every tick.
                if (existing is not null && existing.SameAs(container)) continue;

                // Whatever the user was waiting for has now been observed, whether or not it is
                // the outcome they asked for. The event path clears this the same way.
                if (existing is not null && existing.State != container.State) _store.ClearPending(container.Id);

                _store.Upsert(container);
            }

            foreach (var container in _store.Containers)
            {
                if (!seen.Contains(container.Id)) _store.Remove(container.Id);
            }
        }
    }

    async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach (var e in _runtime.WatchEventsAsync(ct).ConfigureAwait(false))
        {
            // The store says when an event describes something it has never seen, because an
            // event alone cannot describe a new container — no image, no ports.
            if (!_store.Apply(e)) continue;

            try
            {
                var containers = await _runtime.ListContainersAsync(ct: ct).ConfigureAwait(false);
                if (containers.FirstOrDefault(c => c.Id == e.Id) is { } fetched) _store.Upsert(fetched);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed backfill is not worth dropping the stream over; the next event or a
                // reconnect will reconcile.
            }
        }
    }

    void SetState(HostConnectionState state, string? detail)
    {
        if (State == state && StateDetail == detail) return;

        State = state;
        StateDetail = detail;
        StateChanged?.Invoke(state, detail);
    }

    /// <summary>
    /// Turn an exception into something worth showing a user. PRODUCT.md: Dray says what happened
    /// and what it means, never "Something went wrong".
    /// </summary>
    public static string Describe(Exception ex) => ex switch
    {
        RuntimeConnectionException r => r.Message,

        // The engine is installed but not running — by far the most common case.
        //
        // AddressNotAvailable is what a missing unix socket actually produces, verified against a
        // stopped Docker Desktop. It is not FileNotFoundException, which is what one would assume:
        // the connect() fails on the path rather than the runtime opening a file.
        System.Net.Sockets.SocketException
        {
            SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused
                or System.Net.Sockets.SocketError.AddressNotAvailable,
        }
            => "The engine is not running.",

        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound }
            => "Host not found.",
        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.TimedOut }
            => "Timed out reaching the host.",
        System.Net.Sockets.SocketException
        {
            SocketErrorCode: System.Net.Sockets.SocketError.NetworkUnreachable
                or System.Net.Sockets.SocketError.HostUnreachable,
        }
            => "The host is unreachable from this network.",
        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionReset }
            => "The engine closed the connection.",

        UnauthorizedAccessException => "Permission denied on the Docker socket.",
        TimeoutException => "Timed out reaching the host.",

        // The socket file is gone: the engine was stopped or removed.
        FileNotFoundException or DirectoryNotFoundException => "The Docker socket no longer exists.",

        HttpRequestException http => http.InnerException is not null ? Describe(http.InnerException) : "Could not reach the engine.",

        _ => "Could not reach the engine.",
    };

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;

        await _cts.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
    }
}
