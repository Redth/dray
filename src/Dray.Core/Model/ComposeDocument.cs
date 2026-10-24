using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Dray.Core.Model;

/// <summary>A span of a YAML file, 1-based like an editor's line and column numbers.</summary>
public sealed record YamlRange(int Line, int Column, int EndLine, int EndColumn)
{
    internal static YamlRange Of(YamlNode node) => new(
        (int)node.Start.Line, (int)node.Start.Column, (int)node.End.Line, (int)node.End.Column);

    internal static YamlRange Of(Mark start, Mark end) => new(
        (int)start.Line, (int)start.Column, (int)end.Line, (int)end.Column);
}

/// <summary>
/// One entry under a service key, and where it is written.
/// </summary>
/// <param name="Value">The scalar, for a short-syntax entry; null for a mapping.</param>
/// <param name="Fields">A long-syntax entry's scalar fields — <c>published</c>, <c>source</c>.</param>
public sealed record YamlItem(string? Value, YamlRange Range, IReadOnlyDictionary<string, string> Fields);

/// <summary>Why a YAML file does not parse, and where.</summary>
public sealed record YamlSyntaxError(string Message, YamlRange Range);

/// <summary>One service as the file declares it.</summary>
/// <param name="Key">Where the service's name is written, for pointing at it.</param>
/// <param name="DependsOn">Direct <c>depends_on</c> edges, in either of compose's two forms.</param>
/// <param name="DeclaresWatch">Whether it has a <c>develop.watch</c> block.</param>
/// <param name="Builds">It has a <c>build:</c> section — Build has something to do for it.</param>
/// <param name="Profiles">The profiles it belongs to; empty when it always runs.</param>
public sealed record ComposeServiceEntry(
    string Name, YamlRange Key, IReadOnlyList<string> DependsOn, bool DeclaresWatch)
{
    public bool Builds { get; init; }

    public IReadOnlyList<string> Profiles { get; init; } = [];
}

/// <summary>Where a path into the file landed.</summary>
/// <param name="Exact">
/// False when the path ran out of file partway — the range is then the deepest key that does
/// exist, which is still the right place to point: a setting compose rejected under
/// <c>services.web</c> belongs on <c>web</c> even when it cannot be found more precisely.
/// </param>
public sealed record YamlLocation(YamlRange Range, bool Exact);

/// <summary>
/// A compose file, read by a real YAML parser.
/// <para>
/// Two parsers, each for what it is the authority on. <c>docker compose config</c> says what the
/// stack <i>means</i> — merged files, interpolation, profiles, the normalised model — and Dray
/// reimplements none of that. What compose cannot say is where in the user's own text something
/// is: its output is re-serialised, its errors name paths like <c>services.web.ports</c>, and the
/// editor needs a line. That is this type's job, and the reason it keeps positions.
/// </para>
/// <para>
/// YamlDotNet rather than scanning lines, which is what this replaced: anchors, merge keys, flow
/// style and quoted keys are all valid compose, and a line scanner quietly gets each of them wrong.
/// </para>
/// </summary>
public sealed class ComposeDocument
{
    public static readonly ComposeDocument Empty = new(null, [], null);

    readonly YamlMappingNode? _root;

    ComposeDocument(YamlMappingNode? root, IReadOnlyList<ComposeServiceEntry> services, YamlSyntaxError? error)
    {
        _root = root;
        Services = services;
        SyntaxError = error;
        ProjectName = root is not null && Child(root, "name") is YamlScalarNode { Value: { Length: > 0 } name } ? name : null;
    }

    /// <summary>
    /// Links the file names for the stack under <c>x-dray: urls:</c> — the admin page, the docs —
    /// each a URL or a <c>{ title, url }</c> pair. Only http and https: a link in a file someone
    /// else wrote is not a way to run something on this machine.
    /// <code>
    /// x-dray:
    ///   urls:
    ///     - http://localhost:8080
    ///     - { title: Grafana, url: "http://localhost:3000" }
    ///   icon: ./icon.svg
    /// </code>
    /// <para>
    /// An <c>x-</c> key because compose ignores those by design; the file still runs unchanged
    /// anywhere else.
    /// </para>
    /// </summary>
    public IReadOnlyList<(string Title, string Url)> DrayLinks
    {
        get
        {
            if (_root is null || Child(_root, "x-dray") is not YamlMappingNode dray || Child(dray, "urls") is not YamlSequenceNode urls) return [];

            var links = new List<(string, string)>();

            foreach (var item in urls.Children)
            {
                var (title, url) = item switch
                {
                    YamlScalarNode { Value: { } value } => (value, value),
                    YamlMappingNode map when Child(map, "url") is YamlScalarNode { Value: { } value } =>
                        (Child(map, "title") is YamlScalarNode { Value: { Length: > 0 } named } ? named : value, value),
                    _ => (null, null),
                };

                if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                    links.Add((title!, uri.ToString()));
            }

            return links;
        }
    }

