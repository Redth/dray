namespace Dray.Core.Engine;

/// <summary>
/// Writing the <c>--docker</c> argument of <c>docker context create</c>, and checking what goes
/// into it.
/// <para>
/// Adding a host means creating a Docker context, not writing anything of Dray's own. A context is
/// what the whole toolchain already reads — the docker command, compose, buildx — so a host added
/// here works in the user's terminal too, and removing Dray removes nothing they depend on.
/// docs/CREDENTIALS.md holds the other half: TLS material is handed to the CLI, which copies it into
/// its own store, and an SSH key never passes through Dray at all.
/// </para>
/// <para>
/// The argument is a comma-separated list of <c>key=value</c> pairs with no escaping, so a value
/// containing a comma would silently split into a second, wrong field. Every value is checked
/// before it is written rather than quoted after.
/// </para>
/// </summary>
public static class ContextSpec
{
    /// <summary>
    /// Why this cannot be a context name, or null when it can. Docker's own rule: a letter or digit,
    /// then letters, digits, <c>_ . + -</c>.
    /// </summary>
    public static string? ValidateName(string? name)
    {
        var value = name?.Trim() ?? string.Empty;

        if (value.Length == 0) return "Give the host a name.";

        if (!char.IsAsciiLetterOrDigit(value[0]))
            return "A host's name has to start with a letter or a digit.";

        if (value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '+' or '-')))
            return "Use letters, digits, and _ . + - only — Docker names contexts the same way.";

        if (string.Equals(value, "default", StringComparison.OrdinalIgnoreCase))
            return "\"default\" is reserved by Docker for the engine it finds without a context.";

        return null;
    }

    /// <summary>
    /// A context name suggested from what the user typed for the host: <c>me@nas.local</c> becomes
    /// <c>nas</c>. Sanitised the same way <see cref="DockerCliContext"/> names its own.
    /// </summary>
    public static string SuggestName(string? host)
    {
        var value = (host ?? string.Empty).Trim();

        var at = value.LastIndexOf('@');
        if (at >= 0) value = value[(at + 1)..];

        // The machine's own name, not its domain, and not an IP split at its first dot.
        if (!System.Net.IPAddress.TryParse(value, out _))
        {
            var dot = value.IndexOf('.');
            if (dot > 0) value = value[..dot];
        }

        var cleaned = new string([.. value.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')]);
        while (cleaned.Contains("--", StringComparison.Ordinal)) cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);

        cleaned = cleaned.Trim('-');
        return cleaned.Length == 0 ? "remote" : cleaned;
    }

    /// <summary>
    /// Why this is not something ssh can be pointed at, or null. Accepts what a user types after
    /// <c>ssh</c>: <c>host</c>, <c>user@host</c>, or an alias from their ssh config.
    /// </summary>
    public static string? ValidateSshTarget(string? target)
    {
        var value = target?.Trim() ?? string.Empty;

        if (value.Length == 0) return "Enter the machine to connect to — the same thing you would type after ssh.";
        if (value.Contains("://", StringComparison.Ordinal)) return "Leave off the ssh:// — Dray adds it.";
        if (value.Any(char.IsWhiteSpace)) return "A host name has no spaces in it.";
        if (value.Contains(',') || value.Contains('=')) return "A host name has no commas or equals signs in it.";
        if (value.StartsWith('@') || value.EndsWith('@')) return "Put a user before the @ and a host after it.";
        if (value.StartsWith('-')) return "A host name cannot start with a dash.";

        return null;
    }

    /// <summary>Why this is not a usable port, or null. Empty means the default.</summary>
    public static string? ValidatePort(string? port, out int? value)
    {
        value = null;
        var text = port?.Trim() ?? string.Empty;
        if (text.Length == 0) return null;

        if (!int.TryParse(text, out var parsed) || parsed is < 1 or > 65535)
            return "A port is a number from 1 to 65535.";

        value = parsed;
        return null;
    }

    /// <summary>Why this is not a usable host for TCP, or null.</summary>
    public static string? ValidateTcpHost(string? host)
    {
        var value = host?.Trim() ?? string.Empty;

        if (value.Length == 0) return "Enter the address the daemon listens on.";
        if (value.Contains("://", StringComparison.Ordinal)) return "Leave off the tcp:// — Dray adds it.";
        if (value.Any(char.IsWhiteSpace) || value.Contains(',') || value.Contains('=') || value.Contains('/') || value.Contains('@'))
            return "Enter a host name or an IP address, without a path or a user.";

        return null;
    }

    /// <summary>
    /// Why a certificate path cannot go into a context, or null. Empty is allowed — every one of
    /// the three is optional to the CLI.
    /// </summary>
    public static string? ValidatePath(string? path)
        => path is { Length: > 0 } p && (p.Contains(',') || p.Contains('='))
            ? "This path has a comma or an equals sign in it, which the docker command cannot accept here. Move the file somewhere without one."
            : null;

    /// <summary>The endpoint an SSH context points at.</summary>
    public static string SshEndpoint(string target, int? port)
        => $"ssh://{target.Trim()}{(port is { } p ? ":" + p : string.Empty)}";

    /// <summary>The endpoint a TCP context points at. IPv6 literals are bracketed.</summary>
    public static string TcpEndpoint(string host, int port)
    {
        var value = host.Trim();
        if (value.Contains(':') && !value.StartsWith('[')) value = $"[{value}]";

        return $"tcp://{value}:{port}";
    }

    /// <summary>
    /// The <c>--docker</c> value for an endpoint, with TLS material where there is some.
    /// </summary>
    public static string Docker(string endpoint, string? ca = null, string? cert = null, string? key = null, bool skipVerify = false)
    {
        List<string> parts = [$"host={endpoint}"];

        if (ca is { Length: > 0 }) parts.Add($"ca={ca}");
        if (cert is { Length: > 0 }) parts.Add($"cert={cert}");
        if (key is { Length: > 0 }) parts.Add($"key={key}");
        if (skipVerify) parts.Add("skip-tls-verify=true");

        return string.Join(',', parts);
    }
}

