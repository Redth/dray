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
}
