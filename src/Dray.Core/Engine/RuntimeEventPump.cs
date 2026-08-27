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

                await ConsumeAsync(ct).ConfigureAwait(false);

                // The stream ended without an error and without cancellation. The engine went
                // away cleanly; treat it as a drop and reconnect.
                if (!ct.IsCancellationRequested)
                    SetState(HostConnectionState.Degraded, "The event stream ended. Reconnecting…");
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
        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused }
            => "The engine is not running.",
        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound }
            => "Host not found.",
        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.TimedOut }
            => "Timed out reaching the host.",

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
