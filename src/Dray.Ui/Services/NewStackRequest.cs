using Dray.Core.Model;

namespace Dray.Ui.Services;

/// <summary>Where a new stack's file starts from. Chosen once, when the window opens.</summary>
public enum NewStackSource
{
    Blank,
    Image,
    Run,
}

/// <summary>What the New stack dialog collects, and the files it will write.</summary>
public sealed class NewStackRequest
{
    /// <summary>Where the stack is written: one of <see cref="Folders"/>.</summary>
    public required string StacksFolder { get; set; }

    /// <summary>Every stacks folder there is to write into. One is no choice, and none is offered.</summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    /// <summary>Names already taken on this host — a new stack must not shadow one.</summary>
    public IReadOnlyList<string> Existing { get; init; } = [];

    public string Name { get; set; } = "";

    public NewStackSource Source { get; set; } = NewStackSource.Blank;

    public string Image { get; set; } = "";

    public string RunLine { get; set; } = "";

    /// <summary>The files as they stand, or null while they cannot be made — see <see cref="SourceProblem"/>.</summary>
    public AuthoredStack? Result { get; set; }

    /// <summary>Why the chosen source does not produce a file yet, in one sentence.</summary>
    public string? SourceProblem { get; set; }

    public string? NameProblem => Name.Length == 0 ? null : StackCreation.Validate(StacksFolder, Name, Existing);

    public bool IsComplete => Name.Length > 0 && NameProblem is null && Result is not null;
}
