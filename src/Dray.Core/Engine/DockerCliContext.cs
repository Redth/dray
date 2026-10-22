namespace Dray.Core.Engine;

/// <summary>
/// Pointing the user's own <c>docker</c> command at the host Dray is showing.
/// <para>
/// This is the only "switch" Dray offers on Windows, and it is deliberately not a mode. Both of
/// Docker Desktop's engines run at once on their own pipes, so Dray shows them as two hosts and
/// needs no mode of its own (docs/ARCHITECTURE.md section 2.4). What a user does still need is for
/// their terminal to agree with what is on screen, and that is a Docker context.
/// </para>
/// <para>
/// A context rather than <c>DOCKER_HOST</c>, because a context is what the whole toolchain reads.
/// <c>docker compose</c> is a CLI plugin that resolves its endpoint the same way <c>docker</c>
/// does, so setting the context covers compose, buildx and anything else that speaks to the engine
/// — where exporting a variable would only cover the shell it was exported in.
/// </para>
/// </summary>
public sealed class DockerCliContext(IProcessRunner? runner = null)
{
    readonly IProcessRunner _runner = runner ?? new SystemProcessRunner();

    /// <summary>
    /// What Dray names the contexts it creates. Prefixed so it is obvious in <c>docker context ls</c>
    /// which entries are Dray's, and so Dray never adopts or overwrites one the user made.
    /// </summary>
    public const string ContextPrefix = "dray-";

    /// <summary>
    /// Whether the CLI can be pointed at this host at all.
    /// <para>
    /// A context has to name an endpoint the Docker CLI can dial, which rules out exactly the two
    /// schemes Dray reaches through a relay process. A WSL distribution's engine has no address on
    /// the Windows side — that is the whole reason Dray relays into it — so there is nothing to put
    /// in a context. Inside the distribution the <c>docker</c> command already works, which is what
    /// the UI says instead of offering a button that cannot do anything.
    /// </para>
    /// <para>
    /// SSH is different: the CLI has its own <c>ssh://</c> support, so a context pointing at one
    /// works even though Dray reaches it another way.
    /// </para>
    /// </summary>
    public static bool CanTarget(DockerEndpoint endpoint) => endpoint.Scheme
        is EndpointScheme.Unix
        or EndpointScheme.NamedPipe
        or EndpointScheme.Tcp
        or EndpointScheme.Ssh;

    /// <summary>Which context the CLI would use right now, or null when it could not be asked.</summary>
    public async Task<string?> CurrentAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _runner
                .RunAsync("docker", ["context", "show"], null, ct)
                .ConfigureAwait(false);

            if (result.ExitCode != 0) return null;

