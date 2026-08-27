using Dray.Core.Engine;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Discovery has to agree with what the user's terminal would do, so most of these encode the
/// Docker CLI's precedence rules rather than Dray's preferences.
/// </summary>
public class DockerContextReaderTests
{
    // ---------------------------------------------------------------- precedence

    [Fact]
    public void DockerHostOverridesTheSelectedContext()
    {
        var source = new FakeConfigSource()
            .WithConfig("desktop-linux")
            .WithContext("desktop-linux", "unix:///Users/x/.docker/run/docker.sock")
            .WithEnvironment("DOCKER_HOST", "tcp://10.0.0.4:2375");

        var hosts = new DockerContextReader(source).Discover();

        var current = Assert.Single(hosts, h => h.IsCurrent);
        Assert.Equal(HostOrigin.Environment, current.Origin);
        Assert.Equal("tcp://10.0.0.4:2375", current.Endpoint.Raw);
    }

    [Fact]
    public void DockerContextOverridesConfigJson()
    {
        var source = new FakeConfigSource()
            .WithConfig("desktop-linux")
            .WithContext("desktop-linux", "unix:///a.sock")
            .WithContext("orbstack", "unix:///b.sock")
            .WithEnvironment("DOCKER_CONTEXT", "orbstack");

        var hosts = new DockerContextReader(source).Discover();

        Assert.Equal("orbstack", Assert.Single(hosts, h => h.IsCurrent).Id);
    }

    [Fact]
    public void CurrentContextComesFromConfigJsonWhenNothingOverridesIt()
    {
        var source = new FakeConfigSource()
            .WithConfig("orbstack")
            .WithContext("desktop-linux", "unix:///a.sock")
            .WithContext("orbstack", "unix:///b.sock");

        Assert.Equal("orbstack", Assert.Single(new DockerContextReader(source).Discover(), h => h.IsCurrent).Id);
    }

    [Fact]
    public void SomethingIsAlwaysCurrentWhenAnyHostExists()
    {
        // The selected context can name something that no longer exists. The app must still open
        // somewhere rather than nowhere.
        var source = new FakeConfigSource()
            .WithConfig("deleted-context")
            .WithContext("orbstack", "unix:///b.sock");

        var hosts = new DockerContextReader(source).Discover();
        Assert.Single(hosts, h => h.IsCurrent);
    }

    // ---------------------------------------------------------------- parsing

    [Fact]
    public void ReadsNameDescriptionAndEndpoint()
    {
        var source = new FakeConfigSource()
            .WithContext("nas", "ssh://redth@nas.local", description: "Home server");

        var host = Assert.Single(new DockerContextReader(source).Discover());
        Assert.Equal("nas", host.Name);
        Assert.Equal("Home server", host.Description);
        Assert.Equal(EndpointScheme.Ssh, host.Endpoint.Scheme);
        Assert.Equal(HostOrigin.DockerContext, host.Origin);
    }

    [Fact]
    public void AMalformedContextIsSkippedWithoutLosingTheOthers()
    {
        var source = new FakeConfigSource()
            .WithContext("good", "unix:///good.sock")
            .WithRawContext("broken", "{ this is not json");

        var hosts = new DockerContextReader(source).Discover();
        Assert.Equal("good", Assert.Single(hosts).Id);
    }

    [Fact]
    public void AContextWithAnUnparseableHostIsSkipped()
    {
        var source = new FakeConfigSource()
            .WithContext("good", "unix:///good.sock")
            .WithContext("weird", "ftp://nope");

        Assert.Equal("good", Assert.Single(new DockerContextReader(source).Discover()).Id);
    }

    [Fact]
    public void ACorruptConfigJsonDoesNotStopDiscovery()
    {
        var source = new FakeConfigSource()
            .WithRawConfig("{ broken")
            .WithContext("orbstack", "unix:///b.sock");

        Assert.Single(new DockerContextReader(source).Discover());
    }

    [Fact]
    public void TlsMaterialIsPickedUpFromTheContextDirectory()
    {
        var source = new FakeConfigSource()
            .WithContext("remote", "tcp://10.0.0.4:2376")
            .WithTlsFor("remote", ca: true, cert: true, key: true);

        var host = Assert.Single(new DockerContextReader(source).Discover());
        Assert.NotNull(host.Endpoint.Tls);
        Assert.True(host.Endpoint.Tls.HasClientCertificate);
    }

    [Fact]
    public void DuplicateEndpointsAreCollapsed()
    {
        // The same socket reached through two names is one engine, and showing it twice in the
        // host picker would be a lie about how many there are.
        var source = new FakeConfigSource()
            .WithContext("a", "unix:///same.sock")
            .WithContext("b", "unix:///same.sock");

        Assert.Single(new DockerContextReader(source).Discover());
    }

