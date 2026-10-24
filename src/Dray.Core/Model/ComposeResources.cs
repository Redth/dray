using YamlDotNet.RepresentationModel;

namespace Dray.Core.Model;

/// <summary>The two top-level things a compose file declares beside its services, and that a form can describe.</summary>
public enum ComposeResourceKind
{
    Volume,
    Network,
}

/// <summary>A key and its value, in a map such as <c>labels</c> or <c>driver_opts</c>.</summary>
public sealed record ComposeOption(string Key, string Value);

/// <summary>Where a volume or network is declared — the line its name is on.</summary>
public sealed record ComposeResourceEntry(ComposeResourceKind Kind, string Name, int Line);

/// <summary>
/// A volume or network as a form shows it: the fields the Compose Specification gives it that people
/// set by hand, and nothing else. Anything else in the file's entry is left exactly as written.
/// </summary>
/// <param name="Name">The key in the file — what services refer to it by.</param>
public sealed record ComposeResource(ComposeResourceKind Kind, string Name)
{
    public string? Driver { get; init; }

    public IReadOnlyList<ComposeOption> DriverOptions { get; init; } = [];

    public IReadOnlyList<ComposeOption> Labels { get; init; } = [];

    /// <summary>Made outside this stack; compose uses it and never creates or removes it.</summary>
    public bool External { get; init; }

    /// <summary>The name on the engine, where it differs from <see cref="Name"/> — compose's <c>name:</c>.</summary>
    public string? EngineName { get; init; }

    /// <summary>A network with no route out of the host.</summary>
    public bool Internal { get; init; }

    /// <summary>A network a container outside the stack may join.</summary>
    public bool Attachable { get; init; }

    public bool EnableIpv6 { get; init; }

    /// <summary>A network's address range, as <c>ipam.config[0].subnet</c>.</summary>
    public string? Subnet { get; init; }

    public string? Gateway { get; init; }
}

/// <summary>
/// Reading and writing a compose file's volumes and networks, for a form to fill in.
/// <para>
/// The same promise <see cref="ComposeEdits"/> makes: never regenerated. Adding writes the new entry
/// and touches nothing else; editing rewrites only the lines of the fields that changed, so a
/// comment, an anchor or a key the form has never heard of is still there, byte for byte, after
/// the form has been used on the entry around it.
/// </para>
/// </summary>
public static class ComposeResources
{
    /// <summary>Every volume and network declared, in file order, with the line each is on.</summary>
    public static IReadOnlyList<ComposeResourceEntry> List(string text)
    {
        var document = ComposeDocument.Parse(text);
        var entries = new List<ComposeResourceEntry>();

        foreach (var kind in (ComposeResourceKind[])[ComposeResourceKind.Volume, ComposeResourceKind.Network])
        {
            if (document.NodeAt(Section(kind)) is not YamlMappingNode section) continue;

            foreach (var (key, _) in section.Children)
            {
                if (key is YamlScalarNode { Value: { } name }) entries.Add(new ComposeResourceEntry(kind, name, (int)key.Start.Line));
            }
        }

        return [.. entries.OrderBy(e => e.Line)];
    }

    /// <summary>The section a kind is declared under: <c>volumes</c> or <c>networks</c>.</summary>
    public static string Section(ComposeResourceKind kind) => kind == ComposeResourceKind.Volume ? "volumes" : "networks";

