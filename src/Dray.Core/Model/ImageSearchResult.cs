using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dray.Core.Model;

/// <summary>One repository the registry knows about.</summary>
/// <param name="Name">
/// The reference to pull. Kept as the engine gave it, registry prefix and all — podman answers
/// <c>docker.io/library/redis</c> where Docker answers <c>redis</c>, and both pull — except that a
/// result from another registry gains the host the engine left off; see <see cref="ImageSearch.Parse"/>.
/// </param>
/// <param name="Stars">How many people starred it, or -1 where the engine did not say.</param>
/// <param name="Pulls">How many times it has been pulled, or -1. Only Docker Hub's own API says.</param>
/// <param name="Updated">When anything in it was last pushed, or null.</param>
public sealed record ImageSearchResult(
    string Name,
    string? Description = null,
    int Stars = -1,
    bool IsOfficial = false,
    long Pulls = -1,
    DateTimeOffset? Updated = null)
{
    /// <summary>The name without its registry and <c>library/</c> prefix, for display.</summary>
    public string ShortName
    {
        get
        {
            var text = Name;

            var slash = text.IndexOf('/');
            if (slash > 0 && (text[..slash].Contains('.') || text[..slash].Contains(':')))
                text = text[(slash + 1)..];

            return text.StartsWith("library/", StringComparison.Ordinal) ? text[8..] : text;
        }
    }
}

/// <summary>
/// Reading the engine's answer to <c>/images/search</c>.
/// <para>
/// Written by hand rather than deserialised into one shape, because the two engines do not agree
/// on the shape. Podman's compatibility endpoint answers
/// <c>{"Name":…,"Description":…,"Stars":…,"Official":"[OK]"}</c>; Docker's own daemon answers
/// <c>{"name":…,"description":…,"star_count":…,"is_official":true}</c>. The names differ, not just
/// their case, and <c>Official</c> is a string in one and a boolean in the other — so a reader
/// built for either would return a list of blanks against the other and look like an empty
/// registry rather than a parsing failure.
/// </para>
/// </summary>
public static class ImageSearch
{
    /// <param name="registry">
    /// The registry the search was asked of, when it was not Docker Hub. Docker's engine answers a
    /// search of <c>quay.io/prometheus</c> with names like <c>prometheus/prometheus</c> — no host —
    /// and that name, pulled as given, comes from Docker Hub. Measured against Docker 29 and Quay.
    /// Podman qualifies its own, so a name that already has a host is left alone.
    /// </param>
    public static IReadOnlyList<ImageSearchResult> Parse(string? json, string? registry = null)
    {
        var foreign = registry is { Length: > 0 } host && !RegistryHosts.IsDockerHub(host) ? host : null;

        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

            var results = new List<ImageSearchResult>();

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var name = Text(item, "Name", "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (foreign is not null && ImageReferenceDraft.Parse(name).Registry is null) name = $"{foreign}/{name}";

                results.Add(new ImageSearchResult(
                    name,
                    Plain(Text(item, "Description", "description")),

                    // Quay answers every repository with zero stars, Prometheus included: the field is
                    // absent and the engine fills in its default. Docker Hub is the only registry whose
                    // count means anything, so elsewhere it is unknown rather than a measured zero.
                    foreign is null ? Number(item, "Stars", "star_count") : -1,
                    Flag(item, "Official", "is_official")));
            }

            return results;
        }
        catch (JsonException)
        {
            // An engine that answered with something else has not found nothing — it has failed.
            // The caller says so; returning an empty list here would report it as "no results".
            throw new FormatException("The engine's search response could not be read.");
        }
    }

    /// <summary>
    /// A description as one line of plain text, or null.
    /// <para>
    /// Docker Hub's are a sentence. Quay's are the repository's README — HTML headings, logo images
    /// and a row of CI badges — and rendered as text that is a line of angle brackets. Blazor escapes
    /// it either way; this is about it being readable, not about it being safe.
    /// </para>
    /// </summary>
    internal static string? Plain(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;

        var text = description;

        // Badges and images say nothing in a line of text; a link keeps its words. Both markdown
        // spellings: inline, [text](url), and reference-style, [text][ref] — Quay's READMEs use
        // the second for their row of badges.
        text = Regex.Replace(text, @"\[!\[[^\]]*\](\([^)]*\)|\[[^\]]*\])\](\([^)]*\)|\[[^\]]*\])", " ");
        text = Regex.Replace(text, @"!\[[^\]]*\](\([^)]*\)|\[[^\]]*\])", " ");
        text = Regex.Replace(text, @"\[([^\]]*)\](\([^)]*\)|\[[^\]]*\])", "$1");
        text = Regex.Replace(text, @"^\s*\[[^\]]+\]:\s*\S+.*$", " ", RegexOptions.Multiline);
        text = Regex.Replace(text, @"<[^>]*>", " ");
        text = Regex.Replace(text, @"(^|\s)#{1,6}\s", " ");
        // Emphasis markers, but not a lone underscore — far more often part of a name — and not
        // one escaped to mean itself, as in "\*NIX". The escapes come off last.
        text = Regex.Replace(text, @"(?<!\\)(\*{1,3}|_{2,3}|`+)", "");
        text = Regex.Replace(text, @"\\([\\`*_{}\[\]()#+\-.!])", "$1");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();

        if (text.Length == 0) return null;

        const int limit = 240;
        return text.Length <= limit ? text : text[..limit].TrimEnd() + "…";
    }

    static string? Text(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString()?.Trim();
        }

        return null;
    }

    static int Number(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var number))
            {
                return number;
            }
        }

        // Not zero. An engine that does not report stars is not an engine reporting none, and a
        // list sorted by a fabricated zero would put every result from it last.
        return -1;
    }

    /// <summary>
    /// True, or the string <c>"[OK]"</c> — podman writes the CLI's own column value into the JSON.
    /// Anything else, including the empty string it uses for "no", is false.
    /// </summary>
    static bool Flag(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (!item.TryGetProperty(name, out var value)) continue;

            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString() is { Length: > 0 } text && !text.Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}

/// <summary>
/// What an engine search is narrowed by, before the registry applies its limit.
/// <para>
/// Applied by the engine rather than to the results here, because the registry returns the top
/// twenty-five and filters them afterwards would leave three — the official image for a term is
/// often not in the first page of a popularity sort, and a filter that cannot find it is a broken one.
/// </para>
/// </summary>
/// <param name="OfficialOnly">Docker Official Images only — the <c>is-official</c> filter both engines accept.</param>
public sealed record ImageSearchFilters(bool OfficialOnly = false)
{
    public static ImageSearchFilters None { get; } = new();

    /// <summary>The engine API's <c>filters</c> value, or null when there is nothing to send.</summary>
    public string? ToQuery() => OfficialOnly ? """{"is-official":["true"]}""" : null;
}
