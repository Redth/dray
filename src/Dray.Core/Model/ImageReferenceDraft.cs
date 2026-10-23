namespace Dray.Core.Model;

/// <summary>How far through a reference someone has typed, which decides what is worth suggesting.</summary>
public enum DraftStage
{
    /// <summary>Nothing yet.</summary>
    Empty,

    /// <summary>A name, or part of one — <c>ngi</c>, <c>bitnami/pos</c>. Repositories are suggested.</summary>
    Repository,

    /// <summary>A name and a colon — <c>nginx:</c>, <c>nginx:1.27-al</c>. That repository's tags are suggested.</summary>
    Tag,

    /// <summary>Pinned to a digest. There is nothing left to choose.</summary>
    Digest,
}

/// <summary>
/// A reference as it is being typed, read the way the Docker CLI will read it once it is pulled.
/// <para>
/// The pull dialog suggests a repository while the name is incomplete and that repository's tags
/// once a colon is typed, so the field itself is the state: backspacing over the colon goes back to
/// repositories, and nothing has to remember which step the user is on. That only works if this
/// parse is the CLI's, so the rules below are its rules rather than plausible ones.
/// </para>
/// </summary>
public sealed record ImageReferenceDraft
{
    /// <summary>A registry host named in the text, or null when it names none and Docker Hub is implied.</summary>
    public string? Registry { get; init; }

    /// <summary>The repository path as typed, without its registry — <c>nginx</c>, <c>owner/app</c>.</summary>
    public string Path { get; init; } = "";

    /// <summary>
    /// What follows the colon. Empty when the colon has just been typed, which is a request to list
    /// tags rather than a tag called nothing; null when there is no colon.
    /// </summary>
    public string? Tag { get; init; }

    /// <summary>What follows <c>@</c>, when the reference is pinned.</summary>
    public string? Digest { get; init; }

    public DraftStage Stage => Digest is not null ? DraftStage.Digest
        : Tag is not null ? DraftStage.Tag
        : Path.Length == 0 ? DraftStage.Empty
        : DraftStage.Repository;

    /// <summary>The registry this resolves to. Docker Hub when none is named, as for the CLI.</summary>
    public string Host => Registry ?? RegistryHosts.DockerHub;

    public bool IsDockerHub => RegistryHosts.IsDockerHub(Host);

    /// <summary>
    /// The path the registry itself knows the repository by.
    /// <para>
    /// A one-word Docker Hub name is an official image and lives under <c>library/</c>, which nobody
    /// types and every registry API needs.
    /// </para>
    /// </summary>
    public string RemotePath
    {
        get
        {
            var path = Path.Trim('/');
            return IsDockerHub && path.Length > 0 && !path.Contains('/') ? "library/" + path : path;
        }
    }

    /// <summary>The repository without a tag, spelled as typed — <c>nginx</c>, <c>ghcr.io/owner/app</c>.</summary>
    public string Repository => Registry is null ? Path : $"{Registry}/{Path}";

    /// <summary>
    /// What goes to the engine: the repository and whatever tag or digest was typed. No tag means
    /// <c>latest</c>, and the engine supplies that itself.
    /// </summary>
    public string Reference
    {
        get
        {
            var reference = Repository;
            if (Tag is { Length: > 0 } tag) reference += ":" + tag;
            if (Digest is { Length: > 0 } digest) reference += "@" + digest;
            return reference;
        }
    }

    /// <summary>
    /// The fully spelled-out name, <c>docker.io/library/nginx:latest</c> — what <c>nginx</c> actually
    /// means. Shown beside the Pull button so the implicit registry and the implicit tag are both
    /// visible before anything downloads.
    /// </summary>
    public string Canonical
    {
        get
        {
            var host = IsDockerHub ? RegistryHosts.DockerHub : Host;
            var name = $"{host}/{RemotePath}";

            if (Digest is { Length: > 0 } digest)
                return Tag is { Length: > 0 } pinned ? $"{name}:{pinned}@{digest}" : $"{name}@{digest}";

            return $"{name}:{(Tag is { Length: > 0 } tag ? tag : "latest")}";
        }
    }

