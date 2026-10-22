namespace Dray.Core.Engine;

/// <summary>
/// Looks at this Windows machine and reports what it found.
/// <para>
/// Everything here is deliberately non-elevated. The authoritative check for a Windows feature is
/// <c>Get-WindowsOptionalFeature</c> or DISM, and both need administrator — so asking them would
/// mean a User Account Control prompt on every launch, to answer a question the user has not asked
/// yet. Instead Dray reads what it can see and reports <see cref="SetupFact.Unknown"/> where it
/// cannot be sure, which the setup steps are built to tolerate.
/// </para>
/// </summary>
public sealed class WindowsSetupReader(
    IProcessRunner? runner = null,
    IDockerConfigSource? source = null,
    WslDistroReader? wsl = null)
{
    readonly IProcessRunner _runner = runner ?? new SystemProcessRunner();
    readonly IDockerConfigSource _source = source ?? new SystemDockerConfigSource();
    readonly WslDistroReader _wsl = wsl ?? new WslDistroReader(runner);

    /// <summary>
    /// The Host Compute Service, which is what actually runs a Windows container.
    /// <para>
    /// Its presence on disk is the closest thing to a non-elevated read of whether the
    /// <c>Containers</c> feature is enabled. It is an inference rather than the answer — Hyper-V
    /// installs it too — which is why absence is reported confidently and presence is not.
    /// </para>
    /// </summary>
    const string HostComputeService = @"System32\vmcompute.exe";

    /// <param name="hosts">
    /// What discovery found and connected to. The only signal here that is not an inference: an
    /// engine that answered with an OSType has settled the question for that kind of container.
    /// </param>
    public async Task<WindowsSetupState> ReadAsync(
        IReadOnlyList<DockerHost> hosts, CancellationToken ct = default)
    {
        var (wslPresent, wslVersion) = await ReadWslAsync(ct).ConfigureAwait(false);

        // Enumerating distros needs WSL, so a machine without it is not asked.
        var distros = wslPresent == SetupFact.Present
            ? await _wsl.ListAsync(ct).ConfigureAwait(false)
            : [];

        return new WindowsSetupState(
            Wsl: wslPresent,
            WslVersion: wslVersion,
            Distros: distros,
            ContainersFeature: ReadContainersFeature(),
            DockerCli: await ReadDockerCliAsync(ct).ConfigureAwait(false),

            // Reached, not merely listed. A host that is present in the picker and unreachable has
            // not answered the question of what kind of container it runs.
            HasLinuxEngine: hosts.Any(h => h.Capabilities.ContainerOs == Model.ContainerOs.Linux),
            HasWindowsEngine: hosts.Any(h => h.Capabilities.ContainerOs == Model.ContainerOs.Windows));
    }

    /// <summary>
    /// Whether WSL is installed, and what version.
    /// <para>
    /// <c>wsl --version</c> rather than <c>--status</c>: the Store build answers it with a version
    /// table, and the older in-box build does not recognise it at all — so the command
    /// distinguishes the two installs as well as detecting either.
    /// </para>
    /// </summary>
    async Task<(SetupFact Present, string? Version)> ReadWslAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return (SetupFact.Absent, null);

        try
        {
            var result = await _runner
                .RunAsync("wsl.exe", ["--version"], null, new Dictionary<string, string> { ["WSL_UTF8"] = "1" }, ct)
                .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                // wsl.exe exists and refused the flag. That is the in-box build, which is still WSL
                // and still able to host an engine — so present, with no version to report.
                return (SetupFact.Present, null);
            }

            var version = WslDistroReader.Decode(result.StandardOutput)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            return (SetupFact.Present, version);
        }
        catch (Exception)
        {
            // No wsl.exe on PATH at all.
            return (SetupFact.Absent, null);
        }
    }

    /// <summary>
    /// Whether the Containers feature looks enabled.
    /// <para>
    /// Absence is reliable: without the feature the Host Compute Service is not installed, so a
    /// missing binary means Windows containers cannot run. Presence is not, because Hyper-V
    /// installs the same binary — which is why this returns Unknown rather than Present, and the
    /// step that enables the feature is safe to run either way.
    /// </para>
    /// </summary>
    SetupFact ReadContainersFeature()
    {
        if (!OperatingSystem.IsWindows()) return SetupFact.Absent;

        var root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(root)) return SetupFact.Unknown;

        return _source.FileExists(Path.Combine(root, HostComputeService))
            ? SetupFact.Unknown
            : SetupFact.Absent;
    }

    /// <summary>Whether there is a <c>docker</c> command, which decides what the CLI panel can offer.</summary>
    async Task<SetupFact> ReadDockerCliAsync(CancellationToken ct)
    {
        try
        {
            var result = await _runner
                .RunAsync("docker", ["--version"], null, ct)
                .ConfigureAwait(false);

            return result.ExitCode == 0 ? SetupFact.Present : SetupFact.Absent;
        }
        catch (Exception)
        {
            return SetupFact.Absent;
        }
    }
}
