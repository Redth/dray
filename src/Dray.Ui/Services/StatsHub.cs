using Dray.Core.Engine;
using Dray.Core.Model;

namespace Dray.Ui.Services;

/// <summary>
/// Keeps live resource readings for a set of containers.
/// <para>
/// The engine has no endpoint that reports usage for everything at once — <c>docker stats</c> opens
/// one stream per container and so must Dray. That is the one place in the app where cost scales
/// with the number of rows on screen, so it is opt-in and bounded rather than always on: a hundred
/// idle streams against a busy engine is a real load, and most of the time nobody is looking at the
/// numbers.
/// </para>
/// </summary>
public sealed class StatsHub(EngineManager engine) : IAsyncDisposable
{
    /// <summary>
    /// How many containers are sampled at once.
    /// <para>
    /// Each is a held HTTP connection. Beyond this the list stops adding streams rather than
    /// quietly opening two hundred — a limit that is visible in the UI is better than an engine
    /// that starts refusing connections.
    /// </para>
    /// </summary>
    public const int MaxStreams = 24;

    /// <summary>Samples kept per container. Two minutes at roughly one a second.</summary>
    const int HistoryDepth = 120;

    readonly Dictionary<string, Watch> _watches = new(StringComparer.Ordinal);
    readonly Lock _gate = new();

    bool _disposed;

    /// <summary>Raised when any watched container has a new reading. Coalesced by the caller.</summary>
    public event Action? Updated;

    /// <summary>True when the last <see cref="WatchAsync"/> had to leave containers unsampled.</summary>
    public bool AtCapacity { get; private set; }

    public StatsHistory? For(string containerId)
    {
        lock (_gate) return _watches.GetValueOrDefault(containerId)?.History;
    }

    public ContainerStats? Latest(string containerId) => For(containerId)?.Latest;

    /// <summary>
    /// Sample exactly these containers: start streams for the new ones, stop the rest.
    /// <para>
    /// Called with what is on screen, so scrolling a long list moves the sampling with it rather
    /// than accumulating streams for rows nobody is looking at any more.
    /// </para>
    /// </summary>
    public async Task WatchAsync(IEnumerable<string> containerIds)
    {
        if (_disposed) return;

        var wanted = containerIds.Take(MaxStreams).ToHashSet(StringComparer.Ordinal);
        AtCapacity = containerIds.Count() > MaxStreams;

        List<Watch> stopping = [];

        lock (_gate)
        {
            foreach (var (id, watch) in _watches)
            {
                if (!wanted.Contains(id)) stopping.Add(watch);
            }

            foreach (var watch in stopping) _watches.Remove(watch.ContainerId);

            foreach (var id in wanted)
            {
                if (_watches.ContainsKey(id)) continue;

                var watch = new Watch(id, new StatsHistory(HistoryDepth), new CancellationTokenSource());
                _watches[id] = watch;

                _ = PumpAsync(watch);
            }
        }

        foreach (var watch in stopping) await watch.StopAsync().ConfigureAwait(false);
    }

    /// <summary>Stop everything. Called when the feature is switched off or the page goes away.</summary>
    public async Task ClearAsync()
    {
        List<Watch> stopping;

        lock (_gate)
        {
            stopping = [.. _watches.Values];
            _watches.Clear();
        }

        AtCapacity = false;

        foreach (var watch in stopping) await watch.StopAsync().ConfigureAwait(false);
    }

    async Task PumpAsync(Watch watch)
    {
        try
        {
            await foreach (var sample in engine.StreamStatsAsync(watch.ContainerId, watch.Cancellation.Token))
            {
                watch.History.Add(sample);
                Updated?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A container that stopped, an engine that refused, a dropped connection. The row
            // simply shows no reading; a failed sample is not worth interrupting a list for.
        }
        finally
        {
            // Leave the entry so the last reading stays on screen rather than blanking the moment
            // a container stops — but let a later WatchAsync restart it.
            lock (_gate)
            {
                if (_watches.TryGetValue(watch.ContainerId, out var current) && current == watch)
                    _watches.Remove(watch.ContainerId);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await ClearAsync().ConfigureAwait(false);
    }

    sealed record Watch(string ContainerId, StatsHistory History, CancellationTokenSource Cancellation)
    {
        public async Task StopAsync()
        {
            await Cancellation.CancelAsync().ConfigureAwait(false);
            Cancellation.Dispose();
        }
    }
}
