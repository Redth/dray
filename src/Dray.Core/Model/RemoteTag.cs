namespace Dray.Core.Model;

/// <summary>The image a tag holds for one platform.</summary>
/// <param name="SizeBytes">Compressed — what a pull downloads — or -1 where the registry did not say.</param>
public sealed record TagImage(ImagePlatform Platform, long SizeBytes = -1);

/// <summary>One tag in a registry, with whatever the registry said about it.</summary>
/// <param name="Pushed">When the tag last moved. Null where the registry keeps no dates — the standard API does not.</param>
/// <param name="Images">
/// One per platform, or null when the listing did not say. Null and empty are different answers:
/// a plain tag list is names and nothing else, which is "unknown", not "built for nothing".
/// </param>
/// <param name="Digest">The digest the tag points at — an index for a multi-platform tag.</param>
public sealed record RemoteTag(
    string Name,
    DateTimeOffset? Pushed = null,
    IReadOnlyList<TagImage>? Images = null,
    string? Digest = null)
{
    /// <summary>The platforms, in the registry's order, without its attestations.</summary>
    public IReadOnlyList<ImagePlatform> Platforms =>
        Images is null ? [] : [.. Images.Select(i => i.Platform).Where(p => !p.IsAttestation).Distinct()];

    /// <summary>
    /// Whether this tag has a build that runs natively on <paramref name="engine"/>. Null when either
    /// side is unknown — which must stay unknown rather than become a no.
    /// </summary>
    public bool? RunsOn(ImagePlatform? engine)
        => engine is null || Images is null || Platforms.Count == 0 ? null : Platforms.Any(p => p.RunsOn(engine));

    /// <summary>The image a pull on <paramref name="engine"/> would fetch, when the registry said.</summary>
    public TagImage? ImageFor(ImagePlatform? engine)
        => engine is null || Images is null ? null : Images.FirstOrDefault(i => !i.Platform.IsAttestation && i.Platform.RunsOn(engine));
}

/// <summary>A repository's tags, as far as the registry would list them.</summary>
/// <param name="Total">How many the registry has, when it said; -1 otherwise.</param>
/// <param name="Truncated">
/// Dray stopped reading before the registry ran out. The standard API has no filter and no order,
/// so a repository with thousands of tags is read a page at a time up to a limit, and saying where
/// that limit fell is the honest alternative to a list that looks complete.
/// </param>
/// <param name="ByRecency">Ordered newest first by the registry. False means Dray ordered it by version.</param>
public sealed record TagListing(
    IReadOnlyList<RemoteTag> Tags,
    int Total = -1,
    bool Truncated = false,
    bool ByRecency = false);

/// <summary>
/// Putting tags in the order people look for them, for registries that hand over an unordered list.
/// <para>
/// The standard registry API returns names in lexical order, which puts <c>10.0</c> between
/// <c>1.9</c> and <c>2.0</c> and the newest release of a date-versioned project last. Docker Hub
/// orders by when each tag moved; nothing else does, so this is the next best thing: <c>latest</c>
/// first, then tags that begin with a version, highest first, then the rest by name.
/// </para>
/// </summary>
public static class TagOrder
{
    public static IReadOnlyList<RemoteTag> Newest(IEnumerable<RemoteTag> tags)
        => [.. tags.Order(Comparer<RemoteTag>.Create((a, b) => Compare(a.Name, b.Name)))];