    /// <summary>What the file says about one entry, or null when it does not declare it.</summary>
    public static ComposeResource? Read(string text, ComposeResourceKind kind, string name)
    {
        var document = ComposeDocument.Parse(text);
        if (document.KeyAt(Section(kind), name) is null) return null;

        var resource = new ComposeResource(kind, name);
        if (document.NodeAt(Section(kind), name) is not YamlMappingNode body) return resource;

        var ipam = Child(body, "ipam") is YamlMappingNode i && Child(i, "config") is YamlSequenceNode { Children.Count: > 0 } config
            ? config.Children[0] as YamlMappingNode
            : null;

        return resource with
        {
            Driver = Scalar(Child(body, "driver")),
            DriverOptions = Options(Child(body, "driver_opts")),
            Labels = Options(Child(body, "labels")),
            External = IsTrue(Child(body, "external")) || Child(body, "external") is YamlMappingNode,
            EngineName = Scalar(Child(body, "name")),
            Internal = IsTrue(Child(body, "internal")),
            Attachable = IsTrue(Child(body, "attachable")),
            EnableIpv6 = IsTrue(Child(body, "enable_ipv6")),
            Subnet = ipam is null ? null : Scalar(Child(ipam, "subnet")),
            Gateway = ipam is null ? null : Scalar(Child(ipam, "gateway")),
        };
    }

    /// <summary>Declare a new volume or network, starting the section if the file has none.</summary>
    public static ComposeEdits.Result Add(string text, ComposeResource resource)
    {
        var document = ComposeDocument.Parse(text);
        if (document.SyntaxError is not null) return ComposeEdits.Result.No("The file does not parse, so there is nowhere safe to put it.");

        var section = Section(resource.Kind);
        if (document.KeyAt(section, resource.Name) is not null)
            return ComposeEdits.Result.No($"There is already a {Noun(resource.Kind)} called {resource.Name}.");

        var nl = ComposeEdits.BreakOf(text);

        switch (document.NodeAt(section))
        {
            case null when document.KeyAt(section) is null:
            {
                // A section of its own at the end, a blank line from what is above it.
                var lead = text.Trim().Length == 0 ? "" : nl;
                var block = $"{lead}{section}:{nl}{Entry(resource, "  ", "  ", nl)}";
                return Made(new ComposeChange(ComposeEdits.LineCount(text) + 1, ComposeEdits.LineCount(text), block));
            }

            // `volumes:` with nothing under it yet.
            case YamlScalarNode { Value: null or "" } empty:
                return Made(new ComposeChange((int)empty.Start.Line + 1, (int)empty.Start.Line, Entry(resource, "  ", "  ", nl)));

            case YamlMappingNode { Style: not YamlDotNet.Core.Events.MappingStyle.Flow } block:
            {
                var indent = block.Children.Count > 0 ? new string(' ', (int)block.Children.Keys.First().Start.Column - 1) : "  ";
                var step = ComposeEdits.StepUnder(block) ?? "  ";
                var at = ComposeEdits.LastLine(block) + 1;
                return Made(new ComposeChange(at, at - 1, Entry(resource, indent, step, nl)));
            }

            default:
                return ComposeEdits.Result.No($"{section} is written inline ({{ … }}), so adding to it would mean rewriting it. Add it in the YAML.");
        }
    }

