namespace Dray.Core.Engine;

/// <summary>
/// Running a command on an SSH host — the user's own <c>ssh</c>, with their keys, agent and
/// config, exactly as the Docker CLI's own SSH transport does.
/// <para>
/// <c>BatchMode</c> always: a password prompt has nowhere to appear in a desktop app, and a command
/// waiting on one hangs until it times out. A host that needs one says so by failing at once.
/// </para>
/// </summary>
public sealed class SshShell(DockerEndpoint endpoint, IProcessRunner? runner = null)
{
    readonly IProcessRunner _runner = runner ?? new SystemProcessRunner();

    public DockerEndpoint Endpoint { get; } = endpoint;

    public static bool Handles(DockerEndpoint endpoint) => endpoint.Scheme == EndpointScheme.Ssh && endpoint.Host is { Length: > 0 };

    /// <summary><c>user@host</c>, or the host alone — whatever the user's ssh config makes of it.</summary>
    public string Target => Endpoint.User is { Length: > 0 } user ? $"{user}@{Endpoint.Host}" : Endpoint.Host ?? "";

    /// <summary>
    /// ssh's arguments for running <paramref name="command"/> remotely. One string after <c>--</c>:
    /// ssh joins its arguments with spaces and hands them to the remote shell anyway, so the
    /// quoting is done once, here, deliberately.
    /// </summary>
    public IReadOnlyList<string> Arguments(string command)
    {
        List<string> arguments = ["-o", "BatchMode=yes", "-o", "ConnectTimeout=10"];
        if (Endpoint.Port is { } port) arguments.AddRange(["-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        arguments.AddRange([Target, "--", command]);
        return arguments;
    }

    /// <summary>How the invocation reads in a terminal, for showing before it runs.</summary>
    public string Display(string command)
        => $"ssh {(Endpoint.Port is { } port ? $"-p {port} " : "")}{Target} -- {Quote(command)}";

    public Task<ProcessResult> RunAsync(string command, CancellationToken ct = default)
        => _runner.RunAsync("ssh", Arguments(command), null, ct);

    public Task<ProcessResult> RunWithBytesAsync(string command, byte[] input, CancellationToken ct = default)
        => _runner.RunWithBytesAsync("ssh", Arguments(command), input, ct);

    public IAsyncEnumerable<ComposeOutput> StreamAsync(string command, IReadOnlyDictionary<string, string> environment, CancellationToken ct = default)
        => _runner.StreamAsync("ssh", Arguments(command), null, environment, ct);

    /// <summary>
    /// POSIX single-quoting: everything literal, a single quote closed, escaped and reopened. The
    /// one form every remote shell — sh, bash, dash, ash, zsh — reads the same way.
    /// </summary>
    public static string Quote(string value)
        => value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || "-_./:=@,+%".Contains(c))
            ? value
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public static string Join(IEnumerable<string> words) => string.Join(' ', words.Select(Quote));
}
