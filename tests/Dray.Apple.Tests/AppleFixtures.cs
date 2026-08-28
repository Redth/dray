namespace Dray.Apple.Tests;

/// <summary>
/// Output captured verbatim from Apple's <c>container</c> 1.3.0 on macOS 26.
/// <para>
/// Kept as literal strings rather than built from objects on purpose: the value of these tests is
/// that they run against the shapes the engine really emits, including the parts that surprised us
/// — a container whose id is its name, a stopped container with no exit code anywhere, and a
/// network address carrying its CIDR suffix.
/// </para>
/// </summary>
public static class AppleFixtures
{
    public const string Version = "container CLI version 1.3.0 (build: release, commit: unspeci)\n";

    public const string SystemStatusRunning = """
        FIELD              VALUE
        status             running
        appRoot            /Users/redth/Library/Application Support/com.apple.container/
        installRoot        /opt/homebrew/Cellar/container/1.3.0/
        apiserver.version  container-apiserver version 1.3.0 (build: release, commit: unspeci)
        """;

    public const string SystemStatusStopped =
        "Error: The container system service is not running. Run `container system start` to start it.";

    /// <summary>One running container and one that exited, exactly as <c>container ls --all</c> prints them.</summary>
    public const string ListJson = """
        [{"configuration":{"capAdd":[],"capDrop":[],"creationDate":"2026-08-27T20:59:56Z","dns":{"nameservers":[],"options":[],"searchDomains":[]},"id":"dray-apple-test","image":{"descriptor":{"digest":"sha256:28bd5fe8b56d1bd048e5babf5b10710ebe0bae67db86916198a6eec434943f8b","mediaType":"application/vnd.oci.image.index.v1+json","size":9218},"reference":"docker.io/library/alpine:latest"},"initProcess":{"arguments":["-c","while true; do sleep 30; done"],"environment":["PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"],"executable":"sh","rlimits":[],"supplementalGroups":[],"terminal":false,"user":{"id":{"gid":0,"uid":0}},"workingDirectory":"/"},"labels":{},"mounts":[],"networks":[{"network":"default","options":{"hostname":"dray-apple-test","mtu":1280}}],"platform":{"architecture":"arm64","os":"linux"},"publishedPorts":[],"publishedSockets":[],"readOnly":false,"resources":{"cpuOverhead":1,"cpus":4,"memoryInBytes":1073741824},"rosetta":false,"runtimeHandler":"container-runtime-linux","ssh":false,"sysctls":{},"useInit":false,"virtualization":false},"id":"dray-apple-test","status":{"networks":[{"hostname":"dray-apple-test","ipv4Address":"192.168.64.2/24","ipv4Gateway":"192.168.64.1","macAddress":"f6:6a:50:81:25:25","mtu":1280,"network":"default","variant":"reserved"}],"startedDate":"2026-08-27T20:59:57Z","state":"running"}},{"configuration":{"creationDate":"2026-08-27T21:00:15Z","id":"dray-apple-exit","image":{"descriptor":{"digest":"sha256:28bd5fe8b56d1bd048e5babf5b10710ebe0bae67db86916198a6eec434943f8b","mediaType":"application/vnd.oci.image.index.v1+json","size":9218},"reference":"docker.io/library/alpine:latest"},"initProcess":{"arguments":["-c","exit 7"],"environment":["PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"],"executable":"sh","user":{"id":{"gid":0,"uid":0}},"workingDirectory":"/"},"labels":{},"mounts":[],"networks":[{"network":"default"}],"publishedPorts":[],"resources":{"cpus":4,"memoryInBytes":1073741824}},"id":"dray-apple-exit","status":{"state":"stopped"}}]
        """;

    /// <summary>A container compose created, with a published port and a bind mount.</summary>
    public const string ComposeContainerJson = """
        [{"configuration":{"creationDate":"2026-08-27T20:59:56Z","id":"shop-web-1","image":{"reference":"docker.io/library/nginx:alpine"},"initProcess":{"arguments":[],"environment":["TZ=UTC","DB_PASSWORD=hunter2"],"executable":"nginx","user":{"id":{"gid":0,"uid":101}},"workingDirectory":"/"},"labels":{"com.docker.compose.project":"shop","com.docker.compose.service":"web","com.docker.compose.container-number":"1"},"mounts":[{"destination":"/usr/share/nginx/html","source":"/Users/redth/site"}],"networks":[{"network":"default"}],"publishedPorts":[{"containerPort":80,"hostPort":8080,"proto":"tcp"},{"containerPort":443,"hostPort":8443,"proto":"tcp"}],"resources":{"cpus":4,"memoryInBytes":1073741824}},"id":"shop-web-1","status":{"networks":[{"hostname":"shop-web-1","ipv4Address":"192.168.64.3/24","ipv4Gateway":"192.168.64.1","macAddress":"aa:bb:cc:dd:ee:ff","network":"default"}],"startedDate":"2026-08-27T21:05:00Z","state":"running"}}]
        """;

    public const string ImagesJson = """
        [{"configuration":{"creationDate":"2026-06-16T12:00:00Z","descriptor":{"digest":"sha256:28bd5fe8b56d1bd048e5babf5b10710ebe0bae67db86916198a6eec434943f8b","mediaType":"application/vnd.oci.image.index.v1+json","size":9218},"name":"docker.io/library/alpine:latest"},"id":"28bd5fe8b56d1bd048e5babf5b10710ebe0bae67db86916198a6eec434943f8b"}]
        """;

    /// <summary>Captured from <c>container volume ls --format json</c> after creating one volume.</summary>
    public const string VolumesJson = """
        [{"configuration":{"creationDate":"2026-08-28T00:27:48Z","driver":"local","format":"ext4","labels":{},"name":"dray-vol-check","options":{},"sizeInBytes":549755813888,"source":"/Users/redth/Library/Application Support/com.apple.container/volumes/dray-vol-check/volume.img"},"id":"dray-vol-check"}]
        """;

    /// <summary>A container mounting a named volume, for the "who is holding this" lookup.</summary>
    public const string ContainerWithVolumeJson = """
        [{"configuration":{"creationDate":"2026-08-28T00:30:00Z","id":"shop-db-1","image":{"reference":"docker.io/library/postgres:16"},"initProcess":{"arguments":[],"environment":[],"executable":"postgres","user":{"id":{"gid":0,"uid":0}},"workingDirectory":"/"},"labels":{},"mounts":[{"source":"dray-vol-check","destination":"/var/lib/postgresql/data"}],"networks":[{"network":"default"}],"publishedPorts":[],"resources":{"cpus":4,"memoryInBytes":1073741824}},"id":"shop-db-1","status":{"startedDate":"2026-08-28T00:30:01Z","state":"running"}}]
        """;

    public const string StatsJson = """
        [{"blockReadBytes":1679360,"blockWriteBytes":0,"cpuUsageUsec":16577908,"id":"dray-apple-load","memoryLimitBytes":1073741824,"memoryUsageBytes":17707008,"networkRxBytes":2396,"networkTxBytes":602,"numProcesses":2}]
        """;
}
