using Dray.Core.Engine;

namespace Dray.Apple;

/// <summary>Creates <see cref="AppleRuntime"/> instances. The only place Core meets Apple's CLI.</summary>
public sealed class AppleRuntimeFactory : IContainerRuntimeFactory
{
    public bool Handles(DockerEndpoint endpoint) => endpoint.Scheme == EndpointScheme.AppleContainer;

    /// <summary>
    /// The endpoint carries the executable discovery actually found, so a Homebrew install and a
    /// hand-built one stay distinct engines rather than both resolving to whatever <c>PATH</c>
    /// happens to hit first.
    /// </summary>
    public IContainerRuntime Create(DockerEndpoint endpoint) => new AppleRuntime(executable: endpoint.Path);
}
