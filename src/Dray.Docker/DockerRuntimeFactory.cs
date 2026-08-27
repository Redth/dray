using Dray.Core.Engine;

namespace Dray.Docker;

/// <summary>Creates <see cref="DockerRuntime"/> instances. The only place Core meets Docker.</summary>
public sealed class DockerRuntimeFactory : IContainerRuntimeFactory
{
    /// <summary>Every endpoint that speaks the Docker Engine API — which is every one but Apple's.</summary>
    public bool Handles(DockerEndpoint endpoint) => endpoint.Scheme != EndpointScheme.AppleContainer;

    public IContainerRuntime Create(DockerEndpoint endpoint) => new DockerRuntime(endpoint);
}
