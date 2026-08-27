using Dray.Core.Engine;

namespace Dray.Apple.Tests;

/// <summary>
/// Stands in for the <c>container</c> CLI, and records what was asked of it.
/// <para>
/// Every canned response in these tests is output the real CLI actually produced on version 1.3.0
/// — captured, not invented. A fake built from a guess at the JSON would pass while the real thing
/// failed, which is the exact failure mode this project has hit before.
/// </para>
/// </summary>
public sealed class ScriptedProcessRunner : IProcessRunner
{
    readonly List<(string Match, ProcessResult Result)> _responses = [];

    /// <summary>Every argument list this runner was invoked with, in order.</summary>
    public List<string> Invocations { get; } = [];

    public List<string> StreamedLines { get; } = [];

    /// <summary>Answer any invocation whose joined arguments contain <paramref name="match"/>.</summary>
    public ScriptedProcessRunner Returns(string match, string stdout, int exitCode = 0, string stderr = "")
    {
        _responses.Add((match, new ProcessResult(exitCode, stdout, stderr)));
        return this;
    }

    public Task<ProcessResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct)
    {
        var line = string.Join(' ', arguments);
        Invocations.Add(line);

        foreach (var (match, result) in _responses)
        {
            if (line.Contains(match, StringComparison.Ordinal)) return Task.FromResult(result);
        }

        return Task.FromResult(new ProcessResult(1, "", $"no scripted response for `{executable} {line}`"));
    }

    public Task<ProcessResult> RunWithInputAsync(
        string executable, IReadOnlyList<string> arguments, string input, CancellationToken ct)
        => RunAsync(executable, arguments, null, ct);

    public async IAsyncEnumerable<ComposeOutput> StreamAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Invocations.Add(string.Join(' ', arguments));

        foreach (var line in StreamedLines)
        {
            await Task.Yield();

            // "!" marks a line the CLI wrote to its own stderr.
            yield return line.StartsWith('!')
                ? new ComposeOutput(line[1..], IsError: true)
                : new ComposeOutput(line, IsError: false);
        }
    }
}
