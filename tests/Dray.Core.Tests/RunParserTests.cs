using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The run dialog accepts what people already have in their shell history, so these encode
/// <c>docker run</c>'s forms rather than Dray's preferences. Every failure returns a sentence
/// naming the offending line — an error that says only "invalid" makes the user hunt.
/// </summary>
public class RunParserTests
{
    // ---------------------------------------------------------------- ports

    [Fact]
    public void PortsTakeTheFormPeopleAlreadyType()
    {
        var ports = RunParser.ParsePorts("8080:80\n5432:5432/tcp\n1194:1194/udp", out var problem);

        Assert.Null(problem);
        Assert.Equal(
            [(8080, 80, "tcp"), (5432, 5432, "tcp"), (1194, 1194, "udp")],
            ports.Select(p => (p.HostPort, p.ContainerPort, p.Protocol)));
    }

    [Fact]
    public void ABarePortMeansTheSameOnBothSides()
    {
        // `docker run -p 80` does not mean this, but it is what everyone means when they type it,
        // and rejecting something unambiguous is worse than accepting it.
        var port = Assert.Single(RunParser.ParsePorts("6379", out var problem));

        Assert.Null(problem);
        Assert.Equal((6379, 6379), (port.HostPort, port.ContainerPort));
    }

    [Fact]
    public void BlankLinesAndCommentsAreSkipped()
    {
        var ports = RunParser.ParsePorts("\n8080:80\n\n# the admin port\n9090:9090\n", out var problem);

        Assert.Null(problem);
        Assert.Equal(2, ports.Count);
    }

    [Theory]
    [InlineData("80:", "8080:80")]
    [InlineData("http:80", "8080:80")]
    [InlineData("70000:80", "8080:80")]
    [InlineData("0:80", "8080:80")]
    public void AnUnreadablePortNamesTheLineAndTheForm(string bad, string hint)
    {
        RunParser.ParsePorts(bad, out var problem);

        Assert.NotNull(problem);
        Assert.Contains(bad, problem, StringComparison.Ordinal);
        Assert.Contains(hint, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownProtocolIsRejectedRatherThanPassedThrough()
    {
        RunParser.ParsePorts("8080:80/http", out var problem);

        Assert.NotNull(problem);
        Assert.Contains("tcp, udp or sctp", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameHostPortTwiceIsCaughtHereRatherThanByTheEngine()
    {
        // The engine fails this at run time with a message about an address already in use, which
        // reads as something else holding the port rather than as a typo two lines up.
        RunParser.ParsePorts("8080:80\n8080:443", out var problem);

        Assert.NotNull(problem);
        Assert.Contains("8080/tcp is mapped twice", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void OneHostPortOnTcpAndUdpIsNotAConflict()
    {
        var ports = RunParser.ParsePorts("53:53/tcp\n53:53/udp", out var problem);

        Assert.Null(problem);
        Assert.Equal(2, ports.Count);
    }

    // ---------------------------------------------------------------- environment

    [Fact]
    public void AValueKeepsEverythingAfterTheFirstEquals()
    {
        // A connection string is full of equals signs, and splitting on the last one would hand
        // the container a truncated URL that fails somewhere far from here.
        var vars = RunParser.ParseEnvironment("DATABASE_URL=postgres://u:p@h/db?a=1&b=2", out var problem);

        Assert.Null(problem);
        Assert.Equal("postgres://u:p@h/db?a=1&b=2", Assert.Single(vars).Value);
    }

    [Fact]
    public void AnEmptyValueIsAllowed()
        => Assert.Equal("", Assert.Single(RunParser.ParseEnvironment("DEBUG=", out _)).Value);

    [Fact]
    public void ALineWithNoEqualsIsAnErrorRatherThanAnEmptyVariable()
    {
        // Almost always a half-typed line. Silently creating FOO="" hides that.
        RunParser.ParseEnvironment("POSTGRES_PASSWORD", out var problem);

        Assert.NotNull(problem);
        Assert.Contains("NAME=value", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameWithASpaceIsRejected()
    {
        RunParser.ParseEnvironment("MY VAR=1", out var problem);

        Assert.NotNull(problem);
        Assert.Contains("cannot contain spaces", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingOneVariableTwiceIsFlagged()
    {
        // The engine silently takes the last. Saying so beats setting a value the user can see in
        // the box and cannot find in the container.
        RunParser.ParseEnvironment("TZ=UTC\nTZ=Europe/London", out var problem);

        Assert.NotNull(problem);
        Assert.Contains("TZ is set twice", problem, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- mounts

    [Fact]
    public void ASourceWithoutALeadingSlashIsANamedVolume()
    {
        var mount = Assert.Single(RunParser.ParseMounts("pgdata:/var/lib/postgresql/data", out var problem));

        Assert.Null(problem);
        Assert.Equal(MountKind.Volume, mount.Kind);
        Assert.Equal("pgdata", mount.Source);
        Assert.Equal("/var/lib/postgresql/data", mount.Destination);
        Assert.False(mount.ReadOnly);
    }

    [Fact]
    public void ASourceStartingWithASlashIsAHostPath()
    {
        var mount = Assert.Single(RunParser.ParseMounts("/Users/me/site:/usr/share/nginx/html:ro", out var problem));

        Assert.Null(problem);
        Assert.Equal(MountKind.Bind, mount.Kind);
        Assert.True(mount.ReadOnly);
    }

    [Fact]
    public void ATildeIsExpandedBecauseTheEngineWillNotDoIt()
    {
        // The shell expands ~; the daemon does not. Left alone it reaches the engine as a literal
        // directory named "~" and mounts an empty folder — which looks like the container ignoring
        // the mount rather than like a path problem.
        var mount = Assert.Single(RunParser.ParseMounts("~/site:/usr/share/nginx/html", out _));

        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            mount.Source,
            StringComparison.Ordinal);

        Assert.DoesNotContain('~', mount.Source);
    }

    [Fact]
    public void ARelativeDestinationIsRejected()
    {
        // Accepted by no engine, and nearly always a mount written backwards.
        RunParser.ParseMounts("pgdata:data", out var problem);

        Assert.NotNull(problem);
        Assert.Contains("must be absolute", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AThirdPartThatIsNotRoOrRwIsRejected()
    {
        RunParser.ParseMounts("pgdata:/data:readonly", out var problem);

        Assert.NotNull(problem);
        Assert.Contains("ro or rw", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoMountsOnOneDestinationAreCaught()
    {
        // The engine takes one and drops the other with no complaint, so the container comes up
        // with a volume the user believes is mounted somewhere it is not.
        RunParser.ParseMounts("a:/data\nb:/data/", out var problem);

        Assert.NotNull(problem);
        Assert.Contains("both land on", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AMountWithNoDestinationNamesTheForm()
    {
        RunParser.ParseMounts("pgdata", out var problem);

        Assert.NotNull(problem);
        Assert.Contains("source:/path/in/container", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyInputParsesToNothingRatherThanFailing()
    {
        Assert.Empty(RunParser.ParsePorts(null, out var a));
        Assert.Empty(RunParser.ParseEnvironment("   ", out var b));
        Assert.Empty(RunParser.ParseMounts("\n\n", out var c));

        Assert.Null(a);
        Assert.Null(b);
        Assert.Null(c);
    }
}
