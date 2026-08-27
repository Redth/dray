using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Every fixture here is real output captured from a container, not invented. The format is not
/// standardised — GNU coreutils, BusyBox and Toybox differ in their date columns, in whether they
/// append an SELinux dot to the mode, and in their column padding — and Dray has no say in which
/// one an image ships.
/// </summary>
public class LsParserTests
{
    // Captured from `podman run --rm alpine ls -la /etc`.
    const string BusyBox = """
        total 88
        drwxr-xr-x    1 root     root            25 Aug 27 17:15 .
        dr-xr-xr-x    1 root     root            28 Aug 27 17:15 ..
        -rw-r--r--    1 root     root             7 Jun 13 15:17 alpine-release
        drwxr-xr-x    4 root     root            88 Jun 13 16:39 apk
        -rw-r--r--    1 root     root            89 Mar 24 19:40 fstab
        """;

    // Captured from `podman run --rm debian:stable-slim ls -la /etc`. Note the SELinux dot.
    const string Gnu = """
        total 132
        -rw-r--r--. 1 root root    118 Aug 24 00:00 shells
        drwxr-xr-x. 2 root root     57 Aug 24 00:00 skel
        drwxr-xr-x. 4 root root     32 Aug 20  2025 systemd
        -rw-r--r--. 1 root root    681 Feb 21  2025 xattr.conf
        """;

    [Fact]
    public void ParsesBusyBoxOutput()
    {
        var entries = LsParser.Parse(BusyBox, "/etc");

        Assert.Equal(["alpine-release", "apk", "fstab"], entries.Select(e => e.Name));
        Assert.True(entries.Single(e => e.Name == "apk").IsDirectory);
        Assert.Equal(89, entries.Single(e => e.Name == "fstab").Size);
    }

    [Fact]
    public void ParsesGnuOutputIncludingTheSelinuxDot()
    {
        // The mode is 11 characters here, not 10, which a strict length check would reject.
        var entries = LsParser.Parse(Gnu, "/etc");

        Assert.Equal(["shells", "skel", "systemd", "xattr.conf"], entries.Select(e => e.Name));
        Assert.Equal(118, entries.Single(e => e.Name == "shells").Size);
        Assert.True(entries.Single(e => e.Name == "systemd").IsDirectory);
    }

    [Fact]
    public void HandlesAYearWhereTheTimeWouldBe()
    {
        // A file older than six months shows "Aug 20  2025" rather than "Aug 20 17:15", with the
        // extra space. Counting date columns would work; assuming a time would not.
        var entries = LsParser.Parse(Gnu, "/etc");

        Assert.Equal("xattr.conf", entries.Last().Name);
        Assert.Equal(681, entries.Last().Size);
    }

    [Fact]
    public void DotAndDotDotAreDropped()
    {
        // Present in every listing and never worth showing.
        Assert.DoesNotContain(LsParser.Parse(BusyBox, "/etc"), e => e.Name is "." or "..");
    }

    [Fact]
    public void TotalHeaderIsIgnored()
        => Assert.DoesNotContain(LsParser.Parse(BusyBox, "/etc"), e => e.Name.StartsWith("total", StringComparison.Ordinal));

    // ---------------------------------------------------------------- awkward names

    [Fact]
    public void SymlinksCarryTheirTarget()
    {
        // Captured from alpine /bin.
        const string output = "lrwxrwxrwx    1 root     root            12 Jun 13 16:39 arch -> /bin/busybox";

        var entry = Assert.Single(LsParser.Parse(output, "/bin"));

        Assert.Equal("arch", entry.Name);
        Assert.True(entry.IsSymlink);
        Assert.Equal("/bin/busybox", entry.LinkTarget);
    }

    [Fact]
    public void NamesWithSpacesSurviveIntact()
    {
        // The name is taken as the rest of the line rather than a single token, because splitting
        // on whitespace would truncate this to "my".
        const string output = "-rw-r--r--. 1 root root  0 Aug 27 17:15 my file.txt";

        Assert.Equal("my file.txt", Assert.Single(LsParser.Parse(output, "/t")).Name);
    }

    [Fact]
    public void PathsAreBuiltFromTheContainingDirectory()
    {
        var entry = Assert.Single(LsParser.Parse("-rw-r--r-- 1 root root 7 Jun 13 15:17 hosts", "/etc"));
        Assert.Equal("/etc/hosts", entry.Path);
    }

    [Fact]
    public void PathsAtTheRootDoNotDoubleTheSlash()
    {
        var entry = Assert.Single(LsParser.Parse("drwxr-xr-x 2 root root 4096 Jun 13 16:39 bin", "/"));
        Assert.Equal("/bin", entry.Path);
    }

    // ---------------------------------------------------------------- robustness

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("ls: /nope: No such file or directory")]
    [InlineData("-rw-r--r--")]
    [InlineData("zrw-r--r-- 1 root root 0 Aug 27 17:15 weird-type")]
    public void UnparseableLinesAreSkippedRatherThanThrowing(string line)
    {
        // One odd entry must not cost the user the other ninety-nine.
        Assert.Empty(LsParser.Parse(line, "/"));
    }

    [Fact]
    public void OneBadLineDoesNotLoseTheGoodOnes()
    {
        var output = "total 4\ngarbage line here\n-rw-r--r-- 1 root root 7 Jun 13 15:17 good\n";

        Assert.Equal("good", Assert.Single(LsParser.Parse(output, "/etc")).Name);
    }

    [Fact]
    public void CarriageReturnsFromAnyExecTtyAreTolerated()
    {
        var output = "-rw-r--r-- 1 root root 7 Jun 13 15:17 hosts\r\n";
        Assert.Equal("hosts", Assert.Single(LsParser.Parse(output, "/etc")).Name);
    }

    // ---------------------------------------------------------------- path helpers

    [Theory]
    [InlineData("/etc/nginx", "/etc")]
    [InlineData("/etc", "/")]
    [InlineData("/", null)]
    public void ParentOfWalksUpToTheRoot(string path, string? expected)
        => Assert.Equal(expected, FileEntry.ParentOf(path));

    [Theory]
    [InlineData("", "/")]
    [InlineData("etc", "/etc")]
    [InlineData("//etc//nginx//", "/etc/nginx")]
    [InlineData("/etc/", "/etc")]
    public void PathsAreNormalisedToPosixAbsolute(string input, string expected)
        => Assert.Equal(expected, FileEntry.Normalize(input));

    [Fact]
    public void NormalizeUsesForwardSlashesEvenOnWindows()
    {
        // Container paths are POSIX whatever the host running Dray. System.IO.Path would produce
        // backslashes here and break every lookup.
        Assert.Equal("/etc/nginx", FileEntry.Normalize(@"\etc\nginx"));
    }

    [Fact]
    public void ListingSortsDirectoriesFirstThenAlphabetically()
    {
        var listing = new DirectoryListing("/", [
            new("zebra", "/zebra", false, 0),
            new("apple", "/apple", false, 0),
            new("mango", "/mango", true, 0),
        ], ListingMethod.Exec);

        Assert.Equal(["mango", "apple", "zebra"], listing.Sorted.Select(e => e.Name));
    }
}
