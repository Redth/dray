using System.Collections.Concurrent;
using Dray.Core.Model;

namespace Dray.Core.Engine;

/// <summary>How a container list changed, so a view can re-render one row instead of all of them.</summary>
public enum StoreChangeKind
{
    /// <summary>The whole set was replaced — a cold list on connect, or a host switch.</summary>
    Reset,

    Added,
    Updated,
    Removed,
}

public sealed record StoreChange(StoreChangeKind Kind, string? ContainerId = null)
{
    public static readonly StoreChange Reset = new(StoreChangeKind.Reset);
}

/// <summary>
/// The app's view of one host's containers.
/// <para>
/// A cold list seeds it once on connect; after that only events mutate it. There is no timer that
/// re-fetches the list, because a 400-container table must not re-render because one container
/// stopped (PRODUCT.md).
/// </para>
/// </summary>
public sealed class EntityStore
{
    readonly ConcurrentDictionary<string, ContainerSummary> _containers = new(StringComparer.Ordinal);

    /// <summary>Ids whose row changed recently, so the view can pulse them once (DESIGN.md section 7).</summary>
    readonly ConcurrentDictionary<string, DateTimeOffset> _recentlyChanged = new(StringComparer.Ordinal);

    readonly TimeProvider _time;

    public EntityStore(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>How long a row counts as recently changed.</summary>
    public TimeSpan ChangeHighlightWindow { get; init; } = TimeSpan.FromMilliseconds(600);

    /// <summary>Raised for every mutation. Views subscribe to this, not to a service.</summary>
    public event Action<StoreChange>? Changed;

    public int Count => _containers.Count;

    /// <summary>
    /// A stable snapshot, ordered so the list does not reshuffle under the user: running first,
    /// then by name.
    /// </summary>
    public IReadOnlyList<ContainerSummary> Containers =>
        [.. _containers.Values
            .OrderByDescending(c => c.State == DockerState.Running)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];

    public ContainerSummary? Find(string id) => _containers.GetValueOrDefault(id);

    /// <summary>True while the row should carry the "just changed" pulse.</summary>
    public bool WasRecentlyChanged(string id)
        => _recentlyChanged.TryGetValue(id, out var at)
           && _time.GetUtcNow() - at < ChangeHighlightWindow;

    /// <summary>Seed from a cold list. Used once per connection, and on host switch.</summary>
    public void Reset(IEnumerable<ContainerSummary> containers)
    {
        _containers.Clear();
        _recentlyChanged.Clear();

        foreach (var c in containers) _containers[c.Id] = c;

        Changed?.Invoke(StoreChange.Reset);
    }

    /// <summary>Drop everything — the host went away or was switched.</summary>
    public void Clear() => Reset([]);

    /// <summary>
    /// Mark every container stale rather than removing them.
    /// <para>
    /// When a host becomes unreachable the containers still exist; Dray simply cannot see them.
    /// Emptying the list would claim they are gone, which is a different and false statement.
    /// </para>
    /// </summary>
    public void MarkAllStale()
    {
        foreach (var (id, container) in _containers)
        {
            if (container.State == DockerState.Unknown) continue;
            _containers[id] = container with { State = DockerState.Unknown };
        }

        Changed?.Invoke(StoreChange.Reset);
    }

    public void Upsert(ContainerSummary container)
    {
        var existed = _containers.ContainsKey(container.Id);
        _containers[container.Id] = container;

        Touch(container.Id);
        Changed?.Invoke(new StoreChange(existed ? StoreChangeKind.Updated : StoreChangeKind.Added, container.Id));
    }

    public void Remove(string id)
    {
        if (!_containers.TryRemove(id, out _)) return;

        _recentlyChanged.TryRemove(id, out _);
        Changed?.Invoke(new StoreChange(StoreChangeKind.Removed, id));
    }

    /// <summary>
    /// Apply one engine event.
    /// <para>
    /// Returns true when the caller must fetch the container, because the event says something
    /// changed but does not carry enough to describe the new state. Events that only change
    /// lifecycle or health are applied directly, which is what keeps a busy engine from producing
    /// a fetch per event.
    /// </para>
    /// </summary>
    public bool Apply(RuntimeEvent e)
    {
        if (e.Entity != RuntimeEntity.Container) return false;

        if (e.IsRemoval)
        {
            Remove(e.Id);
            return false;
        }

        var existing = _containers.GetValueOrDefault(e.Id);
        if (existing is null)
        {
            // Something we have never seen. The event alone cannot describe it — no image, no
            // ports — so the caller has to fetch it.
            return true;
        }

        // Docker reports health either as the bare action with an attribute, or as
        // `health_status: unhealthy` in the action itself, depending on API version. Normalising
        // first means the switch below does not have to know that.
        var action = e.Action.Contains(':') ? e.Action[..e.Action.IndexOf(':')].Trim() : e.Action;

        var updated = action switch
        {
            "start" or "unpause" or "restart" => existing with { State = DockerState.Running, ExitCode = null, Since = e.Timestamp },
            "pause" => existing with { State = DockerState.Paused, Since = e.Timestamp },
            "kill" => existing,   // "die" follows with the exit code; acting now would flicker.
            "die" => existing with { State = DockerState.Exited, ExitCode = ParseExitCode(e), Since = e.Timestamp },
            "stop" => existing with { State = DockerState.Exited, Since = e.Timestamp },
            "destroy" => existing,
            "health_status" => existing with { Health = ParseHealth(e) },
            "rename" => existing with { Name = e.Name ?? existing.Name },

            // create/attach/exec_start/top and friends do not change what the list shows.
            _ => existing,
        };

        if (ReferenceEquals(updated, existing)) return false;

        _containers[e.Id] = updated;
        Touch(e.Id);
        Changed?.Invoke(new StoreChange(StoreChangeKind.Updated, e.Id));
        return false;
    }

    void Touch(string id) => _recentlyChanged[id] = _time.GetUtcNow();

    static int? ParseExitCode(RuntimeEvent e)
        => e.Attributes.TryGetValue("exitCode", out var raw) && int.TryParse(raw, out var code)
            ? code
            : null;

    static DockerHealth ParseHealth(RuntimeEvent e)
    {
        // Docker reports this as `health_status: healthy` in the action, or as an attribute
        // depending on API version. Accept both rather than depending on one.
        var value = e.Attributes.GetValueOrDefault("health_status")
            ?? (e.Action.Contains(':') ? e.Action[(e.Action.IndexOf(':') + 1)..] : null);

        return value?.Trim().ToLowerInvariant() switch
        {
            "healthy" => DockerHealth.Healthy,
            "unhealthy" => DockerHealth.Unhealthy,
            "starting" => DockerHealth.Starting,
            _ => DockerHealth.None,
        };
    }
}
