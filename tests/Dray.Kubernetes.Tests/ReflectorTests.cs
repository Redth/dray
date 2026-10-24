using System.Runtime.CompilerServices;
using Dray.Core.Kubernetes;
using Xunit;

namespace Dray.Kubernetes.Tests;

/// <summary>
/// The list-then-watch loop against a scripted API, with each behaviour the spike measured on a
/// real 1.37 cluster written down as a script (docs/KUBERNETES.md §9.3).
/// </summary>
public class ReflectorTests
{
    static NamespaceSummary Ns(string name) => new() { Uid = "uid-" + name, Name = name };

    static async Task<List<ClusterChange>> Take(Reflector reflector, int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var changes = new List<ClusterChange>();

        await foreach (var change in reflector.RunAsync(cts.Token))
        {
            changes.Add(change);
            if (changes.Count == count) break;
        }

        return changes;
    }

    static Reflector Fast(IKindApi api, bool streaming = false) => new(api, streaming)
    {
        InitialRetryDelay = TimeSpan.FromMilliseconds(1),
        MaxRetryDelay = TimeSpan.FromMilliseconds(5),
        ForbiddenRetryDelay = TimeSpan.FromMilliseconds(5),
        TimeoutSeconds = () => 60,
    };

    [Fact]
    public async Task ListsThenWatchesFromTheListsCursor()
    {
        var api = new ScriptedApi
        {
            Lists = { ([Ns("default")], "100") },
            Watches = { new Watch([new WatchItem(WatchEventKind.Added, Ns("spike"), "uid-spike", "101")]) },
        };

        var changes = await Take(Fast(api), 2);

        Assert.Equal(["default"], Assert.IsType<ClusterSynced>(changes[0]).Items.Select(i => i.Name));
        Assert.Equal("spike", Assert.IsType<ClusterUpserted>(changes[1]).Item.Name);
        Assert.Equal("100", api.WatchedFrom[0]);
    }

    /// <summary>
    /// Measured: the server ends a watch on its timeout with no exception and no event, after a
    /// bookmark. The watch resumes from the bookmark's cursor and nothing is relisted.
    /// </summary>
    [Fact]
    public async Task AWatchTheServerEndsResumesFromTheBookmarkWithoutRelisting()
    {
        var api = new ScriptedApi
        {
            Lists = { ([Ns("default")], "100") },
            Watches =
            {
                new Watch([new WatchItem(WatchEventKind.Bookmark, null, null, "150")], Linger: TimeSpan.FromMilliseconds(1100)),
                new Watch([new WatchItem(WatchEventKind.Deleted, null, "uid-default", "151")]),
            },
        };

        var changes = await Take(Fast(api), 2);

        Assert.IsType<ClusterDeleted>(changes[1]);
        Assert.Equal(["100", "150"], api.WatchedFrom);
        Assert.Equal(1, api.ListCalls);
    }

    /// <summary>
    /// Measured: 410 arrives only through the error callback, and the stream then ends normally —
    /// exactly as a timeout does. The callback is what says relist; without it this would re-watch
    /// the compacted cursor for ever.
    /// </summary>
    [Fact]
    public async Task GoneRelistsAndStartsAgainFromTheNewCursor()
    {
        var api = new ScriptedApi
        {
            Lists = { ([Ns("default")], "100"), ([Ns("default"), Ns("spike")], "900") },
            Watches =
            {
                new Watch([], Failure: new WatchFailure(410, "too old resource version: 100 (1152)")),
                new Watch([new WatchItem(WatchEventKind.Added, Ns("late"), "uid-late", "901")]),
            },
        };

        var reflector = Fast(api);
        var changes = await Take(reflector, 3);

        Assert.Equal(["default", "spike"], Assert.IsType<ClusterSynced>(changes[1]).Items.Select(i => i.Name));
        Assert.Equal("late", Assert.IsType<ClusterUpserted>(changes[2]).Item.Name);
        Assert.Equal(["100", "900"], api.WatchedFrom);
        Assert.Equal(1, reflector.Relists);
    }

