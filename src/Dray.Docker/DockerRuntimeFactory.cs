using Dray.Core.Engine;

namespace Dray.Docker;

/// <summary>Creates <see cref="DockerRuntime"/> instances. The only place Core meets Docker.</summary>
public sealed class DockerRuntimeFactory : IContainerRuntimeFactory
{
    public IContainerRuntime Create(DockerEndpoint endpoint) => new DockerRuntime(endpoint);
}
