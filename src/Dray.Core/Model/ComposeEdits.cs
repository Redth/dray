using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Dray.Core.Model;

/// <summary>
/// Lines to insert into a file, before a 1-based line — the whole of an edit Dray makes.
/// </summary>
/// <param name="BeforeLine">Insert before this line; one past the last line appends.</param>
/// <param name="Text">The lines, each ending with the file's own line break.</param>
public sealed record ComposeSplice(int BeforeLine, string Text)
{
    /// <summary>The file with the splice applied — what the editor will hold once it is.</summary>
    public string ApplyTo(string text)
    {
        var lines = SplitKeepingBreaks(text);
        var index = Math.Clamp(BeforeLine - 1, 0, lines.Count);

        // Appending to a file without a trailing newline would join the last line to the new one.
        if (index == lines.Count && lines.Count > 0 && !lines[^1].EndsWith('\n'))
            lines[^1] += ComposeEdits.BreakOf(text);

        lines.Insert(index, Text);
        return string.Concat(lines);
    }

    internal static List<string> SplitKeepingBreaks(string text)
    {
        var lines = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;

            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }

        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }
}

/// <summary>
/// Whole lines of a file replaced: <see cref="StartLine"/> to <see cref="EndLine"/>, inclusive and
/// 1-based, become <see cref="Text"/>. An <see cref="EndLine"/> before the start replaces nothing —
/// an insertion before <see cref="StartLine"/> — and an empty <see cref="Text"/> is a removal.
/// </summary>
public sealed record ComposeChange(int StartLine, int EndLine, string Text)
{
    /// <summary>
    /// The file with every change made. The changes are each about the text as given and must not
    /// overlap; they are applied from the bottom up so each one's line numbers still hold.
    /// </summary>
    public static string Apply(string text, IReadOnlyList<ComposeChange> changes)
    {
        var lines = ComposeSplice.SplitKeepingBreaks(text);

        foreach (var change in changes.OrderByDescending(c => c.StartLine).ThenByDescending(c => c.EndLine))
        {
            var start = Math.Clamp(change.StartLine - 1, 0, lines.Count);
            var count = Math.Clamp(change.EndLine - change.StartLine + 1, 0, lines.Count - start);

            // Inserting after a last line that has no break would join the two.
            if (count == 0 && start == lines.Count && lines.Count > 0 && !lines[^1].EndsWith('\n') && change.Text.Length > 0)
                lines[^1] += ComposeEdits.BreakOf(text);

            lines.RemoveRange(start, count);
            if (change.Text.Length > 0) lines.Insert(start, change.Text);
        }

        return string.Concat(lines);
    }
}

/// <summary>
/// The edits Dray makes to a compose file — add a service, a port, a variable — as insertions.
/// <para>
/// Never regenerated. Dockge rebuilds the whole file from an object on every form edit, and its
/// issue tracker is the list of what that loses: comments, quotes, blank lines, anchors, custom
/// tags. An insertion cannot lose any of them, because every byte that was there is still there.
/// Where the file is written in a shape a line insertion cannot extend — a flow-style
/// <c>{ ... }</c> block — the edit declines rather than reformatting, and the YAML editor is the
/// answer.
/// </para>
/// </summary>
public static class ComposeEdits
{
    /// <summary>Why an edit was declined, in one sentence. Null when it was made.</summary>
    public sealed record Result(ComposeSplice? Splice, string? Declined)
    {
        /// <summary>The edit as line changes — the one shape every edit can take, an insertion included.</summary>
        public IReadOnlyList<ComposeChange> Changes { get; init; } =
            Splice is null ? [] : [new ComposeChange(Splice.BeforeLine, Splice.BeforeLine - 1, Splice.Text)];

        public static Result Made(ComposeSplice splice) => new(splice, null);

        public static Result Changed(IReadOnlyList<ComposeChange> changes) => new(null, null) { Changes = changes };

        public static Result No(string why) => new(null, why);
    }

