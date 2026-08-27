using System.Text;
using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Opening a file out of a container and writing it back. The rule throughout: saving a file
/// nobody edited must produce the bytes that were already there.
/// </summary>
public class FileContentTests
{
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void ATextFileDecodes()
    {
        var file = FileContent.Decode("/etc/app.conf", Utf8("mode = production\n"));

        Assert.True(file.CanEdit);
        Assert.Equal("mode = production\n", file.Text);
    }

    [Fact]
    public void ARoundTripWithNoEditIsByteIdentical()
    {
        var original = Utf8("listen 8080;\nroot /srv;\n");
        var file = FileContent.Decode("/etc/nginx.conf", original);

        Assert.Equal(original, file.Encode(file.Text));
    }

    [Fact]
    public void CrlfSurvivesTheRoundTrip()
    {
        // The editor normalises to LF internally. Writing LF back into a file that used CRLF would
        // rewrite every line and turn a one-value change into a whole-file diff.
        var original = Utf8("a=1\r\nb=2\r\n");
        var file = FileContent.Decode("/app/.env", original);

        Assert.Equal("a=1\nb=2\n", file.Text);
        Assert.Equal(original, file.Encode(file.Text));
    }

    [Fact]
    public void AFileWithNoTrailingNewlineDoesNotGainOne()
    {
        var original = Utf8("no newline at the end");
        var file = FileContent.Decode("/tmp/x", original);

        Assert.False(file.HadTrailingNewline);
        Assert.Equal(original, file.Encode(file.Text));
    }

    [Fact]
    public void AFileWithATrailingNewlineKeepsExactlyOne()
    {
        var file = FileContent.Decode("/tmp/x", Utf8("line\n"));

        // The editor may hand back text without the final newline; the file's own convention wins.
        Assert.Equal(Utf8("line\n"), file.Encode("line"));
    }

    [Fact]
    public void ABomIsStrippedForDisplay()
    {
        var file = FileContent.Decode("/tmp/x", Utf8("﻿hello\n"));

        Assert.Equal("hello\n", file.Text);
    }

    // ---------------------------------------------------------------- refusals

    [Fact]
    public void ABinaryFileIsRefusedRatherThanShownAsMojibake()
    {
        var file = FileContent.Decode("/bin/sh", [0x7f, 0x45, 0x4c, 0x46, 0x02, 0x00, 0x00]);

        Assert.False(file.CanEdit);
        Assert.Equal(FileOpenRefusal.Binary, file.Refusal);
    }

    [Fact]
    public void AFileOverTheLimitIsRefused()
    {
        var file = FileContent.Decode("/var/lib/db", new byte[FileContent.MaxEditableBytes + 1]);

        Assert.Equal(FileOpenRefusal.TooLarge, file.Refusal);
    }

    [Fact]
    public void SizeIsCheckedBeforeContent()
    {
        // A huge file is also full of NULs; reporting it as binary would tell the user the wrong
        // thing about why it will not open.
        var file = FileContent.Decode("/var/lib/db", new byte[FileContent.MaxEditableBytes + 1]);

        Assert.NotEqual(FileOpenRefusal.Binary, file.Refusal);
    }

    [Fact]
    public void AnEmptyFileIsEditableRatherThanRefused()
        => Assert.True(FileContent.Decode("/tmp/empty", []).CanEdit);

    [Fact]
    public void TextContainingHighUnicodeIsNotBinary()
        => Assert.True(FileContent.Decode("/tmp/x", Utf8("héllo · wörld ✓\n")).CanEdit);

    // ---------------------------------------------------------------- language

    [Theory]
    [InlineData("/app/config.json", "json")]
    [InlineData("/app/docker-compose.yml", "yaml")]
    [InlineData("/etc/nginx/nginx.conf", "ini")]
    [InlineData("/app/Dockerfile", "dockerfile")]
    [InlineData("/app/.env", "ini")]
    [InlineData("/app/.env.production", "ini")]
    [InlineData("/entrypoint.sh", "shell")]
    [InlineData("/etc/hosts", "plaintext")]
    [InlineData("/README.md", "markdown")]
    [InlineData("/app/main.go", "go")]
    public void LanguageIsPickedFromTheNameOrExtension(string path, string expected)
        => Assert.Equal(expected, FileContent.LanguageFor(path));

    [Fact]
    public void AnUnknownExtensionFallsBackToPlainText()
        => Assert.Equal("plaintext", FileContent.LanguageFor("/tmp/thing.qqq"));

    [Fact]
    public void AFileWithNoExtensionIsPlainText()
        => Assert.Equal("plaintext", FileContent.LanguageFor("/usr/bin/whatever"));
}
