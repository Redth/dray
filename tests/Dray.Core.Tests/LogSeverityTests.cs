using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The level a program labelled its own line with.
/// <para>
/// The rule these protect: read what the program printed, never infer. A viewer that guessed would
/// be wrong on exactly the lines that matter.
/// </para>
/// </summary>
public class LogSeverityTests
{
    [Theory]
    [InlineData("[error] 17#17: *6 open() failed", LogLevel.Error)]
    [InlineData("ERROR could not bind to port", LogLevel.Error)]
    [InlineData("FATAL: database system is shut down", LogLevel.Error)]
    [InlineData("1:M # WARNING Memory overcommit must be enabled!", LogLevel.Warning)]
    [InlineData("[warn] worker process exited", LogLevel.Warning)]
    [InlineData("INFO starting up", LogLevel.Info)]
    [InlineData("[1] LOG: database system is ready", LogLevel.Info)]
    [InlineData("DEBUG cache miss for key", LogLevel.Debug)]
    public void TheWordTheProgramPrintedIsTheLevel(string line, LogLevel expected)
        => Assert.Equal(expected, LogSeverity.Of(line));

    [Theory]
    // Real redis output. None of it is labelled, and none of it should be coloured.
    [InlineData("1:M * Ready to accept connections tcp")]
    [InlineData("1:M * DB saved on disk")]
    [InlineData("1:M * Saving the final RDB snapshot before exiting.")]
    [InlineData("Running in standalone mode")]
    [InlineData("")]
    public void MostLinesSayNothingAboutTheirLevel(string line)
        => Assert.Equal(LogLevel.None, LogSeverity.Of(line));

    [Fact]
    public void APluralIsNotTheWord()
    {
        // "no errors found" is a program reporting success. Matching it would colour the one line
        // that says everything is fine.
        Assert.Equal(LogLevel.None, LogSeverity.Of("scan complete, no errors found"));
    }

    [Fact]
    public void AWordFurtherInDoesNotCount()
    {
        // A level is printed before the message. Three hundred characters in, "error" is prose or a
        // stack frame.
        var line = new string('x', 60) + " error";

        Assert.Equal(LogLevel.None, LogSeverity.Of(line));
    }

    [Fact]
    public void TheWorstLabelWins()
    {
        // "WARN could not parse, treating as error" is a warning: the program said so first.
        // Reading it as an error is the safer way to be wrong, and it is what this does.
        Assert.Equal(LogLevel.Error, LogSeverity.Of("WARN could not parse, treating as error"));
    }

    [Fact]
    public void NullSaysNothing()
        => Assert.Equal(LogLevel.None, LogSeverity.Of(null));
}
