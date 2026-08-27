using System.Text;

namespace Dray.Core.Model;

/// <summary>What to run inside a container.</summary>
/// <param name="Command">
/// The argv to execute. Null means "give me a shell", and the runtime works out which one the
/// image actually has — a decision Dray must not guess at, because being wrong produces a terminal
/// that opens and immediately dies with no explanation.
/// </param>
/// <param name="Tty">
/// Allocate a pseudo-terminal. On for an interactive shell, which is what makes prompts, line
/// editing, colour and <c>top</c> work. Off would give a pipe, and most shells detect that and
/// turn themselves into something much less useful.
/// </param>
public sealed record ExecOptions(
    IReadOnlyList<string>? Command = null,
    bool Tty = true,
    string? WorkingDirectory = null,
    string? User = null)
{
    public static readonly ExecOptions InteractiveShell = new();
}

/// <summary>
/// A live exec inside a container.
/// <para>
/// Owned by whatever opened it and disposed when that view goes away — an exec left running holds a
/// process inside the user's container, which is a worse leak than an idle HTTP connection.
/// </para>
/// </summary>
public interface IExecSession : IAsyncDisposable
{
    /// <summary>What actually started, e.g. <c>/bin/bash</c>. Shown so the user knows where they are.</summary>
    string Command { get; }

    /// <summary>Output as it arrives, already decoded. Ends when the process exits or the session is disposed.</summary>
    IAsyncEnumerable<string> ReadAsync(CancellationToken ct = default);

    /// <summary>Send keystrokes. Includes control characters — the terminal sends what the user typed.</summary>
    Task WriteAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Tell the pseudo-terminal how big it is. Without this, anything that draws a full-screen UI —
    /// <c>top</c>, <c>vim</c>, even line wrapping — renders to the wrong width.
    /// </summary>
    Task ResizeAsync(int columns, int rows, CancellationToken ct = default);

    /// <summary>The process's exit code once it has finished, or null while it is still running.</summary>
    Task<int?> GetExitCodeAsync(CancellationToken ct = default);
}

/// <summary>
/// No shell could be started in the container.
/// <para>
/// A real and common outcome rather than a failure: <c>scratch</c> and distroless images contain no
/// shell by design, and a stopped container cannot execute anything at all. Both deserve an
/// explanation rather than a stack trace.
/// </para>
/// </summary>
public sealed class NoShellException(string message) : Exception(message);

/// <summary>
/// Decodes a byte stream to text across chunk boundaries.
/// <para>
/// Terminal output arrives in arbitrary chunks, and a multi-byte character is routinely split
/// across two of them. Decoding each chunk independently turns every such character into a pair of
/// replacement characters — which is exactly what a box-drawing TUI is made of, so the corruption
/// is immediate and total rather than rare.
/// </para>
/// </summary>
public sealed class Utf8StreamDecoder
{
    // A stateful Decoder holds the leftover bytes of an incomplete sequence between calls, which is
    // the whole point.
    readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    public string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return string.Empty;

        var chars = new char[_decoder.GetCharCount(bytes, flush: false)];
        var written = _decoder.GetChars(bytes, chars, flush: false);

        return new string(chars, 0, written);
    }

    /// <summary>
    /// Emit whatever is left when the stream ends, so a truncated final character is visible as a
    /// replacement character rather than silently dropped.
    /// </summary>
    public string Flush()
    {
        var chars = new char[8];
        var written = _decoder.GetChars([], chars, flush: true);

        return written == 0 ? string.Empty : new string(chars, 0, written);
    }
}
