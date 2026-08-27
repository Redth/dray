namespace Dray.Core.Model;

/// <summary>A published port mapping, host side to container side.</summary>
public sealed record PortBinding(int HostPort, int ContainerPort, string Protocol = "tcp")
{
    public string Display => $"{HostPort}:{ContainerPort}" + (Protocol == "tcp" ? "" : $"/{Protocol}");

    /// <summary>Where clicking the port should take the user. Only meaningful for TCP.</summary>
    public string? LocalUrl => Protocol == "tcp" ? $"http://localhost:{HostPort}" : null;
}

/// <summary>
/// One row of the containers list. Deliberately only the fields that answer the question someone
/// opened Dray to ask — state, health, ports, and how long it has been that way. Everything else
/// lives behind the detail pane's Inspect tab (PRODUCT.md: "answer the question in the first screen").
/// </summary>
public sealed record ContainerSummary
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Image { get; init; }

    public required DockerState State { get; init; }

    public DockerHealth Health { get; init; } = DockerHealth.None;

    public int? ExitCode { get; init; }

    /// <summary>When the container last entered its current state.</summary>
    public DateTimeOffset? Since { get; init; }

    public IReadOnlyList<PortBinding> Ports { get; init; } = [];

    /// <summary>Compose project name, from the <c>com.docker.compose.project</c> label.</summary>
    public string? Stack { get; init; }

    public double? CpuPercent { get; init; }

    public long? MemoryBytes { get; init; }

    public ContainerStatus Status => ContainerStatusVocabulary.Resolve(State, Health, ExitCode);

    /// <summary>The short id users actually recognise.</summary>
    public string ShortId => Id.Length <= 12 ? Id : Id[..12];
}
