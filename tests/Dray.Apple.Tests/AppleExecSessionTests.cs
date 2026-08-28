using Dray.Apple;
using Xunit;

namespace Dray.Apple.Tests;

/// <summary>
/// What makes a shell without a pseudo-terminal usable.
/// <para>
/// Found by driving the real terminal in a browser and watching a typed command be echoed and then
/// never run — a failure that looked exactly like the shell ignoring the user.
/// </para>
/// </summary>
public class AppleExecSessionTests
{
    [Fact]
    public void EnterIsTranslatedFromCarriageReturnToNewline()
    {
        // A terminal sends CR for Enter — xterm does — and a real pseudo-terminal's driver turns
        // it into LF before the shell sees it. There is no driver here, and a shell reading a pipe
        // waits for LF, so without this every command is typed, echoed, and never executed.
        Assert.Equal("ls -la\n", AppleExecSession.ForShell("ls -la\r"));
    }

    [Fact]
    public void EveryCarriageReturnIsTranslatedNotJustTheLast()
        => Assert.Equal("one\ntwo\n", AppleExecSession.ForShell("one\rtwo\r"));

    [Fact]
    public void ANewlineIsLeftAlone()
    {
        // Pasted text arrives with real newlines already.
        Assert.Equal("one\ntwo\n", AppleExecSession.ForShell("one\ntwo\n"));
    }

    [Fact]
    public void ControlCharactersReachTheShellUntouched()
    {
        // Ctrl-C is how a runaway command is stopped, and it is not a line ending. Translating it
        // would send the shell an empty line instead of an interrupt.
        Assert.Equal("", AppleExecSession.ForShell(""));
        Assert.Equal("", AppleExecSession.ForShell(""));
        Assert.Equal("\t", AppleExecSession.ForShell("\t"));
    }

    [Fact]
    public void OrdinaryTextIsUnchanged()
        => Assert.Equal("echo hello", AppleExecSession.ForShell("echo hello"));
}