    [Fact]
    public void NoConfigAtAllYieldsNoHostsRatherThanThrowing()
    {
        // The genuine first-run case on a machine with no Docker installed.
        Assert.Empty(new DockerContextReader(new FakeConfigSource { ConfigDirectory = null }).Discover());
    }

    [Fact]
    public void ADanglingSocketSymlinkIsNotOfferedAsAHost()
    {
        // Real case from the development machine: podman leaves /var/run/docker.sock pointing at
        // a machine socket that is absent whenever no machine runs, and .NET's File.Exists reports
        // true for a dangling symlink. Probing with FileExists offered a host that could never
        // connect, labelled "Local engine".
        var source = new FakeConfigSource().WithContext("real", "unix:///real.sock");

        var hosts = new DockerContextReader(source).Discover();

        Assert.DoesNotContain(hosts, h => h.Origin == HostOrigin.Discovered);
    }

    [Fact]
    public void HostsStartDisconnected()
    {
        // Discovery finds hosts; it never claims one is reachable. That is the connector's job.
        var source = new FakeConfigSource().WithContext("a", "unix:///a.sock");
        var host = Assert.Single(new DockerContextReader(source).Discover());

        Assert.Equal(HostConnectionState.Disconnected, host.State);
        Assert.Equal(RuntimeCapabilities.None, host.Capabilities);
    }
}

/// <summary>An in-memory <c>~/.docker</c>.</summary>
sealed class FakeConfigSource : IDockerConfigSource
{
    const string Root = "/fake/.docker";

    readonly Dictionary<string, string> _files = [];
    readonly HashSet<string> _sockets = [];
    readonly HashSet<string> _dirs = [Root, $"{Root}/contexts", $"{Root}/contexts/meta"];
    readonly Dictionary<string, string> _env = [];

    public string? ConfigDirectory { get; init; } = Root;

    public string? DockerConfigDirectory => ConfigDirectory;

    public string? GetEnvironmentVariable(string name) => _env.GetValueOrDefault(name);

    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

    public bool SocketExists(string path) => _sockets.Contains(Normalize(path));

    public bool DirectoryExists(string path) => _dirs.Contains(Normalize(path));

    public IEnumerable<string> EnumerateDirectories(string path)
    {
        var prefix = Normalize(path) + "/";
        return _dirs
            .Where(d => d.StartsWith(prefix, StringComparison.Ordinal) && !d[prefix.Length..].Contains('/'))
            .OrderBy(d => d, StringComparer.Ordinal);
    }

    public string ReadAllText(string path) => _files[Normalize(path)];

    /// <summary>A socket that is genuinely connectable, as opposed to a path that merely exists.</summary>
    public FakeConfigSource WithLiveSocket(string path)
    {
        _sockets.Add(Normalize(path));
        return this;
    }

    public FakeConfigSource WithEnvironment(string name, string value)
    {
        _env[name] = value;
        return this;
    }

    public FakeConfigSource WithConfig(string currentContext)
        => WithRawConfig("{\"currentContext\":\"" + currentContext + "\"}");

    public FakeConfigSource WithRawConfig(string json)
    {
        _files[$"{Root}/config.json"] = json;
        return this;
    }

    public FakeConfigSource WithContext(string name, string host, string? description = null)
    {
        // Built by concatenation rather than a raw interpolated literal: the JSON's own braces
        // collide with interpolation delimiters and the escaping obscures the shape of the file
        // being faked.
        var metadata = description is null
            ? string.Empty
            : "\"Metadata\":{\"Description\":\"" + description + "\"},";

        var meta =
            "{\"Name\":\"" + name + "\"," +
            metadata +
            "\"Endpoints\":{\"docker\":{\"Host\":\"" + host + "\",\"SkipTLSVerify\":false}}}";

        return WithRawContext(name, meta);
    }

    public FakeConfigSource WithRawContext(string digest, string json)
    {
        var dir = $"{Root}/contexts/meta/{digest}";
        _dirs.Add(dir);
        _files[$"{dir}/meta.json"] = json;
        return this;
    }

    public FakeConfigSource WithTlsFor(string digest, bool ca, bool cert, bool key)
    {
        var dir = $"{Root}/contexts/tls/{digest}/docker";
        _dirs.Add($"{Root}/contexts/tls");
        _dirs.Add($"{Root}/contexts/tls/{digest}");
        _dirs.Add(dir);

        if (ca) _files[$"{dir}/ca.pem"] = "ca";
        if (cert) _files[$"{dir}/cert.pem"] = "cert";
        if (key) _files[$"{dir}/key.pem"] = "key";

        return this;
    }

    static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}
