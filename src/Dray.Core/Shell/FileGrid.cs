using Dray.Core.Model;

namespace Dray.Core.Shell;

/// <summary>
/// A directory listing's columns.
/// <para>
/// The same component as every other list in the app, which is the answer to wanting sorting here:
/// there is no general-purpose file browser worth vendoring — the ones that exist are jQuery file
/// managers expecting their own server protocol, or React components — and a listing is a table.
/// </para>
/// </summary>
public static class FileGrid
{
    public const string KeyField = "__key";

    /// <summary>
    /// Directories before files, whatever column is sorted.
    /// <para>
    /// Sorting by size with folders scattered through the result is not what anyone means by
    /// sorting by size — a folder has no size to compare. So the grouping is not a sort the user
    /// chose; it survives the one they did.
    /// </para>
    /// </summary>
    public const string GroupField = "__group";

    public static IReadOnlyList<GridColumn> Columns() =>
    [
        new("name", "Name", GridCell.Entry, Priority: 1, MinWidth: 220),

        new("size", "Size", GridCell.Bytes, Priority: 2, Numeric: true, MinWidth: 90),

        // Reference material rather than something scanned, so it is the first to go.
        new("mode", "Mode", GridCell.Mono, Priority: 5, MinWidth: 110),

        new("modified", "Modified", GridCell.Since, Priority: 3, Numeric: true, MinWidth: 110),
    ];

    public static IReadOnlyDictionary<string, object?> Row(FileEntry entry, DateTimeOffset? now = null) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [KeyField] = entry.Path,

            // Directories first. Not a column, so nothing sorts by it directly.
            [GroupField] = entry.IsDirectory ? 0 : 1,

            ["name"] = new GridEntry(
                entry.Name,
                entry.IsDirectory ? IconRef.Folder : IconRef.File,

                // The one thing a name does not say. A symlink pointing somewhere that no longer
                // exists looks exactly like one that works, and the target is the only clue.
                entry.LinkTarget is { Length: > 0 } target ? $"→ {target}" : null),

            // A directory's size is the size of its own inode, which is not what anyone reading a
            // file list means by size — so it is left blank rather than reported.
            ["size"] = GridValue.Bytes(
                entry.IsDirectory ? null : entry.Size,
                entry.IsDirectory ? "—" : Humanize.Bytes(entry.Size)),

            ["mode"] = entry.Mode ?? "—",
            ["modified"] = GridValue.When(
                entry.Modified,
                entry.Modified is { } modified ? Humanize.Since(modified, now) : "—"),
        };
}
