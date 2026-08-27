using System.Formats.Tar;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Dray.Core.Engine;
using Dray.Core.Model;

namespace Dray.Docker;

/// <summary>
/// Browsing and editing a container's filesystem.
/// <para>
/// The Engine API has no directory listing. It offers <c>GET/PUT/HEAD /archive</c> — tar in, tar
/// out — and nothing else, so listing has to be built out of one of two imperfect options:
/// </para>
/// <list type="bullet">
/// <item><b>exec</b> — run <c>ls</c> inside the container. Cheap and complete, but needs the
/// container to be running and to contain an <c>ls</c> binary, which <c>scratch</c> and distroless
/// images do not.</item>
/// <item><b>archive</b> — read a tar of the directory and use only its headers. Works on any image
/// and on stopped containers, but the engine streams the directory's real contents to build it, so
/// listing a directory with a 2 GB file transfers 2 GB.</item>
/// </list>
/// <para>
/// Dray tries exec and falls back, which means the common case is fast and the awkward case still
/// works. The listing records which path it took so the UI can explain a slow or partial one.
/// </para>
/// </summary>
internal static class DockerFileSystem
{
    /// <summary>
    /// How much of a tar Dray will read to list one directory before giving up.
    /// <para>
    /// Without a budget, listing <c>/var/lib</c> on a database container would try to stream the
    /// entire data directory. The listing is returned truncated with a note rather than either
    /// hanging or silently lying about being complete.
    /// </para>
    /// </summary>
    const long ArchiveListingBudgetBytes = 32 * 1024 * 1024;

    /// <summary>A single file Dray will open in the editor. Beyond this it is not a text file.</summary>
    public const long MaxEditableBytes = 4 * 1024 * 1024;