            var name = result.StandardOutput.Trim();
            return name.Length == 0 ? null : name;
        }
        catch (Exception)
        {
            // No docker CLI. Not an error — plenty of people drive Docker only through a GUI.
            return null;
        }
    }

    /// <summary>
    /// Point the CLI at this host, creating a context for it if there is not one already.
    /// <para>
    /// An existing context whose endpoint matches is reused rather than duplicated: a user who has
    /// already named this engine should keep their own name for it, and the picker should not
    /// accumulate a Dray-prefixed twin of every context on the machine.
    /// </para>
    /// </summary>
    /// <returns>Null on success, or a sentence explaining the refusal.</returns>
    public async Task<string?> UseAsync(DockerHost host, CancellationToken ct = default)
    {
        if (!CanTarget(host.Endpoint))
        {
            return $"The docker command has no way to address {host.Endpoint.Display}. "
                + "Run docker inside the distribution instead, where its socket already is.";
        }

        try
        {
            var existing = await FindContextForAsync(host.Endpoint, ct).ConfigureAwait(false);

            if (existing is null)
            {
                var created = await CreateAsync(host, ct).ConfigureAwait(false);
                if (created is not null) return created;

                existing = NameFor(host);
            }

            var result = await _runner
                .RunAsync("docker", ["context", "use", existing], null, ct)
                .ConfigureAwait(false);

            return result.ExitCode == 0 ? null : Describe(result);
        }
        catch (Exception ex)
        {
            return $"Could not reach the docker command: {ex.Message}";
        }
    }

    /// <summary>A context already pointing at this endpoint, whoever created it.</summary>
    async Task<string?> FindContextForAsync(DockerEndpoint endpoint, CancellationToken ct)
    {
        var result = await _runner
            .RunAsync("docker", ["context", "ls", "--format", "{{.Name}}\t{{.DockerEndpoint}}"], null, ct)
            .ConfigureAwait(false);

        if (result.ExitCode != 0) return null;

        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length < 2) continue;

            if (SameEndpoint(parts[1].Trim(), endpoint)) return parts[0].Trim();
        }

        return null;
    }

    async Task<string?> CreateAsync(DockerHost host, CancellationToken ct)
    {
        var result = await _runner
            .RunAsync(
                "docker",
                [
                    "context", "create", NameFor(host),
                    "--description", $"Created by Dray for {host.Name}",
                    "--docker", $"host={CliEndpoint(host.Endpoint)}",
                ],
                null,
                ct)
            .ConfigureAwait(false);

        return result.ExitCode == 0 ? null : Describe(result);
    }

    /// <summary>
    /// The context name for a host. Sanitised because Docker restricts what a context may be
    /// called, and a Docker Desktop host is literally named "Docker Desktop (Linux containers)".
    /// </summary>
    internal static string NameFor(DockerHost host)
    {
        var cleaned = new string([.. host.Name
            .Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')]);

        while (cleaned.Contains("--", StringComparison.Ordinal))
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);

        cleaned = cleaned.Trim('-');

        return ContextPrefix + (cleaned.Length == 0 ? "host" : cleaned);
    }

    /// <summary>
    /// How the Docker CLI wants this endpoint written, which is not always how Dray holds it.
    /// <para>
    /// Contexts and the CLI write a pipe as <c>npipe:////./pipe/name</c>, while discovery reads and
    /// stores the bare <c>//./pipe/name</c> that also appears in the wild.
    /// </para>
    /// </summary>
    internal static string CliEndpoint(DockerEndpoint endpoint) => endpoint.Scheme switch
    {
        EndpointScheme.NamedPipe => "npipe://" + (endpoint.Path ?? string.Empty).Replace('\\', '/').TrimStart('/'),
        EndpointScheme.Unix => "unix://" + endpoint.Path,
        _ => endpoint.Raw,
    };

    /// <summary>
    /// Whether a context's endpoint string names the same engine as this one.
    /// <para>
    /// Compared after normalising, because the same pipe is spelled several ways —
    /// <c>npipe:////./pipe/x</c>, <c>npipe://./pipe/x</c> and <c>//./pipe/x</c> are one engine, and
    /// treating them as three would make Dray create a duplicate context every time.
    /// </para>
    /// </summary>
    internal static bool SameEndpoint(string contextEndpoint, DockerEndpoint endpoint)
        => string.Equals(Canonical(contextEndpoint), Canonical(CliEndpoint(endpoint)), StringComparison.OrdinalIgnoreCase);

    static string Canonical(string raw)
    {
        var value = raw.Trim().Replace('\\', '/');

        var split = value.IndexOf("://", StringComparison.Ordinal);
        var scheme = split < 0 ? string.Empty : value[..split].ToLowerInvariant();
        var rest = split < 0 ? value : value[(split + 3)..];

        if (scheme is "npipe" or "")
        {
            rest = rest.TrimStart('/');
            if (rest.StartsWith("./pipe/", StringComparison.OrdinalIgnoreCase)) rest = rest[7..];
            else if (rest.StartsWith("pipe/", StringComparison.OrdinalIgnoreCase)) rest = rest[5..];

            return "npipe:" + rest.ToLowerInvariant();
        }

        return scheme + ":" + rest.TrimEnd('/');
    }

    /// <summary>The CLI's own words for the failure, or a fallback when it gave none.</summary>
    static string Describe(ProcessResult result)
    {
        var message = result.StandardError.Trim();
        if (message.Length == 0) message = result.StandardOutput.Trim();

        return message.Length == 0
            ? "The docker command failed without saying why."
            : message;
    }
}
