using System.Runtime.CompilerServices;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Dray.Core.Model;

namespace Dray.Docker;

/// <summary>
/// Reads a container's multiplexed log stream and turns it into lines.
/// <para>
/// Docker frames stdout and stderr over one connection with an 8-byte header per chunk.
/// <c>MultiplexedStream</c> decodes the framing; what is left is that a frame is not a line, so
/// the bytes still have to be assembled (see <see cref="LogLineAssembler"/>).
/// </para>
/// </summary>
internal static class DockerLogStream
{
    /// <summary>
    /// 16 KB. Large enough that a chatty container is not read a syllable at a time, small enough
    /// that a quiet one still delivers its next line promptly.
    /// </summary>
    const int BufferSize = 16 * 1024;

    public static async IAsyncEnumerable<LogLine> ReadAsync(
        DockerClient client,
        string containerId,
        LogOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var parameters = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Follow = options.Follow,
            Timestamps = options.Timestamps,
            Tail = options.Tail?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "all",
            Since = options.Since is { } since
                ? since.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null,
        };

        using var stream = await client.Containers
            .GetContainerLogsAsync(containerId, parameters, ct)
            .ConfigureAwait(false);

        var assembler = new LogLineAssembler();
        var buffer = new byte[BufferSize];

        // The engine may split a UTF-8 sequence across frames, so the decoder is kept across reads
        // rather than created per chunk — otherwise a multi-byte character straddling a boundary
        // becomes two replacement characters.
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[BufferSize];

        while (!ct.IsCancellationRequested)
        {
            MultiplexedStream.ReadResult read;
            try
            {
                read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            if (read.EOF) break;
            if (read.Count == 0) continue;

            var charCount = decoder.GetChars(buffer, 0, read.Count, chars, 0);
            if (charCount == 0) continue;

            var target = read.Target == MultiplexedStream.TargetStream.StandardError
                ? LogStream.StdErr
                : LogStream.StdOut;

            foreach (var line in assembler.Append(target, new string(chars, 0, charCount), options.Timestamps))
                yield return line;
        }

        // A container that dies mid-line, or whose last line has no trailing newline, would
        // otherwise lose its final and often most interesting output.
        foreach (var line in assembler.Flush(options.Timestamps)) yield return line;
    }
}
