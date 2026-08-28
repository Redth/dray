using Dray.Core.Model;
using Dray.Core.Shell;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// A directory listing's columns.
/// </summary>
public class FileGridTests
{
    static FileEntry Dir(string name) => new(name, $"/{name}", IsDirectory: true, Size: 4096);

    static FileEntry File(string name, long size = 1234, string? link = null) =>
        new(name, $"/{name}", IsDirectory: false, size, Mode: "-rw-r--r--", LinkTarget: link);

    [Fact]
    public void DirectoriesGroupBeforeFiles()
    {
        // Not a column, so nothing sorts by it directly — it survives whatever sort the user chose.
        Assert.Equal(0, FileGrid.Row(Dir("etc"))[FileGrid.GroupField]);
        Assert.Equal(1, FileGrid.Row(File("hosts"))[FileGrid.GroupField]);
    }

    [Fact]
    public void ADirectoryReportsNoSize()
    {
        // A directory's size is the size of its own inode, which is not what anyone reading a file
        // list means by size. Nothing to sort by either, so it sorts last rather than as zero.
        var size = Assert.IsType<GridValue>(FileGrid.Row(Dir("etc"))["size"]);

        Assert.Equal("—", size.Display);
        Assert.Null(size.Sort);
    }

    [Fact]
    public void AFileSortsByItsBytesAndShowsItsWords()
    {
        // The bug this exists to stop: "702 B", "746 B" and "89 B" sort in that order as text.
        var size = Assert.IsType<GridValue>(FileGrid.Row(File("hosts", 1234))["size"]);

        Assert.Equal("1.2 KB", size.Display);
        Assert.Equal(1234L, size.Sort);
    }

    [Fact]
    public void BiggerFilesSortAfterSmallerOnes()
    {
        var small = FileGrid.Row(File("fstab", 89))["size"];
        var large = FileGrid.Row(File("passwd", 746))["size"];

        Assert.True(GridSort.Compare(small, large) < 0);
    }

    [Fact]
    public void TheIconSaysWhichItIs()
    {
        // Faster to read than the name or the mode, which is the whole reason the column has one.
        Assert.Equal(IconRef.Folder, Assert.IsType<GridEntry>(FileGrid.Row(Dir("etc"))["name"]).Icon);
        Assert.Equal(IconRef.File, Assert.IsType<GridEntry>(FileGrid.Row(File("hosts"))["name"]).Icon);
    }

    [Fact]
    public void ASymlinkShowsWhereItPoints()
    {
        // A symlink to something that no longer exists looks exactly like one that works, and the
        // target is the only clue in the row.
        var name = Assert.IsType<GridEntry>(FileGrid.Row(File("sh", link: "/bin/busybox"))["name"]);

        Assert.Equal("→ /bin/busybox", name.Note);
    }

    [Fact]
    public void APlainFileHasNothingExtraToSay()
        => Assert.Null(Assert.IsType<GridEntry>(FileGrid.Row(File("hosts"))["name"]).Note);

    [Fact]
    public void TheRowIsKeyedByPathBecauseNamesRepeatAcrossDirectories()
        => Assert.Equal("/etc", FileGrid.Row(Dir("etc"))[FileGrid.KeyField]);

    [Fact]
    public void ModeIsTheFirstColumnToGo()
    {
        // Reference material rather than something scanned.
        var columns = FileGrid.Columns();

        var mode = columns.Single(c => c.Field == "mode").Priority;
        var name = columns.Single(c => c.Field == "name").Priority;

        Assert.True(mode > name);
    }

    [Fact]
    public void EveryColumnHasAValueInEveryRow()
    {
        var row = FileGrid.Row(File("hosts"));

        foreach (var column in FileGrid.Columns())
            Assert.True(row.ContainsKey(column.Field), $"row has no value for '{column.Field}'");
    }
}
