using System.Security.Cryptography;

namespace Dray.Core.Model;

/// <summary>Per service, each mounted file's path and the SHA-256 of its contents.</summary>
public sealed record StackMountSnapshot(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Services)
{
    public static readonly StackMountSnapshot Empty = new(new Dictionary<string, IReadOnlyDictionary<string, string>>());
}

/// <summary>
/// Noticing an edited <c>nginx.conf</c>, which compose does not.
/// <para>
/// Compose's configuration hash covers the compose file, not the files it mounts. Edit a config
/// file a service binds in and compose sees nothing to change, so "only what changed" leaves the
/// container running the old one — the reason Portainer stamps a hash of bind-mounted files on its
/// containers. Dray keeps the hashes at the last deploy it ran and compares, so Deploy can recreate
/// exactly the services whose mounted files moved.
/// </para>
/// <para>
/// Files only, never directories: a directory mounted in is usually data the service writes, and
/// it changing is the service working, not an edit. Files over a megabyte are skipped for the same
/// reason — that is a database, not a config.
/// </para>
/// </summary>
public static class StackMounts
{
    public const long MaxFileBytes = 1024 * 1024;

    /// <summary>Hash the files the stack's services bind-mount, as they are on disk now.</summary>
    /// <param name="files">The stack's compose files, in order; relative paths resolve against the first one's folder.</param>
    public static StackMountSnapshot Snapshot(IReadOnlyList<string> files, string? workingDirectory)
    {
        var services = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var root = workingDirectory ?? (files.Count > 0 ? Path.GetDirectoryName(files[0]) : null);

        if (root is null) return StackMountSnapshot.Empty;

        foreach (var file in files)
        {
            ComposeDocument document;

            try
            {
                document = ComposeDocument.Parse(File.ReadAllText(file));
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var service in document.Services)
            {
                foreach (var volume in document.ItemsOf(service.Name, "volumes"))
                {
                    if (BindSource(volume) is not { } source) continue;

                    var path = Path.GetFullPath(Path.Combine(root, ExpandHome(source)));
                    if (Hash(path) is not { } hash) continue;

                    if (!services.TryGetValue(service.Name, out var hashes)) services[service.Name] = hashes = new(StringComparer.Ordinal);
                    hashes[path] = hash;
                }
            }
        }

        return new StackMountSnapshot(services.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)kv.Value, StringComparer.Ordinal));
    }

    /// <summary>
    /// The services with a mounted file that differs from <paramref name="deployed"/> — added,
    /// edited or gone. A service the earlier snapshot has no entry for has nothing to compare with
    /// and is not reported: unknown is not changed.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Changed(StackMountSnapshot now, StackMountSnapshot? deployed)
    {
        var changed = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (deployed is null) return changed;

        foreach (var (service, then) in deployed.Services)
        {
            var current = now.Services.GetValueOrDefault(service) ?? new Dictionary<string, string>();

            var files = then.Keys.Union(current.Keys)
                .Where(path => then.GetValueOrDefault(path) != current.GetValueOrDefault(path))
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToList();

            if (files.Count > 0) changed[service] = files;
        }

        return changed;
    }

    /// <summary>The host path a volume entry binds, or null for a named volume.</summary>
    static string? BindSource(YamlItem item)
    {
        var source = item.Value is { } text
            ? (text.IndexOf(':') is var colon and > 0 ? text[..colon] : null)
            : item.Fields.GetValueOrDefault("type") is null or "bind" ? item.Fields.GetValueOrDefault("source") : null;

        // A named volume has no slash and does not start with a dot or a tilde.
        return source is null || (!source.Contains('/') && !source.StartsWith('.') && !source.StartsWith('~')) ? null : source;
    }

    static string ExpandHome(string path)
        => path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;

    static string? Hash(string path)
    {
        try
        {
            var info = new FileInfo(path);

            // Regular files only: not a directory, not a socket like docker.sock, not anything huge.
            if (!info.Exists || info.Length > MaxFileBytes || info.Name.EndsWith(".sock", StringComparison.Ordinal)) return null;
            if ((info.Attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0) return null;

            using var stream = info.OpenRead();
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
