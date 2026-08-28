using System.Text.Json;

namespace Dray.Core.Model;

/// <summary>One repository the registry knows about.</summary>
/// <param name="Name">
/// The reference to pull. Kept exactly as the engine gave it, registry prefix and all — podman
/// answers <c>docker.io/library/redis</c> where Docker answers <c>redis</c>, and both pull.
/// </param>
/// <param name="Stars">How many people starred it, or -1 where the engine did not say.</param>
public sealed record ImageSearchResult(
    string Name,
    string? Description = null,
    int Stars = -1,
    bool IsOfficial = false)
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
    public static IReadOnlyList<ImageSearchResult> Parse(string? json)
    {
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

                results.Add(new ImageSearchResult(
                    name,
                    Text(item, "Description", "description") is { Length: > 0 } d ? d : null,
                    Number(item, "Stars", "star_count"),
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