    /// <summary>
    /// Tags containing <paramref name="filter"/>: the tag itself first, then the ones that start with
    /// it, then the rest, otherwise in the order given. Case-insensitive: <c>Alpine</c> is not a tag
    /// anyone has, and a filter that is fussier than the user is just a filter that finds nothing.
    /// <para>
    /// The exact tag leads because version order alone puts it last of its family: typing
    /// <c>2025.1</c> otherwise lists <c>2025.12</c> and its eleven patch releases above it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<RemoteTag> Filter(IReadOnlyList<RemoteTag> tags, string? filter)
    {
        if (string.IsNullOrEmpty(filter)) return tags;

        var exact = new List<RemoteTag>();
        var starts = new List<RemoteTag>();
        var contains = new List<RemoteTag>();

        foreach (var tag in tags)
        {
            if (tag.Name.Equals(filter, StringComparison.OrdinalIgnoreCase)) exact.Add(tag);
            else if (tag.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase)) starts.Add(tag);
            else if (tag.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) contains.Add(tag);
        }

        return [.. exact, .. starts, .. contains];
    }

    /// <summary>
    /// Negative when <paramref name="a"/> belongs above <paramref name="b"/>.
    /// <para>
    /// Within one version, the shorter tag first: <c>18</c> is the tag that floats to the newest 18.x
    /// and is usually the one wanted, so it sits above <c>18-alpine</c> and <c>18.0</c>. A release
    /// sits above its own pre-releases for the same reason — <c>2023.4.0</c> before <c>2023.4.0b1</c>.
    /// </para>
    /// </summary>
    internal static int Compare(string a, string b)
    {
        if (a == b) return 0;
        if (a == "latest") return -1;
        if (b == "latest") return 1;

        var (va, sa) = Version(a);
        var (vb, sb) = Version(b);

        if (va.Count > 0 && vb.Count == 0) return -1;
        if (va.Count == 0 && vb.Count > 0) return 1;

        if (va.Count > 0)
        {
            for (var i = 0; i < Math.Min(va.Count, vb.Count); i++)
            {
                var parts = Numeric(va[i], vb[i]);
                if (parts != 0) return -parts;
            }

            if (va.Count != vb.Count) return va.Count.CompareTo(vb.Count);

            // Same version: the plain release first, then its variants by name.
            if (sa.Length == 0 && sb.Length > 0) return -1;
            if (sa.Length > 0 && sb.Length == 0) return 1;

            return Natural(sa, sb);
        }

        return Natural(a, b);
    }

    /// <summary>
    /// The version a tag starts with — <c>v1.2.3-alpine</c> is <c>[1, 2, 3]</c> and <c>-alpine</c> —
    /// or nothing when it does not start with one. Parts stay digit strings: a date-stamped tag like
    /// <c>20210427</c> is a perfectly good version and too big for an int.
    /// </summary>
    internal static (IReadOnlyList<string> Parts, string Suffix) Version(string tag)
    {
        var i = tag.Length > 1 && tag[0] is 'v' or 'V' && char.IsAsciiDigit(tag[1]) ? 1 : 0;
        var parts = new List<string>();

        while (i < tag.Length && char.IsAsciiDigit(tag[i]))
        {
            var start = i;
            while (i < tag.Length && char.IsAsciiDigit(tag[i])) i++;
            parts.Add(tag[start..i]);

            // Another part only if a dot is followed by a digit: "1.27-alpine3.21" stops at the dash,
            // so "alpine3.21" is a suffix rather than three more parts.
            if (i + 1 < tag.Length && tag[i] == '.' && char.IsAsciiDigit(tag[i + 1])) i++;
            else break;
        }

        return parts.Count == 0 ? ([], tag) : (parts, tag[i..]);
    }

    static int Numeric(string a, string b)
    {
        var x = a.TrimStart('0');
        var y = b.TrimStart('0');

        return x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
    }

    /// <summary>Name order with digit runs compared as numbers, so <c>alpine3.9</c> sits before <c>alpine3.21</c>.</summary>
    internal static int Natural(string a, string b)
    {
        int i = 0, j = 0;

        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var si = i;
                var sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;

                var parts = Numeric(a[si..i], b[sj..j]);
                if (parts != 0) return parts;
            }
            else
            {
                var chars = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (chars != 0) return chars;
                i++;
                j++;
            }
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }
}

