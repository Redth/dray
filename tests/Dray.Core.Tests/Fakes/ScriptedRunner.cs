using System.Runtime.CompilerServices;
using Dray.Core.Engine;

namespace Dray.Core.Tests.Fakes;

/// <summary>
/// A process runner that answers from a script of rules, and records every call — including what
/// went to stdin, which is how a test proves a secret travelled there and nowhere else.
/// <para>
/// Anything no rule matches exits 1, so a test that forgot a rule fails loudly rather than being
/// answered by a program on the machine running it.
/// </para>
/// </summary>
sealed class ScriptedRunner : IProcessRunner
{
    readonly List<(Func<string, IReadOnlyList<string>, bool> When, ProcessResult Result)> _rules = [];

    public List<(string Executable, IReadOnlyList<string> Arguments, string? Input)> Calls { get; } = [];

    /// <summary>Answer any call whose arguments contain all of <paramref name="arguments"/>, in order.</summary>
    public ScriptedRunner On(string[] arguments, int exitCode, string stdout = "", string stderr = "")
    {
        _rules.Add(((_, args) => ContainsInOrder(args, arguments), new ProcessResult(exitCode, stdout, stderr)));
        return this;
    }

    static bool ContainsInOrder(IReadOnlyList<string> haystack, string[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Count; i++)
        {
            if (needle.Select((n, j) => haystack[i + j] == n).All(x => x)) return true;
        }

        return false;
    }

    ProcessResult Answer(string executable, IReadOnlyList<string> arguments, string? input)
    {
        Calls.Add((executable, arguments, input));

        return _rules.LastOrDefault(r => r.When(executable, arguments)) is { Result: { } result }
            ? result
            : new ProcessResult(1, "", "no rule for this call");
    }

    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct)
        => Task.FromResult(Answer(executable, arguments, null));

    public Task<ProcessResult> RunLongAsync(
        string executable, IReadOnlyList<string> arguments, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
        => Task.FromResult(Answer(executable, arguments, null));

    public Task<ProcessResult> RunWithInputAsync(string executable, IReadOnlyList<string> arguments, string input, CancellationToken ct)
        => Task.FromResult(Answer(executable, arguments, input));

    public Task<ProcessResult> RunWithBytesAsync(string executable, IReadOnlyList<string> arguments, byte[] input, CancellationToken ct)
        => Task.FromResult(Answer(executable, arguments, null));

    public async IAsyncEnumerable<ComposeOutput> StreamAsync(
        string executable, IReadOnlyList<string> arguments, string? workingDirectory, [EnumeratorCancellation] CancellationToken ct)
    {
        Answer(executable, arguments, null);
        await Task.CompletedTask;
        yield break;
    }
}