    public static async Task<DirectoryListing> ListAsync(
        DockerClient client,
        string containerId,
        string path,
        bool containerIsRunning,
        CancellationToken ct)
    {
        path = FileEntry.Normalize(path);

        // exec needs a running container, so a stopped one goes straight to the archive path —
        // which is exactly the case that makes the fallback worth having.
        if (containerIsRunning)
        {
            var viaExec = await TryListViaExecAsync(client, containerId, path, ct).ConfigureAwait(false);
            if (viaExec is not null) return viaExec;
        }

        return await ListViaArchiveAsync(client, containerId, path, containerIsRunning, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- exec

    static async Task<DirectoryListing?> TryListViaExecAsync(
        DockerClient client,
        string containerId,
        string path,
        CancellationToken ct)
    {
        try
        {
            // Argv directly rather than `sh -c`: no quoting to get wrong, and it works in an image
            // that has ls but no shell. `--` stops a path beginning with a dash being read as a flag.
            var exec = await client.Exec.CreateContainerExecAsync(
                containerId,
                new ContainerExecCreateParameters
                {
                    AttachStdout = true,
                    AttachStderr = true,
                    Cmd = ["ls", "-la", "--", path],
                },
                ct).ConfigureAwait(false);

            using var stream = await client.Exec
                .StartContainerExecAsync(exec.ID, new ContainerExecStartParameters { Detach = false, TTY = false }, ct)
                .ConfigureAwait(false);

            var (stdout, _) = await stream.ReadOutputToEndAsync(ct).ConfigureAwait(false);

            var inspect = await client.Exec.InspectContainerExecAsync(exec.ID, ct).ConfigureAwait(false);
            if (inspect.ExitCode != 0)
            {
                // A non-zero exit is a real answer, not a transport failure: the path may not
                // exist, or there may be no `ls`. Either way the archive path can still try.
                return null;
            }

            var entries = LsParser.Parse(stdout, path);

            // `ls` succeeded but produced nothing parseable — more likely a format Dray does not
            // understand than a genuinely empty directory, and the archive path will settle it.
            if (entries.Count == 0 && !string.IsNullOrWhiteSpace(stdout) && stdout.Contains('\n'))
                return null;

            return new DirectoryListing(path, entries, ListingMethod.Exec);
        }
        catch (Exception ex) when (ex is DockerApiException or IOException or InvalidOperationException)
        {
            // No shell, exec disabled, container not running — all recoverable by falling back.
            return null;
        }
    }

    // ---------------------------------------------------------------- archive

    static async Task<DirectoryListing> ListViaArchiveAsync(
        DockerClient client,
        string containerId,
        string path,
        bool containerIsRunning,
        CancellationToken ct)
    {
        var response = await client.Containers
            .GetArchiveFromContainerAsync(containerId, new ContainerPathStatParameters { Path = path }, statOnly: false, ct)
            .ConfigureAwait(false);

        if (response.Stream is null)
            return new DirectoryListing(path, [], ListingMethod.Archive, "The engine returned nothing for that path.");

        await using var counting = new CountingStream(response.Stream);
        await using var tar = new TarReader(counting, leaveOpen: true);

        var entries = new List<FileEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;

        // The tar is rooted at the requested directory's own name, so entries look like
        // "etc/hosts" for a request of "/etc". Only the immediate children are wanted.
        var rootName = path == "/" ? string.Empty : path[(path.LastIndexOf('/') + 1)..];

        while (await tar.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
        {
            if (counting.BytesRead > ArchiveListingBudgetBytes)
            {
                truncated = true;
                break;
            }

            var relative = Relative(entry.Name, rootName);
            if (relative is null) continue;

            // Depth 1 only: the tar is recursive and everything below is a different directory.
            if (relative.Contains('/', StringComparison.Ordinal)) continue;
            if (relative.Length == 0 || !seen.Add(relative)) continue;

            entries.Add(new FileEntry(
                relative,
                FileEntry.Combine(path, relative),
                IsDirectory: entry.EntryType is TarEntryType.Directory,
                Size: entry.Length,
                Modified: entry.ModificationTime,
                Mode: FormatMode(entry),
                LinkTarget: entry.EntryType is TarEntryType.SymbolicLink ? entry.LinkName : null));
        }

        var note = truncated
            ? "This listing stopped early. The engine has no directory listing, so Dray reads a tar of the folder, and this one is larger than the budget."
            : containerIsRunning
                ? "Listed by reading a tar of the folder, because this image has no usable ls."
                : "Listed by reading a tar of the folder, because the container is not running.";

        return new DirectoryListing(path, entries, ListingMethod.Archive, truncated ? note : note);
    }

    /// <summary>Strip the tar's root component to get a path relative to the requested directory.</summary>
    static string? Relative(string entryName, string rootName)
    {
        var name = entryName.Replace('\\', '/').TrimEnd('/');

        if (rootName.Length == 0) return name.TrimStart('/');

        if (!name.StartsWith(rootName, StringComparison.Ordinal)) return null;
        if (name.Length == rootName.Length) return string.Empty;

        return name[rootName.Length] == '/' ? name[(rootName.Length + 1)..] : null;
    }

    static string FormatMode(TarEntry entry)
    {
        var type = entry.EntryType switch
        {
            TarEntryType.Directory => 'd',
            TarEntryType.SymbolicLink => 'l',
            TarEntryType.CharacterDevice => 'c',
            TarEntryType.BlockDevice => 'b',
            TarEntryType.Fifo => 'p',
            _ => '-',
        };

        var mode = (int)entry.Mode;
        var builder = new StringBuilder(10).Append(type);

        foreach (var shift in new[] { 6, 3, 0 })
        {
            var bits = (mode >> shift) & 0b111;
            builder.Append((bits & 0b100) != 0 ? 'r' : '-');
            builder.Append((bits & 0b010) != 0 ? 'w' : '-');
            builder.Append((bits & 0b001) != 0 ? 'x' : '-');
        }

        return builder.ToString();
    }

    // ---------------------------------------------------------------- read and write

    /// <summary>
    /// Read one file's bytes.
    /// <para>
    /// Uses the archive endpoint, which works on stopped containers and on images with no shell —
    /// so reading a file never depends on the container being able to run anything.
    /// </para>
    /// </summary>
    public static async Task<byte[]> ReadFileAsync(
        DockerClient client,
        string containerId,
        string path,
        CancellationToken ct)
    {
        path = FileEntry.Normalize(path);

        var response = await client.Containers
            .GetArchiveFromContainerAsync(containerId, new ContainerPathStatParameters { Path = path }, statOnly: false, ct)
            .ConfigureAwait(false);

        if (response.Stream is null) throw new RuntimeConnectionException("That file could not be read from the container.");

        await using var stream = response.Stream;
        await using var tar = new TarReader(stream, leaveOpen: true);

        // copyData: true is required here. The engine's response is an unseekable chunked HTTP
        // stream, and without the copy the entry's DataStream reads past the end of it. Listing
        // can stay on `false` because it only ever touches headers — this is the one place the
        // bytes are actually wanted.
        while (await tar.GetNextEntryAsync(copyData: true, ct).ConfigureAwait(false) is { } entry)
        {
            if (entry.EntryType is TarEntryType.Directory) continue;
            if (entry.DataStream is not { } data) continue;

            if (entry.Length > MaxEditableBytes)
                throw new RuntimeConnectionException($"That file is {entry.Length / 1_000_000.0:0.#} MB, which is too large to open here.");

            using var buffer = new MemoryStream();
            await data.CopyToAsync(buffer, ct).ConfigureAwait(false);
            return buffer.ToArray();
        }

        throw new RuntimeConnectionException("That file could not be read from the container.");
    }

    /// <summary>
    /// Write one file back.
    /// <para>
    /// The engine extracts a tar into a directory, so the content is wrapped in a one-entry tar
    /// named for the file and extracted into its parent. Like reading, this works on a stopped
    /// container.
    /// </para>
    /// </summary>
    public static async Task WriteFileAsync(
        DockerClient client,
        string containerId,
        string path,
        byte[] content,
        CancellationToken ct)
    {
        path = FileEntry.Normalize(path);

        var parent = FileEntry.ParentOf(path) ?? "/";
        var name = path[(path.LastIndexOf('/') + 1)..];

        // Preserve the existing mode rather than inventing one: writing back a file that was
        // executable must not quietly make it non-executable.
        var mode = await TryGetModeAsync(client, containerId, path, ct).ConfigureAwait(false);

        using var buffer = new MemoryStream();

        await using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(content),
                ModificationTime = DateTimeOffset.UtcNow,
            };

            if (mode is { } m) entry.Mode = m;

            await writer.WriteEntryAsync(entry, ct).ConfigureAwait(false);
        }

        buffer.Position = 0;

        await client.Containers
            .ExtractArchiveToContainerAsync(containerId, new CopyToContainerParameters { Path = parent }, buffer, ct)
            .ConfigureAwait(false);
    }

    static async Task<UnixFileMode?> TryGetModeAsync(
        DockerClient client,
        string containerId,
        string path,
        CancellationToken ct)
    {
        try
        {
            var response = await client.Containers
                .GetArchiveFromContainerAsync(containerId, new ContainerPathStatParameters { Path = path }, statOnly: false, ct)
                .ConfigureAwait(false);

            if (response.Stream is null) return null;

            await using var stream = response.Stream;
            await using var tar = new TarReader(stream, leaveOpen: true);

            if (await tar.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry) return entry.Mode;
        }
        catch (Exception ex) when (ex is DockerApiException or IOException)
        {
            // A file that does not exist yet has no mode to preserve.
        }

        return null;
    }

    /// <summary>Counts bytes pulled from the engine so a listing can stop at its budget.</summary>
    sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            BytesRead += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
