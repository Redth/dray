using System.Text.Json;

namespace Dray.Core.Model;

/// <summary>One machine inside a builder.</summary>
/// <param name="Status">
/// buildx's own word: <c>running</c>, <c>inactive</c>, <c>error</c>. Kept verbatim rather than
/// mapped onto Dray's container vocabulary — a builder is not a container and giving it the same
/// words would say it was.
/// </param>
public sealed record BuildxNode(
    string Name,
    string? Endpoint = null,
    string? Status = null,
    string? Error = null,
    IReadOnlyList<string>? Platforms = null);

/// <summary>A buildx builder, as <c>buildx ls</c> describes it.</summary>
public sealed record BuildxBuilder(
    string Name,
    string? Driver = null,
    bool IsCurrent = false,
    IReadOnlyList<BuildxNode>? Nodes = null)
{
    public IReadOnlyList<BuildxNode> NodeList => Nodes ?? [];

    /// <summary>
    /// The first thing wrong with this builder, or null.
    /// <para>
    /// A configured builder pointing at an endpoint that no longer exists is the common case —
    /// a context was removed, or a VM was renamed — and it is worth saying before a build is
    /// started with it rather than after.
    /// </para>
    /// </summary>
    public string? Problem => NodeList
        .Select(n => n.Error ?? (n.Status == "error" ? $"{n.Name} reported an error." : null))
        .FirstOrDefault(p => p is not null);

    public bool IsUsable => Problem is null;

    /// <summary>What this builder can build for, across its nodes.</summary>
    public IReadOnlyList<string> Platforms =>
        [.. NodeList.SelectMany(n => n.Platforms ?? []).Distinct(StringComparer.Ordinal)];
}

/// <summary>
/// Reading <c>buildx ls --format json</c>.
/// <para>
/// One JSON object per line rather than an array — buildx streams them — so this is read line by
/// line and a line it cannot parse costs that builder rather than the list.
/// </para>
/// </summary>
public static class Buildx
{
    public static IReadOnlyList<BuildxBuilder> Parse(string? output)
    {
        var builders = new List<BuildxBuilder>();

        if (string.IsNullOrWhiteSpace(output)) return builders;

        foreach (var raw in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('{')) continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (Text(root, "Name") is not { Length: > 0 } name) continue;

                builders.Add(new BuildxBuilder(
                    name,
                    Text(root, "Driver"),
                    root.TryGetProperty("Current", out var current) && current.ValueKind == JsonValueKind.True,
                    ReadNodes(root)));
            }
            catch (JsonException)
            {
                // A line buildx wrote that this cannot read costs that builder, not the list.
            }
        }

        return builders;
    }

    static IReadOnlyList<BuildxNode> ReadNodes(JsonElement root)
    {
        if (!root.TryGetProperty("Nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) return [];

        var found = new List<BuildxNode>();

        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (Text(node, "Name") is not { Length: > 0 } name) continue;

            found.Add(new BuildxNode(
                name,
                Text(node, "Endpoint"),
                Text(node, "Status"),
                Text(node, "Err"),
                Strings(node, "Platforms")));
        }

        return found;
    }

    static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() is { Length: > 0 } text ? text : null
            : null;

    static IReadOnlyList<string>? Strings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return null;

        return [.. value.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString()!)
            .Where(s => s.Length > 0)];
    }
}
