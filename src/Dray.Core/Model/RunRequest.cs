namespace Dray.Core.Model;

/// <summary>
/// What to run, and the four things people actually change when they run it.
/// <para>
/// <c>docker run</c> takes well over a hundred flags. Offering all of them would be a form nobody
/// can complete and a set of choices nobody can evaluate — the same reason
/// <see cref="NetworkRequest"/> carries five fields rather than twenty. These four are what a
/// person types when they want a database up: a name they can find it by, a port they can reach it
/// on, the environment it needs to boot, and somewhere for its data to live.
/// </para>
/// <para>
/// Anything more specific belongs in a compose file, which Dray can already bring up. This dialog
/// is for the one-off container, not a replacement for a stack definition.
/// </para>
/// </summary>
public sealed record RunRequest
{
    public required string Image { get; init; }

    /// <summary>
    /// What to call it, or null to let the engine invent one.
    /// <para>
    /// Optional because the engine's generated names are fine for a throwaway, and requiring one
    /// would put a mandatory field in front of the most common case.
    /// </para>
    /// </summary>
    public string? Name { get; init; }

    public IReadOnlyList<PortBinding> Ports { get; init; } = [];

    public IReadOnlyList<EnvVar> Environment { get; init; } = [];

    public IReadOnlyList<MountPoint> Mounts { get; init; } = [];

    /// <summary>
    /// Extra labels to stamp on the container.
    /// <para>
    /// Used to record which variables the user marked secret, which the engine then carries for
    /// the container's whole life — see <see cref="SecretMarks"/>.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Start it as well as create it.
    /// <para>
    /// False creates the container and leaves it stopped, which is how you inspect what an image
    /// would do — its command, its ports, its declared volumes — without letting it run.
    /// </para>
    /// </summary>
    public bool Start { get; init; } = true;
}

/// <summary>
/// Turning what someone types into a <see cref="RunRequest"/>.
/// <para>
/// The forms accepted are the ones from <c>docker run</c>, because that is what people already have
/// in their shell history and in the README they are copying from. A dialog that accepted a
/// different syntax for the same idea would be a dialog people retype things for.
/// </para>
/// <para>
/// Every parser here reports the first thing it could not read rather than throwing, so the dialog
/// can say which line is wrong while the user is still looking at it.
/// </para>
/// </summary>
public static class RunParser
{
    /// <summary>
    /// Port mappings, one per line: <c>8080:80</c>, <c>8080:80/udp</c>, or a bare <c>80</c>.
    /// </summary>
    /// <param name="problem">The first unreadable line, or null.</param>
    public static IReadOnlyList<PortBinding> ParsePorts(string? text, out string? problem)
    {
        problem = null;

        var ports = new List<PortBinding>();
        var claimed = new HashSet<(int Port, string Protocol)>();

        foreach (var line in Lines(text))
        {
            var body = line;
            var protocol = "tcp";

            var slash = body.LastIndexOf('/');
            if (slash > 0)
            {
                protocol = body[(slash + 1)..].Trim().ToLowerInvariant();
                body = body[..slash];

                if (protocol is not ("tcp" or "udp" or "sctp"))
                {
                    problem = $"“{line}” — a port is tcp, udp or sctp.";
                    return ports;
                }
            }

            var colon = body.IndexOf(':');

            // A bare number means the same port on both sides. `docker run -p 80` does not, but
            // it is what everyone means when they type it, and the alternative is an error for
            // something unambiguous.
            var hostText = colon < 0 ? body : body[..colon];
            var containerText = colon < 0 ? body : body[(colon + 1)..];

            if (!TryPort(hostText, out var host) || !TryPort(containerText, out var container))
            {
                problem = $"“{line}” — write a port as 8080:80, or 80 for the same port on both sides.";
                return ports;
            }

            // Two rules mapping the same host port fails at run time with a message about an
            // address already in use, which reads as something else being on the port rather than
            // as a typo two lines up.
            if (!claimed.Add((host, protocol)))
            {
                problem = $"Host port {host}/{protocol} is mapped twice.";
                return ports;
            }

            ports.Add(new PortBinding(host, container, protocol));
        }

        return ports;
    }

