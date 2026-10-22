using System.Runtime.CompilerServices;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Dray.Core.Model;

namespace Dray.Docker;

/// <summary>Starting and running an interactive exec inside a container.</summary>
public static class DockerExec
{
    /// <summary>
    /// Shells to look for, best first.
    /// <para>
    /// bash where it exists because line editing and history are what people expect; ash on Alpine,
    /// which is what BusyBox provides; sh last because it is the one thing almost everything has.
    /// The bare names cover an image whose shell is somewhere unusual but on PATH.
    /// </para>
    /// </summary>
    static readonly string[] ShellCandidates =
        ["/bin/bash", "/usr/bin/bash", "/bin/ash", "/bin/sh", "/usr/bin/sh", "bash", "sh"];

    /// <summary>
    /// The same question for a Windows container, where none of the above exists.
    /// <para>
    /// PowerShell first because it is what a Windows image's own documentation assumes and what
    /// makes a session usable; <c>cmd.exe</c> last because it is the one thing every Windows base
    /// image has — Nano Server dropped PowerShell at ltsc2019 and still ships <c>cmd.exe</c>, so
    /// a cmd session is the difference between a terminal and no terminal on the smallest images.
    /// </para>
    /// <para>
    /// <c>pwsh.exe</c> is offered above <c>powershell.exe</c> only where an image has installed it
    /// deliberately, which is the case in most .NET images.
    /// </para>
    /// </summary>
    static readonly string[] WindowsShellCandidates =
        ["pwsh.exe", "powershell.exe", "cmd.exe"];

    public static async Task<IExecSession> StartAsync(
        DockerClient client,
        string containerId,
        ExecOptions options,
        ContainerOs os,
        CancellationToken ct = default)
    {
        // Asked before trying, because the two engines disagree about how they refuse: Docker
        // answers 409 and podman answers 500 with the reason in a message string. Matching on
        // either would be guessing, and getting it wrong tells the user their image has no shell
        // when in fact their container is simply stopped — sending them to look in the wrong place.
        await EnsureRunningAsync(client, containerId, ct).ConfigureAwait(false);

        var command = options.Command is { Count: > 0 }
            ? options.Command
            : [await FindShellAsync(client, containerId, os, ct).ConfigureAwait(false)];

        try
        {
            var created = await client.Exec.CreateContainerExecAsync(
                containerId,
                new ContainerExecCreateParameters
                {
                    AttachStdin = true,
                    AttachStdout = true,
                    AttachStderr = true,
                    TTY = options.Tty,
                    Cmd = [.. command],
                    WorkingDir = options.WorkingDirectory ?? string.Empty,
                    User = options.User ?? string.Empty,

                    // Without TERM the shell assumes a dumb terminal and turns off colour, and
                    // anything full-screen refuses to draw at all. Windows has no TERM convention
                    // — the console host reads its capabilities from the pseudoconsole the engine
                    // allocates — so setting one there would be cargo cult.
                    Env = options.Tty && os != ContainerOs.Windows ? ["TERM=xterm-256color"] : [],
                },
                ct).ConfigureAwait(false);

            var stream = await client.Exec
                .StartContainerExecAsync(created.ID, new ContainerExecStartParameters { Detach = false, TTY = options.Tty }, ct)
                .ConfigureAwait(false);

            return new DockerExecSession(client, created.ID, string.Join(' ', command), stream);
        }
        catch (DockerApiException ex)
        {
            throw new NoShellException(
                $"Could not start {string.Join(' ', command)} in this container: {ex.Message}");
        }
    }

    static async Task EnsureRunningAsync(DockerClient client, string containerId, CancellationToken ct)
    {
        ContainerInspectResponse inspect;

        try
        {
            inspect = await client.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
        }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 404)
        {
            throw new NoShellException("That container no longer exists.");
        }

        // Paused is checked first, and must be: the engine reports a paused container as
        // Running == true, because its processes exist and are merely frozen. Testing Running
        // first therefore lets a paused container through, and the exec then hangs or fails as
        // though the image had no shell.
        if (inspect.State?.Paused == true)
        {
            throw new NoShellException(
                "This container is paused, so nothing can run inside it. Resume it first.");
        }

        if (inspect.State?.Running == true) return;

