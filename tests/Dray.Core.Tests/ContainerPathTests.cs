using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Container paths in both shapes.
/// <para>
/// Every case here is one the Windows head cannot be used to check yet, so they are written from
/// the engine's documented behaviour rather than from a running daemon. What they pin down is
/// Dray's own arithmetic — that a Windows path never grows a forward slash and a Linux path never
/// grows a drive letter — which is where the bugs would be.
/// </para>
/// </summary>
public class ContainerPathTests
{
    [Theory]
    [InlineData("/etc/nginx", "/etc/nginx")]
    [InlineData("etc/nginx", "/etc/nginx")]
    [InlineData("//etc///nginx//", "/etc/nginx")]
    [InlineData(@"\etc\nginx", "/etc/nginx")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    public void NormalizesPosixPaths(string input, string expected)
        => Assert.Equal(expected, ContainerPath.Normalize(input, ContainerOs.Linux));

    [Theory]
    [InlineData(@"C:\inetpub", @"C:\inetpub")]
    [InlineData("C:/inetpub", @"C:\inetpub")]
    [InlineData(@"c:\inetpub\", @"C:\inetpub")]
    [InlineData(@"C:\\inetpub\\wwwroot", @"C:\inetpub\wwwroot")]
    [InlineData(@"\inetpub", @"C:\inetpub")]
    [InlineData("/inetpub", @"C:\inetpub")]
    [InlineData("", @"C:\")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("C:", @"C:\")]
    public void NormalizesWindowsPaths(string input, string expected)
        => Assert.Equal(expected, ContainerPath.Normalize(input, ContainerOs.Windows));

    /// <summary>
    /// The drive letter is upper-cased so two spellings of one directory are one cache key: the
    /// file browser keys its listing on the path string, and two spellings being two strings would
    /// fetch one directory twice and render it as two places.
    /// </summary>
    [Fact]
    public void WindowsDriveLetterIsCanonicalised()
        => Assert.Equal(
            ContainerPath.Normalize(@"C:\Windows", ContainerOs.Windows),
            ContainerPath.Normalize(@"c:\Windows", ContainerOs.Windows));

    /// <summary>
    /// Only the drive. Windows is case-insensitive but case-preserving, and a browser that
    /// lower-cased "ProgramData" on the way through would show the user a name that is not what is
    /// on disk.
    /// </summary>
    [Fact]
    public void WindowsPathCaseIsOtherwisePreserved()
        => Assert.Equal(@"C:\ProgramData\Docker", ContainerPath.Normalize("c:/ProgramData/Docker", ContainerOs.Windows));

    [Theory]
    [InlineData("/etc/nginx", "/etc")]
    [InlineData("/etc", "/")]
    [InlineData("/", null)]
    public void FindsPosixParent(string path, string? expected)
        => Assert.Equal(expected, ContainerPath.ParentOf(path, ContainerOs.Linux));

    [Theory]
    [InlineData(@"C:\inetpub\wwwroot", @"C:\inetpub")]
    [InlineData(@"C:\inetpub", @"C:\")]
    [InlineData(@"C:\", null)]
    [InlineData(@"D:\data\logs", @"D:\data")]
    [InlineData(@"D:\data", @"D:\")]
    public void FindsWindowsParent(string path, string? expected)
        => Assert.Equal(expected, ContainerPath.ParentOf(path, ContainerOs.Windows));

    /// <summary>
    /// A non-C drive must keep its own letter on the way up. Cutting to "C:\" would silently move
    /// the user to a different volume, which is the kind of navigation bug that reads as data loss.
    /// </summary>
    [Fact]
    public void WalkingUpFromANonSystemDriveStaysOnThatDrive()
        => Assert.Equal(@"D:\", ContainerPath.ParentOf(@"D:\data", ContainerOs.Windows));

    [Theory]
    [InlineData("/", "etc", "/etc")]
    [InlineData("/etc", "nginx", "/etc/nginx")]
    public void CombinesPosix(string directory, string name, string expected)
        => Assert.Equal(expected, ContainerPath.Combine(directory, name, ContainerOs.Linux));

    [Theory]
    [InlineData(@"C:\", "inetpub", @"C:\inetpub")]
    [InlineData(@"C:\inetpub", "wwwroot", @"C:\inetpub\wwwroot")]
    public void CombinesWindows(string directory, string name, string expected)
        => Assert.Equal(expected, ContainerPath.Combine(directory, name, ContainerOs.Windows));

    /// <summary>
    /// The root already ends in its separator and must not gain a second one — the one place the
    /// two shapes genuinely differ, since "/" + "etc" is right and "C:\\" + "\\inetpub" is not.
    /// </summary>
    [Fact]
    public void CombiningAtTheRootDoesNotDoubleTheSeparator()
    {
        Assert.Equal("/etc", ContainerPath.Combine("/", "etc", ContainerOs.Linux));
        Assert.Equal(@"C:\inetpub", ContainerPath.Combine(@"C:\", "inetpub", ContainerOs.Windows));
    }

    [Fact]
    public void WalkingDownAndBackUpReturnsWhereItStarted()
    {
        foreach (var os in new[] { ContainerOs.Linux, ContainerOs.Windows })
        {
            var root = ContainerPath.Root(os);
            var child = ContainerPath.Combine(root, "app", os);
            var grandchild = ContainerPath.Combine(child, "logs", os);

            Assert.Equal(child, ContainerPath.ParentOf(grandchild, os));
            Assert.Equal(root, ContainerPath.ParentOf(child, os));
            Assert.Null(ContainerPath.ParentOf(root, os));
        }
    }

    [Theory]
    [InlineData("/etc", ContainerOs.Linux, true)]
    [InlineData("etc", ContainerOs.Linux, false)]
    [InlineData(@"C:\etc", ContainerOs.Windows, true)]
    [InlineData("C:/etc", ContainerOs.Windows, true)]
    [InlineData(@"C:", ContainerOs.Windows, false)]
    [InlineData(@"\etc", ContainerOs.Windows, false)]
    [InlineData("", ContainerOs.Linux, false)]
    public void RecognisesAnAbsolutePath(string path, ContainerOs os, bool expected)
        => Assert.Equal(expected, ContainerPath.IsAbsolute(path, os));

    /// <summary>
    /// A Windows path must never come back with a forward slash in it, and a POSIX one must never
    /// come back with a backslash. Everything downstream — the engine's archive endpoint, the
    /// crumb split, the parent cut — assumes exactly one separator.
    /// </summary>
    [Fact]
    public void NormalisedPathsCarryOnlyTheirOwnSeparator()
    {
        var mixed = @"C:/inetpub\wwwroot/logs";

        Assert.DoesNotContain('/', ContainerPath.Normalize(mixed, ContainerOs.Windows));
        Assert.DoesNotContain('\\', ContainerPath.Normalize("/var\\log/nginx", ContainerOs.Linux));
    }
}
