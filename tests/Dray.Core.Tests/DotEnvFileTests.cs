using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Reading and writing the <c>.env</c> beside a compose file. Hand-edited far more often than
/// generated, so the parser is lenient and the writer is quiet.
/// </summary>
public class DotEnvFileTests
{
    [Fact]
    public void ReadsPlainAssignments()
    {
        var vars = DotEnvFile.Parse("TAG=1.2\nPORT=8080");

        Assert.Equal([("TAG", "1.2"), ("PORT", "8080")], vars.Select(v => (v.Key, v.Value)));
    }

    [Fact]
    public void SkipsBlanksAndComments()
        => Assert.Single(DotEnvFile.Parse("# the image tag\n\nTAG=1.2\n\n# trailing note\n"));

    [Fact]
    public void AcceptsTheExportPrefix()
    {
        // The file doubles as something people source in a shell, and Compose accepts it.
        Assert.Equal("1.2", Assert.Single(DotEnvFile.Parse("export TAG=1.2")).Value);
    }

    [Fact]
    public void KeepsEverythingAfterTheFirstEquals()
    {
        // A connection string is full of them, and splitting on the last would truncate the URL.
        Assert.Equal(
            "postgres://u:p@h/db?a=1&b=2",
            Assert.Single(DotEnvFile.Parse("DATABASE_URL=postgres://u:p@h/db?a=1&b=2")).Value);
    }

    [Fact]
    public void StripsAMatchingPairOfQuotes()
    {
        Assert.Equal("hello world", Assert.Single(DotEnvFile.Parse("GREETING=\"hello world\"")).Value);
        Assert.Equal("hello world", Assert.Single(DotEnvFile.Parse("GREETING='hello world'")).Value);
    }

    [Fact]
    public void LeavesAnUnmatchedQuoteAlone()
    {
        // A value that starts with a quote and does not end with one is not quoted — it is a value
        // that starts with a quote.
        Assert.Equal("\"unfinished", Assert.Single(DotEnvFile.Parse("ODD=\"unfinished")).Value);
    }

    [Fact]
    public void ASingleQuotedValueIsLiteral()
    {
        // As in a shell: escapes are a double-quote feature.
        Assert.Equal("a\\nb", Assert.Single(DotEnvFile.Parse("X='a\\nb'")).Value);
    }

    [Fact]
    public void ADoubleQuotedValueUnescapesNewlines()
        => Assert.Equal("a\nb", Assert.Single(DotEnvFile.Parse("X=\"a\\nb\"")).Value);

    [Fact]
    public void AnEmptyValueIsKept()
    {
        // It matters: with ${TAG:-latest} an empty TAG takes the default and with ${TAG-latest}
        // it does not, so the difference between empty and absent is load-bearing.
        Assert.Equal("", Assert.Single(DotEnvFile.Parse("TAG=")).Value);
    }

    [Fact]
    public void TheLastAssignmentWins()
    {
        // What Compose does. Showing the first would show a value that will not be used.
        var only = Assert.Single(DotEnvFile.Parse("TAG=1\nTAG=2"));

        Assert.Equal("2", only.Value);
    }

    [Fact]
    public void AnUnreadableLineCostsThatLineAndNotTheFile()
    {
        // The other twenty variables still work, and refusing to show any of them helps nobody.
        var vars = DotEnvFile.Parse("TAG=1.2\nthis is not an assignment\nPORT=8080");

        Assert.Equal(["TAG", "PORT"], vars.Select(v => v.Key));
    }

    [Fact]
    public void HandlesWindowsLineEndings()
        => Assert.Equal(2, DotEnvFile.Parse("TAG=1.2\r\nPORT=8080\r\n").Count);

    // ---------------------------------------------------------------- writing

    [Fact]
    public void WritesWithoutUnnecessaryQuotes()
    {
        // A file full of quotes is one people stop hand-editing, and hand-editing it is normal.
        Assert.Equal("TAG=1.2\nPORT=8080\n",
            DotEnvFile.Serialize([new EnvVar("TAG", "1.2"), new EnvVar("PORT", "8080")]));
    }

    [Fact]
    public void QuotesAValueThatNeedsIt()
    {
        Assert.Equal("GREETING=\"hello world\"\n",
            DotEnvFile.Serialize([new EnvVar("GREETING", "hello world")]));

        // A `#` unquoted would be read back as the start of a comment.
        Assert.Equal("COLOUR=\"#ff0000\"\n",
            DotEnvFile.Serialize([new EnvVar("COLOUR", "#ff0000")]));
    }

    [Fact]
    public void EverythingSurvivesARoundTrip()
    {
        EnvVar[] original =
        [
            new("TAG", "1.2"),
            new("GREETING", "hello world"),
            new("EMPTY", ""),
            new("COLOUR", "#ff0000"),
            new("DATABASE_URL", "postgres://u:p@h/db?a=1"),
            new("MULTI", "one\ntwo"),
        ];

        var read = DotEnvFile.Parse(DotEnvFile.Serialize(original));

        Assert.Equal(
            original.Select(v => (v.Key, v.Value)),
            read.Select(v => (v.Key, v.Value)));
    }

    [Fact]
    public void SecretMarksAreNotWrittenIntoTheFile()
    {
        // The mark is Dray's view of the value, not part of the value. Writing it would put
        // Dray's metadata into a file compose and every other tool also reads.
        var written = DotEnvFile.Serialize([new EnvVar("TOKEN", "abc") { Marked = true }]);

        Assert.Equal("TOKEN=abc\n", written);
    }
}