/// <summary>
/// Where minikube's engine is, read from minikube itself.
/// <para>
/// <c>minikube docker-env</c> is the one authority on it: the address depends on the driver (a
/// VM's IP, or a localhost port forwarded into a node container), and the certificates live in
/// minikube's own directory. Guessing either would produce a host that looks right and never
/// connects.
/// </para>
/// </summary>
public sealed class MinikubeDockerEnv(IProcessRunner? runner = null)
{
    readonly IProcessRunner _runner = runner ?? new SystemProcessRunner();

    /// <summary>What Dray calls the context it creates for minikube.</summary>
    public const string ContextName = "dray-minikube";

    /// <summary>The <c>--docker</c> value for minikube's engine, or a sentence saying why there is none.</summary>
    public async Task<(string? Spec, string? Problem)> ReadAsync(CancellationToken ct = default)
    {
        ProcessResult result;

        try
        {
            result = await _runner
                .RunAsync("minikube", ["docker-env", "--shell", "none"], null, ct)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return (null, "Dray could not find the minikube command. Install it, then try again.");
        }

        if (result.ExitCode != 0)
        {
            var message = (result.StandardError.Trim() is { Length: > 0 } err ? err : result.StandardOutput.Trim());

            return (null, message.Length == 0
                ? "minikube could not say where its engine is. Check the cluster is running with minikube status."
                : message);
        }

        return Parse(result.StandardOutput) is { } spec
            ? (spec, null)
            : (null, "minikube answered without naming an engine. Start the cluster with --container-runtime=docker.");
    }

    /// <summary>
    /// Turns minikube's variables into a context spec. Accepts <c>KEY=value</c> lines and the
    /// <c>export KEY="value"</c> a shell-flavoured output would use.
    /// </summary>
    internal static string? Parse(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = raw.StartsWith("export ", StringComparison.Ordinal) ? raw[7..] : raw;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            values[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"');
        }

        if (!values.TryGetValue("DOCKER_HOST", out var host) || DockerEndpoint.Parse(host) is not { Scheme: EndpointScheme.Tcp })
            return null;

        if (values.TryGetValue("DOCKER_CERT_PATH", out var certs) && certs.Length > 0)
        {
            var ca = Path.Combine(certs, "ca.pem");
            var cert = Path.Combine(certs, "cert.pem");
            var key = Path.Combine(certs, "key.pem");

            if (ContextSpec.ValidatePath(certs) is not null) return null;

            return ContextSpec.Docker(host, ca, cert, key);
        }

        return ContextSpec.Docker(host);
    }
}
