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

/// <summary>Why a YAML file does not parse, and where.</summary>
public sealed record YamlSyntaxError(string Message, YamlRange Range);

/// <summary>One service as the file declares it.</summary>
/// <param name="Key">Where the service's name is written, for pointing at it.</param>
/// <param name="DependsOn">Direct <c>depends_on</c> edges, in either of compose's two forms.</param>
/// <param name="DeclaresWatch">Whether it has a <c>develop.watch</c> block.</param>
public sealed record ComposeServiceEntry(
    string Name, YamlRange Key, IReadOnlyList<string> DependsOn, bool DeclaresWatch);

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
    }

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
                body is not null && Child(body, "develop") is YamlMappingNode develop && Child(develop, "watch") is not null and not YamlScalarNode { Value: null or "" }));
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
