using System.Text.RegularExpressions;

namespace Dray.Core.Engine;

/// <summary>
/// Where a path an engine recorded can be opened from this machine.
/// <para>
/// Compose writes the paths of a stack's files onto its containers as the engine's side saw them.
/// For an engine inside a WSL distribution those are Linux paths — <c>/home/jon/app/compose.yaml</c>
/// — which mean nothing to Windows; opened as they are, the Stacks page reported every WSL stack's
/// compose file as missing. WSL publishes every distribution's filesystem at
/// <c>\\wsl.localhost\&lt;distro&gt;</c>, so the file is reachable; only the path needs translating.
/// </para>
/// </summary>
public static partial class EnginePaths
{
    /// <summary>A Windows drive as a distribution mounts it: <c>/mnt/c/...</c>.</summary>
    [GeneratedRegex("^/mnt/([a-zA-Z])(/.*)?$")]
    private static partial Regex MountedDrive();

    /// <summary>
    /// <paramref name="path"/> as this machine reaches it. Unchanged for every engine whose paths
    /// are already this machine's.
    /// </summary>
    public static string ToLocal(DockerEndpoint? endpoint, string path)
    {
        if (endpoint is not { Scheme: EndpointScheme.Wsl, Distro: { } distro } || !path.StartsWith('/')) return path;

        // A project on the Windows side, mounted into the distribution: straight back to the drive
        // rather than round through the distribution's view of it.
        if (MountedDrive().Match(path) is { Success: true } drive)
        {
            var rest = drive.Groups[2].Value.Replace('/', '\\');
            return $"{char.ToUpperInvariant(drive.Groups[1].Value[0])}:{(rest.Length == 0 ? "\\" : rest)}";
        }

        return $@"\\wsl.localhost\{distro}{path.Replace('/', '\\')}";
    }

    /// <summary>
    /// The directory holding <paramref name="path"/>, taken in the engine's own terms before it is
    /// translated. <c>Path.GetDirectoryName</c> on a Linux path, run on Windows, turns the slashes
    /// round and yields <c>\home\jon</c> — a path on whichever drive Dray happens to be on.
    /// </summary>
    public static string DirectoryOf(DockerEndpoint? endpoint, string path)
    {
        if (endpoint is { Scheme: EndpointScheme.Wsl } && path.StartsWith('/'))
        {
            var slash = path.LastIndexOf('/');
            return ToLocal(endpoint, slash <= 0 ? "/" : path[..slash]);
        }

        return Path.GetDirectoryName(path) ?? path;
    }
}