    /// <summary>
    /// Bring an entry in line with the form: each field that differs from what the file says is
    /// rewritten, added or removed on its own lines; every other line is left alone.
    /// </summary>
    public static ComposeEdits.Result Update(string text, ComposeResource resource)
    {
        var document = ComposeDocument.Parse(text);
        var section = Section(resource.Kind);

        if (Read(text, resource.Kind, resource.Name) is not { } current || document.KeyAt(section, resource.Name) is not { } key)
            return ComposeEdits.Result.No($"There is no {Noun(resource.Kind)} called {resource.Name} in the file any more.");

        var nl = ComposeEdits.BreakOf(text);
        var value = document.NodeAt(section, resource.Name);
        var changes = new List<ComposeChange>();

        // An entry with nothing under it: whatever the form sets goes straight below its name.
        if (value is null or YamlScalarNode { Value: null or "" })
        {
            var step = document.NodeAt(section) is YamlMappingNode parent ? ComposeEdits.StepUnder(parent) ?? "  " : "  ";
            var indent = new string(' ', (int)key.Start.Column - 1) + step;
            var lines = Fields(resource, indent, step, nl);

            return lines.Length == 0 ? ComposeEdits.Result.Changed([]) : Made(new ComposeChange((int)key.Start.Line + 1, (int)key.Start.Line, lines));
        }

        if (value is not YamlMappingNode { Style: not YamlDotNet.Core.Events.MappingStyle.Flow } body)
            return ComposeEdits.Result.No($"{resource.Name} is written inline ({{ … }}). Edit it in the YAML.");

        var childIndent = new string(' ', (int)body.Children.Keys.First().Start.Column - 1);
        var unit = ComposeEdits.StepUnder(body) ?? "  ";
        var inserts = new List<string>();

        void Set(string field, string? rendered)
        {
            var existing = KeyOf(body, field);

            if (existing is null)
            {
                if (rendered is not null) inserts.Add(rendered);
                return;
            }

            var first = (int)existing.Start.Line;
            var last = ComposeEdits.LastLine(Child(body, field)!) is var end && end >= first ? end : first;
            changes.Add(new ComposeChange(first, last, rendered ?? ""));
        }

        if (current.Driver != resource.Driver) Set("driver", resource.Driver is { Length: > 0 } d ? Line(childIndent, "driver", d, nl) : null);
        if (!Same(current.DriverOptions, resource.DriverOptions)) Set("driver_opts", Map(childIndent, unit, "driver_opts", resource.DriverOptions, nl));
        if (!Same(current.Labels, resource.Labels)) Set("labels", Map(childIndent, unit, "labels", resource.Labels, nl));
        if (current.External != resource.External) Set("external", resource.External ? $"{childIndent}external: true{nl}" : null);
        if (current.EngineName != resource.EngineName) Set("name", resource.EngineName is { Length: > 0 } n ? Line(childIndent, "name", n, nl) : null);

        if (resource.Kind == ComposeResourceKind.Network)
        {
            if (current.Internal != resource.Internal) Set("internal", resource.Internal ? $"{childIndent}internal: true{nl}" : null);
            if (current.Attachable != resource.Attachable) Set("attachable", resource.Attachable ? $"{childIndent}attachable: true{nl}" : null);
            if (current.EnableIpv6 != resource.EnableIpv6) Set("enable_ipv6", resource.EnableIpv6 ? $"{childIndent}enable_ipv6: true{nl}" : null);

            if (current.Subnet != resource.Subnet || current.Gateway != resource.Gateway)
            {
                // The form knows one subnet and its gateway. An ipam block that says more than that
                // — a driver of its own, a second range — is not the form's to rewrite.
                if (Child(body, "ipam") is YamlMappingNode ipam
                    && (ipam.Children.Count > 1 || Child(ipam, "config") is YamlSequenceNode { Children.Count: > 1 }))
                {
                    return ComposeEdits.Result.No($"{resource.Name}'s ipam says more than one subnet and its gateway. Edit it in the YAML.");
                }

                Set("ipam", Ipam(childIndent, unit, resource, nl));
            }
        }

        if (inserts.Count > 0)
        {
            var at = ComposeEdits.LastLine(body) + 1;
            changes.Add(new ComposeChange(at, at - 1, string.Concat(inserts)));
        }

        return ComposeEdits.Result.Changed(changes);
    }

    static ComposeEdits.Result Made(ComposeChange change) => ComposeEdits.Result.Changed([change]);

    static string Noun(ComposeResourceKind kind) => kind == ComposeResourceKind.Volume ? "volume" : "network";

    /// <summary>A whole new entry: its name, then its fields one level in.</summary>
    static string Entry(ComposeResource resource, string indent, string step, string nl)
        => $"{indent}{resource.Name}:{nl}{Fields(resource, indent + step, step, nl)}";

