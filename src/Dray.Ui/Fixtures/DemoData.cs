using Dray.Core.Model;

namespace Dray.Ui.Fixtures;

/// <summary>
/// Fixtures for Phase 1, so the shell and the component kit can be built and reviewed before any
/// Docker code exists. Deliberately covers every branch of the state vocabulary, including the
/// awkward ones — a stack whose service failed its healthcheck, an OOM kill, an unreachable host.
/// Replaced by the live entity store in Phase 2.
/// </summary>
public static class DemoData
{
    static readonly DateTimeOffset Now = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset Clock => Now;

    public static IReadOnlyList<ContainerSummary> Containers { get; } =
    [
        new()
        {
            Id = "3f2a91c4e8b7d05a", Name = "web", Image = "ghcr.io/redth/dray-web:1.4.2",
            State = DockerState.Running, Health = DockerHealth.Healthy,
            Since = Now.AddDays(-3), Stack = "dray",
            Ports = [new(8080, 80), new(8443, 443)],
            CpuPercent = 1.4, MemoryBytes = 128_500_000,
        },
        new()
        {
            Id = "9c1d77aa2b3e4f60", Name = "api", Image = "ghcr.io/redth/dray-api:1.4.2",
            State = DockerState.Running, Health = DockerHealth.Unhealthy,
            Since = Now.AddHours(-6), Stack = "dray",
            Ports = [new(5000, 5000)],
            CpuPercent = 42.8, MemoryBytes = 512_300_000,
        },
        new()
        {
            Id = "b47e0f5c19d82a3b", Name = "postgres", Image = "postgres:16-alpine",
            State = DockerState.Running, Health = DockerHealth.Healthy,
            Since = Now.AddDays(-31), Stack = "dray",
            Ports = [new(5432, 5432)],
            CpuPercent = 0.3, MemoryBytes = 96_000_000,
        },
        new()
        {
            Id = "0a5b8e2f77c13d94", Name = "migrate", Image = "ghcr.io/redth/dray-api:1.4.2",
            State = DockerState.Exited, ExitCode = 0,
            Since = Now.AddHours(-6), Stack = "dray",
        },
        new()
        {
            Id = "e81c46b0d9f27a55", Name = "worker", Image = "ghcr.io/redth/dray-worker:1.4.2",
            State = DockerState.Exited, ExitCode = 137,
            Since = Now.AddMinutes(-18), Stack = "dray",
        },
        new()
        {
            Id = "7d3f10ab55e6c982", Name = "redis", Image = "redis:7",
            State = DockerState.Running, Health = DockerHealth.Starting,
            Since = Now.AddSeconds(-22), Stack = "dray",
            Ports = [new(6379, 6379)],
            CpuPercent = 0.1, MemoryBytes = 12_400_000,
        },
        new()
        {
            Id = "c92a7e14fb03d6a8", Name = "minio", Image = "quay.io/minio/minio:latest",
            State = DockerState.Paused,
            Since = Now.AddDays(-1),
            Ports = [new(9000, 9000), new(9001, 9001)],
        },
        new()
        {
            Id = "45f6b8e0c1a7d239", Name = "flaky-scraper", Image = "scraper:dev",
            State = DockerState.Restarting,
            Since = Now.AddMinutes(-2),
        },
        new()
        {
            Id = "1b7c93de5a08f462", Name = "old-jenkins", Image = "jenkins/jenkins:lts",
            State = DockerState.Dead,
            Since = Now.AddDays(-92),
        },
        new()
        {
            Id = "a0e5c72b6d41f983", Name = "nas-plex", Image = "linuxserver/plex:latest",
            State = DockerState.Unknown,
            Since = Now.AddDays(-14),
        },
    ];
}