    static bool TryPort(string text, out int port)
        => int.TryParse(text.Trim(), out port) && port is > 0 and <= 65535;

    /// <summary>
    /// Environment variables, one <c>KEY=value</c> per line.
    /// <para>
    /// The value is taken verbatim after the first <c>=</c>, so a connection string full of them
    /// survives intact. A line with no <c>=</c> is an error rather than an empty variable: it is
    /// almost always a half-typed line, and silently creating <c>FOO=""</c> hides that.
    /// </para>
    /// </summary>
    public static IReadOnlyList<EnvVar> ParseEnvironment(string? text, out string? problem)
    {
        problem = null;

        var vars = new List<EnvVar>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in Lines(text))
        {
            var split = line.IndexOf('=');

            if (split <= 0)
            {
                problem = $"“{line}” — write a variable as NAME=value.";
                return vars;
            }

            var key = line[..split].Trim();

            if (key.Length == 0 || key.Any(char.IsWhiteSpace))
            {
                problem = $"“{line}” — a variable name cannot contain spaces.";
                return vars;
            }

            if (!seen.Add(key))
            {
                // The engine takes the last one silently. Saying so beats setting a value the user
                // can see in the box and cannot find in the container.
                problem = $"{key} is set twice.";
                return vars;
            }

            vars.Add(new EnvVar(key, line[(split + 1)..]));
        }

        return vars;
    }

    /// <summary>
    /// Mounts, one per line: <c>data:/var/lib/postgresql/data</c> or
    /// <c>/Users/me/site:/usr/share/nginx/html:ro</c>.
    /// <para>
    /// A source starting with <c>/</c> or <c>~</c> is a host path; anything else is a named volume,
    /// which is the rule the Docker CLI uses and the one people already have in their heads.
    /// </para>
    /// </summary>
    public static IReadOnlyList<MountPoint> ParseMounts(string? text, out string? problem)
    {
        problem = null;

        var mounts = new List<MountPoint>();
        var destinations = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in Lines(text))
        {
            // Split from the right so a Windows-style source keeps its drive letter, and so the
            // optional :ro suffix is found before the destination is.
            var parts = line.Split(':', StringSplitOptions.TrimEntries);

            if (parts.Length is < 2 or > 3)
            {
                problem = $"“{line}” — write a mount as source:/path/in/container, optionally :ro.";
                return mounts;
            }

            var readOnly = false;

            if (parts.Length == 3)
            {
                if (parts[2] is not ("ro" or "rw"))
                {
                    problem = $"“{line}” — the third part is ro or rw.";
                    return mounts;
                }

                readOnly = parts[2] == "ro";
            }

            var source = parts[0];
            var destination = parts[1];

            if (source.Length == 0)
            {
                problem = $"“{line}” — a mount needs a volume name or a host path.";
                return mounts;
            }

            if (!destination.StartsWith('/'))
            {
                // A relative destination is accepted by no engine and is nearly always a mount
                // written backwards.
                problem = $"“{line}” — the path inside the container must be absolute.";
                return mounts;
            }

            if (!destinations.Add(FileEntry.Normalize(destination)))
            {
                problem = $"Two mounts both land on {destination}.";
                return mounts;
            }

            var isHostPath = source.StartsWith('/') || source.StartsWith('~');

            mounts.Add(new MountPoint(
                isHostPath ? MountKind.Bind : MountKind.Volume,
                isHostPath ? ExpandHome(source) : source,
                FileEntry.Normalize(destination),
                readOnly));
        }

        return mounts;
    }

    /// <summary>
    /// <c>~</c> is the shell's, not the engine's: a bind source starting with it reaches the daemon
    /// as a literal directory named "~" and mounts an empty folder, which looks like the container
    /// ignoring the mount.
    /// </summary>
    static string ExpandHome(string path)
        => path.StartsWith('~')
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..]
            : path;

    static IEnumerable<string> Lines(string? text)
        => (text ?? string.Empty)
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'));
}