    public static Result AddService(string text, string name, string image, IReadOnlyList<string>? ports = null)
    {
        var document = ComposeDocument.Parse(text);
        if (document.SyntaxError is not null) return Result.No("The file does not parse, so there is nowhere safe to put a service.");
        if (document.Services.Any(s => s.Name == name)) return Result.No($"There is already a service called {name}.");

        var nl = BreakOf(text);
        var services = document.NodeAt("services");

        if (services is null)
        {
            var body = $"services:{nl}{ServiceBlock(name, image, ports, "  ", "  ", nl)}";
            var prefix = text.Length == 0 || text.EndsWith('\n') ? "" : nl;
            return Result.Made(new ComposeSplice(LineCount(text) + 1, (text.Trim().Length == 0 ? "" : nl) + prefix + body));
        }

        if (services is not YamlMappingNode { Style: not MappingStyle.Flow } block)
            return Result.No("services is written inline ({ … }), so a new service would mean rewriting it. Add it in the YAML.");

        var indent = block.Children.Count > 0 ? new string(' ', (int)block.Children.Keys.First().Start.Column - 1) : "  ";
        var step = StepUnder(block) ?? "  ";

        return Result.Made(new ComposeSplice(LastLine(block) + 1, nl + ServiceBlock(name, image, ports, indent, step, nl)));
    }

    public static Result AddPort(string text, string service, string mapping)
        => AddToList(text, service, "ports", Quote(mapping));

    /// <summary>Mount a named volume into a service: <c>- "data:/var/lib/data"</c>.</summary>
    public static Result AddVolumeMount(string text, string service, string volume, string path)
        => AddToList(text, service, "volumes", Quote($"{volume}:{path}"));

    /// <summary>
    /// Put a service on a network, in whichever form its <c>networks</c> already takes — a list of
    /// names, or a map whose keys are names (and whose values say how).
    /// </summary>
    public static Result JoinNetwork(string text, string service, string network)
    {
        var document = ComposeDocument.Parse(text);

        if (document.NodeAt("services", service, "networks") is YamlMappingNode { Style: not MappingStyle.Flow } map)
        {
            if (map.Children.Keys.OfType<YamlScalarNode>().Any(k => k.Value == network)) return Result.No($"{service} is already on {network}.");

            var indent = new string(' ', (int)(map.Children.Count > 0 ? map.Children.Keys.First().Start.Column : map.Start.Column) - 1);
            return Result.Made(new ComposeSplice(LastLine(map) + 1, $"{indent}{network}:{BreakOf(text)}"));
        }

        if (document.ItemsOf(service, "networks").Any(i => i.Value == network)) return Result.No($"{service} is already on {network}.");

        return AddToList(text, service, "networks", network);
    }

    /// <summary>
    /// Add a variable to a service's <c>environment</c>, in whichever of compose's two forms the
    /// service already uses — a list of <c>KEY=value</c> or a map of <c>KEY: value</c>.
    /// </summary>
    public static Result AddEnvironment(string text, string service, string key, string value)
    {
        var document = ComposeDocument.Parse(text);
        if (document.NodeAt("services", service, "environment") is YamlMappingNode { Style: not MappingStyle.Flow } map)
        {
            if (map.Children.Keys.OfType<YamlScalarNode>().Any(k => k.Value == key)) return Result.No($"{service} already sets {key}.");

            var indent = new string(' ', (int)(map.Children.Count > 0 ? map.Children.Keys.First().Start.Column : map.Start.Column) - 1);
            return Result.Made(new ComposeSplice(LastLine(map) + 1, $"{indent}{key}: {Quote(value)}{BreakOf(text)}"));
        }

        if (document.ItemsOf(service, "environment").Any(i => i.Value?.StartsWith(key + "=", StringComparison.Ordinal) == true))
            return Result.No($"{service} already sets {key}.");

        // No environment yet: start the map form, which reads as what it is — a name and a value.
        if (document.NodeAt("services", service) is YamlMappingNode { Style: not MappingStyle.Flow } body
            && document.NodeAt("services", service, "environment") is null)
        {
            var nl = BreakOf(text);
            var childIndent = new string(' ', (int)body.Children.Keys.First().Start.Column - 1);
            var step = StepUnder(body) ?? "  ";
            return Result.Made(new ComposeSplice(LastLine(body) + 1, $"{childIndent}environment:{nl}{childIndent}{step}{key}: {Quote(value)}{nl}"));
        }

        return AddToList(text, service, "environment", Quote($"{key}={value}"));
    }

