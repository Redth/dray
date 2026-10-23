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

    /// <summary>
    /// True for the Docker CLI plugin, which accepts the CLI's global flags — <c>--context</c>,
    /// <c>-H</c>, the TLS options — ahead of <c>compose</c>. The standalone binary and podman's
    /// wrapper take none of them and are addressed through the environment instead.
    /// </summary>
    public bool TakesDockerFlags => Executable == "docker" && LeadingArguments.Count > 0;
}

/// <summary>One line of output from a compose command.</summary>
public sealed record ComposeOutput(string Text, bool IsError)
{
    /// <summary>
    /// How the process ended. Set on the last item of a run and nowhere else.
    /// <para>
    /// Null on every line, and null when the runner could not say — a test fake replaying a
    /// transcript, or a run the consumer abandoned. Unreported is not zero: a run with no exit code
    /// is not a run that succeeded, and only a reported zero may be shown as one.
    /// </para>
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>The item that says how the process ended, rather than a line it printed.</summary>
    public bool IsExit => ExitCode is not null;

    public static ComposeOutput Exited(int exitCode) => new(string.Empty, IsError: false) { ExitCode = exitCode };
}

/// <summary>
/// Driving compose.
/// <para>
/// docs/ARCHITECTURE.md §2.5: there is no viable .NET Compose library and reimplementing the spec is
/// a trap, so Dray runs the real thing. That means compose is a <b>capability</b>, not an
/// assumption — it may be absent, and the UI has to say so rather than failing at the moment
/// someone presses Up.
/// </para>
/// </summary>
public sealed class ComposeCli(
    IProcessRunner? runner = null,
    Func<IReadOnlyDictionary<string, string>>? environment = null)
{
    readonly IProcessRunner _runner = runner ?? new SystemProcessRunner();

    // A function rather than a snapshot so a test can hand it a fixed environment, and the app
    // reads the real one at the moment of each run.
    readonly Func<IReadOnlyDictionary<string, string>> _environment = environment ?? ComposeEnvironment.Current;

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
    /// What <see cref="DetectAsync"/> found, without asking again. Null both before the probe and
    /// when it found nothing; <see cref="IsProbed"/> tells the two apart.
    /// </summary>
    public ComposeCommand? Found => _found;

    public bool IsProbed => _probed;

    /// <summary>
    /// Run a compose subcommand against a project, streaming its output as it arrives.
    /// <para>
    /// Streamed rather than collected: <c>up</c> on a stack that has to pull images runs for
    /// minutes, and a spinner with nothing behind it is exactly what people distrust. The last item
    /// carries the exit code, which is what failure is — compose prints warnings to stderr on runs
    /// that succeed, so reading its text for the word "error" gets both directions wrong.
    /// </para>
    /// </summary>
    /// <param name="target">Which host the process addresses. See <see cref="ComposeTarget"/>.</param>
    /// <param name="project">The compose project name, passed as <c>-p</c>.</param>
    /// <param name="files">Compose files to use, or empty to let compose find them.</param>
    public IAsyncEnumerable<ComposeOutput> RunAsync(
        ComposeCommand command,
        ComposeTarget target,
        string project,
        IReadOnlyList<string> files,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken ct = default)
        => _runner.StreamAsync(
            command.Executable,
            Arguments(command, target, project, files, arguments),
            workingDirectory,
            ComposeEnvironment.For(_environment(), target),
            ct);

    /// <summary>
    /// Ask compose what it makes of a stack: <c>compose config</c>, the same parse, merge,
    /// interpolation and validation <c>up</c> runs first — so a file it accepts is one <c>up</c>
    /// will read, and a problem it reports is one Deploy would have hit.
    /// <para>
    /// The authority, rather than anything Dray checks itself. The schema the editor validates
    /// against catches a typo while typing; this catches what only compose knows — an undefined
    /// dependency, a required variable with no value, an override that does not merge.
    /// </para>
    /// </summary>
    public async Task<Model.ComposeConfig> ConfigAsync(
        ComposeCommand command,
        ComposeTarget target,
        string project,
        IReadOnlyList<string> files,
        string? workingDirectory,
        CancellationToken ct = default)
    {
        var yaml = new System.Text.StringBuilder();
        var errors = new System.Text.StringBuilder();
        int? exitCode = null;

        await foreach (var line in RunAsync(command, target, project, files, ["config"], workingDirectory, ct).ConfigureAwait(false))
        {
            if (line.IsExit) exitCode = line.ExitCode;
            else if (line.IsError) errors.AppendLine(line.Text);
            else yaml.Append(line.Text).Append('\n');
        }

        return new Model.ComposeConfig(
            exitCode == 0 ? yaml.ToString() : null,
            Model.ComposeProblems.Parse(errors.ToString()),
            exitCode);
    }

    /// <summary>
    /// One invocation as a person would type it: the program, then every argument, quoting the ones
    /// a shell would split. For display — the run itself never goes through a shell.
    /// </summary>
    public static string Display(
        ComposeCommand command,
        ComposeTarget target,
        string project,
        IReadOnlyList<string> files,
        IReadOnlyList<string> arguments)
    {
        var parts = new List<string> { command.Executable };
        parts.AddRange(Arguments(command, target, project, files, arguments).Select(Quote));

        // The environment is how the standalone binary is aimed; leaving it out of the shown command
        // would show a command that targets a different host when pasted.
        var prefix = string.Concat(target.Environment.Select(kv => $"{kv.Key}={Quote(kv.Value)} "));
        return prefix + string.Join(' ', parts);
    }

    static string Quote(string value)
        => value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || "-_./:=@,+%".Contains(c))
            ? value
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// The full argument list for one compose invocation.
    /// <para>
    /// A pure function so that what Dray runs can be tested exactly, and so a UI that shows the
    /// command before running it shows the one that will run rather than a description of it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Arguments(
        ComposeCommand command,
        ComposeTarget target,
        string project,
        IReadOnlyList<string> files,
        IReadOnlyList<string> arguments)
    {
        // Global flags first: `docker --context nas compose`, never `docker compose --context nas`,
        // which compose rejects as an unknown flag.
        List<string> args = [.. target.GlobalArguments, .. command.LeadingArguments, "-p", project];

        foreach (var file in files)
        {
            args.Add("-f");
            args.Add(file);
        }

        args.AddRange(arguments);

        return args;
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
    /// Run a program with extra environment variables set for it alone.
    /// <para>
    /// Exists for one program: <c>wsl.exe</c> writes UTF-16LE unless <c>WSL_UTF8</c> is set, and
    /// setting that in Dray's own environment to reach it would leak into every other child
    /// process for the life of the app.
    /// </para>
    /// <para>
    /// Defaulted rather than abstract because a runner that cannot set an environment can still
    /// answer the question — a test fake replays a recorded transcript, and the encoding of a
    /// string it already holds is not in question.
    /// </para>
    /// </summary>
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct)
        => RunAsync(executable, arguments, workingDirectory, ct);

    /// <summary>
    /// Run a program, writing <paramref name="input"/> to its stdin.
    /// <para>
    /// How the credential helper protocol works: the payload goes in on stdin, never on the command
    /// line, so a secret never appears in a process listing.
    /// </para>
    /// </summary>
    Task<ProcessResult> RunWithInputAsync(
        string executable, IReadOnlyList<string> arguments, string input, CancellationToken ct);

    /// <summary>
    /// Run a program, writing raw bytes to its stdin.
    /// <para>
    /// Separate from the string overload because a file's contents are not text. Encoding them
    /// through a string would corrupt anything that is not valid UTF-16 — an image, a binary, a
    /// certificate — and a file editor that silently mangles what it writes is worse than one that
    /// cannot write at all.
    /// </para>
    /// </summary>
    Task<ProcessResult> RunWithBytesAsync(
        string executable, IReadOnlyList<string> arguments, byte[] input, CancellationToken ct);

    IAsyncEnumerable<ComposeOutput> StreamAsync(
        string executable, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct);

    /// <summary>
    /// Stream a program given exactly <paramref name="environment"/> — nothing inherited — and end
    /// with an item carrying its exit code (<see cref="ComposeOutput.Exited"/>).
    /// <para>
    /// Exact rather than additive because the point is what the child does <i>not</i> see. See
    /// <see cref="ComposeEnvironment"/> for why compose in particular must not see the user's shell.
    /// </para>
    /// <para>
    /// Defaulted for the same reason as the environment overload of <c>RunAsync</c>: a fake that
    /// replays a transcript has no environment to apply, and no exit code to report — so it
    /// reports none, which callers treat as unknown rather than as success.
    /// </para>
    /// </summary>
    IAsyncEnumerable<ComposeOutput> StreamAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken ct)
        => StreamAsync(executable, arguments, workingDirectory, ct);
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

    public Task<ProcessResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct)
        => RunAsync(executable, arguments, workingDirectory, environment: null, ct);

    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct)
    {
        using var process = Start(executable, arguments, workingDirectory, environment: environment);

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

    public async Task<ProcessResult> RunWithBytesAsync(
        string executable, IReadOnlyList<string> arguments, byte[] input, CancellationToken ct)
    {
        using var process = Start(executable, arguments, null, redirectInput: true);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);

        // The base stream, not the writer: StandardInput is a StreamWriter and would encode these
        // bytes as text on the way out, which is the whole thing this overload exists to avoid.
        await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token).ConfigureAwait(false);
        await process.StandardInput.BaseStream.FlushAsync(timeout.Token).ConfigureAwait(false);

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

    public IAsyncEnumerable<ComposeOutput> StreamAsync(
        string executable, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct)
        => StreamCoreAsync(executable, arguments, workingDirectory, environment: null, ct);

    public IAsyncEnumerable<ComposeOutput> StreamAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken ct)
        => StreamCoreAsync(executable, arguments, workingDirectory, environment, ct);

    /// <param name="environment">
    /// Null to inherit Dray's environment and report no exit code, which is what every caller that
    /// predates the exit code expects. Otherwise the child's whole environment, and the stream ends
    /// with <see cref="ComposeOutput.Exited"/>.
    /// </param>
    async IAsyncEnumerable<ComposeOutput> StreamCoreAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var process = Start(
            executable, arguments, workingDirectory, environment: environment, replaceEnvironment: environment is not null);

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

                // After both pipes have drained, so the exit code is always the last thing read
                // and never lands ahead of the line that explains it.
                if (environment is not null) channel.Writer.TryWrite(ComposeOutput.Exited(process.ExitCode));
            }
            catch (Exception)
            {
                // Cancelled, or a broken pipe as the process ended. Either way there is no exit
                // code to report, and inventing one would be worse than leaving it unknown.
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
        bool redirectInput = false,
        IReadOnlyDictionary<string, string>? environment = null,
        bool replaceEnvironment = false)
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

        // Cleared first when the caller is stating the whole environment rather than adding to it.
        if (replaceEnvironment) info.Environment.Clear();

        if (environment is not null)
        {
            foreach (var (key, value) in environment) info.Environment[key] = value;
        }

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
