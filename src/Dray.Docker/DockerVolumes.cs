using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Models;
using Dray.Core.Engine;
using Dray.Core.Model;

namespace Dray.Docker;

/// <summary>Listing volumes, and working out which containers hold them.</summary>
public static class DockerVolumes
{
    public static async Task<IReadOnlyList<VolumeSummary>> ListAsync(DockerClient client, CancellationToken ct = default)
    {
        var response = await client.Volumes.ListAsync(ct).ConfigureAwait(false);

        // The volumes endpoint does not say who is using a volume, but that is the one fact worth
        // knowing before deleting one — so it is joined in from the container list rather than
        // left for the user to work out.
        var usage = await BuildUsageMapAsync(client, ct).ConfigureAwait(false);

        return
        [
            .. (response.Volumes ?? [])
                .Select(v => Map(v, usage.GetValueOrDefault(v.Name) ?? []))

                // In-use first, then by name: a volume nothing holds is the one that is safe to
                // remove and the one most likely to be junk, so it should not be at the top.
                .OrderByDescending(v => v.IsInUse)
                .ThenBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase),
        ];
    }

    internal static VolumeSummary Map(VolumeResponse v, IReadOnlyList<string> usedBy) => new()
    {
        Name = v.Name,
        // The API sends an empty string rather than null when it has nothing, so a null-coalesce
        // alone would leave the column blank.
        Driver = string.IsNullOrWhiteSpace(v.Driver) ? "local" : v.Driver,
        Mountpoint = string.IsNullOrWhiteSpace(v.Mountpoint) ? null : v.Mountpoint,
        Created = ParseCreated(v.CreatedAt),
        Labels = v.Labels is { Count: > 0 } labels
            ? new Dictionary<string, string>(labels, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal),
        UsedBy = usedBy,

        // Only populated by `system df`; a plain list would have to walk the volume to know.
        SizeBytes = v.UsageData?.Size is > 0 ? v.UsageData.Size : null,
    };

    static DateTimeOffset? ParseCreated(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    static async Task<Dictionary<string, List<string>>> BuildUsageMapAsync(DockerClient client, CancellationToken ct)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        var containers = await client.Containers
            .ListContainersAsync(new ContainersListParameters { All = true }, ct)
            .ConfigureAwait(false);

        foreach (var container in containers)
        {
            // Dray's own volume-browser helpers mount the volume too. Counting them would tell the
            // user their volume is in use by something they never started, and would make an
            // unused volume look held the moment they looked inside it.
            if (container.Labels?.ContainsKey(DockerVolumeSession.HelperLabel) == true) continue;

            var name = container.Names?.FirstOrDefault()?.TrimStart('/') ?? container.ID[..12];

            foreach (var mount in container.Mounts ?? [])
            {
                // Binds have a host path and no name; only named volumes belong in this map.
                if (!string.Equals(mount.Type, "volume", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(mount.Name)) continue;

                if (!map.TryGetValue(mount.Name, out var users)) map[mount.Name] = users = [];
                if (!users.Contains(name)) users.Add(name);
            }
        }

        return map;
    }
}

/// <summary>
/// Browsing a volume by mounting it into a container that never runs.
/// <para>
/// The Engine API exposes storage only through containers — there is no way to read a volume
/// directly. So Dray creates one with the volume mounted and reads through the archive endpoint,
/// which operates on a container's filesystem including its mounts and does <b>not</b> require the
/// container to have been started. That matters twice over: nothing is executed inside the
/// container, so browsing a volume cannot run anything, and the helper image needs no shell, no
/// <c>ls</c>, and no working entrypoint — it is never executed at all.
/// </para>
/// <para>
/// The helper is created from an image already on the host wherever possible, so opening a volume
/// does not quietly pull from a registry.
/// </para>
/// </summary>
public sealed class DockerVolumeSession : IVolumeSession
{
    /// <summary>
    /// Where the volume is mounted inside the helper. Namespaced so it cannot collide with a path
    /// the image already has, and recognisable if a helper ever survives a crash.
    /// </summary>
    internal const string MountPath = "/dray-volume";

    internal const string HelperLabel = "codes.redth.dray.helper";

    /// <summary>Pulled only when the host has no image at all to borrow. Small and universally available.</summary>
    const string FallbackImage = "docker.io/library/alpine:latest";

    readonly DockerClient _client;
    readonly string _helperId;