/// <summary>Whether a tag is already on the engine, and whether the registry's copy has moved since.</summary>
public enum LocalTagState
{
    /// <summary>Not on this engine.</summary>
    Absent,

    /// <summary>On this engine. Either the digests match, or there is nothing to compare.</summary>
    Present,

    /// <summary>
    /// On this engine, and the registry's tag now points somewhere else. "The tag has moved", never
    /// "a newer version": a multi-platform index moves when any one platform is rebuilt.
    /// </summary>
    Moved,
}

/// <summary>
/// The images on the engine, keyed the way the registry names them, so a tag in a registry listing
/// can say whether it is already here.
/// <para>
/// Keyed by the canonical spelling because the two engines disagree: Docker lists <c>nginx:alpine</c>
/// and podman <c>docker.io/library/nginx:alpine</c>, and both are the same thing to the registry.
/// </para>
/// </summary>
public sealed class LocalImageIndex
{
    readonly Dictionary<string, ImageSummary> _byTag = new(StringComparer.Ordinal);

    /// <summary>
    /// Every tagged image, newest first, each under the spelling the engine uses for it — for listing
    /// what is here, so pulling one again to update it is one click.
    /// </summary>
    public IReadOnlyList<(string Reference, ImageSummary Image)> Tagged { get; }

    public LocalImageIndex(IEnumerable<ImageSummary> images)
    {
        var tagged = new List<(string, ImageSummary)>();

        foreach (var image in images)
        {
            foreach (var tag in image.Tags)
            {
                if (tag.Tag is "<none>" || tag.Repository is "<none>" or "") continue;

                _byTag.TryAdd(ImageReferenceDraft.Parse(tag.Display).Canonical, image);
                tagged.Add((tag.Display, image));
            }
        }

        Tagged = [.. tagged.OrderByDescending(t => t.Item2.Created ?? DateTimeOffset.MinValue).ThenBy(t => t.Item1, StringComparer.Ordinal)];
    }

    public static LocalImageIndex Empty { get; } = new([]);

    /// <summary>The image on the engine under this exact repository and tag, if there is one.</summary>
    public ImageSummary? Find(ImageReferenceDraft draft) => _byTag.GetValueOrDefault(draft.Canonical);

    /// <summary>
    /// Whether <paramref name="tag"/> of <paramref name="repository"/> is here, and whether it has
    /// moved at the registry since it was pulled.
    /// <para>
    /// The digests an image was pulled as are its <c>RepoDigests</c>, and Docker Hub's tag digest is
    /// the same value while the tag stands still — measured against Docker Desktop, the same equality
    /// the stack update check relies on. An image built or loaded here has no digest for the
    /// repository, so there is nothing to compare and the honest answer is only "present".
    /// </para>
    /// </summary>
    public LocalTagState StateOf(ImageReferenceDraft repository, RemoteTag tag)
    {
        var draft = repository with { Tag = tag.Name, Digest = null };
        if (Find(draft) is not { } image) return LocalTagState.Absent;

        if (tag.Digest is not { Length: > 0 } remote) return LocalTagState.Present;

        var canonical = repository with { Tag = null, Digest = null };
        var name = $"{(canonical.IsDockerHub ? RegistryHosts.DockerHub : canonical.Host)}/{canonical.RemotePath}";

        var pulledAs = image.Digests
            .Select(d => d.Split('@', 2))
            .Where(p => p.Length == 2)
            .Where(p =>
            {
                var local = ImageReferenceDraft.Parse(p[0]);
                return $"{(local.IsDockerHub ? RegistryHosts.DockerHub : local.Host)}/{local.RemotePath}" == name;
            })
            .Select(p => p[1])
            .ToList();

        if (pulledAs.Count == 0) return LocalTagState.Present;

        return pulledAs.Contains(remote, StringComparer.Ordinal) ? LocalTagState.Present : LocalTagState.Moved;
    }
}
