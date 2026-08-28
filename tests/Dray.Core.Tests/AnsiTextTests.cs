using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Deciding whether a log line needs a colour parser, and getting plain text back out of one.
/// </summary>
public class AnsiTextTests
{
    const string Esc = "";

    [Fact]
    public void AlmostNoLinesCarryEscapes()
    {
        // The point of the check: it is a scan for one byte, and it says no for nearly everything.
        Assert.False(AnsiText.Contains("1:M 27 Aug 2026 21:49:47.403 * Ready to accept connections"));
        Assert.False(AnsiText.Contains(""));
        Assert.False(AnsiText.Contains(null));
    }

    [Fact]
    public void AColouredLineIsRecognised()
        => Assert.True(AnsiText.Contains($"{Esc}[32mokay{Esc}[0m"));

    [Fact]
    public void StrippingLeavesTheWords()
        => Assert.Equal("okay", AnsiText.Strip($"{Esc}[32mokay{Esc}[0m"));

    [Fact]
    public void StrippingHandlesTheLongerColourForms()
    {
        // 8-bit and 24-bit colour, which are the sequences a naive "two digits and an m" strip
        // leaves half of on screen.
        Assert.Equal("hi", AnsiText.Strip($"{Esc}[38;5;208mhi{Esc}[0m"));
        Assert.Equal("hi", AnsiText.Strip($"{Esc}[38;2;255;128;0mhi{Esc}[0m"));
    }

    [Fact]
    public void APlainLineComesBackUnchanged()
    {
        const string line = "listening on [::]:8080";

        Assert.Same(line, AnsiText.Strip(line));
    }

    [Fact]
    public void AnUnterminatedEscapeDoesNotEatTheRestOfTheLine()
    {
        // A truncated line is a real thing in a log. Swallowing everything after it would hide
        // output rather than colour it.
        Assert.Equal("", AnsiText.Strip($"{Esc}[32"));
    }

    [Fact]
    public void SomethingThatIsNotACsiIsLeftAlone()
    {
        // An escape Dray does not understand is text, not a guess.
        Assert.Equal("bell", AnsiText.Strip($"{Esc}bell"));
    }

    [Fact]
    public void NullStripsToEmptyRatherThanThrowing()
        => Assert.Equal("", AnsiText.Strip(null));
}