    /// <summary>A path to an image for the stack, from <c>x-dray: icon:</c>, relative to the file.</summary>
    public string? DrayIcon => _root is not null && Child(_root, "x-dray") is YamlMappingNode dray
        && Child(dray, "icon") is YamlScalarNode { Value: { Length: > 0 } icon }
            ? icon
            : null;

    /// <summary>
    /// The top-level <c>name:</c>, as written. May contain <c>${VAR}</c>, which only compose can
    /// resolve — callers that need the project name should treat such a value as unknown.
    /// </summary>
    public string? ProjectName { get; }

    /// <summary>Every service, in the order the file declares them.</summary>
    public IReadOnlyList<ComposeServiceEntry> Services { get; }

    /// <summary>Why the text is not YAML, or null when it is.</summary>
    public YamlSyntaxError? SyntaxError { get; }

    /// <summary>
    /// Parse <paramref name="yaml"/>. Never throws: text that is not YAML is a document with a
    /// <see cref="SyntaxError"/> and no services, because half-typed YAML is the normal state of a
    /// file someone is editing.
    /// </summary>
    public static ComposeDocument Parse(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return Empty;

        try
        {
            var stream = new YamlStream();

            // MergingParser resolves `<<: *defaults`, which compose files use constantly to share a
            // block between services. Without it a service built from an anchor has no depends_on.
            stream.Load(new MergingParser(new Parser(new StringReader(yaml))));

            if (stream.Documents.FirstOrDefault()?.RootNode is not YamlMappingNode root)
                return Empty;

            return new ComposeDocument(root, ReadServices(root), null);
        }
        catch (YamlException ex)
        {
            return new ComposeDocument(null, [], new YamlSyntaxError(Describe(ex), YamlRange.Of(ex.Start, ex.End)));
        }
    }

    /// <summary>
    /// Find a path of keys in the file: <c>["services", "web", "ports"]</c>.
    /// <para>
    /// A segment that is a number indexes a sequence; <c>[]</c> — compose's way of saying "some item
    /// of this list" — stops at the list, since which item is unknown. Into a list of scalars a
    /// segment may also name the item's value, so <c>depends_on</c> → <c>db</c> lands on the line
    /// that says <c>- db</c>.
    /// </para>
    /// </summary>
    /// <returns>Null when not even the first segment exists.</returns>
    public YamlLocation? Locate(IReadOnlyList<string> path)
    {
        if (_root is null || path.Count == 0) return null;

        YamlNode node = _root;
        YamlRange? found = null;

        foreach (var segment in path)
        {
            YamlNode? next = null;
            YamlRange? at = null;

            switch (node)
            {
                case YamlMappingNode mapping:
                    foreach (var (key, value) in mapping.Children)
                    {
                        if (key is YamlScalarNode { Value: { } name } && name == segment)
                        {
                            next = value;
                            at = YamlRange.Of(key);
                            break;
                        }
                    }

                    break;

                case YamlSequenceNode sequence when int.TryParse(segment, out var index):
                    if (index >= 0 && index < sequence.Children.Count)
                    {
                        next = sequence.Children[index];
                        at = YamlRange.Of(next);
                    }

                    break;

                case YamlSequenceNode sequence:
                    next = sequence.Children.FirstOrDefault(c => c is YamlScalarNode { Value: { } v } && v == segment);
                    if (next is not null) at = YamlRange.Of(next);
                    break;
            }

            if (next is null || at is null) return found is null ? null : new YamlLocation(found, Exact: false);

            node = next;
            found = at;
        }

        return new YamlLocation(found!, Exact: true);
    }

    /// <summary>
    /// What a service says under one key, item by item, with where each item is — the
    /// <c>ports</c> list, the <c>volumes</c> list, the <c>image</c> scalar.
    /// <para>
    /// A scalar value is one item; a sequence is one per entry; a mapping (environment written as
    /// a map) is one per pair, as <c>KEY=value</c>. Long-syntax entries keep their fields, so a
    /// port written as <c>published: 8080</c> is as readable as one written <c>"8080:80"</c>.
    /// </para>
    /// </summary>
    public IReadOnlyList<YamlItem> ItemsOf(string service, string key)
    {
        if (_root is null || Child(_root, "services") is not YamlMappingNode services) return [];
        if (Child(services, service) is not YamlMappingNode body || Child(body, key) is not { } value) return [];

        return value switch
        {
            YamlScalarNode scalar => [Item(scalar)],
            YamlSequenceNode list => [.. list.Children.Select(Item)],
            YamlMappingNode map => [.. map.Children.Select(kv => new YamlItem(
                $"{(kv.Key as YamlScalarNode)?.Value}={(kv.Value as YamlScalarNode)?.Value}",
                YamlRange.Of(kv.Key.Start, kv.Value.End),
                Fields: new Dictionary<string, string>()))],
            _ => [],
        };
    }

