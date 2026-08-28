using System.Text.Json.Serialization;

namespace Dray.Apple;

/// <summary>
/// The JSON Apple's <c>container</c> CLI emits.
/// <para>
/// Nothing here resembles the Docker API. There is no <c>/containers/json</c>, no shared field
/// names, and no compatibility layer — this engine is not Docker-shaped and does not pretend to be.
/// These shapes were captured from <c>container ls --format json</c> and friends on version 1.3.0
/// rather than read from a specification, because there is not much of one.
/// </para>
/// </summary>
internal sealed class AppleContainer
{
    /// <summary>
    /// The container's identity — and its <b>name</b>. Apple has no separate 64-hex id: what you
    /// call a container is what it is called, which is why Dray's short-id column is empty here.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("configuration")]
    public AppleConfiguration? Configuration { get; set; }

    [JsonPropertyName("status")]
    public AppleStatus? Status { get; set; }
}

internal sealed class AppleConfiguration
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("creationDate")]
    public DateTimeOffset? CreationDate { get; set; }

    [JsonPropertyName("image")]
    public AppleImageRef? Image { get; set; }

    [JsonPropertyName("initProcess")]
    public AppleInitProcess? InitProcess { get; set; }

    [JsonPropertyName("labels")]
    public Dictionary<string, string>? Labels { get; set; }

    [JsonPropertyName("publishedPorts")]
    public List<ApplePublishedPort>? PublishedPorts { get; set; }

    [JsonPropertyName("mounts")]
    public List<AppleMount>? Mounts { get; set; }

    [JsonPropertyName("resources")]
    public AppleResources? Resources { get; set; }

    [JsonPropertyName("platform")]
    public ApplePlatform? Platform { get; set; }
}

internal sealed class AppleImageRef
{
    [JsonPropertyName("reference")]
    public string? Reference { get; set; }

    [JsonPropertyName("descriptor")]
    public AppleDescriptor? Descriptor { get; set; }
}

internal sealed class AppleDescriptor
{
    [JsonPropertyName("digest")]
    public string? Digest { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

internal sealed class AppleInitProcess
{
    [JsonPropertyName("executable")]
    public string? Executable { get; set; }

    [JsonPropertyName("arguments")]
    public List<string>? Arguments { get; set; }

    [JsonPropertyName("environment")]
    public List<string>? Environment { get; set; }

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    [JsonPropertyName("user")]
    public AppleUser? User { get; set; }
}

internal sealed class AppleUser
{
    [JsonPropertyName("id")]
    public AppleUserId? Id { get; set; }
}

internal sealed class AppleUserId
{
    [JsonPropertyName("uid")]
    public int Uid { get; set; }

    [JsonPropertyName("gid")]
    public int Gid { get; set; }
}

internal sealed class ApplePublishedPort
{
    [JsonPropertyName("hostPort")]
    public int HostPort { get; set; }

    [JsonPropertyName("containerPort")]
    public int ContainerPort { get; set; }

    [JsonPropertyName("proto")]
    public string? Proto { get; set; }
}

internal sealed class AppleMount
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("destination")]
    public string? Destination { get; set; }

    [JsonPropertyName("type")]
    public object? Type { get; set; }
}

internal sealed class AppleResources
{
    [JsonPropertyName("cpus")]
    public int Cpus { get; set; }

    [JsonPropertyName("memoryInBytes")]
    public long MemoryInBytes { get; set; }
}

internal sealed class ApplePlatform
{
    [JsonPropertyName("os")]
    public string? Os { get; set; }

    [JsonPropertyName("architecture")]
    public string? Architecture { get; set; }
}

internal sealed class AppleStatus
{
    /// <summary>
    /// <c>running</c>, <c>stopped</c>, and nothing else observed.
    /// <para>
    /// Notably there is <b>no exit code anywhere</b> — not here, not in <c>container inspect</c>.
    /// A container that failed and one that succeeded are both simply "stopped".
    /// </para>
    /// </summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("startedDate")]
    public DateTimeOffset? StartedDate { get; set; }

    [JsonPropertyName("networks")]
    public List<AppleNetworkStatus>? Networks { get; set; }
}

internal sealed class AppleNetworkStatus
{
    [JsonPropertyName("network")]
    public string? Network { get; set; }

    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    [JsonPropertyName("ipv4Address")]
    public string? Ipv4Address { get; set; }

    [JsonPropertyName("ipv4Gateway")]
    public string? Ipv4Gateway { get; set; }

    [JsonPropertyName("macAddress")]
    public string? MacAddress { get; set; }
}

// ---------------------------------------------------------------- images

internal sealed class AppleImage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("configuration")]
    public AppleImageConfiguration? Configuration { get; set; }
}

internal sealed class AppleImageConfiguration
{
    /// <summary>The full reference, registry included: <c>docker.io/library/alpine:latest</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("creationDate")]
    public DateTimeOffset? CreationDate { get; set; }

    [JsonPropertyName("descriptor")]
    public AppleDescriptor? Descriptor { get; set; }
}

// ---------------------------------------------------------------- stats

internal sealed class AppleStats
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Cumulative CPU microseconds. There is no "previous" sample, so a rate needs two polls.</summary>
    [JsonPropertyName("cpuUsageUsec")]
    public long CpuUsageUsec { get; set; }

    [JsonPropertyName("memoryUsageBytes")]
    public long MemoryUsageBytes { get; set; }

    [JsonPropertyName("memoryLimitBytes")]
    public long MemoryLimitBytes { get; set; }

    [JsonPropertyName("networkRxBytes")]
    public long NetworkRxBytes { get; set; }

    [JsonPropertyName("networkTxBytes")]
    public long NetworkTxBytes { get; set; }

    [JsonPropertyName("blockReadBytes")]
    public long BlockReadBytes { get; set; }

    [JsonPropertyName("blockWriteBytes")]
    public long BlockWriteBytes { get; set; }

    [JsonPropertyName("numProcesses")]
    public int NumProcesses { get; set; }
}

// ---------------------------------------------------------------- volumes

internal sealed class AppleVolume
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("configuration")]
    public AppleVolumeConfiguration? Configuration { get; set; }
}

internal sealed class AppleVolumeConfiguration
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("driver")]
    public string? Driver { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("creationDate")]
    public DateTimeOffset? CreationDate { get; set; }

    /// <summary>
    /// The disk image backing the volume, on the host.
    /// <para>
    /// Unusually for a container engine, this really is a path on the user's own machine rather
    /// than one inside a VM — the volume is a file in Application Support.
    /// </para>
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// The image's allocated size, not the space it is using. Reported as unknown rather than as
    /// a 512 GB volume, which is what the sparse file claims.
    /// </summary>
    [JsonPropertyName("sizeInBytes")]
    public long SizeInBytes { get; set; }

    [JsonPropertyName("labels")]
    public Dictionary<string, string>? Labels { get; set; }
}
