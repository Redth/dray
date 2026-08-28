using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Whether a log line already tells you the time.
/// <para>
/// Every line here was copied from a container actually running on this machine, because the whole
/// value of this is that it works on the formats real programs print rather than the ones a regex
/// was written against.
/// </para>
/// </summary>
public class LogTimestampsTests
{
    [Theory]
    // redis
    [InlineData("1:M 27 Aug 2026 21:49:47.403 * Ready to accept connections tcp")]
    // nginx, error log
    [InlineData("2026/08/27 17:06:45 [error] 17#17: *6 open() \"/usr/share/nginx/html/missing\" failed")]
    // nginx, access log — the timestamp is bracketed and mid-line
    [InlineData("10.88.0.7 - - [27/Aug/2026:17:06:45 +0000] \"GET /missing HTTP/1.1\" 404 153")]
    // Go and .NET both default to something like this
    [InlineData("2026-08-27T21:47:59.214Z INFO  starting up")]
    [InlineData("2026-08-27 21:47:59.214 +00:00 [INF] Now listening on: http://[::]:8080")]
    // syslog
    [InlineData("Aug 27 21:47:59 host sshd[1234]: Accepted publickey")]
    // postgres
    [InlineData("2026-08-27 21:47:59.214 UTC [1] LOG:  database system is ready")]
    public void ALineThatPrintsItsOwnClockIsRecognised(string line)
        => Assert.True(LogTimestamps.CarriesItsOwn(line));

    [Theory]
    // redis prints a banner before it prints any timestamps
    [InlineData("                _._                                                  ")]
    [InlineData("Redis 7.4.11 (00000000/0) 64 bit")]
    [InlineData("Running in standalone mode")]
    [InlineData("Ready to accept connections")]
    // a stack trace, which is the case where suppressing the engine's timestamp would hurt
    [InlineData("   at Dray.Core.Engine.EngineManager.SelectAsync(String hostId)")]
    [InlineData("")]
    public void ALineWithNoClockKeepsTheEnginesTimestamp(string line)
        => Assert.False(LogTimestamps.CarriesItsOwn(line));

    [Fact]
    public void NullIsNotATimestamp()
        => Assert.False(LogTimestamps.CarriesItsOwn(null));

    [Fact]
    public void AClockFurtherIntoTheLineDoesNotCount()
    {
        // A time inside a URL, a quoted message or a request path is not the line's own clock, and
        // treating it as one would blank the column on a line that never said what time it was.
        var line = new string('x', 60) + " failed at 21:47:59 according to the server";

        Assert.False(LogTimestamps.CarriesItsOwn(line));
    }

    [Fact]
    public void APortIsNotATime()
    {
        // The shape that would break a looser match: colons and digits that are not a clock.
        Assert.False(LogTimestamps.CarriesItsOwn("connecting to 10.88.0.7:6379 with 3 retries"));
        Assert.False(LogTimestamps.CarriesItsOwn("listening on [::]:8080"));
    }

    // ---------------------------------------------------------------- lifting it out

    [Theory]
    // redis: the process role and the severity glyph stay, the clock goes
    [InlineData(
        "1:M 27 Aug 2026 21:49:47.403 * Ready to accept connections tcp",
        "1:M * Ready to accept connections tcp")]
    // nginx error log
    [InlineData(
        "2026/08/27 17:06:45 [error] 17#17: *6 open() failed",
        "[error] 17#17: *6 open() failed")]
    // an access log, whose timestamp is bracketed — the empty brackets go with it
    [InlineData(
        "10.88.0.7 - - [27/Aug/2026:17:06:45 +0000] \"GET / HTTP/1.1\" 404 153",
        "10.88.0.7 - - \"GET / HTTP/1.1\" 404 153")]
    // Go, and anything else printing ISO 8601
    [InlineData("2026-08-27T21:47:59.214Z INFO starting up", "INFO starting up")]
    // postgres, which puts a timezone after the fraction
    [InlineData(
        "2026-08-27 21:47:59.214 UTC [1] LOG:  database system is ready",
        "[1] LOG: database system is ready")]
    // syslog, which prints no year
    [InlineData("Aug 27 21:47:59 host sshd[1234]: Accepted publickey", "host sshd[1234]: Accepted publickey")]
    public void TheProgramsOwnClockIsLiftedOut(string line, string expected)
        => Assert.Equal(expected, LogTimestamps.WithoutItsOwn(line));

    [Fact]
    public void ALineWithNoClockIsUntouched()
    {
        const string line = "Running in standalone mode";

        Assert.Equal(line, LogTimestamps.WithoutItsOwn(line));
    }

    [Fact]
    public void ALineThatIsNothingButItsClockKeepsIt()
    {
        // An empty row says less than a redundant one, and this is a display change rather than an
        // edit to the program's output.
        Assert.Equal("21:47:59", LogTimestamps.WithoutItsOwn("21:47:59"));
    }

    [Fact]
    public void OnlyTheFirstOneGoes()
    {
        // A line that quotes a second time — a duration, a deadline — keeps it. Removing every
        // clock would cut holes in the message.
        Assert.Equal(
            "1:M * retrying until 22:00:00",
            LogTimestamps.WithoutItsOwn("1:M 27 Aug 2026 21:49:47.403 * retrying until 22:00:00"));
    }

    [Fact]
    public void ADateWithNoClockIsLeftAlone()
    {
        // It is the time that repeats in the column. A line about a date is a line about a date.
        const string line = "backup for 2026-08-27 completed";

        Assert.Equal(line, LogTimestamps.WithoutItsOwn(line));
    }

    [Fact]
    public void NullBecomesEmptyRatherThanThrowing()
        => Assert.Equal("", LogTimestamps.WithoutItsOwn(null));
}
