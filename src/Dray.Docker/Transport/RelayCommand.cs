using Dray.Core.Engine;

namespace Dray.Docker.Transport;

/// <summary>
/// A program that, once started, has the Docker Engine API on its standard input and output.
/// <para>
/// This is the whole abstraction behind reaching an engine Dray has no socket to. The Docker CLI
/// uses exactly this shape for <c>ssh://</c> — it runs <c>ssh host docker system dial-stdio</c> and
/// speaks HTTP down the pipe — and a WSL2 distribution is the same problem with a different
/// launcher.
/// </para>
/// </summary>
/// <param name="FileName">The executable to run.</param>
/// <param name="Arguments">Its arguments, unquoted; the process layer quotes them per platform.</param>
/// <param name="Describe">
/// What to call this in an error message, in the user's terms rather than the command line's.
/// </param>
public sealed record RelayCommand(string FileName, IReadOnlyList<string> Arguments, string Describe)
{
    /// <summary>What kind of relay this is, which decides how its failures read.</summary>
    public RelayKind Kind { get; init; }

    /// <summary>The distribution or host it reaches, as the user would name it.</summary>
    public string? Target { get; init; }
}

public enum RelayKind
{
    Other,
    Wsl,
    Ssh,
}

/// <summary>
/// How Dray reaches an engine that has no endpoint on this machine.
/// <para>
/// Every command here ends in <c>docker system dial-stdio</c>, which is the Docker CLI's own
/// hidden subcommand for this: it connects to whatever <c>DOCKER_HOST</c> resolves to on the far
/// side and copies bytes to its stdio. Using it rather than <c>socat</c> or <c>nc</c> means the
/// only dependency is a <c>docker</c> binary — and a machine running a Docker engine has one by
/// definition, whereas plenty of minimal distributions have neither of the others.
/// </para>
/// </summary>
public static class RelayCommands
{
    /// <summary>
    /// Reach the engine inside a WSL2 distribution.
    /// <para>
    /// Three flags, each load-bearing:
    /// </para>
    /// <list type="bullet">
    /// <item><c>--exec</c> runs the program directly instead of through a login shell. This is not
    /// an optimisation: a login shell prints whatever the user's <c>.profile</c> prints, and those
    /// bytes would land in the middle of an HTTP response. Anyone with a banner or a version
    /// manager's greeting in their profile would see the connection fail to parse.</item>
    /// <item><c>-u root</c> because <c>/var/run/docker.sock</c> is owned by root and mode 660. A
    /// user who has added themselves to the <c>docker</c> group would not need it, and one who has
    /// not would get a permission error that reads as the engine being absent.</item>
    /// <item><c>-d</c> names the distribution, so this never depends on which one is default.</item>
    /// </list>
    /// </summary>
    public static RelayCommand ForWsl(string distro) => new(
        "wsl.exe",
        ["-d", distro, "-u", "root", "--exec", "docker", "system", "dial-stdio"],
        $"the Docker engine inside the WSL distribution '{distro}'")
    {
        Kind = RelayKind.Wsl,
        Target = distro,
    };

    /// <summary>
    /// Reach a remote engine over SSH.
    /// <para>
    /// The system <c>ssh</c> binary rather than a library, so the user's <c>~/.ssh/config</c>,
    /// their agent, their jump hosts and their hardware keys all work exactly as they already do
    /// (docs/ARCHITECTURE.md section 2.2: do not reimplement SSH config parsing).
    /// </para>
    /// <para>
    /// <c>BatchMode=yes</c> is what keeps a dead host degrading one sidebar entry instead of
    /// hanging the app: without it <c>ssh</c> blocks forever on a password prompt that has no
    /// terminal to appear on.
    /// </para>
    /// </summary>
    public static RelayCommand ForSsh(DockerEndpoint endpoint)
    {
        var target = endpoint.User is { Length: > 0 } user
            ? $"{user}@{endpoint.Host}"
            : endpoint.Host ?? string.Empty;

        List<string> arguments = ["-o", "BatchMode=yes", "-o", "ConnectTimeout=10"];

        if (endpoint.Port is { } port) arguments.AddRange(["-p", port.ToString()]);

        arguments.AddRange([target, "--", "docker", "system", "dial-stdio"]);

        return new RelayCommand("ssh", arguments, $"the Docker engine on {target}")
        {
            Kind = RelayKind.Ssh,
            Target = target,
        };
    }

    /// <summary>
    /// Why a relay closed without answering, from what it wrote to standard error.
    /// <para>
    /// The relay's own words are accurate and unreadable — <c>execvpe(docker) failed</c>, <c>dial
    /// unix /var/run/docker.sock: connect: connection refused</c> — and each one has a different
    /// fix. These are the ones measured against a real distribution and a real <c>ssh</c>; anything
    /// else is passed through rather than guessed at.
    /// </para>
    /// </summary>
    public static string Explain(RelayCommand command, string? stderr)
    {
        // wsl.exe's own messages are UTF-16 unless WSL_UTF8 is set, and the relay does not set it:
        // a relay's stdout is the HTTP stream and must not have its encoding changed.
        var said = (stderr ?? string.Empty).Replace("\0", string.Empty).Trim();

        if (said.Length == 0) return $"Could not reach {command.Describe}: it closed without answering.";

        var where = command.Kind == RelayKind.Wsl
            ? $"the WSL distribution '{command.Target}'"
            : command.Target ?? "the host";

        if (Says(said, "execvpe(docker) failed") || Says(said, "docker: not found") || Says(said, "docker: command not found"))
            return $"Docker is not installed in {where}.";

        if (Says(said, "docker.sock") && (Says(said, "connection refused") || Says(said, "no such file or directory")))
            return $"Docker is installed in {where}, but its engine is not running.";

        if (Says(said, "docker.sock") && Says(said, "permission denied"))
            return $"Permission denied on the Docker socket in {where}.";

        if (command.Kind == RelayKind.Wsl
            && (Says(said, "WSL_E_DISTRO_NOT_FOUND") || Says(said, "no distribution with the supplied name")))
            return $"There is no WSL distribution called '{command.Target}' any more.";

        if (command.Kind == RelayKind.Ssh)
        {
            // BatchMode means a password prompt is a refusal rather than a hang, so this is what a
            // key the server does not accept looks like.
            if (Says(said, "Permission denied"))
                return $"{where} refused the SSH key. Dray uses your ssh agent and ~/.ssh/config, and cannot answer a password prompt.";

            if (Says(said, "Host key verification failed"))
                return $"{where}'s host key is not trusted yet. Connect once with ssh in a terminal to accept it.";

            if (Says(said, "Could not resolve hostname"))
                return $"{where} could not be found.";
        }

        return $"Could not reach {command.Describe}: {said.Split('\n')[0].Trim()}";
    }

    static bool Says(string text, string phrase) => text.Contains(phrase, StringComparison.OrdinalIgnoreCase);

    /// <summary>The command for an endpoint, or null when it does not need one.</summary>
    public static RelayCommand? For(DockerEndpoint endpoint) => endpoint.Scheme switch
    {
        EndpointScheme.Wsl when endpoint.Distro is { Length: > 0 } distro => ForWsl(distro),
        EndpointScheme.Ssh => ForSsh(endpoint),
        _ => null,
    };
}
