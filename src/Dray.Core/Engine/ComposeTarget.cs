namespace Dray.Core.Engine;

/// <summary>
/// How one compose process is pointed at one host.
/// <para>
/// Compose runs on this machine and talks to whichever engine it is told to. Left untold, it asks
/// the CLI's current context — which is whatever the user last switched their terminal to, not the
/// host on screen. A stack on the NAS deployed to the laptop because the terminal said so is the
/// bug this type exists to make impossible.
/// </para>
/// <para>
/// <b>On the command line wherever the program allows it.</b> The Docker plugin takes the CLI's
/// global flags, so the host is <c>--context</c> or <c>-H</c> there. The environment is the
/// fallback for the forms that read no such flag, and only those: a <c>DOCKER_HOST</c> in compose's
/// environment is visible to compose's own interpolation, so a file that says
/// <c>${DOCKER_HOST}</c> — a socket proxy, a CI runner — would silently pick up Dray's value.
/// </para>
/// </summary>
/// <param name="GlobalArguments">Arguments before the compose subcommand, e.g. <c>--context nas</c>.</param>
/// <param name="Environment">Variables the process needs to find the host. Empty for the plugin.</param>
public sealed record ComposeTarget(
    IReadOnlyList<string> GlobalArguments,
    IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>
    /// How to address <paramref name="host"/> with <paramref name="command"/>, or null when that
    /// compose cannot reach it at all.
    /// <para>
    /// Null is an answer, not a failure: a WSL distribution's socket has no address on this side,
    /// and Apple's engine speaks no Docker API. The caller turns it into
    /// <see cref="RuntimeCapabilities.SupportsCompose"/> being false, so the Stacks page explains
    /// itself instead of offering an Up that cannot work.
    /// </para>
    /// </summary>
    public static ComposeTarget? For(DockerHost host, ComposeCommand command)
    {
        if (!DockerCliContext.CanTarget(host.Endpoint)) return null;

        // A context already carries its endpoint and TLS material, so naming it is both shorter
        // and the only way to reuse the user's SSH options exactly as they set them up.
        var isContext = host.Origin == HostOrigin.DockerContext;

        return command.TakesDockerFlags
            ? new ComposeTarget(isContext ? ["--context", host.Id] : HostFlags(host.Endpoint), Empty)
            : isContext
                ? new ComposeTarget([], new Dictionary<string, string> { ["DOCKER_CONTEXT"] = host.Id })
                : HostVariables(host.Endpoint) is { } variables ? new ComposeTarget([], variables) : null;
    }

    static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    static List<string> HostFlags(DockerEndpoint endpoint)
    {
        List<string> flags = ["-H", DockerCliContext.CliEndpoint(endpoint)];

        if (endpoint.Tls is not { } tls) return flags;

        // --tls encrypts without checking who answered; --tlsverify checks. Asking for the second
        // when the context said to skip verification would refuse a host the user already trusts.
        flags.Add(tls.SkipVerify ? "--tls" : "--tlsverify");

        if (tls.CaPath is { } ca) flags.AddRange(["--tlscacert", ca]);
        if (tls.CertPath is { } cert) flags.AddRange(["--tlscert", cert]);
        if (tls.KeyPath is { } key) flags.AddRange(["--tlskey", key]);

        return flags;
    }

    /// <summary>
    /// The variables the standalone binary reads, or null when they cannot express this endpoint.
    /// <para>
    /// <c>DOCKER_CERT_PATH</c> names a directory, not files: it means <c>ca.pem</c>,
    /// <c>cert.pem</c> and <c>key.pem</c> in that directory, under exactly those names. TLS
    /// material anywhere else has no way to be said, and a host that cannot be addressed honestly
    /// is better reported as unreachable than reached without its certificate.
    /// </para>
    /// </summary>
    static Dictionary<string, string>? HostVariables(DockerEndpoint endpoint)
    {
        var variables = new Dictionary<string, string> { ["DOCKER_HOST"] = DockerCliContext.CliEndpoint(endpoint) };

        if (endpoint.Tls is not { } tls) return variables;

        if (!tls.SkipVerify) variables["DOCKER_TLS_VERIFY"] = "1";

        string?[] files = [tls.CaPath, tls.CertPath, tls.KeyPath];
        string[] expected = ["ca.pem", "cert.pem", "key.pem"];

        string? directory = null;

        for (var i = 0; i < files.Length; i++)
        {
            if (files[i] is not { } file) continue;

            if (!string.Equals(Path.GetFileName(file), expected[i], StringComparison.Ordinal)) return null;

            var parent = Path.GetDirectoryName(file);
            if (directory is not null && !string.Equals(directory, parent, StringComparison.Ordinal)) return null;

            directory = parent;
        }

        if (directory is not null) variables["DOCKER_CERT_PATH"] = directory;

        return variables;
    }
}

/// <summary>
/// The environment a compose process is given: the few variables a program needs to run on this
/// machine, and nothing of the user's shell.
/// <para>
/// Compose reads its own process environment ahead of the stack's <c>.env</c>, so a variable that
/// happens to be exported where Dray was launched silently overrides the value the user can see in
/// the editor. <c>COMPOSE_FILE</c> or <c>COMPOSE_PROJECT_NAME</c> left over in a shell would point
/// the command at a different stack altogether. Passing a short list through rather than filtering
/// a long one is the only version of this that stays correct when someone exports something new.
/// </para>
/// </summary>
public static class ComposeEnvironment
{
    /// <summary>
    /// What a program needs to find itself, its user, its temp directory and its SSH agent.
    /// <para>
    /// <c>SSH_AUTH_SOCK</c> is load-bearing for an <c>ssh://</c> context: without it the CLI's
    /// <c>ssh</c> cannot reach the agent holding the key. The proxy variables are there because a
    /// build inside <c>up</c> fetches through them. The Windows block is what any Win32 process
    /// expects to inherit; Go's runtime fails in odd ways without <c>SystemRoot</c>.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> PassedThrough =
    [
        "PATH", "HOME", "USER", "LOGNAME", "TMPDIR", "LANG", "LC_ALL", "LC_CTYPE",
        "SSH_AUTH_SOCK", "XDG_RUNTIME_DIR", "XDG_CONFIG_HOME", "DOCKER_CONFIG",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy",
        "SystemRoot", "SystemDrive", "windir", "ComSpec", "PATHEXT", "TEMP", "TMP",
        "USERPROFILE", "USERNAME", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA",
        "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
    ];

    /// <summary>
    /// The pass-through variables present in <paramref name="current"/>, then the target's own.
    /// </summary>
    public static IReadOnlyDictionary<string, string> For(
        IReadOnlyDictionary<string, string> current, ComposeTarget target)
    {
        // Windows variable names are case-insensitive and the process environment there arrives
        // as "Path", not "PATH". Matching exactly would hand compose no PATH and no docker.
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var wanted = new HashSet<string>(PassedThrough, comparer);

        var environment = new Dictionary<string, string>(comparer);

        foreach (var (key, value) in current)
        {
            if (wanted.Contains(key)) environment[key] = value;
        }

        foreach (var (key, value) in target.Environment) environment[key] = value;

        return environment;
    }

    /// <summary>This process's environment, as a dictionary.</summary>
    public static IReadOnlyDictionary<string, string> Current()
    {
        var environment = new Dictionary<string, string>();

        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value) environment[key] = value;
        }

        return environment;
    }
}
