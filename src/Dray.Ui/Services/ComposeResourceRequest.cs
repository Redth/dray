using Dray.Core.Model;

namespace Dray.Ui.Services;

/// <summary>One row of a key-and-value list in a form — a label, a driver option.</summary>
public sealed class OptionRow
{
    public string Key { get; set; } = "";

    public string Value { get; set; } = "";
}

/// <summary>
/// What the volume and network form edits: the fields of a <see cref="ComposeResource"/>, mutable
/// for binding, and — when adding — the services that should use what is being added.
/// </summary>
public sealed partial class ComposeResourceRequest
{
    public required ComposeResourceKind Kind { get; init; }

    /// <summary>Editing an entry already in the file; its name is then fixed, since services refer to it by it.</summary>
    public bool Editing { get; init; }

    /// <summary>Names already declared of this kind, which a new one must not take.</summary>
    public IReadOnlyList<string> Taken { get; init; } = [];

    /// <summary>The file's services, for mounting a new volume or connecting a new network.</summary>
    public IReadOnlyList<string> Services { get; init; } = [];

    public string Name { get; set; } = "";

    public string Driver { get; set; } = "";

    public bool External { get; set; }

    public string EngineName { get; set; } = "";

    public bool Internal { get; set; }

    public bool Attachable { get; set; }

    public bool EnableIpv6 { get; set; }

    public string Subnet { get; set; } = "";

    public string Gateway { get; set; } = "";

    public List<OptionRow> DriverOptions { get; } = [];

    public List<OptionRow> Labels { get; } = [];

    /// <summary>A new volume's service to mount it into, or empty for none.</summary>
    public string MountInto { get; set; } = "";

    /// <summary>Where in that service's containers it is mounted.</summary>
    public string MountPath { get; set; } = "";

    /// <summary>The services a new network connects.</summary>
    public HashSet<string> Join { get; } = new(StringComparer.Ordinal);

    public static ComposeResourceRequest From(ComposeResource resource, IReadOnlyList<string> services)
    {
        var request = new ComposeResourceRequest
        {
            Kind = resource.Kind,
            Editing = true,
            Services = services,
            Name = resource.Name,
            Driver = resource.Driver ?? "",
            External = resource.External,
            EngineName = resource.EngineName ?? "",
            Internal = resource.Internal,
            Attachable = resource.Attachable,
            EnableIpv6 = resource.EnableIpv6,
            Subnet = resource.Subnet ?? "",
            Gateway = resource.Gateway ?? "",
        };

        request.DriverOptions.AddRange(resource.DriverOptions.Select(o => new OptionRow { Key = o.Key, Value = o.Value }));
        request.Labels.AddRange(resource.Labels.Select(o => new OptionRow { Key = o.Key, Value = o.Value }));
        return request;
    }

    /// <summary>The form as the file will have it.</summary>
    public ComposeResource ToResource() => new(Kind, Name.Trim())
    {
        Driver = External || Driver.Trim().Length == 0 ? null : Driver.Trim(),
        DriverOptions = External ? [] : Rows(DriverOptions),
        Labels = Rows(Labels),
        External = External,
        EngineName = EngineName.Trim().Length == 0 ? null : EngineName.Trim(),
        Internal = Kind == ComposeResourceKind.Network && Internal,
        Attachable = Kind == ComposeResourceKind.Network && Attachable,
        EnableIpv6 = Kind == ComposeResourceKind.Network && EnableIpv6,
        Subnet = Kind == ComposeResourceKind.Network && !External && Subnet.Trim().Length > 0 ? Subnet.Trim() : null,
        Gateway = Kind == ComposeResourceKind.Network && !External && Gateway.Trim().Length > 0 ? Gateway.Trim() : null,
    };

    static IReadOnlyList<ComposeOption> Rows(List<OptionRow> rows)
        => [.. rows.Where(r => r.Key.Trim().Length > 0).Select(r => new ComposeOption(r.Key.Trim(), r.Value))];

    /// <summary>What is wrong right now, most fundamental first, or null.</summary>
    public string? Problem
    {
        get
        {
            var name = Name.Trim();
            var noun = Kind == ComposeResourceKind.Volume ? "volume" : "network";

            if (name.Length > 0 && !NamePattern().IsMatch(name))
                return $"A {noun} name is letters, digits, dots, dashes and underscores, starting with a letter or digit.";

            if (!Editing && Taken.Contains(name, StringComparer.Ordinal))
                return $"There is already a {noun} called {name}.";

            if (Subnet.Trim() is { Length: > 0 } subnet && !subnet.Contains('/'))
                return "A subnet is an address range with its size, like 172.28.0.0/16.";

            if (!Editing && MountInto.Length > 0 && !MountPath.Trim().StartsWith('/'))
                return "Say where it goes in the container: a path starting with /, like /var/lib/data.";

            return null;
        }
    }

    public bool IsComplete => Name.Trim().Length > 0 && Problem is null;

    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial System.Text.RegularExpressions.Regex NamePattern();
}
