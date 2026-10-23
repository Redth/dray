using Dray.Core.Model;

namespace Dray.Ui.Services;

public enum QuickEditKind
{
    Service,
    Port,
    Variable,
}

/// <summary>What a quick-edit dialog collects, and the one rule each field has to meet.</summary>
public sealed class QuickEditRequest
{
    public QuickEditKind Kind { get; init; }

    public IReadOnlyList<string> Services { get; init; } = [];

    public string Service { get; set; } = "";
    public string Name { get; set; } = "";
    public string Image { get; set; } = "";
    public string Port { get; set; } = "";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";

    /// <summary>What is wrong with the fields as they stand, or null — checked while typing.</summary>
    public string? Problem => Kind switch
    {
        QuickEditKind.Service when Name.Length > 0 && !ServiceName(Name) => "A service name is letters, digits, dots, dashes and underscores.",
        QuickEditKind.Service when Port.Length > 0 && !PortMapping(Port) => "A port is the host's and the container's, like 8080:80.",
        QuickEditKind.Port when Port.Length > 0 && !PortMapping(Port) => "A port is the host's and the container's, like 8080:80.",
        QuickEditKind.Variable when Key.Length > 0 && !VariableName(Key) => "A variable name is letters, digits and underscores, not starting with a digit.",
        _ => null,
    };

    /// <summary>Everything the edit needs is there and valid.</summary>
    public bool IsComplete => Problem is null && Kind switch
    {
        QuickEditKind.Service => Name.Length > 0 && Image.Trim().Length > 0,
        QuickEditKind.Port => Service.Length > 0 && Port.Length > 0,
        _ => Service.Length > 0 && Key.Length > 0,
    };

    static bool ServiceName(string name) => name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    static bool VariableName(string name) => !char.IsAsciiDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>Read by the same rule the run dialog uses, so the two never disagree about a port.</summary>
    static bool PortMapping(string text)
    {
        RunParser.ParsePorts(text, out var problem);
        return problem is null;
    }
}