        throw new NoShellException(
            "This container is not running, so nothing can be executed inside it. Start it first.");
    }

    /// <summary>
    /// Work out which shell the image actually has.
    /// <para>
    /// Probed rather than assumed. Starting <c>/bin/bash</c> in an Alpine image succeeds at the API
    /// level and then dies immediately, which reaches the user as a terminal that opens blank and
    /// closes — the least diagnosable failure available. One cheap exec answers it properly.
    /// </para>
    /// </summary>
    internal static Task<string> FindShellAsync(
        DockerClient client, string containerId, ContainerOs os, CancellationToken ct)
        => os == ContainerOs.Windows
            ? FindWindowsShellAsync(client, containerId, ct)
            : FindPosixShellAsync(client, containerId, ct);

    /// <summary>
    /// Which Windows shell this image has.
    /// <para>
    /// One exec per candidate rather than one script that tests them all, because the trick the
    /// POSIX path uses is not available: that probe runs a <c>sh</c> loop, and the whole problem
    /// here is that there is no shell to run a loop in. Testing for a file with <c>cmd.exe</c>
    /// instead would answer a slightly different question — an image can have
    /// <c>powershell.exe</c> on disk and still fail to start it — and would cost a round trip
    /// anyway.
    /// </para>
    /// <para>
    /// At most three round trips, and usually one: a Server Core image answers on the second and a
    /// Nano Server image on the third.
    /// </para>
    /// </summary>
    static async Task<string> FindWindowsShellAsync(
        DockerClient client, string containerId, CancellationToken ct)
    {
        foreach (var candidate in WindowsShellCandidates)
        {
            try
            {
                var probe = await client.Exec.CreateContainerExecAsync(
                    containerId,
                    new ContainerExecCreateParameters
                    {
                        // Attached, though nothing is read from them: with nothing attached the
                        // start call returns as soon as the process starts rather than when it ends.
                        AttachStdout = true,
                        AttachStderr = true,

                        // Every one of the three accepts this and exits 0. `cmd /c exit` without a
                        // code exits with whatever ERRORLEVEL happened to be, so the 0 is written.
                        Cmd = candidate.StartsWith("cmd", StringComparison.OrdinalIgnoreCase)
                            ? [candidate, "/c", "exit", "0"]
                            : [candidate, "-NoLogo", "-NoProfile", "-Command", "exit 0"],
                    },
                    ct).ConfigureAwait(false);

                using var stream = await client.Exec
                    .StartContainerExecAsync(probe.ID, new ContainerExecStartParameters { Detach = false }, ct)
                    .ConfigureAwait(false);

                await stream.ReadOutputToEndAsync(ct).ConfigureAwait(false);

                var exitCode = await ExitCodeAsync(
                    token => client.Exec.InspectContainerExecAsync(probe.ID, token), ProbeExitWait, ct).ConfigureAwait(false);
                if (exitCode == 0) return candidate;
            }
            catch (Exception ex) when (ex is DockerApiException or IOException)
            {
                // Not present, or not startable. Either way the next candidate is the answer.
            }
        }

        throw new NoShellException(
            "No shell was found in this container. A Windows image built on a minimal base may have "
            + "neither PowerShell nor cmd.exe — use the Files tab to read and edit what is inside instead.");
    }

    /// <summary>How long a shell probe may take to be recorded as finished.</summary>
    static readonly TimeSpan ProbeExitWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The exit code of an exec whose process has ended, or null if the engine still reports it
    /// running after <paramref name="wait"/>.
    /// <para>
    /// The engine records an exec's exit a moment after its output stream closes. Under Hyper-V
    /// isolation that moment is long enough that an immediate inspect reads Running with no code:
    /// the probe took <c>cmd.exe</c> for missing in every Nano Server container, and the terminal
    /// said there was no shell. A probe still running after the wait is not a shell answering
    /// <c>exit 0</c>, so null is a true answer rather than a timeout.
    /// </para>
    /// </summary>
    internal static async Task<long?> ExitCodeAsync(
        Func<CancellationToken, Task<ContainerExecInspectResponse>> inspect, TimeSpan wait, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + wait;

        while (true)
        {
            var state = await inspect(ct).ConfigureAwait(false);
            if (!state.Running) return state.ExitCode;
            if (DateTime.UtcNow >= deadline) return null;

            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    static async Task<string> FindPosixShellAsync(
        DockerClient client, string containerId, CancellationToken ct)
    {
        // `test -x` on each candidate in turn, printing the first that exists. Run through sh
        // because a loop needs one; an image with no sh at all fails this and is reported honestly.
        var script = string.Join(
            "; ",
            ShellCandidates.Select(s => $"[ -x {s} ] && echo {s} && exit 0"));

        try
        {
            var probe = await client.Exec.CreateContainerExecAsync(
                containerId,
                new ContainerExecCreateParameters
                {
                    AttachStdout = true,
                    AttachStderr = false,
                    Cmd = ["/bin/sh", "-c", script + "; exit 1"],
                },
                ct).ConfigureAwait(false);

            using var stream = await client.Exec
                .StartContainerExecAsync(probe.ID, new ContainerExecStartParameters { Detach = false }, ct)
                .ConfigureAwait(false);

            var (stdout, _) = await stream.ReadOutputToEndAsync(ct).ConfigureAwait(false);

            var found = stdout.Trim().Split('\n').FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(found)) return found;
        }
        catch (Exception ex) when (ex is DockerApiException or IOException)
        {
            // The probe itself needs /bin/sh. Failing here means there is very likely no shell at
            // all, which the message below says.
        }

        throw new NoShellException(
            "No shell was found in this container. Images built from scratch or on a distroless base "
            + "contain no shell by design — use the Files tab to read and edit what is inside instead.");
    }
}

