namespace Dray.Core.Engine;

/// <summary>
/// A stack's files, wherever they are. On this machine for a stack whose files are here; on the
/// engine's host, over SSH, for one whose files are there — the editor, the .env panel and the
/// graph read and write through this and do not know which.
/// </summary>
public interface IStackFiles
{
    /// <summary>True when the files are on another machine.</summary>
    bool IsRemote { get; }

    /// <summary>Where the files are, for saying so: "this Mac", "nas".</summary>
    string Where { get; }

    /// <summary>The file's bytes, or null when it is not there.</summary>
    Task<byte[]?> ReadAsync(string path, CancellationToken ct = default);

    /// <summary>Write the file. Null on success, or a sentence explaining why not.</summary>
    Task<string?> WriteAsync(string path, byte[] content, CancellationToken ct = default);

    Task<bool> CanWriteAsync(string path, CancellationToken ct = default);
}

public sealed class LocalStackFiles : IStackFiles
{
    public static readonly LocalStackFiles Instance = new();

    public bool IsRemote => false;

    public string Where => "this machine";

    public async Task<byte[]?> ReadAsync(string path, CancellationToken ct = default)
        => File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null;

    public async Task<string?> WriteAsync(string path, byte[] content, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, content, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    public Task<bool> CanWriteAsync(string path, CancellationToken ct = default)
        => Task.FromResult(!File.Exists(path) || !new FileInfo(path).IsReadOnly);
}

/// <summary>
/// Files on an SSH host, through <c>cat</c>.
/// <para>
/// Writes go to a temporary file beside the target and are moved over it, so a dropped connection
/// mid-write leaves the old file rather than half of the new one — the same rule the registry
/// follows locally. The bytes travel on stdin, never on a command line.
/// </para>
/// </summary>
public sealed class SshStackFiles(SshShell shell) : IStackFiles
{
    public bool IsRemote => true;

    public string Where => shell.Endpoint.Host ?? shell.Target;

    public async Task<byte[]?> ReadAsync(string path, CancellationToken ct = default)
    {
        var q = SshShell.Quote(path);
        var result = await shell.RunAsync($"test -f {q} && cat {q}", ct).ConfigureAwait(false);

        return result.ExitCode == 0 ? System.Text.Encoding.UTF8.GetBytes(result.StandardOutput) : null;
    }

    public async Task<string?> WriteAsync(string path, byte[] content, CancellationToken ct = default)
    {
        var q = SshShell.Quote(path);
        var temporary = SshShell.Quote(path + ".dray-tmp");

        var result = await shell
            .RunWithBytesAsync($"mkdir -p \"$(dirname {q})\" && cat > {temporary} && mv {temporary} {q}", content, ct)
            .ConfigureAwait(false);

        return result.ExitCode == 0
            ? null
            : (result.StandardError.Trim() is { Length: > 0 } error ? error : $"Writing {path} on {Where} failed.");
    }

    public async Task<bool> CanWriteAsync(string path, CancellationToken ct = default)
    {
        var q = SshShell.Quote(path);
        var result = await shell.RunAsync($"test ! -e {q} -o -w {q}", ct).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    /// <summary>Whether every one of <paramref name="paths"/> is a file on the host, in one round trip.</summary>
    public async Task<bool> AllExistAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) return false;

        var result = await shell.RunAsync(string.Join(" && ", paths.Select(p => $"test -f {SshShell.Quote(p)}")), ct).ConfigureAwait(false);
        return result.ExitCode == 0;
    }
}

/// <summary>
/// Files inside a WSL distribution, opened from Windows through <c>\\wsl.localhost</c>.
/// <para>
/// The paths stay the distribution's own — compose runs in there and reads them as recorded — and
/// are translated only at the moment this machine opens one (<see cref="EnginePaths"/>). Measured
/// on Windows: opened untranslated, every WSL stack's compose file read as missing.
/// </para>
/// </summary>
public sealed class WslStackFiles(DockerEndpoint endpoint) : IStackFiles
{
    public bool IsRemote => true;

    public string Where => endpoint.Distro ?? "the distribution";

    string Local(string path) => EnginePaths.ToLocal(endpoint, path);

    public Task<byte[]?> ReadAsync(string path, CancellationToken ct = default) => LocalStackFiles.Instance.ReadAsync(Local(path), ct);

    public Task<string?> WriteAsync(string path, byte[] content, CancellationToken ct = default) => LocalStackFiles.Instance.WriteAsync(Local(path), content, ct);

    public Task<bool> CanWriteAsync(string path, CancellationToken ct = default) => LocalStackFiles.Instance.CanWriteAsync(Local(path), ct);

    /// <summary>Whether every one of <paramref name="paths"/> is there, as Windows reaches it.</summary>
    public bool AllExist(IReadOnlyList<string> paths) => paths.Count > 0 && paths.All(p => File.Exists(Local(p)));
}
