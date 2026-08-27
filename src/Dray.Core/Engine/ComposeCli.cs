using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Dray.Core.Engine;

/// <summary>How compose is invoked on this machine.</summary>
/// <param name="Executable">The program to run.</param>
/// <param name="LeadingArguments">
/// Arguments before the compose subcommand. Empty for the standalone binary; <c>compose</c> for the
/// Docker and podman plugins.
/// </param>
public sealed record ComposeCommand(string Executable, IReadOnlyList<string> LeadingArguments, string Version)
{
    public string Display => LeadingArguments.Count == 0
        ? Executable
        : $"{Executable} {string.Join(' ', LeadingArguments)}";
}

/// <summary>One line of output from a compose command.</summary>
public sealed record ComposeOutput(string Text, bool IsError);

/// <summary>
/// Driving compose.
/// <para>
/// docs/ARCHITECTURE.md §2.5: there is no viable .NET Compose library and reimplementing the spec is
/// a trap, so Dray runs the real thing. That means compose is a <b>capability</b>, not an
/// assumption — it may be absent, and the UI has to say so rather than failing at the moment
/// someone presses Up.
/// </para>
/// </summary>
public sealed class ComposeCli(IProcessRunner? runner = null)
{
    readonly IProcessRunner _runner = runner ?? new SystemProcessRunner();

    /// <summary>
    /// The ways compose ships, best first.
    /// <para>
    /// The plugin forms are preferred because they are the ones the engine's own tooling installs
    /// and they share its context. The standalone binary is last and still common on machines that
    /// predate the plugin, or where podman delegates to it.
    /// </para>
    /// </summary>
    static readonly (string Executable, string[] Leading)[] Candidates =
    [
        ("docker", ["compose"]),
        ("podman", ["compose"]),
        ("docker-compose", []),
        ("podman-compose", []),
    ];

    ComposeCommand? _found;
    bool _probed;

    /// <summary>
    /// Find a working compose, or null. Probed once and remembered — this shells out, and the
    /// answer does not change while the app is running.
    /// </summary>
    public async Task<ComposeCommand?> DetectAsync(CancellationToken ct = default)
    {
        if (_probed) return _found;

        foreach (var (executable, leading) in Candidates)
        {
            try
            {
                var result = await _runner
                    .RunAsync(executable, [.. leading, "version", "--short"], null, ct)
                    .ConfigureAwait(false);

                if (result.ExitCode != 0) continue;

                // podman prints a notice about delegating to an external provider before the
                // version, so the version is the last non-empty line rather than the first.
                var version = result.StandardOutput
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .LastOrDefault();

                if (string.IsNullOrWhiteSpace(version)) continue;

                _found = new ComposeCommand(executable, leading, version);
                break;
            }
            catch (Exception)
            {
                // Not installed, not on PATH, or not executable. Try the next form.
            }
        }

        _probed = true;
        return _found;
    }

    /// <summary>
    /// Run a compose subcommand against a project, streaming its output as it arrives.
    /// <para>
    /// Streamed rather than collected: <c>up</c> on a stack that has to pull images runs for
    /// minutes, and a spinner with nothing behind it is exactly what people distrust.
    /// </para>
    /// </summary>
    /// <param name="project">The compose project name, passed as <c>-p</c>.</param>
    /// <param name="files">Compose files to use, or empty to let compose find them.</param>
    public async IAsyncEnumerable<ComposeOutput> RunAsync(
        ComposeCommand command,
        string project,
        IReadOnlyList<string> files,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var args = new List<string>(command.LeadingArguments) { "-p", project };

        foreach (var file in files)
        {
            args.Add("-f");
            args.Add(file);
        }

        args.AddRange(arguments);

        await foreach (var line in _runner
            .StreamAsync(command.Executable, args, workingDirectory, ct)
            .ConfigureAwait(false))
        {
            yield return line;
        }
    }
}

/// <summary>The result of a process that ran to completion.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Running an external program.
/// <para>
/// An interface so the compose logic can be tested without a compose installed — the alternative is
/// tests that pass or fail depending on what is on the developer's PATH.
/// </para>
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct);

    /// <summary>
    /// Run a program, writing <paramref name="input"/> to its stdin.
    /// <para>
    /// How the credential helper protocol works: the payload goes in on stdin, never on the command
    /// line, so a secret never appears in a process listing.
    /// </para>
    /// </summary>
    Task<ProcessResult> RunWithInputAsync(
        string executable, IReadOnlyList<string> arguments, string input, CancellationToken ct);

    IAsyncEnumerable<ComposeOutput> StreamAsync(
        string executable, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct);
}

/// <summary>The real one.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    /// <summary>
    /// How long a probe may take before it is treated as absent.
    /// <para>
    /// Bounded because a broken PATH entry or a program waiting on something can hang, and a
    /// capability probe must never be the reason the app does not start.
    /// </para>
    /// </summary>
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    public async Task<ProcessResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct)
    {
        using var process = Start(executable, arguments, workingDirectory);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
    }

    public async Task<ProcessResult> RunWithInputAsync(
        string executable, IReadOnlyList<string> arguments, string input, CancellationToken ct)
    {
        using var process = Start(executable, arguments, null, redirectInput: true);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);

        await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token).ConfigureAwait(false);

        // Closed rather than flushed: the helper reads to end-of-stream and would otherwise wait
        // forever for input that has already been written.
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
    }

    public async IAsyncEnumerable<ComposeOutput> StreamAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var process = Start(executable, arguments, workingDirectory);

        // Both streams into one channel, in arrival order. Compose writes its progress to stderr
        // and its results to stdout, and separating them would show the user the ending before the
        // middle.
        var channel = Channel.CreateUnbounded<ComposeOutput>(new UnboundedChannelOptions { SingleReader = true });

        var readers = Task.WhenAll(
            PumpAsync(process.StandardOutput, isError: false, channel, ct),
            PumpAsync(process.StandardError, isError: true, channel, ct));

        _ = Task.Run(async () =>
        {
            try
            {
                await readers.ConfigureAwait(false);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Reported through the exit code below; a broken pipe here is the process ending.
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var line in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return line;
        }
        finally
        {
            // The user navigated away or cancelled. Compose is mid-operation; killing it is the
            // only way to stop it, and leaving it running would mean a stack half-way up with
            // nothing watching.
            TryKill(process);
        }
    }

    static async Task PumpAsync(
        StreamReader reader, bool isError, Channel<ComposeOutput> channel, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            channel.Writer.TryWrite(new ComposeOutput(line, isError));
        }
    }

    static Process Start(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        bool redirectInput = false)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };

        // ArgumentList rather than a joined string: it quotes each argument for the platform, so a
        // compose file under a path with a space works without Dray inventing escaping rules.
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        return Process.Start(info) ?? throw new InvalidOperationException($"Could not start {executable}.");
    }

    static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone, or not ours to kill.
        }
    }
}
