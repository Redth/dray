using Dray.Core.Engine;
using Dray.Core.Tests.Fakes;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Two engines behind one seam. These check the dispatch, because sending an endpoint to the wrong
/// runtime does not fail loudly — it produces a runtime that connects to nothing and reports the
/// engine as down, which reads as the user's problem rather than Dray's.
/// </summary>
public class CompositeRuntimeFactoryTests
{
    sealed class NothingRuntime : StubRuntime
    {
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class SchemeFactory(EndpointScheme scheme) : IContainerRuntimeFactory
    {
        public int Created { get; private set; }

        public bool Handles(DockerEndpoint endpoint) => endpoint.Scheme == scheme;

        public IContainerRuntime Create(DockerEndpoint endpoint)
        {
            Created++;
            return new NothingRuntime();
        }
    }

    static DockerEndpoint Endpoint(EndpointScheme scheme) => new() { Scheme = scheme, Raw = scheme.ToString() };

    [Fact]
    public void EachEndpointGoesToTheFactoryThatClaimsIt()
    {
        var apple = new SchemeFactory(EndpointScheme.AppleContainer);
        var docker = new SchemeFactory(EndpointScheme.Unix);
        var composite = new CompositeRuntimeFactory(apple, docker);

        composite.Create(Endpoint(EndpointScheme.AppleContainer));
        composite.Create(Endpoint(EndpointScheme.Unix));

        Assert.Equal(1, apple.Created);
        Assert.Equal(1, docker.Created);
    }

    [Fact]
    public void TheFirstClaimWins()
    {
        var first = new SchemeFactory(EndpointScheme.Unix);
        var second = new SchemeFactory(EndpointScheme.Unix);

        new CompositeRuntimeFactory(first, second).Create(Endpoint(EndpointScheme.Unix));

        Assert.Equal(1, first.Created);
        Assert.Equal(0, second.Created);
    }

    [Fact]
    public void AnEndpointNothingCanServeSaysSoRatherThanReturningNull()
    {
        var composite = new CompositeRuntimeFactory(new SchemeFactory(EndpointScheme.Unix));

        Assert.False(composite.Handles(Endpoint(EndpointScheme.Ssh)));

        // A null runtime would surface later as a NullReferenceException on a page, which tells
        // the user nothing about what actually happened.
        Assert.Throws<RuntimeConnectionException>(() => composite.Create(Endpoint(EndpointScheme.Ssh)));
    }
}
