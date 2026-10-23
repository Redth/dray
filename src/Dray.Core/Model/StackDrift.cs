namespace Dray.Core.Model;

/// <summary>How one service's file compares with what is running.</summary>
public enum ServiceDrift
{
    /// <summary>The running containers were created from this configuration.</summary>
    Unchanged,

    /// <summary>The file says something else now. Deploy recreates these.</summary>
    Changed,

    /// <summary>In the file, no container. Deploy creates it.</summary>
    NotCreated,

    /// <summary>Running, and no longer in the file. Deploy with remove-orphans takes it away.</summary>
    Removed,

    /// <summary>The engine reported no hash for it, so there is nothing honest to compare.</summary>
    Unknown,
}

/// <summary>
/// "Edited, not deployed" — what Deploy would change, read without running anything.
/// <para>
/// Compose stamps every container with a hash of the service configuration it was created from,
/// and <c>compose config --hash</c> prints the same hash for the file as it is now. They were
/// measured equal for an unedited file and different for exactly the edited service, so the
/// comparison is compose's own definition of "changed" — the one <c>up</c> uses to decide what to
/// recreate. No dry run, no polling, and no second opinion from Dray about what counts.
/// </para>
/// </summary>
public static class StackDrift
{
    /// <param name="hashes">Service → hash, from <c>compose config --hash '*'</c>.</param>
    public static IReadOnlyDictionary<string, ServiceDrift> Compare(StackSummary stack, IReadOnlyDictionary<string, string> hashes)
    {
        var drift = new Dictionary<string, ServiceDrift>(StringComparer.Ordinal);

        foreach (var service in stack.Services)
        {
            if (!hashes.TryGetValue(service.Name, out var wanted))
            {
                drift[service.Name] = service.Replicas.Count > 0 ? ServiceDrift.Removed : ServiceDrift.Unknown;
                continue;
            }

            if (service.Replicas.Count == 0)
            {
                drift[service.Name] = ServiceDrift.NotCreated;
                continue;
            }

            var running = service.Replicas.Select(r => r.Compose?.ConfigHash).ToList();

            drift[service.Name] = running.Any(h => h is null)
                ? ServiceDrift.Unknown
                : running.All(h => h == wanted) ? ServiceDrift.Unchanged : ServiceDrift.Changed;
        }

        foreach (var name in hashes.Keys.Where(n => !drift.ContainsKey(n))) drift[name] = ServiceDrift.NotCreated;

        return drift;
    }

    /// <summary>Read <c>compose config --hash</c> output: one <c>service hash</c> per line.</summary>
    public static IReadOnlyDictionary<string, string> ParseHashes(string? output)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(output)) return hashes;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Length >= 32 && parts[1].All(char.IsAsciiHexDigit)) hashes[parts[0]] = parts[1];
        }

        return hashes;
    }
}
