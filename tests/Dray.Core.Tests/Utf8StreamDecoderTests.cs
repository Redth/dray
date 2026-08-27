using System.Text;
using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Terminal output arrives in arbitrary byte chunks, and a multi-byte character is routinely split
/// across two of them. Decoding each chunk independently turns every such character into a pair of
/// replacement characters — and a box-drawing TUI is made almost entirely of those, so the
/// corruption is total rather than occasional.
/// </summary>
public class Utf8StreamDecoderTests
{
    [Fact]
    public void PlainAsciiPassesThrough()
    {
        var decoder = new Utf8StreamDecoder();

        Assert.Equal("hello", decoder.Decode("hello"u8));
    }

    [Fact]
    public void ACharacterSplitAcrossTwoChunksIsRejoined()
    {
        // "é" is two bytes. Split between them, a per-chunk decode yields two replacement
        // characters and loses the original entirely.
        var bytes = Encoding.UTF8.GetBytes("é");
        var decoder = new Utf8StreamDecoder();

        var first = decoder.Decode(bytes.AsSpan(0, 1));
        var second = decoder.Decode(bytes.AsSpan(1));

        Assert.Equal("", first);
        Assert.Equal("é", second);
    }

    [Fact]
    public void ABoxDrawingCharacterSplitThreeWaysSurvives()
    {
        // "─" is three bytes, and the frame of every TUI is built from these.
        var bytes = Encoding.UTF8.GetBytes("─");
        var decoder = new Utf8StreamDecoder();

        var output = decoder.Decode(bytes.AsSpan(0, 1))
            + decoder.Decode(bytes.AsSpan(1, 1))
            + decoder.Decode(bytes.AsSpan(2, 1));

        Assert.Equal("─", output);
    }

    [Fact]
    public void AFourByteEmojiSplitAnywhereSurvives()
    {
        var bytes = Encoding.UTF8.GetBytes("🐳");

        for (var split = 1; split < bytes.Length; split++)
        {
            var decoder = new Utf8StreamDecoder();

            var output = decoder.Decode(bytes.AsSpan(0, split)) + decoder.Decode(bytes.AsSpan(split));

            Assert.Equal("🐳", output);
        }
    }

    [Fact]
    public void TextEitherSideOfASplitIsKept()
    {
        var bytes = Encoding.UTF8.GetBytes("before—after");
        var decoder = new Utf8StreamDecoder();

        // Land the split inside the em dash.
        var at = Array.IndexOf(bytes, (byte)0xE2) + 1;

        var output = decoder.Decode(bytes.AsSpan(0, at)) + decoder.Decode(bytes.AsSpan(at));

        Assert.Equal("before—after", output);
    }

    [Fact]
    public void AnEmptyChunkProducesNothing()
        => Assert.Equal("", new Utf8StreamDecoder().Decode([]));

    [Fact]
    public void FlushOnACleanStreamProducesNothing()
    {
        var decoder = new Utf8StreamDecoder();
        decoder.Decode("done\n"u8);

        Assert.Equal("", decoder.Flush());
    }

    [Fact]
    public void ATruncatedFinalCharacterIsVisibleRatherThanDropped()
    {
        // The stream ended mid-character. A replacement character says something arrived and was
        // unreadable, which is true; silence would say nothing arrived, which is not.
        var decoder = new Utf8StreamDecoder();
        decoder.Decode(Encoding.UTF8.GetBytes("🐳").AsSpan(0, 2));

        Assert.NotEmpty(decoder.Flush());
    }

    [Fact]
    public void ManySmallChunksReassembleExactly()
    {
        // The realistic case: a pty writing a screenful, delivered a few bytes at a time.
        const string original = "┌─ dray ─┐\n│ 🐳 ok │\n└────────┘\n";

        var bytes = Encoding.UTF8.GetBytes(original);
        var decoder = new Utf8StreamDecoder();
        var output = new StringBuilder();

        for (var i = 0; i < bytes.Length; i += 3)
            output.Append(decoder.Decode(bytes.AsSpan(i, Math.Min(3, bytes.Length - i))));

        output.Append(decoder.Flush());

        Assert.Equal(original, output.ToString());
    }
}
