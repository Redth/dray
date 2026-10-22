using System.Diagnostics;
using System.Text;
using Docker.DotNet.Handler.Abstractions;
using Dray.Core.Engine;

namespace Dray.Docker.Transport;

/// <summary>
/// A relay process, presented as the duplex stream the HTTP layer expects.
/// <para>
/// Reads take from the process's standard output and writes go to its standard input, so an
/// ordinary HTTP client can drive an engine that has no socket on this machine.
/// </para>
/// <para>
/// It derives from <see cref="WriteClosableStream"/> because Docker's exec and attach endpoints
/// hijack the connection: after the upgrade, the client must be able to say "I have sent
/// everything" — an EOF on stdin — without tearing down the half it is still reading. On a socket
/// that is a half-close; here it is closing the child's stdin, which the relay passes on as a
/// half-close of the real socket.
/// </para>
/// </summary>
sealed class ProcessRelayStream : WriteClosableStream
{
    /// <summary>
    /// How much of the relay's standard error to keep for a diagnostic.
    /// <para>
    /// Bounded because this is drained for the life of the connection, and an <c>ssh</c> that has
    /// decided to be chatty must not grow a buffer for as long as the app is open.
    /// </para>
    /// </summary>
    const int MaxCapturedErrorChars = 4 * 1024;

    readonly Process _process;
    readonly Stream _input;
    readonly Stream _output;
    readonly StringBuilder _errors = new();
    readonly RelayCommand _command;
    readonly Task _drained;

    bool _disposed;

    /// <summary>Whether the relay has produced a single byte of response yet.</summary>
    bool _answered;

    ProcessRelayStream(Process process, RelayCommand command)
    {
        _process = process;
        _command = command;
        _input = process.StandardInput.BaseStream;
        _output = process.StandardOutput.BaseStream;

        // Drained on a background loop rather than left alone. Two reasons, and the first is not
        // optional: a pipe nobody reads fills, and a relay that blocks writing to stderr stops
        // relaying altogether — the connection would hang with no explanation anywhere.
        //
        // The second is that the explanation lives here. When a distribution has no docker binary,
        // the failure the HTTP layer sees is an empty stream; the sentence saying why is on stderr.
        _drained = DrainErrorsAsync();
    }

    public static ProcessRelayStream Start(RelayCommand command)
    {
        var info = new ProcessStartInfo(command.FileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);

        Process process;

        try
        {
            process = Process.Start(info)
                ?? throw new RuntimeConnectionException($"Could not start {command.FileName}.");
        }
        catch (Exception ex) when (ex is not RuntimeConnectionException)
        {
            // The launcher itself is missing — no wsl.exe, no ssh on PATH. Worth distinguishing
            // from the engine being unreachable, because the fix is completely different.
            throw new RuntimeConnectionException(
                $"Could not reach {command.Describe}: {command.FileName} could not be started. {ex.Message}", ex);
        }

        return new ProcessRelayStream(process, command);
    }

    /// <summary>What the relay said on standard error, trimmed, or null when it said nothing.</summary>
    public string? Diagnostic
    {
        get
        {
            lock (_errors)
            {
                var text = _errors.ToString().Trim();
                return text.Length == 0 ? null : text;
            }
        }
    }

    public override bool CanCloseWrite => true;

    /// <summary>
    /// Half-close: tell the far side there is no more input, and keep reading.
    /// <para>
    /// What makes an exec session terminate cleanly rather than hanging when the user closes the
    /// terminal, and what lets an archive upload signal the end of its tar.
    /// </para>
    /// </summary>
    public override void CloseWrite()
    {
        try
        {
            _input.Flush();
            _process.StandardInput.Close();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Already gone. A half-close on a connection that has fully closed is a no-op, not a
            // failure worth propagating to a caller who is about to read EOF anyway.
        }
    }

    async Task DrainErrorsAsync()
    {
        try
        {
            var buffer = new char[1024];

            while (true)
            {
                var read = await _process.StandardError.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                if (read == 0) return;

                lock (_errors)
                {
                    if (_errors.Length < MaxCapturedErrorChars) _errors.Append(buffer, 0, read);
                }
            }
        }
        catch (Exception)
        {
            // The process ended, or the stream was disposed underneath us. Either way there is
            // nothing left to drain and nothing here worth reporting.
        }
    }

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _output.Read(buffer, offset, count);

        if (read > 0)
        {
            _answered = true;
            return read;
        }

        if (!_answered && count > 0) throw ExplainAsync().GetAwaiter().GetResult();

        return 0;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var read = await _output.ReadAsync(buffer, ct).ConfigureAwait(false);

        if (read > 0)
        {
            _answered = true;
            return read;
        }

        // End of stream before any response: the relay gave up. Left alone, the HTTP layer reports
        // this as a malformed response, which says nothing. The reason is on stderr — "docker:
        // not found", "connection refused" on the socket — so it is waited for and said instead.
        if (!_answered && buffer.Length > 0) throw await ExplainAsync().ConfigureAwait(false);

        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    async Task<RuntimeConnectionException> ExplainAsync()
    {
        // stderr closes when the relay exits, which it is doing. Bounded, because a relay that has
        // closed stdout and kept stderr open must not hang the connection it already failed.
        await Task.WhenAny(_drained, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);

        return new RuntimeConnectionException(RelayCommands.Explain(_command, Diagnostic));
    }

    public override void Write(byte[] buffer, int offset, int count) => _input.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => _input.WriteAsync(buffer, ct);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => _input.WriteAsync(buffer, offset, count, ct);

    public override void Flush() => _input.Flush();

    public override Task FlushAsync(CancellationToken ct) => _input.FlushAsync(ct);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            try
            {
                // Killed rather than waited for. The relay's job is to live exactly as long as the
                // connection, and it has no reason to exit on its own — a `docker system
                // dial-stdio` whose caller has gone away would otherwise sit on the socket, and a
                // long-lived app would accumulate one per request it ever made.
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or SystemException)
            {
                // Already exited between the check and the kill, which is the normal race and the
                // outcome we wanted anyway.
            }

            _process.Dispose();
        }

        base.Dispose(disposing);
    }

    public override string ToString() => $"relay to {_command.Describe}";
}