    static YamlItem Item(YamlNode node) => node switch
    {
        YamlScalarNode scalar => new YamlItem(scalar.Value, YamlRange.Of(scalar), new Dictionary<string, string>()),
        YamlMappingNode map => new YamlItem(
            null,
            YamlRange.Of(map),
            map.Children
                .Where(kv => kv.Key is YamlScalarNode { Value: not null } && kv.Value is YamlScalarNode)
                .ToDictionary(kv => ((YamlScalarNode)kv.Key).Value!, kv => ((YamlScalarNode)kv.Value).Value ?? "", StringComparer.Ordinal)),
        _ => new YamlItem(null, YamlRange.Of(node), new Dictionary<string, string>()),
    };

    /// <summary>
    /// The raw node for a path, for the editing helpers in this assembly — positions, styles and
    /// children, which a splice needs and nothing outside should.
    /// </summary>
    internal YamlNode? NodeAt(params string[] path)
    {
        YamlNode? node = _root;

        foreach (var segment in path)
        {
            if (node is not YamlMappingNode mapping) return null;
            node = Child(mapping, segment);
        }

        return node;
    }

    /// <summary>The 1-based line a key is written on — <c>["volumes"]</c>, <c>["services", "web"]</c> — or null.</summary>
    public int? LineOfKey(params string[] path) => KeyAt(path) is { } key ? (int)key.Start.Line : null;

    /// <summary>The key node for the last segment of a path — where a key is written.</summary>
    internal YamlScalarNode? KeyAt(params string[] path)
    {
        if (path.Length == 0 || NodeAt(path[..^1]) is not YamlMappingNode parent) return null;

        foreach (var (k, _) in parent.Children)
        {
            if (k is YamlScalarNode { Value: { } name } scalar && name == path[^1]) return scalar;
        }

        return null;
    }

    static List<ComposeServiceEntry> ReadServices(YamlMappingNode root)
    {
        var services = new List<ComposeServiceEntry>();

        if (Child(root, "services") is not YamlMappingNode block) return services;

        foreach (var (key, value) in block.Children)
        {
            if (key is not YamlScalarNode { Value: { Length: > 0 } name }) continue;

            var body = value as YamlMappingNode;

            services.Add(new ComposeServiceEntry(
                name,
                YamlRange.Of(key),
                body is null ? [] : DependsOn(body),
                body is not null && Child(body, "develop") is YamlMappingNode develop && Child(develop, "watch") is not null and not YamlScalarNode { Value: null or "" })
            {
                Builds = body is not null && Child(body, "build") is not null,
                Profiles = body is not null && Child(body, "profiles") is YamlSequenceNode profiles
                    ? [.. profiles.Children.OfType<YamlScalarNode>().Select(p => p.Value).OfType<string>()]
                    : [],
            });
        }

        return services;
    }

    /// <summary>
    /// Both forms compose accepts: a list of names, or a map whose keys are names and whose values
    /// say what to wait for.
    /// </summary>
    static IReadOnlyList<string> DependsOn(YamlMappingNode service) => Child(service, "depends_on") switch
    {
        YamlSequenceNode list => [.. list.Children.OfType<YamlScalarNode>().Select(n => n.Value).OfType<string>().Distinct(StringComparer.Ordinal)],
        YamlMappingNode map => [.. map.Children.Keys.OfType<YamlScalarNode>().Select(n => n.Value).OfType<string>().Distinct(StringComparer.Ordinal)],
        _ => [],
    };

    static YamlNode? Child(YamlMappingNode mapping, string key)
    {
        foreach (var (k, v) in mapping.Children)
        {
            if (k is YamlScalarNode { Value: { } name } && name == key) return v;
        }

        return null;
    }

    /// <summary>
    /// YamlDotNet's message without the position it prepends — the editor draws the position, and
    /// "(Line: 3, Col: 5, Idx: 27) - (Line: 3, Col: 5, Idx: 27): " in front of a sentence is noise.
    /// </summary>
    static string Describe(YamlException ex)
    {
        var message = ex.InnerException is YamlException inner ? inner.Message : ex.Message;
        var split = message.IndexOf("): ", StringComparison.Ordinal);

        message = message.StartsWith("(Line:", StringComparison.Ordinal) && split > 0 ? message[(split + 3)..] : message;
        return message.TrimEnd('.');
    }
}
