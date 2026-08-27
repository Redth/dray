namespace Dray.Core.Model;

/// <summary>
/// One tag on an image — <c>nginx:alpine</c> split into the parts people actually read.
/// </summary>
public sealed record ImageTag(string Repository, string Tag)
{
    public string Display => $"{Repository}:{Tag}";

    /// <summary>The repository without its registry and library prefix, which is noise for the common case.</summary>
    public string ShortRepository
    {
        get
        {
            var name = Repository;

            // docker.io/library/nginx is nginx to everyone except the registry.
            foreach (var prefix in Noise)
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal)) name = name[prefix.Length..];
            }

            return name;
        }
    }

    static readonly string[] Noise = ["docker.io/library/", "docker.io/", "library/"];

    /// <summary>
    /// The registry this tag pushes to, in the form <c>config.json</c> keys by.
    /// <para>
    /// The rule is the Docker CLI's and is not guessable from the shape alone: the first path
    /// segment is a registry only when it contains a dot or a colon, or is exactly
    /// <c>localhost</c>. Without that, <c>team/app</c> would push to a registry called "team"
    /// rather than to Docker Hub, and the credential lookup would find nothing.
    /// </para>
    /// </summary>
    public string Registry
    {
        get
        {
            var slash = Repository.IndexOf('/');
            if (slash <= 0) return RegistryEntryDockerHub;

            var head = Repository[..slash];

            return head.Contains('.', StringComparison.Ordinal)
                   || head.Contains(':', StringComparison.Ordinal)
                   || head == "localhost"
                ? head
                : RegistryEntryDockerHub;
        }
    }

    /// <summary>
    /// Docker Hub's key in <c>config.json</c>. A long legacy URL nobody would recognise, and the
    /// only string a helper will match for a Hub credential.
    /// </summary>
    public const string RegistryEntryDockerHub = "https://index.docker.io/v1/";

    /// <summary>Parse <c>registry:5000/team/app:1.2</c> without mistaking the port for a tag.</summary>
    public static ImageTag Parse(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return new ImageTag("", "");

        var slash = reference.LastIndexOf('/');
        var colon = reference.LastIndexOf(':');

        // A colon before the last slash belongs to a registry's port, not to a tag.
        return colon > slash
            ? new ImageTag(reference[..colon], reference[(colon + 1)..])
            : new ImageTag(reference, "latest");
    }
}

/// <summary>One image on the host.</summary>
public sealed record ImageSummary
{
    public required string Id { get; init; }

    /// <summary>Every tag pointing at this image. Empty means it is dangling.</summary>
    public IReadOnlyList<ImageTag> Tags { get; init; } = [];

    public IReadOnlyList<string> Digests { get; init; } = [];