    /// <summary>
    /// Measured on 1.37: a streamed initial state is Added events, then a bookmark annotated
    /// initial-events-end, then live changes on the same connection. Handed on as one state.
    /// </summary>
    [Fact]
    public async Task AStreamedInitialStateArrivesWholeAtItsClosingBookmark()
    {
        var api = new ScriptedApi
        {
            Watches =
            {
                new Watch([
                    new WatchItem(WatchEventKind.Added, Ns("default"), "uid-default", "10"),
                    new WatchItem(WatchEventKind.Added, Ns("spike"), "uid-spike", "11"),
                    new WatchItem(WatchEventKind.Bookmark, null, null, "12", InitialEventsEnd: true),
                    new WatchItem(WatchEventKind.Deleted, null, "uid-spike", "13"),
                ]),
            },
        };

        var changes = await Take(Fast(api, streaming: true), 2);

        Assert.Equal(["default", "spike"], Assert.IsType<ClusterSynced>(changes[0]).Items.Select(i => i.Name));
        Assert.IsType<ClusterDeleted>(changes[1]);
        Assert.Equal(0, api.ListCalls);
        Assert.True(api.AskedForInitialEvents[0]);
    }

    /// <summary>A server that takes sendInitialEvents and ignores it sends no closing bookmark; listing is what always works.</summary>
    [Fact]
    public async Task AServerThatDoesNotStreamTheInitialStateIsListedInstead()
    {
        var api = new ScriptedApi
        {
            Lists = { ([Ns("default")], "100") },
            Watches = { new Watch([]), new Watch([new WatchItem(WatchEventKind.Added, Ns("spike"), "uid-spike", "101")]) },
        };

        var changes = await Take(Fast(api, streaming: true), 2);

        Assert.IsType<ClusterSynced>(changes[0]);
        Assert.Equal([true, false], api.AskedForInitialEvents);
    }

    [Fact]
    public async Task AForbiddenWatchSaysSoAndKeepsTrying()
    {
        var api = new ScriptedApi
        {
            ListFailure = new WatchFailure(403, "pods is forbidden: User cannot list resource pods"),
        };

        var changes = await Take(Fast(api), 2);

        var interrupted = Assert.IsType<ClusterInterrupted>(changes[0]);
        Assert.True(interrupted.Forbidden);
        Assert.Contains("forbidden", interrupted.Reason);
        Assert.IsType<ClusterInterrupted>(changes[1]);
    }

    sealed record Watch(IReadOnlyList<WatchItem> Items, WatchFailure? Failure = null, TimeSpan? Linger = null);

    sealed class ScriptedApi : IKindApi
    {
        public List<(IReadOnlyList<ClusterObject> Items, string? Rv)> Lists { get; } = [];

        public List<Watch> Watches { get; } = [];

        public WatchFailure? ListFailure { get; init; }

        public List<string?> WatchedFrom { get; } = [];

        public List<bool> AskedForInitialEvents { get; } = [];

        public int ListCalls { get; private set; }

        int _watch;

        public ClusterKind Kind => ClusterKind.Namespace;

        public Task<(IReadOnlyList<ClusterObject> Items, string? ResourceVersion)> ListAsync(CancellationToken ct)
        {
            if (ListFailure is { } failure)
                throw new k8s.Autorest.HttpOperationException(failure.Message)
                {
                    Response = new k8s.Autorest.HttpResponseMessageWrapper(new HttpResponseMessage((System.Net.HttpStatusCode)failure.Code!.Value), "{\"message\":\"" + failure.Message + "\"}"),
                };

            var list = Lists[Math.Min(ListCalls, Lists.Count - 1)];
            ListCalls++;
            return Task.FromResult(list);
        }

        public async IAsyncEnumerable<WatchItem> WatchAsync(
            string? resourceVersion, bool sendInitialEvents, int timeoutSeconds, Action<WatchFailure> onError,
            [EnumeratorCancellation] CancellationToken ct)
        {
            WatchedFrom.Add(resourceVersion);
            AskedForInitialEvents.Add(sendInitialEvents);

            if (_watch >= Watches.Count)
            {
                // Nothing more scripted: hold the watch open, as a quiet one would.
                await Task.Delay(Timeout.Infinite, ct);
                yield break;
            }

            var watch = Watches[_watch++];
            foreach (var item in watch.Items) yield return item;

            if (watch.Linger is { } linger) await Task.Delay(linger, ct);
            if (watch.Failure is { } failure) onError(failure);
        }
    }
}
