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
public sealed record RelayCommand(string FileName, IReadOnlyList<string> Arguments, string Describe);

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
        $"the Docker engine inside the WSL distribution '{distro}'");

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

        return new RelayCommand("ssh", arguments, $"the Docker engine on {target}");
    }

    /// <summary>The command for an endpoint, or null when it does not need one.</summary>
    public static RelayCommand? For(DockerEndpoint endpoint) => endpoint.Scheme switch
    {
        EndpointScheme.Wsl when endpoint.Distro is { Length: > 0 } distro => ForWsl(distro),
        EndpointScheme.Ssh => ForSsh(endpoint),
        _ => null,
    };
}
