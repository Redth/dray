using Docker.DotNet;
using Docker.DotNet.Models;
using Dray.Core.Model;

namespace Dray.Docker;

/// <summary>
/// Pruning, and — the part that matters — saying what a prune would remove first.
/// <para>
/// No engine offers a dry run, so the preview is computed by applying the same rule the engine
/// applies and naming what it selects. PRODUCT.md requires the preview to match reality, so where
/// the two could disagree this errs toward listing something that survives rather than quietly
/// deleting something unlisted.
/// </para>
/// </summary>
public static class DockerPrune
{
    public static async Task<PrunePreview> PreviewAsync(
        DockerClient client, PruneKind kind, CancellationToken ct = default)
        => kind switch
        {
            PruneKind.Images => await PreviewImagesAsync(client, ct).ConfigureAwait(false),
            PruneKind.Containers => await PreviewContainersAsync(client, ct).ConfigureAwait(false),
            PruneKind.Volumes => await PreviewVolumesAsync(client, ct).ConfigureAwait(false),
            PruneKind.Networks => await PreviewNetworksAsync(client, ct).ConfigureAwait(false),
            _ => PrunePreview.Empty(kind),
        };

    /// <summary>
    /// Dangling images only — the default <c>docker image prune</c>, not <c>-a</c>.
    /// <para>
    /// Dray does not offer the <c>-a</c> form from a button. "Delete every image no container is
    /// currently using" reads as tidying and means re-pulling everything, and it is one click away
    /// from being catastrophic on a metered connection.
    /// </para>
    /// </summary>
    static async Task<PrunePreview> PreviewImagesAsync(DockerClient client, CancellationToken ct)
    {
        var images = await DockerImages.ListAsync(client, includeDangling: true, ct).ConfigureAwait(false);
        var dangling = images.Where(i => i.IsDangling).ToList();

        return new PrunePreview(
            PruneKind.Images,
            [.. dangling.Select(i => $"{i.ShortId} · {Size(i.UniqueBytes)}")],

            // Unique bytes, not size: a dangling image sharing every layer with a tagged one frees
            // nothing, and promising its full size back is the single easiest number to get wrong.
            dangling.Sum(i => i.UniqueBytes));
    }

    static async Task<PrunePreview> PreviewContainersAsync(DockerClient client, CancellationToken ct)
    {
        var containers = await client.Containers
            .ListContainersAsync(new ContainersListParameters { All = true }, ct)
            .ConfigureAwait(false);

        var stopped = containers
            .Where(c => !string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(c.State, "paused", StringComparison.OrdinalIgnoreCase))

            // Dray's own volume-browser helpers are not the user's containers and are swept
            // separately; listing them here would be offering to delete something they never made.
            .Where(DockerRuntime.IsUsersOwn)
            .ToList();

        return new PrunePreview(
            PruneKind.Containers,
            [.. stopped.Select(c => c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.ID[..12])],

            // The engine reports a stopped container's writable-layer size only when asked with
            // size=true, which is expensive. Left at zero rather than guessed — the names are what
            // the confirmation is really about.
            0);
    }

    static async Task<PrunePreview> PreviewVolumesAsync(DockerClient client, CancellationToken ct)
    {
        var volumes = await DockerVolumes.ListAsync(client, ct).ConfigureAwait(false);
        var unused = volumes.Where(v => !v.IsInUse).ToList();

        return new PrunePreview(
            PruneKind.Volumes,
            [.. unused.Select(v => v.SizeBytes is { } size ? $"{v.DisplayName} · {Size(size)}" : v.DisplayName)],
            unused.Sum(v => v.SizeBytes ?? 0));
    }

    static async Task<PrunePreview> PreviewNetworksAsync(DockerClient client, CancellationToken ct)
    {
        var networks = await DockerNetworks.ListAsync(client, ct).ConfigureAwait(false);

        var unused = networks
            .Where(n => !n.IsInUse && !n.IsPredefined)
            .ToList();

        return new PrunePreview(PruneKind.Networks, [.. unused.Select(n => n.Name)], 0);
    }

    public static async Task<PruneResult> RunAsync(
        DockerClient client, PruneKind kind, CancellationToken ct = default)
    {
        switch (kind)
        {
            case PruneKind.Images:
            {
                // dangling=true is the engine's default, stated explicitly so a future default
                // change cannot quietly turn this into `prune -a`.
                var response = await client.Images
                    .PruneImagesAsync(new ImagesPruneParameters { Filters = Filter("dangling", "true") }, ct)
                    .ConfigureAwait(false);

                return new PruneResult(kind, response.ImagesDeleted?.Count ?? 0, (long)response.SpaceReclaimed);
            }

            case PruneKind.Containers:
            {
                var response = await client.Containers
                    .PruneContainersAsync(new ContainersPruneParameters(), ct)
                    .ConfigureAwait(false);

                return new PruneResult(kind, response.ContainersDeleted?.Count ?? 0, (long)response.SpaceReclaimed);
            }

            case PruneKind.Volumes:
            {
                var response = await client.Volumes.PruneAsync(new VolumesPruneParameters(), ct)
                    .ConfigureAwait(false);

                return new PruneResult(kind, response.VolumesDeleted?.Count ?? 0, (long)response.SpaceReclaimed);
            }

            case PruneKind.Networks:
            {
                var response = await client.Networks
                    .PruneNetworksAsync(new NetworksDeleteUnusedParameters(), ct)
                    .ConfigureAwait(false);

                return new PruneResult(kind, response.NetworksDeleted?.Count ?? 0, 0);
            }

            default:
                return new PruneResult(kind, 0, 0);
        }
    }

    static Dictionary<string, IDictionary<string, bool>> Filter(string key, string value)
        => new() { [key] = new Dictionary<string, bool> { [value] = true } };

    /// <summary>
    /// A size for a confirmation line. Deliberately not <c>Humanize.Bytes</c> — that lives in the
    /// UI layer, and this string is built where the engine's numbers are.
    /// </summary>
    static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
