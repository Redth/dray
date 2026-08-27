using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

public class BuildProgressTests
{
    [Fact]
    public void DockersStepMarkerParses()
        => Assert.Equal((3, 12), new BuildProgress("Step 3/12 : RUN apk add curl").Step);

    [Fact]
    public void PodmansUppercaseStepMarkerParsesToo()
    {
        // Captured from podman 6.0.2. Matching case-sensitively loses the step count entirely on
        // one of the two engines Dray supports.
        Assert.Equal((1, 4), new BuildProgress("STEP 1/4: FROM alpine:latest").Step);
    }

    [Fact]
    public void ALineWithNoStepMarkerHasNoStep()
        => Assert.Null(new BuildProgress("Successfully tagged dray:latest").Step);

    [Fact]
    public void AMalformedStepIsIgnoredRatherThanThrowing()
    {
        Assert.Null(new BuildProgress("Step abc/def : nonsense").Step);
        Assert.Null(new BuildProgress("Step 3 of 12").Step);
    }

    [Fact]
    public void AnErrorReportedInsideTheStreamIsAnError()
    {
        // The engine reports build failures in the stream rather than by failing the request, so a
        // build that could not resolve its base image otherwise looks like a success.
        Assert.True(new BuildProgress("", Error: "pull access denied").IsError);
        Assert.False(new BuildProgress("Step 1/2 : FROM alpine").IsError);
    }
}

public class BuildRequestTests
{
    [Fact]
    public void ABuildDefaultsToADockerfileInTheContextRoot()
    {
        var request = new BuildRequest("/src");

        Assert.Equal("Dockerfile", request.Dockerfile);
        Assert.Null(request.Tag);
        Assert.False(request.NoCache);
    }
}
