using Docker.DotNet;
using Dray.Docker;
using Xunit;

namespace Dray.Docker.Tests;

/// <summary>
/// Turning the engine's refusal into a sentence.
/// <para>
/// Every message here was produced by a real engine. The wordings differ per engine and none of
/// them says "port", so matching on the obvious word would have caught none of the three.
/// </para>
/// </summary>
public class DockerRunTests
{
    static DockerApiException Refusal(string body)
        => new(System.Net.HttpStatusCode.InternalServerError, body);

    [Fact]
    public void PodmansPortConflictIsRecognisedDespiteSayingProxy()
    {
        // Verified live against podman 6.0.2: running a second container on a taken host port
        // produced exactly this, wrapper and escaped newline included.
        var ex = Refusal("""
            {"cause":"","message":"something went wrong with the request: \"proxy already running\\n\"","response":500}
            """);

        Assert.Equal(
            "That host port is already in use. Pick another, or stop whatever is holding it.",
            DockerRun.Explain(ex));
    }

    [Fact]
    public void DockersTwoWordingsForTheSameConflictBothLand()
    {
        var bind = Refusal("""{"message":"driver failed programming external connectivity on endpoint x: Bind for 0.0.0.0:8080 failed: port is already allocated"}""");
        var proxy = Refusal("""{"message":"listen tcp4 0.0.0.0:8080: bind: address already in use"}""");

        Assert.Equal(DockerRun.Explain(bind), DockerRun.Explain(proxy));
        Assert.StartsWith("That host port is already in use", DockerRun.Explain(bind), StringComparison.Ordinal);
    }

    [Fact]
    public void ANameClashDoesNotQuoteAnIdNobodyRecognises()
    {
        var ex = Refusal("""
            {"message":"Conflict. The container name \"/api\" is already in use by container \"9f2c1e...\". You have to remove (or rename) that container to be able to reuse that name."}
            """);

        Assert.Equal("A container with that name already exists.", DockerRun.Explain(ex));
    }

    [Fact]
    public void AMissingImageSaysWhatToDoAboutIt()
    {
        var ex = Refusal("""{"message":"No such image: postgres:99"}""");

        Assert.Equal("That image is not on this host. Pull it first.", DockerRun.Explain(ex));
    }

    [Fact]
    public void AnUnrecognisedFailureKeepsTheEnginesOwnWords()
    {
        // Dray does not have a sentence for everything, and inventing a vague one would be worse
        // than the engine's specific one.
        var ex = Refusal("""{"message":"invalid mount config for type \"bind\": bind source path does not exist"}""");

        Assert.Equal(
            "invalid mount config for type \"bind\": bind source path does not exist",
            DockerRun.Explain(ex));
    }

    [Fact]
    public void ABodyThatIsNotJsonIsStillShown()
    {
        Assert.Equal("plain text failure", DockerRun.Explain(Refusal("plain text failure")));
    }

    [Fact]
    public void AnEmptyBodyFallsBackToSomethingTrue()
    {
        Assert.Equal("The engine refused to create the container.", DockerRun.Explain(Refusal("")));
    }
}