    public DateTimeOffset? Created { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>
    /// Whether <see cref="SizeBytes"/> is a measurement.
    /// <para>
    /// False on Apple's runtime, which reports only the manifest's size — nine kilobytes for
    /// alpine. "0 B" would read as a measured zero and make a list of images look free; the same
    /// distinction <see cref="Engine.DiskUsage.IsKnown"/> draws, for the same reason.
    /// </para>
    /// </summary>
    public bool SizeReported { get; init; } = true;

    /// <summary>
    /// Bytes shared with other images through common layers.
    /// <para>
    /// The reason a list of image sizes never adds up to what the disk shows, and the reason
    /// deleting a 900 MB image can reclaim almost nothing.
    /// </para>
    /// </summary>
    public long SharedBytes { get; init; }

    /// <summary>Containers using this image, as the engine counts them. -1 when it did not say.</summary>
    public int ContainerCount { get; init; } = -1;

    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Bytes that would actually be freed by deleting this image — its own layers, not the ones it
    /// shares. This is the number a prune preview has to show.
    /// </summary>
    public long UniqueBytes => Math.Max(0, SizeBytes - SharedBytes);

    /// <summary>
    /// No tag points here. Usually a previous build of something that has since been rebuilt, and
    /// the first thing to reclaim.
    /// </summary>
    public bool IsDangling => Tags.Count == 0;

    /// <summary>In use, when the engine told us. Null means it did not.</summary>
    public bool? IsInUse => ContainerCount < 0 ? null : ContainerCount > 0;

    public string ShortId
    {
        get
        {
            // The engine prefixes ids with the algorithm; nobody reads that part.
            var id = Id.StartsWith("sha256:", StringComparison.Ordinal) ? Id[7..] : Id;
            return id.Length <= 12 ? id : id[..12];
        }
    }

    /// <summary>What to call it in a list: its first tag, or its id when nothing points here.</summary>
    public string DisplayName => Tags.Count > 0 ? Tags[0].Display : $"<untagged> · {ShortId}";

    /// <summary>Groups by repository so several tags of one image sit together.</summary>
    public string RepositoryKey => Tags.Count > 0 ? Tags[0].ShortRepository : "<untagged>";
}

/// <summary>One layer in an image's history.</summary>
/// <param name="CreatedBy">The Dockerfile instruction that produced it, as the engine recorded it.</param>
public sealed record ImageLayer(
    string Id,
    DateTimeOffset? Created,
    long SizeBytes,
    string CreatedBy,
    string? Comment)
{
    /// <summary>
    /// The instruction without the build machinery around it.
    /// <para>
    /// The engine records <c>/bin/sh -c #(nop)  CMD ["nginx"]</c> where the Dockerfile said
    /// <c>CMD ["nginx"]</c>. Showing the raw form makes every line start with the same twenty
    /// characters of noise.
    /// </para>
    /// </summary>
    public string Instruction
    {
        get
        {
            var text = CreatedBy.Trim();

            const string nop = "/bin/sh -c #(nop) ";
            if (text.StartsWith(nop, StringComparison.Ordinal)) return text[nop.Length..].Trim();

            const string shell = "/bin/sh -c ";
            if (text.StartsWith(shell, StringComparison.Ordinal)) return "RUN " + text[shell.Length..].Trim();

            return text;
        }
    }

    /// <summary>A layer that adds no bytes — metadata like ENV or CMD.</summary>
    public bool IsEmpty => SizeBytes == 0;
}

/// <summary>
/// One step of a pull, as the engine reports it.
/// <para>
/// A pull is many layers downloading at once and reporting independently, so progress is not a
/// single number — it is a set of layers each in its own state. Modelled that way rather than
/// averaged into a percentage that jumps backwards when a new layer starts.
/// </para>
/// </summary>
/// <param name="LayerId">The layer this concerns, or null for a message about the pull as a whole.</param>
/// <param name="Status">The engine's own word: Downloading, Extracting, Pull complete, and so on.</param>
public sealed record PullProgress(
    string? LayerId,
    string Status,
    long Current = 0,
    long Total = 0,
    string? Error = null)
{
    public bool IsError => Error is not null;

    /// <summary>Fraction complete, or null when the engine has not said how big the layer is.</summary>
    public double? Fraction => Total > 0 ? Math.Clamp((double)Current / Total, 0, 1) : null;

    /// <summary>True once this layer needs nothing further — downloaded, extracted, or already held.</summary>
    public bool IsComplete =>
        Status.Contains("complete", StringComparison.OrdinalIgnoreCase)
        || Status.Contains("already exists", StringComparison.OrdinalIgnoreCase)
        || Status.Contains("up to date", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What to create a network with. Only the fields Dray offers.</summary>
public sealed record NetworkRequest(
    string Name,
    string Driver = "bridge",
    string? Subnet = null,
    string? Gateway = null,
    bool Internal = false);

/// <summary>What to build.</summary>
/// <param name="ContextDirectory">
/// The build context. Everything under it is sent to the engine, which is why a context of the
/// wrong directory is slow rather than broken — and why <c>.dockerignore</c> matters.
/// </param>
/// <param name="Dockerfile">Path to the Dockerfile, relative to the context.</param>
/// <param name="Tag">What to call the result, e.g. <c>myapp:dev</c>. Optional; a build with no tag is dangling.</param>
public sealed record BuildRequest(
    string ContextDirectory,
    string Dockerfile = "Dockerfile",
    string? Tag = null,
    bool NoCache = false,
    bool Pull = false);

/// <summary>One line of a build, as the engine reports it.</summary>
/// <param name="Text">
/// The build output verbatim, including the engine's own step markers. Not reformatted: people
/// read build logs by pattern, and rewriting them makes a familiar thing unfamiliar.
/// </param>
public sealed record BuildProgress(string Text, string? Error = null)
{
    public bool IsError => Error is not null;

    /// <summary>
    /// The step this line begins, if it begins one — <c>Step 3/12</c>. Used to show progress
    /// without pretending to a percentage the engine never gives.
    /// </summary>
    public (int Current, int Total)? Step
    {
        get
        {
            // Docker writes "Step 3/12"; podman writes "STEP 3/12". Matching case-sensitively
            // silently loses the step count on one of the two engines.
            const string marker = "step ";
            var at = Text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;

            var rest = Text[(at + marker.Length)..];

            // Docker writes "Step 3/12 : RUN …" and podman "STEP 1/4: FROM …". Stopping only at a
            // space leaves "1/4:" on podman, which does not parse.
            var end = rest.IndexOfAny([' ', ':']);
            var fragment = end < 0 ? rest : rest[..end];

            var slash = fragment.IndexOf('/');
            if (slash <= 0) return null;

            return int.TryParse(fragment[..slash], out var current)
                   && int.TryParse(fragment[(slash + 1)..], out var total)
                ? (current, total)
                : null;
        }
    }
}