    /// <summary>The part of the text that belongs in the field when the registry is chosen separately.</summary>
    public string Field
    {
        get
        {
            var text = Path;
            if (Tag is not null) text += ":" + Tag;
            if (Digest is not null) text += "@" + Digest;
            return text;
        }
    }

    /// <summary>
    /// Why the engine would refuse this, in a sentence, or null when it would not.
    /// <para>
    /// Only what can be known without asking anyone: the reference grammar. Said while typing, because
    /// the engine's own answer — "invalid reference format" — names neither the part nor the rule.
    /// </para>
    /// </summary>
    public string? Problem
    {
        get
        {
            if (Path.Length == 0) return null;

            if (Path.Any(char.IsWhiteSpace) || (Tag?.Any(char.IsWhiteSpace) ?? false))
                return "A reference has no spaces in it.";

            if (Path.Any(char.IsUpper))
                return $"Repository names are lowercase — the engine refuses “{Path}”. Try “{Path.ToLowerInvariant()}”.";

            // A trailing slash is someone halfway through "bitnami/redis", not a mistake to point out.
            if (Path.StartsWith('/') || Path.Contains("//", StringComparison.Ordinal))
                return "Each part of a repository name needs something in it — check for a stray slash.";

            if (Path.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/')))
                return "Repository names use letters, digits, dots, dashes, underscores and slashes only.";

            if (Tag is { Length: > 0 } tag)
            {
                if (tag.Length > 128) return "A tag is at most 128 characters long.";

                if (tag[0] is '.' or '-' || tag.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
                    return "Tags use letters, digits, dots, dashes and underscores, and do not start with a dot or a dash.";
            }

            return null;
        }
    }

    /// <summary>
    /// Whether this can be handed to the engine as it stands. A trailing colon or <c>@</c> cannot:
    /// they are the user asking to choose, not a choice.
    /// </summary>
    public bool IsPullable => Path.Length > 0 && !Path.EndsWith('/') && Problem is null && Tag is not "" && Digest is not "";

    /// <summary>
    /// Read the text as the CLI would.
    /// <para>
    /// The first segment is a registry only when a slash follows it and it contains a dot or a colon,
    /// or is exactly <c>localhost</c>. Without that rule <c>team/app</c> would be a registry called
    /// "team", and a colon in <c>registry:5000/app</c> would be read as a tag.
    /// </para>
    /// </summary>
    public static ImageReferenceDraft Parse(string? text)
    {
        var rest = (text ?? string.Empty).Trim();

        string? digest = null;
        var at = rest.IndexOf('@');
        if (at >= 0)
        {
            digest = rest[(at + 1)..];
            rest = rest[..at];
        }

        string? registry = null;
        var slash = rest.IndexOf('/');
        if (slash > 0 && RegistryHosts.IsRegistryHost(rest[..slash]))
        {
            registry = rest[..slash].ToLowerInvariant();
            rest = rest[(slash + 1)..];
        }

        string? tag = null;
        var colon = rest.LastIndexOf(':');
        if (colon > rest.LastIndexOf('/'))
        {
            tag = rest[(colon + 1)..];
            rest = rest[..colon];
        }

        return new ImageReferenceDraft { Registry = registry, Path = rest, Tag = tag, Digest = digest };
    }

    /// <summary>
    /// The text in the field, read against the registry chosen beside it. A host typed into the field
    /// wins, because it is the more specific thing the user did.
    /// </summary>
    public static ImageReferenceDraft Compose(string host, string? field)
    {
        var draft = Parse(field);
        if (draft.Registry is not null || RegistryHosts.IsDockerHub(host)) return draft;

        return draft with { Registry = host.Trim().ToLowerInvariant() };
    }
}
