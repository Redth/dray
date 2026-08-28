namespace Dray.Core.Model;

/// <summary>
/// The two pure parts of saving an image to a file and loading one back.
/// <para>
/// The transfer itself is the runtime's job. What is here is the naming and the reading of what
/// the engine said afterwards — both of which are worth being sure about, because a save dialog
/// that proposes <c>image.tar</c> for every image is how a downloads folder becomes unusable, and
/// a load that reports nothing leaves the user guessing whether it worked.
/// </para>
/// </summary>
public static class ImageArchive
{
    public const string Extension = ".tar";

    /// <summary>
    /// What to put in the save dialog for an image reference.
    /// <para>
    /// A reference contains the characters a path cannot: <c>ghcr.io/redth/dray:1.4.2</c> has both
    /// a slash and a colon. Flattened rather than truncated, so the file still says which image it
    /// holds and which tag — the registry is dropped, because it is the part that is usually the
    /// same for everything someone is saving.
    /// </para>
    /// </summary>
    public static string SuggestedFileName(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return "image" + Extension;

        var text = reference.Trim();

        // A bare digest — nothing to make a name from beyond the first few characters.
        if (text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return "image-" + text[7..][..Math.Min(12, text.Length - 7)] + Extension;

        // Drop a registry host, which is the part shared by everything from one place. A first
        // segment counts as a host only if it looks like one: podman prefixes everything with
        // docker.io/library, and "library" is not a host.
        var parts = text.Split('/');
        if (parts.Length > 1 && (parts[0].Contains('.', StringComparison.Ordinal) || parts[0].Contains(':', StringComparison.Ordinal)))
            text = string.Join('/', parts[1..]);

        var name = text
            .Replace('/', '-')
            .Replace(':', '-')
            .Replace('@', '-');

        foreach (var bad in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(bad, '-');

        name = name.Trim('-', '.', ' ');

        return (name.Length == 0 ? "image" : name) + Extension;
    }

    /// <summary>
    /// The image names an engine reports having loaded.
    /// <para>
    /// Docker writes <c>Loaded image: nginx:alpine</c>, or <c>Loaded image ID: sha256:…</c> for an
    /// archive with no tags in it. Apple's <c>container</c> prints the reference on its own line.
    /// Anything else is passed over rather than guessed at: reporting the wrong name is worse than
    /// reporting a count.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> LoadedNames(IEnumerable<string>? output)
    {
        var found = new List<string>();

        foreach (var raw in output ?? [])
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var name = Strip(line, "Loaded image:") ?? Strip(line, "Loaded image ID:");

            if (name is null && Looks(line)) name = line;
            if (name is { Length: > 0 } && !found.Contains(name, StringComparer.Ordinal)) found.Add(name);
        }

        return found;
    }

    static string? Strip(string line, string prefix)
        => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? line[prefix.Length..].Trim()
            : null;

    /// <summary>
    /// Whether a bare line is plausibly an image reference rather than a progress message. Kept
    /// tight on purpose — a sentence with a space in it never is.
    /// </summary>
    static bool Looks(string line)
        => !line.Contains(' ', StringComparison.Ordinal)
           && line.Length > 1
           && (line.Contains(':', StringComparison.Ordinal) || line.Contains('/', StringComparison.Ordinal))
           && line.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/' or ':' or '@');
}
