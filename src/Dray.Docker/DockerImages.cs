using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Docker.DotNet;
using Docker.DotNet.Models;
using Dray.Core.Engine;
using Dray.Core.Model;

namespace Dray.Docker;

/// <summary>Images, over the Engine API.</summary>
public static class DockerImages
{
    public static async Task<IReadOnlyList<ImageSummary>> ListAsync(
        DockerClient client, bool includeDangling, CancellationToken ct = default)
    {
        // All: true returns intermediate build layers as well, which are not images anyone thinks
        // they have. Dangling images — a previous build with no tag — are a different thing and
        // come back either way.
        var responses = await client.Images
            .ListImagesAsync(new ImagesListParameters { All = false }, ct)
            .ConfigureAwait(false);

        var images = responses.Select(Map).ToList();

        if (!includeDangling) images.RemoveAll(i => i.IsDangling);

        return
        [
            .. images
                // Dangling last: they are junk, and putting them at the top buries the images the
                // user recognises under a list of untagged ids.
                .OrderBy(i => i.IsDangling)
                .ThenBy(i => i.RepositoryKey, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(i => i.Created),
        ];
    }

    internal static ImageSummary Map(ImagesListResponse r) => new()
    {
        Id = r.ID,

        // "<none>:<none>" is how the engine spells "no tag"; treating it as a tag would produce a
        // list of identically named images.
        Tags =
        [
            .. (r.RepoTags ?? [])
                .Where(t => !string.IsNullOrEmpty(t) && !t.Contains("<none>", StringComparison.Ordinal))
                .Select(ImageTag.Parse),
        ],

        Digests = [.. (r.RepoDigests ?? []).Where(d => !d.Contains("<none>", StringComparison.Ordinal))],
        Created = DockerTime.From(r.Created),
        SizeBytes = r.Size,
        SharedBytes = r.SharedSize > 0 ? r.SharedSize : 0,

        // The engine reports -1 when it has not counted, which is not the same as zero.
        ContainerCount = (int)r.Containers,

        Labels = r.Labels is { Count: > 0 } labels
            ? new Dictionary<string, string>(labels, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal),
    };

    public static async Task<IReadOnlyList<ImageLayer>> HistoryAsync(
        DockerClient client, string imageId, CancellationToken ct = default)
    {
        try
        {
            var history = await client.Images.GetImageHistoryAsync(imageId, ct).ConfigureAwait(false);

            // The engine returns newest first, which is the order a Dockerfile is read in reverse.
            // Kept as-is: the last instruction is the one being debugged.
            return
            [
                .. history.Select(h => new ImageLayer(
                    h.ID,
                    DockerTime.From(h.Created),
                    h.Size,
                    h.CreatedBy ?? string.Empty,
                    string.IsNullOrWhiteSpace(h.Comment) ? null : h.Comment)),
            ];
        }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 404)
        {
            throw new RuntimeConnectionException("That image no longer exists.", ex);
        }
    }

    public static async Task RemoveAsync(
        DockerClient client, string imageId, bool force, CancellationToken ct = default)
    {
        try
        {
            await client.Images
                .DeleteImageAsync(imageId, new ImageDeleteParameters { Force = force, NoPrune = false }, ct)
                .ConfigureAwait(false);
        }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 409)
        {
            throw new RuntimeConnectionException(
                "A container is still using this image. Remove the container first, or force the removal.", ex);
        }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 404)
        {
            throw new RuntimeConnectionException("That image no longer exists.", ex);
        }
    }

    public static async Task TagAsync(
        DockerClient client, string imageId, string repository, string tag, CancellationToken ct = default)
    {
        try
        {
            await client.Images
                .TagImageAsync(imageId, new ImageTagParameters { RepositoryName = repository, Tag = tag }, ct)
                .ConfigureAwait(false);
        }
        catch (DockerApiException ex)
        {
            throw new RuntimeConnectionException($"Could not tag the image: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Pull an image, forwarding the engine's per-layer progress.
    /// <para>
    /// Unbounded channel, unlike the stats stream: dropping a progress message loses a layer's
    /// completion and leaves a bar stuck at 90% forever. A pull's messages are bounded by the
    /// number of layers anyway.
    /// </para>
    /// </summary>
    public static async IAsyncEnumerable<PullProgress> PullAsync(
        DockerClient client,
        string reference,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<PullProgress>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        var progress = new Progress<JSONMessage>(m => channel.Writer.TryWrite(Map(m)));
        var image = ImageTag.Parse(reference);

        var monitor = Task.Run(async () =>
        {
            try
            {
                await client.Images.CreateImageAsync(
                    new ImagesCreateParameters { FromImage = image.Repository, Tag = image.Tag },
                    authConfig: null,
                    progress,
                    ct).ConfigureAwait(false);

                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var step in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return step;
        }
        finally
        {
            await monitor.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    internal static PullProgress Map(JSONMessage m) => new(
        LayerId: string.IsNullOrEmpty(m.ID) ? null : m.ID,
        Status: m.Status ?? m.Stream?.Trim() ?? string.Empty,
        Current: m.Progress?.Current ?? 0,
        Total: m.Progress?.Total ?? 0,

        // The engine reports a failure inside the stream rather than by failing the request, so a
        // pull that cannot authenticate looks like a success unless this is read.
        Error: string.IsNullOrWhiteSpace(m.Error?.Message) ? null : m.Error.Message);
}