    /// <summary>Append an item to a block list under a service key, creating the key if it is not there.</summary>
    static Result AddToList(string text, string service, string key, string item)
    {
        var document = ComposeDocument.Parse(text);
        if (document.SyntaxError is not null) return Result.No("The file does not parse, so there is nowhere safe to make the edit.");

        if (document.NodeAt("services", service) is not YamlMappingNode body)
            return Result.No($"There is no service called {service}.");

        if (body.Style == MappingStyle.Flow)
            return Result.No($"{service} is written inline ({{ … }}). Add it in the YAML.");

        var nl = BreakOf(text);

        switch (document.NodeAt("services", service, key))
        {
            case YamlSequenceNode { Style: SequenceStyle.Block } list when list.Children.Count > 0:
            {
                // The dash's column, read from the text: the sequence node starts at its first
                // item's content, after the "- ".
                var dash = DashIndent(text, (int)list.Children[0].Start.Line);
                return Result.Made(new ComposeSplice(LastLine(list) + 1, $"{dash}- {item}{nl}"));
            }

            // `ports:` with nothing after it — the key is there and its list is not yet.
            case YamlScalarNode { Value: null or "" } empty:
            {
                var keyIndent = (int)document.KeyAt("services", service, key)!.Start.Column - 1;
                var step = StepUnder(body) ?? "  ";
                return Result.Made(new ComposeSplice((int)empty.Start.Line + 1, $"{new string(' ', keyIndent)}{step}- {item}{nl}"));
            }

            case YamlSequenceNode:
            case YamlMappingNode { Style: MappingStyle.Flow }:
                return Result.No($"{key} under {service} is written inline. Add it in the YAML.");

            case null:
            {
                var childIndent = new string(' ', (int)body.Children.Keys.First().Start.Column - 1);
                var step = StepUnder(body) ?? "  ";
                return Result.Made(new ComposeSplice(LastLine(body) + 1, $"{childIndent}{key}:{nl}{childIndent}{step}- {item}{nl}"));
            }

            default:
                return Result.No($"{key} under {service} is not a list Dray can add to. Edit it in the YAML.");
        }
    }

    static string ServiceBlock(string name, string image, IReadOnlyList<string>? ports, string indent, string step, string nl)
    {
        var block = $"{indent}{name}:{nl}{indent}{step}image: {image}{nl}";
        if (ports is { Count: > 0 })
        {
            block += $"{indent}{step}ports:{nl}";
            foreach (var port in ports) block += $"{indent}{step}{step}- {Quote(port)}{nl}";
        }

        return block;
    }

    /// <summary>
    /// The last line a block's content is on. YamlDotNet ends a block at the start of whatever
    /// follows it, which may be lines later past blank lines and comments; the last leaf inside it
    /// is where the block's own text actually ends.
    /// </summary>
    internal static int LastLine(YamlNode node) => node switch
    {
        YamlMappingNode { Children.Count: > 0 } map => LastLine(map.Children.Last().Value) is var v && v > 0 ? v : (int)map.Children.Last().Key.End.Line,
        YamlSequenceNode { Children.Count: > 0 } list => LastLine(list.Children.Last()),
        YamlScalarNode { Value: null or "" } scalar => (int)scalar.Start.Line,

        // A block scalar (| or >) ends at the start of the line after its text.
        _ when node.End.Column == 1 && node.End.Line > node.Start.Line => (int)node.End.Line - 1,
        _ => (int)node.End.Line,
    };

    /// <summary>The indentation one level down from a mapping's keys, as the file writes it.</summary>
    internal static string? StepUnder(YamlMappingNode parent)
    {
        foreach (var (key, value) in parent.Children)
        {
            if (value is YamlMappingNode { Style: not MappingStyle.Flow, Children.Count: > 0 } child)
                return new string(' ', (int)(child.Children.Keys.First().Start.Column - key.Start.Column));
        }

        return null;
    }

    static string DashIndent(string text, int line)
    {
        var content = text.Split('\n')[line - 1];
        var dash = content.IndexOf('-');
        return dash < 0 ? "" : new string(' ', dash);
    }

    internal static int LineCount(string text) => text.Length == 0 ? 0 : text.Count(c => c == '\n') + (text.EndsWith('\n') ? 0 : 1);

    internal static string BreakOf(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>Double-quoted, so a port mapping is never read as a base-60 number and a value never as a boolean.</summary>
    internal static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