/// <summary>One running exec, over Docker's hijacked bidirectional stream.</summary>
sealed class DockerExecSession(
    DockerClient client,
    string execId,
    string command,
    MultiplexedStream stream) : IExecSession
{
    bool _disposed;

    public string Command { get; } = command;

    public async IAsyncEnumerable<string> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        // Big enough that a screen redraw from a full-screen program arrives in one or two reads
        // rather than a hundred, which matters because each one is a render.
        var buffer = new byte[16 * 1024];
        var decoder = new Utf8StreamDecoder();

        while (!ct.IsCancellationRequested)
        {
            MultiplexedStream.ReadResult result;

            try
            {
                result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The process exited and the engine closed the connection. That is how an exec
                // ends, not a failure to report.
                break;
            }

            if (result.EOF) break;
            if (result.Count == 0) continue;

            // Both streams go to the same place. With a TTY the engine does not separate them
            // anyway, and a shell session where stderr appeared somewhere else would not be a
            // terminal.
            var text = decoder.Decode(buffer.AsSpan(0, result.Count));
            if (text.Length > 0) yield return text;
        }

        var tail = decoder.Flush();
        if (tail.Length > 0) yield return tail;
    }

    public async Task WriteAsync(string text, CancellationToken ct = default)
    {
        if (_disposed) return;

        var bytes = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
    }

    public async Task ResizeAsync(int columns, int rows, CancellationToken ct = default)
    {
        if (_disposed) return;

        // A zero or negative size is what a hidden element measures as, and the engine rejects it.
        if (columns <= 0 || rows <= 0) return;

        try
        {
            await client.Exec.ResizeExecTtyAsync(
                execId,
                new ContainerResizeParameters { Width = columns, Height = rows },
                ct).ConfigureAwait(false);
        }
        catch (DockerApiException)
        {
            // The exec has already finished, or this engine does not implement resize. Neither is
            // worth interrupting the user over — the terminal simply keeps its previous size.
        }
    }

    public async Task<int?> GetExitCodeAsync(CancellationToken ct = default)
    {
        try
        {
            var inspect = await client.Exec.InspectContainerExecAsync(execId, ct).ConfigureAwait(false);

            // Running means there is no exit code yet; the engine also omits it outright in some
            // states, which is the same answer.
            return inspect.Running || inspect.ExitCode is not { } code ? null : (int)code;
        }
        catch (DockerApiException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        // Closing the write half tells the shell its input ended, which is how it is asked to
        // leave. Disposing alone would drop the socket and leave the process running in the
        // container until it happened to notice.
        try
        {
            stream.CloseWrite();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        stream.Dispose();
        return ValueTask.CompletedTask;
    }
}
