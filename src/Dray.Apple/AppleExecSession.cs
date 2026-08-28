using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Dray.Core.Engine;
using Dray.Core.Model;

namespace Dray.Apple;

/// <summary>
/// A shell in a container, over <c>container exec -i</c>.
/// <para>
/// The process is the session: stdin carries keystrokes, stdout and stderr come back interleaved.
/// That is a plainer arrangement than Docker's hijacked HTTP connection, and it needs no frame
/// demultiplexing — this CLI writes bytes, not Docker's multiplexed stream format.
/// </para>
/// <para>
/// <b>No pseudo-terminal.</b> <c>-t</c> exists but wants a real terminal on the other side, and
/// there is not one here. Without a PTY a shell prints no prompt, and anything that draws a
/// full-screen UI — <c>top</c>, <c>vim</c> — has nothing to draw on. The session says so rather
/// than leaving the user wondering why their prompt is missing, and <see cref="ResizeAsync"/> is
/// honest about having nothing to resize.
/// </para>
/// </summary>
public sealed class AppleExecSession : IExecSession
{
    /// <summary>
    /// Shells to try, in order of how pleasant they are to use.
    /// <para>
    /// The same list the Docker runtime tries. A distroless image has none of them, which is a
    /// real outcome rather than a failure — see <see cref="NoShellException"/>.
    /// </para>
    /// </summary>
    static readonly string[] Shells = ["/bin/bash", "/bin/sh", "/bin/ash"];

    readonly Process _process;
    readonly Channel<string> _output;
    readonly CancellationTokenSource _pumping = new();

    bool _disposed;

    AppleExecSession(Process process, string command, Channel<string> output)
    {
        _process = process;
        _output = output;
        Command = command;
    }

    public string Command { get; }

    /// <summary>
    /// False. <c>-t</c> exists but wants a real terminal on the other end and hangs indefinitely
    /// when handed a pipe — verified, and the reason it is not used. The consequence is no prompt
    /// and no echo, which the view compensates for rather than hiding.
    /// </summary>
    public bool HasPseudoTerminal => false;

    public static async Task<IExecSession> StartAsync(
        IProcessRunner runner,
        string executable,
        string containerId,
        ExecOptions options,
        CancellationToken ct = default)
    {
        var shell = options.Command is { Count: > 0 } explicitCommand
            ? explicitCommand[0]
            : await FindShellAsync(runner, executable, containerId, ct).ConfigureAwait(false);

        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,

            // The CLI writes bytes; anything else would corrupt output from a program that emits
            // UTF-8, which is nearly all of them.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--interactive");

        if (options.WorkingDirectory is { Length: > 0 } directory)
        {
            start.ArgumentList.Add("--workdir");
            start.ArgumentList.Add(directory);
        }

        if (options.User is { Length: > 0 } user)
        {
            start.ArgumentList.Add("--user");
            start.ArgumentList.Add(user);
        }

        start.ArgumentList.Add(containerId);
        start.ArgumentList.Add(shell);

        var process = Process.Start(start)
            ?? throw new NoShellException("Could not start a shell in this container.");

        var output = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var session = new AppleExecSession(process, shell, output);
        session.Pump();

        return session;
    }

    /// <summary>
    /// Which shell the image actually has.
    /// <para>
    /// Probed rather than assumed: alpine has no bash, and starting a shell that is not there
    /// fails with a message about an executable rather than about the image.
    /// </para>
    /// </summary>
    static async Task<string> FindShellAsync(
        IProcessRunner runner, string executable, string containerId, CancellationToken ct)
    {
        foreach (var shell in Shells)
        {
            var probe = await runner
                .RunAsync(executable, ["exec", containerId, shell, "-c", "exit 0"], null, ct)
                .ConfigureAwait(false);

            if (probe.ExitCode == 0) return shell;
        }

        throw new NoShellException(
            "This image has no shell. Distroless and scratch images contain no /bin/sh by design, "
            + "so there is nothing to open — the Files tab still works.");
    }

    /// <summary>
    /// Read both streams into one channel.
    /// <para>
    /// Interleaved deliberately: a terminal shows stdout and stderr in the order they arrive, and
    /// separating them here would reorder a program's own output against its own error messages.
    /// </para>
    /// </summary>
    void Pump()
    {
        _ = PumpOneAsync(_process.StandardOutput);
        _ = PumpOneAsync(_process.StandardError);

        _ = Task.Run(async () =>
        {
            try
            {
                await _process.WaitForExitAsync(_pumping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _output.Writer.TryComplete();
        });
    }

    async Task PumpOneAsync(StreamReader reader)
    {
        // Read by block rather than by line: a shell prompt has no trailing newline, so waiting
        // for one would hold every prompt back until the user typed something.
        var buffer = new char[4096];

        try
        {
            while (!_pumping.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(buffer, _pumping.Token).ConfigureAwait(false);
                if (read <= 0) break;

                _output.Writer.TryWrite(new string(buffer, 0, read));
            }
        }
        catch (Exception)
        {
            // The process ended, or the session was disposed. Either way there is no more output,
            // which the completion below says.
        }
    }

    public async IAsyncEnumerable<string> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunk in _output.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return chunk;
    }

    public async Task WriteAsync(string text, CancellationToken ct = default)
    {
        if (_disposed || _process.HasExited) return;

        await _process.StandardInput.WriteAsync(ForShell(text).AsMemory(), ct).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What the shell should actually receive.
    /// <para>
    /// Carriage return becomes newline, which is what a pseudo-terminal's driver would do and what
    /// is missing without one. A terminal sends CR for Enter — xterm does — and a shell reading a
    /// pipe waits for LF, so without this every command is typed, echoed, and never runs. It looks
    /// exactly like the shell ignoring you.
    /// </para>
    /// <para>
    /// Nothing else is touched: Ctrl-C is how a runaway command is stopped and is not a line
    /// ending.
    /// </para>
    /// </summary>
    internal static string ForShell(string text) => text.Replace('\r', '\n');

    /// <summary>
    /// Nothing to resize.
    /// <para>
    /// There is no pseudo-terminal behind this session — see the class remarks — so there is no
    /// window size to report. Doing nothing is the honest implementation; throwing would make the
    /// terminal component treat a normal state as a fault.
    /// </para>
    /// </summary>
    public Task ResizeAsync(int columns, int rows, CancellationToken ct = default) => Task.CompletedTask;

    public Task<int?> GetExitCodeAsync(CancellationToken ct = default)
        => Task.FromResult(_process.HasExited ? _process.ExitCode : (int?)null);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _pumping.CancelAsync().ConfigureAwait(false);

        try
        {
            if (!_process.HasExited)
            {
                // Closing stdin is how a shell is asked to leave. Killing it outright would skip
                // whatever it does on the way out.
                _process.StandardInput.Close();

                if (!_process.WaitForExit(1000)) _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone, which is the outcome this was after.
        }

        _process.Dispose();
        _pumping.Dispose();
    }
}
