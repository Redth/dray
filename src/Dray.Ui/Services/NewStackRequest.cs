using Dray.Core.Model;

namespace Dray.Ui.Services;

public enum NewStackSource
{
    Blank,
    Image,
    Run,
    Containers,
}

/// <summary>What the New stack dialog collects, and the files it will write.</summary>
public sealed class NewStackRequest
{
    public required string StacksFolder { get; init; }

    /// <summary>Names already taken on this host — a new stack must not shadow one.</summary>
    public IReadOnlyList<string> Existing { get; init; } = [];

    public string Name { get; set; } = "";

    public NewStackSource Source { get; set; } = NewStackSource.Blank;

    public string Image { get; set; } = "";

    public string Ports { get; set; } = "";

    public string RunLine { get; set; } = "";

    public HashSet<string> Containers { get; } = [];

    /// <summary>The files as they stand, or null while they cannot be made — see <see cref="SourceProblem"/>.</summary>
    public AuthoredStack? Result { get; set; }

    /// <summary>Why the chosen source does not produce a file yet, in one sentence.</summary>
    public string? SourceProblem { get; set; }

    public string? NameProblem => Name.Length == 0 ? null : StackCreation.Validate(StacksFolder, Name, Existing);

    public bool IsComplete => Name.Length > 0 && NameProblem is null && Result is not null;
}
