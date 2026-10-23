using Dray.Core.Engine;

namespace Dray.Core.Tests.Fakes;

/// <summary>
/// Records what compose would have been asked to do, and answers probes from a script.
/// <para>
/// The streamed invocation is captured whole — program, arguments, working directory and the exact
/// environment — because those four are what decide which host a stack lands on, and the point of
/// these tests is that none of them is left to whatever the developer's shell happens to hold.
/// </para>
/// </summary>
public sealed class RecordingProcessRunner : IProcessRunner
{
    readonly List<(string Match, ProcessResult Result)> _responses = [];

    public sealed record Streamed(
        string Executable,
        IReadOnlyList<string> Arguments,
        string? WorkingDirectory,
        IReadOnlyDictionary<string, string>? Environment);

    public List<Streamed> Streams { get; } = [];

    /// <summary>Lines each stream produces, then the exit code it reports.</summary>
    public List<ComposeOutput> Output { get; } = [];

    public int ExitCode { get; set; }

    public RecordingProcessRunner Returns(string match, string stdout, int exitCode = 0)
    {
        _responses.Add((match, new ProcessResult(exitCode, stdout, string.Empty)));
        return this;
    }

    public Task<ProcessResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct)
    {
        var line = string.Join(' ', [executable, .. arguments]);

        foreach (var (match, result) in _responses)
        {
            if (line.Contains(match, StringComparison.Ordinal)) return Task.FromResult(result);
        }

        // Not installed — the same shape a missing program has on a real machine.
        throw new System.ComponentModel.Win32Exception($"no scripted response for `{line}`");
    }

    public Task<ProcessResult> RunWithInputAsync(
        string executable, IReadOnlyList<string> arguments, string input, CancellationToken ct)
        => RunAsync(executable, arguments, null, ct);

    public Task<ProcessResult> RunWithBytesAsync(
        string executable, IReadOnlyList<string> arguments, byte[] input, CancellationToken ct)
        => RunAsync(executable, arguments, null, ct);

    public async IAsyncEnumerable<ComposeOutput> StreamAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Streams.Add(new Streamed(executable, arguments, workingDirectory, null));

        foreach (var line in Output)
        {
            await Task.Yield();
            yield return line;
        }
    }

    public async IAsyncEnumerable<ComposeOutput> StreamAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Streams.Add(new Streamed(executable, arguments, workingDirectory, environment));

        foreach (var line in Output)
        {
            await Task.Yield();
            yield return line;
        }

        yield return ComposeOutput.Exited(ExitCode);
    }
}
