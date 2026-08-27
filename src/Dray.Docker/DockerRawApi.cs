using System.IO.Pipes;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Dray.Core.Engine;

namespace Dray.Docker;

/// <summary>
/// A direct line to the Engine API, alongside the typed client.
/// <para>
/// Two things need it. The Inspect tab shows the engine's own JSON, and a typed client cannot
/// produce that — it models the fields it knows and drops the rest, so re-serialising its output
/// would quietly present Dray's vocabulary as the engine's. Podman and older daemons both return
/// fields Docker.DotNet has never heard of, and those are exactly the ones worth seeing when
/// something is behaving strangely. Second, <c>system df</c> has no typed binding at all.
/// </para>
/// <para>
/// This deliberately does not reimplement the client. It handles GET, returns text, and leaves
/// every modelled call to the typed API.
/// </para>
/// <para>
/// <b>Every path must carry the API version.</b> Podman serves two different APIs on the same
/// socket: a versioned path gets the Docker-compatible response, an unversioned one gets podman's
/// own. They are not the same shape — <c>/v1.44/system/df</c> returns <c>{"Images": [...]}</c>
/// while <c>/system/df</c> returns <c>{"ImageUsage": {"Items": [...]}}</c> — so an unversioned
/// request parses to a valid-looking object full of zeroes rather than failing. The version is
/// whatever the engine negotiated at connect time, which is why this is constructed after the
/// probe rather than alongside the client.
/// </para>
/// </summary>
public sealed class DockerRawApi : IDisposable
{
    readonly HttpClient _http;
    readonly string _prefix;

    /// <param name="apiVersion">
    /// The version the engine reported, e.g. "1.44". Null falls back to the oldest version Dray
    /// supports, which still beats sending no version at all.
    /// </param>
    public DockerRawApi(DockerEndpoint endpoint, string? apiVersion = null, TimeSpan? timeout = null)
    {
        _prefix = "/v" + (string.IsNullOrWhiteSpace(apiVersion) ? FallbackApiVersion : apiVersion.TrimStart('v'));

        var handler = new SocketsHttpHandler
        {
            // The engine is a local socket or a host on the LAN; connection reuse across a long
            // idle period is not worth a stale-connection failure.
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        };

        Uri baseAddress;

        switch (endpoint.Scheme)
        {
            case EndpointScheme.Unix:
                // The host in the URI is ignored — ConnectCallback decides where the bytes go —
                // but it still has to be a syntactically valid one.
                baseAddress = new Uri("http://localhost");
                handler.ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.Path!), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                };
                break;

            case EndpointScheme.NamedPipe:
                baseAddress = new Uri("http://localhost");
                handler.ConnectCallback = async (_, ct) =>
                {
                    var pipe = new NamedPipeClientStream(
                        ".", PipeName(endpoint.Path), PipeDirection.InOut, PipeOptions.Asynchronous);

                    await pipe.ConnectAsync(ct).ConfigureAwait(false);
                    return pipe;
                };
                break;

            case EndpointScheme.Tcp:
                baseAddress = new Uri($"{(endpoint.Tls is null ? "http" : "https")}://{endpoint.Host}:{endpoint.Port}");
                if (endpoint.Tls is { } tls) ApplyTls(handler, tls);
                break;

            default:
                throw new RuntimeConnectionException(
                    $"The raw API cannot reach a {endpoint.Scheme} endpoint. SSH endpoints are tunnelled to a local socket first.");
        }

        _http = new HttpClient(handler)
        {
            BaseAddress = baseAddress,
            Timeout = timeout ?? DockerClientFactory.DefaultTimeout,
        };
    }

    static string PipeName(string? path)
    {
        var name = (path ?? string.Empty).Replace('\\', '/').TrimStart('/');

        if (name.StartsWith("./pipe/", StringComparison.OrdinalIgnoreCase)) return name[7..];
        if (name.StartsWith("pipe/", StringComparison.OrdinalIgnoreCase)) return name[5..];

        return name;
    }

    static void ApplyTls(SocketsHttpHandler handler, EndpointTls tls)
    {
        var ssl = handler.SslOptions;

        if (tls.HasClientCertificate)
        {
            var certificate = X509Certificate2.CreateFromPemFile(tls.CertPath!, tls.KeyPath!);

            ssl.ClientCertificates =
            [
                OperatingSystem.IsWindows()
                    ? X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable)
                    : certificate,
            ];
        }

        // Mirrors DockerClientFactory exactly: verification is only skipped when the context asked
        // for it, never as a convenience.
        if (tls.SkipVerify)
        {
            ssl.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
        else if (tls.CaPath is not null)
        {
            ssl.CertificateChainPolicy = new X509ChainPolicy
            {
                RevocationMode = X509RevocationMode.NoCheck,
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                CustomTrustStore = { X509CertificateLoader.LoadCertificateFromFile(tls.CaPath) },
            };
        }
    }

    /// <summary>
    /// The oldest API version Dray targets. Used only when the engine did not report one.
    /// </summary>
    const string FallbackApiVersion = "1.41";

    /// <summary>GET a path and return the body, reformatted as indented JSON.</summary>
    public async Task<string> GetJsonAsync(string path, CancellationToken ct = default)
    {
        var raw = await GetStringAsync(path, ct).ConfigureAwait(false);

        // The engine sends this minified. Reading it is the entire point of the Inspect tab, so it
        // is indented here rather than in the browser, where a 200 KB reformat would be on the
        // render thread.
        try
        {
            using var document = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(document.RootElement, IndentedJson);
        }
        catch (JsonException)
        {
            // An engine that returned something unparseable is still worth showing verbatim.
            return raw;
        }
    }

    public async Task<string> GetStringAsync(string path, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(_prefix + path, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>GET a path and deserialise it.</summary>
    public async Task<T?> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(_prefix + path, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>(CaseInsensitiveJson, ct).ConfigureAwait(false);
    }

    static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    static readonly JsonSerializerOptions CaseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };

    public void Dispose() => _http.Dispose();
}
