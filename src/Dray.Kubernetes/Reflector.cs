using System.Runtime.CompilerServices;
using Dray.Core.Kubernetes;

namespace Dray.Kubernetes;

enum WatchEventKind
{
    Added,
    Modified,
    Deleted,
    Bookmark,
}

/// <summary>One event off a watch, already mapped. A bookmark carries a resourceVersion and nothing else.</summary>
sealed record WatchItem(WatchEventKind Type, ClusterObject? Item, string? Uid, string? ResourceVersion, bool InitialEventsEnd = false);

/// <summary>What ended a watch, when something did: the Status the server sent, or the request failing.</summary>
sealed record WatchFailure(int? Code, string Message);

/// <summary>One kind's list and watch calls, behind the shape the reflector needs. Faked in tests.</summary>
interface IKindApi
{
    ClusterKind Kind { get; }

    Task<(IReadOnlyList<ClusterObject> Items, string? ResourceVersion)> ListAsync(CancellationToken ct);

    /// <param name="onError">
    /// Where a Status in the stream arrives — including 410 Gone. Measured: the client reports it
    /// only here, and then ends the enumeration normally.
    /// </param>
    IAsyncEnumerable<WatchItem> WatchAsync(string? resourceVersion, bool sendInitialEvents, int timeoutSeconds, Action<WatchFailure> onError, CancellationToken ct);
}

/// <summary>
/// List, then watch, forever: the loop docs/KUBERNETES.md §9.3 describes, as measured against 1.37.
/// <list type="number">
/// <item>The initial state is streamed (<c>sendInitialEvents</c>) where the server can, ending at a
/// bookmark annotated <c>k8s.io/initial-events-end</c>; otherwise listed. Either way it is handed on
/// whole, as <see cref="ClusterSynced"/>, for the store to diff.</item>
/// <item>The cursor follows every event, bookmarks included. It is opaque and never compared.</item>
/// <item>A watch the server ends on its timeout ends with no exception and no event. That is routine,
/// and the watch resumes from the cursor.</item>
/// <item>410 Gone means the cursor was compacted away. It arrives through the error callback, and the
/// stream then ends exactly as a timeout does — so the callback is what tells them apart, and a
/// reflector that ignored it would re-watch the same stale cursor in a tight loop that looks like a
/// quiet watch. On 410: relist.</item>
/// </list>
/// </summary>
sealed class Reflector(IKindApi api, bool streaming)
{
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A permission refused is not going to change in a second; asked again rarely.</summary>
    public TimeSpan ForbiddenRetryDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long one watch may run before the server ends it. Randomised, so ten kinds do not all reconnect on the same second.</summary>
    public Func<int> TimeoutSeconds { get; init; } = () => Random.Shared.Next(300, 600);

    /// <summary>Relists after a 410 — surfaced so a test can count them.</summary>
    public int Relists { get; private set; }

    public async IAsyncEnumerable<ClusterChange> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        string? cursor = null;
        var synced = false;
        var useStreaming = streaming;
        var delay = InitialRetryDelay;

        while (!ct.IsCancellationRequested)
        {
            WatchFailure? failure = null;

            if (!synced && !useStreaming)
            {
                (IReadOnlyList<ClusterObject> Items, string? ResourceVersion)? listed = null;
                try
                {
                    listed = await api.ListAsync(ct);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    failure = Failure(e);
                }

                if (listed is { } list)
                {
                    cursor = list.ResourceVersion;
                    synced = true;
                    yield return new ClusterSynced(api.Kind, list.Items);
                }
            }

            if (failure is null)
            {
                var initial = !synced && useStreaming;
                List<ClusterObject>? buffer = initial ? [] : null;
                var sawInitialEnd = false;

                var started = Environment.TickCount64;
                var seen = 0;
                var events = api.WatchAsync(initial ? null : cursor, initial, TimeoutSeconds(), f => failure ??= f, ct).GetAsyncEnumerator(ct);
                try
                {
                    while (true)
                    {
                        WatchItem item;
                        try
                        {
                            if (!await events.MoveNextAsync()) break;
                            item = events.Current;
                        }
                        catch (Exception e) when (!ct.IsCancellationRequested)
                        {
                            failure ??= Failure(e);
                            break;
                        }

                        seen++;
                        if (item.ResourceVersion is { Length: > 0 } rv) cursor = rv;

                        if (buffer is not null)
                        {
                            switch (item.Type)
                            {
                                case WatchEventKind.Bookmark when item.InitialEventsEnd:
                                    sawInitialEnd = true;
                                    synced = true;
                                    delay = InitialRetryDelay;
                                    yield return new ClusterSynced(api.Kind, buffer);
                                    buffer = null;
                                    break;

                                case WatchEventKind.Added or WatchEventKind.Modified when item.Item is { } added:
                                    buffer.RemoveAll(b => b.Uid == added.Uid);
                                    buffer.Add(added);
                                    break;

                                case WatchEventKind.Deleted when item.Uid is { } uid:
                                    buffer.RemoveAll(b => b.Uid == uid);
                                    break;
                            }

                            continue;
                        }

                        delay = InitialRetryDelay;
                        switch (item.Type)
                        {
                            case WatchEventKind.Added or WatchEventKind.Modified when item.Item is { } changed:
                                yield return new ClusterUpserted(changed);
                                break;

                            case WatchEventKind.Deleted when item.Uid is { } uid:
                                yield return new ClusterDeleted(api.Kind, uid);
                                break;
                        }
                    }
                }
                finally
                {
                    await events.DisposeAsync();
                }

                // A streamed initial state that ended without its closing bookmark was never
                // streamed: the server took the parameter and ignored it, or refused it. Listing is
                // the way that always works.
                if (initial && !sawInitialEnd && failure is not { Code: 410 })
                {
                    useStreaming = false;
                    continue;
                }

                // A watch the server closes at once, saying nothing, over and over, would spin this
                // loop as fast as the network allows. A real timeout takes minutes; anything that
                // ends inside a second with no events waits as a failure would.
                if (failure is null && seen == 0 && Environment.TickCount64 - started < 1000)
                {
                    await Wait(delay, ct);
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
                    continue;
                }
            }

            if (failure is null) continue;

            switch (failure.Code)
            {
                case 410:
                    synced = false;
                    cursor = null;
                    Relists++;
                    continue;

                case 401 or 403:
                    yield return new ClusterInterrupted(api.Kind, failure.Message, Forbidden: true);
                    await Wait(ForbiddenRetryDelay, ct);
                    continue;

                default:
                    yield return new ClusterInterrupted(api.Kind, failure.Message);
                    await Wait(delay, ct);
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
                    continue;
            }
        }
    }

    static async Task Wait(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static WatchFailure Failure(Exception e) => e switch
    {
        k8s.KubernetesException { Status: { } status } => new(status.Code, status.Message ?? e.Message),
        k8s.Autorest.HttpOperationException http => new((int?)http.Response?.StatusCode, StatusMessage(http) ?? http.Message),
        HttpRequestException request => new((int?)request.StatusCode, request.InnerException?.Message ?? request.Message),
        _ => new(null, e.Message),
    };

    /// <summary>The server's own sentence from a Status body, where the response carried one.</summary>
    static string? StatusMessage(k8s.Autorest.HttpOperationException http)
    {
        try
        {
            return http.Response?.Content is { Length: > 0 } body
                ? System.Text.Json.JsonDocument.Parse(body).RootElement.TryGetProperty("message", out var m) ? m.GetString() : null
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