    /// <summary>Every field the form has set, in the order the specification lists them.</summary>
    static string Fields(ComposeResource r, string indent, string step, string nl)
    {
        var lines = new List<string?>
        {
            r.Driver is { Length: > 0 } d ? Line(indent, "driver", d, nl) : null,
            Map(indent, step, "driver_opts", r.DriverOptions, nl),
            r.External ? $"{indent}external: true{nl}" : null,
            r.EngineName is { Length: > 0 } n ? Line(indent, "name", n, nl) : null,
        };

        if (r.Kind == ComposeResourceKind.Network)
        {
            lines.Add(r.Internal ? $"{indent}internal: true{nl}" : null);
            lines.Add(r.Attachable ? $"{indent}attachable: true{nl}" : null);
            lines.Add(r.EnableIpv6 ? $"{indent}enable_ipv6: true{nl}" : null);
            lines.Add(Ipam(indent, step, r, nl));
        }

        lines.Add(Map(indent, step, "labels", r.Labels, nl));

        return string.Concat(lines.Where(l => l is not null));
    }

    static string Line(string indent, string key, string value, string nl) => $"{indent}{key}: {Plain(value)}{nl}";

    static string? Map(string indent, string step, string key, IReadOnlyList<ComposeOption> options, string nl)
    {
        var set = options.Where(o => o.Key.Trim().Length > 0).ToList();
        if (set.Count == 0) return null;

        return $"{indent}{key}:{nl}" + string.Concat(set.Select(o => $"{indent}{step}{o.Key.Trim()}: {Plain(o.Value)}{nl}"));
    }

    static string? Ipam(string indent, string step, ComposeResource r, string nl)
    {
        if (r.Subnet is not { Length: > 0 } && r.Gateway is not { Length: > 0 }) return null;

        var item = $"{indent}{step}{step}";
        var lines = $"{indent}ipam:{nl}{indent}{step}config:{nl}";
        var first = true;

        foreach (var (key, value) in (ReadOnlySpan<(string, string?)>)[("subnet", r.Subnet), ("gateway", r.Gateway)])
        {
            if (value is not { Length: > 0 }) continue;

            lines += $"{item}{(first ? "- " : "  ")}{key}: {Plain(value)}{nl}";
            first = false;
        }

        return lines;
    }

    /// <summary>
    /// Written bare where YAML would read it back as the same string, quoted where it would not — a
    /// value of <c>true</c>, <c>8080</c> or <c>a: b</c> would otherwise come back as something else.
    /// </summary>
    static string Plain(string value)
    {
        var bare = value.Length > 0
            && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or ':' or '=' or '@')
            && !value.Contains(": ", StringComparison.Ordinal)
            && !value.EndsWith(':')
            && !double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out _)
            && value.ToLowerInvariant() is not ("true" or "false" or "yes" or "no" or "on" or "off" or "null" or "~");

        return bare ? value : ComposeEdits.Quote(value);
    }

    static bool Same(IReadOnlyList<ComposeOption> a, IReadOnlyList<ComposeOption> b)
        => a.Where(o => o.Key.Trim().Length > 0).SequenceEqual(b.Where(o => o.Key.Trim().Length > 0));

    static YamlNode? Child(YamlMappingNode map, string key)
        => map.Children.FirstOrDefault(c => c.Key is YamlScalarNode { Value: { } k } && k == key).Value;

    static YamlScalarNode? KeyOf(YamlMappingNode map, string key)
        => map.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == key);

    static string? Scalar(YamlNode? node) => node is YamlScalarNode { Value: { Length: > 0 } value } ? value : null;

    static bool IsTrue(YamlNode? node) => node is YamlScalarNode { Value: { } v } && v.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>A map, or compose's list form of one: <c>- key=value</c>.</summary>
    static IReadOnlyList<ComposeOption> Options(YamlNode? node) => node switch
    {
        YamlMappingNode map =>
        [
            .. map.Children
                .Where(c => c.Key is YamlScalarNode { Value: not null })
                .Select(c => new ComposeOption(((YamlScalarNode)c.Key).Value!, (c.Value as YamlScalarNode)?.Value ?? "")),
        ],
        YamlSequenceNode list =>
        [
            .. list.Children.OfType<YamlScalarNode>().Where(s => s.Value is not null).Select(s =>
                s.Value!.IndexOf('=') is var eq and > 0 ? new ComposeOption(s.Value[..eq], s.Value[(eq + 1)..]) : new ComposeOption(s.Value, "")),
        ],
        _ => [],
    };
}
