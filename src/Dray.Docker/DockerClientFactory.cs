using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Docker.DotNet;
using Docker.DotNet.NativeHttp;
using Dray.Core.Engine;
using Dray.Docker.Transport;

namespace Dray.Docker;

/// <summary>Builds a <see cref="DockerClient"/> for one <see cref="DockerEndpoint"/>.</summary>
public static class DockerClientFactory
{
    /// <summary>
    /// A connection attempt should fail in seconds, not hang. A remote host that is off must
    /// degrade its sidebar entry quickly rather than making the app feel stuck.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public static DockerClient Create(DockerEndpoint endpoint, TimeSpan? timeout = null)
    {
        // An endpoint with no address on this machine is reached by running something that has it.
        // Handled before the URI is built, because there is no URI that describes "a process".
        if (RelayCommands.For(endpoint) is { } relay) return CreateRelayed(relay, timeout);

        var builder = new DockerClientBuilder()
            .WithEndpoint(ToUri(endpoint))
            .WithTimeout(timeout ?? DefaultTimeout);

        // Unix sockets and named pipes need no extra configuration; the builder picks the
        // transport from the URI scheme. Only TLS over TCP does.
        return endpoint is { Scheme: EndpointScheme.Tcp, Tls: { } tls }
            ? builder.WithTransportOptions(TlsOptions(tls)).Build()
            : builder.Build();
    }

    /// <summary>
    /// A client that reaches its engine through a relay process.
    /// <para>
    /// The timeout is deliberately more generous than <see cref="DefaultTimeout"/>. Starting the
    /// relay is part of the first request, and starting one means starting a WSL distribution that
    /// may be cold or completing an SSH handshake — both of which routinely take longer than the
    /// ten seconds that is right for a socket that either answers or does not.
    /// </para>
    /// </summary>
    public static DockerClient CreateRelayed(RelayCommand command, TimeSpan? timeout = null)
        => new DockerClientBuilder()
            .WithTimeout(timeout ?? RelayTimeout)
            .WithTransportOptions(StdioRelayHandlerFactory.Instance, new StdioRelayTransportOptions(command))
            .Build();

    /// <summary>
    /// How long a relayed request may take. Long enough for a cold WSL distribution to boot, which
    /// is the slowest thing that legitimately happens on this path.
    /// </summary>
    public static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(60);

    static Uri ToUri(DockerEndpoint endpoint) => endpoint.Scheme switch
    {
        EndpointScheme.Unix => new Uri($"unix://{endpoint.Path}"),

        // Contexts and the CLI write //./pipe/<name>; the client wants npipe://./pipe/<name>.
        EndpointScheme.NamedPipe => new Uri($"npipe://./pipe/{PipeName(endpoint.Path)}"),

        EndpointScheme.Tcp => new Uri($"{(endpoint.Tls is null ? "http" : "https")}://{endpoint.Host}:{endpoint.Port}"),

        // Both of these are reached through a relay process and never get this far; Create()
        // routes them before a URI is built. Reaching here means that routing was bypassed.
        EndpointScheme.Ssh or EndpointScheme.Wsl => throw new RuntimeConnectionException(
            $"A {endpoint.Scheme} endpoint is reached through a relay process and has no URI."),

        _ => throw new RuntimeConnectionException($"Unsupported endpoint: {endpoint.Raw}"),
    };

    static string PipeName(string? path)
    {
        var name = (path ?? string.Empty).Replace('\\', '/').TrimStart('/');

        if (name.StartsWith("./pipe/", StringComparison.OrdinalIgnoreCase)) return name[7..];
        if (name.StartsWith("pipe/", StringComparison.OrdinalIgnoreCase)) return name[5..];

        return name;
    }

    static NativeHttpTransportOptions TlsOptions(EndpointTls tls) => new()
    {
        ConfigureHandler = handler =>
        {
            var ssl = handler.SslOptions;

            if (tls.HasClientCertificate)
                ssl.ClientCertificates = [LoadClientCertificate(tls)];

            if (tls.SkipVerify)
            {
                // Only when the context itself set SkipTLSVerify. Dray never decides this on the
                // user's behalf — a silently unverified connection to a remote daemon would be a
                // far worse default than a visible failure.
                ssl.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }
            else if (tls.CaPath is not null)
            {
                // A private CA is the normal case for a self-hosted daemon, and it will not be in
                // the machine trust store. Validate against it explicitly rather than disabling
                // verification altogether.
                var ca = X509CertificateLoader.LoadCertificateFromFile(tls.CaPath);
                ssl.CertificateChainPolicy = new X509ChainPolicy
                {
                    RevocationMode = X509RevocationMode.NoCheck,
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    CustomTrustStore = { ca },
                };
            }
        },
    };

    static X509Certificate2 LoadClientCertificate(EndpointTls tls)
    {
        try
        {
            var certificate = X509Certificate2.CreateFromPemFile(tls.CertPath!, tls.KeyPath!);

            // Windows needs the key persisted through a PKCS#12 round-trip before the handshake
            // will use it.
            return OperatingSystem.IsWindows()
                ? X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable)
                : certificate;
        }
        catch (Exception ex)
        {
            throw new RuntimeConnectionException(
                $"Could not read the TLS certificate for this host: {ex.Message}", ex);
        }
    }
}