    DockerVolumeSession(DockerClient client, string volumeName, string helperId)
    {
        _client = client;
        VolumeName = volumeName;
        _helperId = helperId;
    }

    public string VolumeName { get; }

    public static async Task<DockerVolumeSession> OpenAsync(
        DockerClient client, string volumeName, CancellationToken ct = default)
    {
        var image = await ChooseImageAsync(client, ct).ConfigureAwait(false);

        try
        {
            var created = await client.Containers.CreateContainerAsync(
                new CreateContainerParameters
                {
                    // Named rather than left to the engine's random word pairs. A helper that
                    // outlives its session — Dray killed rather than closed — should announce what
                    // it is and what it was for, not appear as "fervent_benz" next to the user's
                    // real containers. The suffix keeps two views of one volume from colliding.
                    Name = HelperNameFor(volumeName, Guid.NewGuid().ToString("n")[..6]),

                    Image = image,

                    // Never executed. Present because some engines reject a container with no
                    // command at all, and `true` is the most inert thing to name.
                    Cmd = ["/bin/true"],

                    Labels = new Dictionary<string, string>
                    {
                        [HelperLabel] = "volume-browser",
                        ["codes.redth.dray.volume"] = volumeName,
                    },
                    HostConfig = new HostConfig
                    {
                        Binds = [$"{volumeName}:{MountPath}"],

                        // Belt and braces: if this container is ever started by accident, it exits
                        // immediately and removes itself rather than lingering.
                        AutoRemove = true,
                    },
                },
                ct).ConfigureAwait(false);

            return new DockerVolumeSession(client, volumeName, created.ID);
        }
        catch (DockerApiException ex)
        {
            throw new RuntimeConnectionException(
                $"Could not open volume '{volumeName}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Pick an image that is already on the host.
    /// <para>
    /// The helper never runs, so any image will do and the only thing that matters is not pulling.
    /// The smallest local image is preferred purely to keep the helper's own layer references
    /// cheap; a busybox or alpine is preferred over that because a stray helper container is much
    /// less alarming to find named after one of those than after the user's production image.
    /// </para>
    /// </summary>
    static async Task<string> ChooseImageAsync(DockerClient client, CancellationToken ct)
    {
        IList<ImagesListResponse> images;

        try
        {
            images = await client.Images
                .ListImagesAsync(new ImagesListParameters { All = false }, ct)
                .ConfigureAwait(false);
        }
        catch (DockerApiException)
        {
            return await PullFallbackAsync(client, ct).ConfigureAwait(false);
        }

        var tagged = images
            .Where(i => i.RepoTags is { Count: > 0 })
            .SelectMany(i => i.RepoTags.Select(tag => (Tag: tag, i.Size)))

            // "<none>:<none>" is a dangling image; naming one would make the helper look broken.
            .Where(i => !i.Tag.Contains("<none>", StringComparison.Ordinal))
            .ToList();

        if (tagged.Count == 0) return await PullFallbackAsync(client, ct).ConfigureAwait(false);

        // Matched on the repository, not the whole reference: "library/nginx:alpine" contains
        // "alpine" and is emphatically not a scratch image to borrow.
        var preferred = tagged.FirstOrDefault(i => IsThrowawayRepositoryFor(i.Tag));

        return preferred.Tag ?? tagged.OrderBy(i => i.Size).First().Tag;
    }

    /// <summary>
    /// Whether a reference names an image worth borrowing as an inert helper.
    /// <para>
    /// Matched on the repository rather than the whole reference: <c>library/nginx:alpine</c>
    /// contains "alpine" and is the user's web server.
    /// </para>
    /// </summary>
    internal static bool IsThrowawayRepositoryFor(string reference)
    {
        var repository = Repository(reference);
        var name = repository[(repository.LastIndexOf('/') + 1)..];

        return name is "busybox" or "alpine";
    }

    static string Repository(string reference)
    {
        // Strip the tag, taking care not to cut at the colon in a "host:port/repo" registry.
        var slash = reference.LastIndexOf('/');
        var colon = reference.LastIndexOf(':');

        return colon > slash ? reference[..colon] : reference;
    }

    /// <summary>
    /// What to call the helper for a volume.
    /// <para>
    /// Named rather than left to the engine's random word pairs, so a helper that outlives its
    /// session announces what it is and what it was for. The suffix keeps two views of the same
    /// volume from colliding on the name.
    /// </para>
    /// </summary>
    internal static string HelperNameFor(string volumeName, string suffix)
    {
        var cleaned = new string(
            [.. volumeName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);

        // An anonymous volume's name is 64 characters of hex, which would make an unreadable and
        // needlessly long container name.
        if (cleaned.Length > 24) cleaned = cleaned[..24];

        return $"dray-browse-{cleaned}-{suffix}";
    }

    static async Task<string> PullFallbackAsync(DockerClient client, CancellationToken ct)
    {
        try
        {
            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = FallbackImage },
                authConfig: null,
                new Progress<JSONMessage>(),
                ct).ConfigureAwait(false);

            return FallbackImage;
        }
        catch (DockerApiException ex)
        {
            throw new RuntimeConnectionException(
                "Opening a volume needs an image to mount it into, this host has none, and pulling one failed. "
                + "Pull any image and try again.", ex);
        }
    }

    /// <summary>Map a volume-relative path onto the helper's mount point.</summary>
    internal static string ToHelperPath(string path)
    {
        var normalized = FileEntry.Normalize(path);
        return normalized == "/" ? MountPath : MountPath + normalized;
    }

    /// <summary>Map a helper path back, so nothing above this class ever sees the mount point.</summary>
    internal static string ToVolumePath(string helperPath)
    {
        var normalized = FileEntry.Normalize(helperPath);

        if (normalized == MountPath) return "/";

        return normalized.StartsWith(MountPath + "/", StringComparison.Ordinal)
            ? normalized[MountPath.Length..]
            : normalized;
    }

    public async Task<DirectoryListing> ListDirectoryAsync(string path, CancellationToken ct = default)
    {
        // The helper is never started, so exec is not an option and the archive route is the only
        // one. Saying so up front avoids a guaranteed-to-fail exec attempt per directory.
        var listing = await DockerFileSystem
            .ListAsync(_client, _helperId, ToHelperPath(path), containerIsRunning: false, ct)
            .ConfigureAwait(false);

        return listing with
        {
            Path = ToVolumePath(listing.Path),
            Entries = [.. listing.Entries.Select(e => e with { Path = ToVolumePath(e.Path) })],

            // The archive route's note explains that the container is not running, which is true
            // and meaningless here: the helper is an implementation detail the user never sees and
            // has no reason to reason about. A truncation note is kept, because that one describes
            // the listing rather than how it was obtained.
            Note = listing.IsTruncated ? listing.Note : null,
        };
    }

    public Task<byte[]> ReadFileAsync(string path, CancellationToken ct = default)
        => DockerFileSystem.ReadFileAsync(_client, _helperId, ToHelperPath(path), ct);

    public Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default)
        => DockerFileSystem.WriteFileAsync(_client, _helperId, ToHelperPath(path), content, ct);

    /// <summary>
    /// Remove helper containers left behind by a previous run.
    /// <para>
    /// A session removes its own helper on disposal, but a Dray that was killed rather than closed
    /// never gets there — and the helpers accumulate silently in the user's container list, one
    /// per volume they ever opened. Swept at connect, when it is certain no session of this
    /// process owns one.
    /// </para>
    /// </summary>
    public static async Task<int> SweepOrphansAsync(DockerClient client, CancellationToken ct = default)
    {
        try
        {
            var orphans = await client.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["label"] = new Dictionary<string, bool> { [HelperLabel] = true },
                    },
                },
                ct).ConfigureAwait(false);

            var removed = 0;

            foreach (var orphan in orphans)
            {
                try
                {
                    await client.Containers.RemoveContainerAsync(
                        orphan.ID,
                        new ContainerRemoveParameters { Force = true, RemoveVolumes = false },
                        ct).ConfigureAwait(false);

                    removed++;
                }
                catch (DockerApiException)
                {
                    // Someone else removed it, or it is wedged. Neither is worth failing a connect.
                }
            }

            return removed;
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TaskCanceledException)
        {
            // Housekeeping. An engine that will not answer this has bigger problems, and they will
            // surface on the next call the user actually asked for.
            return 0;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // RemoveVolumes stays false, emphatically. This container exists only to expose the
            // volume; removing it must never take the volume with it.
            await _client.Containers.RemoveContainerAsync(
                _helperId,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = false },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (DockerApiException)
        {
            // Already gone, or the engine went away. Either way there is nothing useful to tell
            // the user while they are navigating away from the screen that created it.
        }
    }
}
