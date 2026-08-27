namespace Dray.Core.Engine;

/// <summary>How Dray reaches an engine.</summary>
public enum EndpointScheme
{
    /// <summary>Unix domain socket — macOS and Linux.</summary>
    Unix,

    /// <summary>Windows named pipe, e.g. <c>//./pipe/dockerDesktopLinuxEngine</c>.</summary>
    NamedPipe,

    /// <summary>TCP, optionally with TLS material from the context.</summary>
    Tcp,

    /// <summary>SSH to a remote host's socket.</summary>
    Ssh,
}

/// <summary>TLS material for a <see cref="EndpointScheme.Tcp"/> endpoint.</summary>
public sealed record EndpointTls(string? CaPath, string? CertPath, string? KeyPath, bool SkipVerify)
{
    public bool HasClientCertificate => CertPath is not null && KeyPath is not null;
}

/// <summary>
/// A parsed Docker endpoint.
/// <para>
/// Dray treats every scheme the same from here up: a remote host over SSH is not a special mode,
/// it is another endpoint (PRODUCT.md positioning).
/// </para>
/// </summary>
public sealed record DockerEndpoint
{
    public required EndpointScheme Scheme { get; init; }

    /// <summary>The original string, exactly as the context or environment gave it.</summary>
    public required string Raw { get; init; }

    /// <summary>Socket or pipe path, for <see cref="EndpointScheme.Unix"/> and <see cref="EndpointScheme.NamedPipe"/>.</summary>
    public string? Path { get; init; }

    /// <summary>Host for <see cref="EndpointScheme.Tcp"/> and <see cref="EndpointScheme.Ssh"/>.</summary>
    public string? Host { get; init; }

    public int? Port { get; init; }

    /// <summary>SSH user, when the endpoint carried one.</summary>
    public string? User { get; init; }

    public EndpointTls? Tls { get; init; }

    /// <summary>True when reaching this endpoint leaves the machine.</summary>
    public bool IsRemote => Scheme is EndpointScheme.Ssh or EndpointScheme.Tcp;

    /// <summary>
    /// A short label for the host picker: "docker.sock", "ssh://nas", "tcp://10.0.0.4:2376".
    /// </summary>
    public string Display => Scheme switch
    {
        // Every engine's socket is called docker.sock, so the filename alone identifies nothing.
        // The home-relative path is what tells OrbStack from Docker Desktop from Colima.
        EndpointScheme.Unix => ShortenHome(Path) ?? Raw,
        EndpointScheme.NamedPipe =>
            System.IO.Path.GetFileName(Path?.TrimEnd('/')) is { Length: > 0 } name ? name : Raw,
        EndpointScheme.Ssh => $"ssh://{(User is null ? "" : User + "@")}{Host}{(Port is null ? "" : ":" + Port)}",
        EndpointScheme.Tcp => $"tcp://{Host}{(Port is null ? "" : ":" + Port)}",
        _ => Raw,
    };

    /// <summary>
    /// Parse a Docker host string. Returns null for anything unrecognised rather than throwing —
    /// a malformed context should degrade that one host, not take down discovery.
    /// </summary>
    public static DockerEndpoint? Parse(string? raw, EndpointTls? tls = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        raw = raw.Trim();

        // Bare paths and the Windows pipe shorthand both appear in the wild.
        if (raw.StartsWith("//./pipe/", StringComparison.Ordinal) || raw.StartsWith(@"\\.\pipe\", StringComparison.Ordinal))
            return new() { Scheme = EndpointScheme.NamedPipe, Raw = raw, Path = raw };

        if (raw.StartsWith('/'))
            return new() { Scheme = EndpointScheme.Unix, Raw = raw, Path = raw };

        var split = raw.IndexOf("://", StringComparison.Ordinal);
        if (split < 0) return null;

        var scheme = raw[..split].ToLowerInvariant();
        var rest = raw[(split + 3)..];

        switch (scheme)
        {
            case "unix":
                return rest.Length == 0 ? null : new() { Scheme = EndpointScheme.Unix, Raw = raw, Path = rest };

            case "npipe":
                return rest.Length == 0 ? null : new() { Scheme = EndpointScheme.NamedPipe, Raw = raw, Path = "//./pipe/" + rest.TrimStart('/').Replace("./pipe/", "") };

            case "tcp":
            case "http":
            case "https":
            {
                var (host, port) = SplitHostPort(rest);
                if (host is null) return null;

                // 2376 is the TLS port by convention; 2375 is plaintext.
                var effectiveTls = tls ?? (scheme == "https" ? new EndpointTls(null, null, null, false) : null);
                return new() { Scheme = EndpointScheme.Tcp, Raw = raw, Host = host, Port = port ?? (effectiveTls is null ? 2375 : 2376), Tls = effectiveTls };
            }

            case "ssh":
            {
                var user = (string?)null;
                var at = rest.IndexOf('@');
                if (at > 0)
                {
                    user = rest[..at];
                    rest = rest[(at + 1)..];
                }

                var (host, port) = SplitHostPort(rest);
                if (host is null) return null;

                return new() { Scheme = EndpointScheme.Ssh, Raw = raw, Host = host, Port = port, User = user };
            }

            default:
                return null;
        }
    }

    /// <summary>Replaces the user's home directory with <c>~</c>, the way a shell would show it.</summary>
    static string? ShortenHome(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.Ordinal)
            ? "~" + path[home.Length..]
            : path;
    }

    static (string? Host, int? Port) SplitHostPort(string value)
    {
        // Strip any path portion; Docker host strings occasionally carry a trailing slash.
        var slash = value.IndexOf('/');
        if (slash >= 0) value = value[..slash];
        if (value.Length == 0) return (null, null);

        // Bracketed IPv6: [::1]:2375
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0) return (null, null);

            var v6 = value[1..close];
            var tail = value[(close + 1)..];
            return tail.StartsWith(':') && int.TryParse(tail[1..], out var v6Port)
                ? (v6, v6Port)
                : (v6, null);
        }

        var colon = value.LastIndexOf(':');
        if (colon < 0) return (value, null);

        // An unbracketed IPv6 literal has several colons and no port.
        if (value.IndexOf(':') != colon) return (value, null);

        return int.TryParse(value[(colon + 1)..], out var port)
            ? (value[..colon], port)
            : (value[..colon], null);
    }
}
