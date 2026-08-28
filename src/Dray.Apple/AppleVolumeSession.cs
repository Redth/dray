using Dray.Core.Engine;
using Dray.Core.Model;

namespace Dray.Apple;

/// <summary>
/// Browsing a volume by mounting it into a throwaway container.
/// <para>
/// The same trick <c>DockerVolumeSession</c> uses, for the same reason: no engine exposes a
/// volume's contents directly — storage is only ever reachable through a container. So one is
/// created, kept stopped where possible, and removed when the session ends.
/// </para>
/// <para>
/// The helper is named rather than left to the engine's own naming, so a session that outlives its
/// window — Dray killed rather than closed — announces what it is instead of sitting in the user's
/// list looking like something they created.
/// </para>
/// </summary>
public sealed class AppleVolumeSession : IVolumeSession
{
    /// <summary>Where the volume is mounted inside the helper. Never shown to the caller.</summary>
    const string MountPath = "/dray-volume";

    readonly AppleRuntime _runtime;
    readonly IProcessRunner _runner;
    readonly string _executable;
    readonly string _container;

    bool _disposed;

    AppleVolumeSession(AppleRuntime runtime, IProcessRunner runner, string executable, string volumeName, string container)
    {
        _runtime = runtime;
        _runner = runner;
        _executable = executable;
        _container = container;
        VolumeName = volumeName;
    }

    public string VolumeName { get; }

    internal static string HelperNameFor(string volume, string suffix)
        => $"dray-volume-{Sanitize(volume)}-{suffix}";

    /// <summary>A volume name may contain characters a container name may not.</summary>
    static string Sanitize(string name)
        => new([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);

    public static async Task<IVolumeSession> OpenAsync(
        AppleRuntime runtime,
        IProcessRunner runner,
        string executable,
        string volumeName,
        CancellationToken ct = default)
    {
        var name = HelperNameFor(volumeName, Guid.NewGuid().ToString("n")[..6]);

        // Started, not merely created: this engine refuses both `exec` and `cp` on a container
        // that is not running, so a stopped helper could not read anything. It sleeps and uses no
        // CPU while it waits.
        var result = await runner.RunAsync(
            executable,
            [
                "run", "--detach", "--name", name,
                "--volume", $"{volumeName}:{MountPath}",
                "docker.io/library/alpine:latest",
                "sh", "-c", "while true; do sleep 30; done",
            ],
            null,
            ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            var detail = result.StandardError.Trim();

            // A volume here is an ext4 disk image, and an image can be attached to exactly one
            // virtual machine at a time. So a volume a running container is already using cannot
            // also be opened for browsing — which Docker allows, and which arrives from this engine
            // as "The storage device attachment is invalid", a sentence about nothing the user did.
            if (detail.Contains("storage device attachment", StringComparison.OrdinalIgnoreCase))
            {
                throw new RuntimeConnectionException(
                    $"{volumeName} is attached to a running container, and this engine can only "
                    + "attach a volume to one at a time. Stop the container using it to browse it.");
            }

            throw new RuntimeConnectionException(
                detail.Length == 0
                    ? $"Could not open {volumeName} for browsing."
                    : $"Could not open {volumeName} for browsing: {detail}");
        }

        return new AppleVolumeSession(runtime, runner, executable, volumeName, name);
    }

    /// <summary>
    /// Paths are the caller's, relative to the volume's root — they never learn where it was
    /// mounted, which is what lets the mount point change without breaking anyone.
    /// </summary>
    string Inside(string path) => MountPath + FileEntry.Normalize(path);

    public async Task<DirectoryListing> ListDirectoryAsync(string path, CancellationToken ct = default)
    {
        var listing = await _runtime
            .ListDirectoryAsync(_container, Inside(path), containerIsRunning: true, ct)
            .ConfigureAwait(false);

        // Rewritten back into the volume's own space: an entry pointing at /dray-volume/x would
        // send the next click somewhere the caller has never heard of.
        return listing with
        {
            Path = FileEntry.Normalize(path),
            Entries = [.. listing.Entries.Select(e => e with { Path = Outside(e.Path) })],
        };
    }

    static string Outside(string path)
        => path.StartsWith(MountPath, StringComparison.Ordinal)
            ? FileEntry.Normalize(path[MountPath.Length..])
            : path;

    public Task<byte[]> ReadFileAsync(string path, CancellationToken ct = default)
        => _runtime.ReadFileAsync(_container, Inside(path), ct);

    public Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default)
        => _runtime.WriteFileAsync(_container, Inside(path), content, ct);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            // Not the caller's token: disposal happens when the view goes away, which is often
            // exactly when that token has just been cancelled. Using it would leave the helper
            // running forever.
            await _runner
                .RunAsync(_executable, ["delete", "--force", _container], null, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A helper that could not be removed is a stray container, not a crash on the way out
            // of a screen. Its name says what it was.
        }
    }
}
