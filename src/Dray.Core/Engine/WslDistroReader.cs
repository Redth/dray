namespace Dray.Core.Engine;

/// <summary>
/// One WSL distribution, as <c>wsl.exe</c> reports it.
/// </summary>
/// <param name="Name">The distribution's registered name, which is also how it is addressed.</param>
/// <param name="Version">1 or 2. Version 1 has no real kernel and cannot host an engine.</param>
/// <param name="IsRunning">Whether the distribution is up right now.</param>
/// <param name="IsDefault">The distribution a bare <c>wsl</c> would enter.</param>
public sealed record WslDistro(string Name, int Version, bool IsRunning, bool IsDefault)
{
    /// <summary>
    /// Distributions that exist to back a desktop container product rather than to be used.
    /// <para>
    /// Excluded from the host picker for two different reasons, both of which matter.
    /// <c>docker-desktop-data</c> and its equivalents hold storage and run no engine at all, so
    /// offering them produces a host that can never connect. <c>docker-desktop</c> and
    /// <c>rancher-desktop</c> do run one, but each product already publishes it on a named pipe —
    /// so offering the distro as well would list one engine twice in a picker whose whole job is
    /// to say which engine you are looking at.
    /// </para>
    /// <para>
    /// The pipe is also the better of the two routes: it is a direct connection, where reaching
    /// the distro means a relay process per connection.
    /// </para>
    /// </summary>
    static readonly string[] ManagedNames =
    [
        "docker-desktop",
        "docker-desktop-data",
        "rancher-desktop",
        "rancher-desktop-data",
    ];

    /// <summary>
    /// Podman names its machines <c>podman-machine-default</c>, or after the machine. Matched by
    /// prefix because the name is the user's to choose, and reached through podman's own pipe for
    /// the same reason as the others.
    /// </summary>
    const string PodmanMachinePrefix = "podman-machine";

    public bool IsManagedByADesktopProduct =>
        ManagedNames.Contains(Name, StringComparer.OrdinalIgnoreCase)
        || Name.StartsWith(PodmanMachinePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this distribution is worth offering as a host.
    /// <para>
    /// WSL 1 is excluded on a fact rather than a preference: it translates Linux syscalls onto the
    /// NT kernel and has no cgroups, no namespaces and no real init, so no container engine runs
    /// on it. Offering one would be offering a host that cannot work.
    /// </para>
    /// <para>
    /// A stopped WSL 2 distribution is still offered. It may well have an engine installed, and
    /// connecting is what starts it — the same bargain Dray already makes with named pipes, where
    /// existence is decided by connecting rather than by looking.
    /// </para>
    /// </summary>
    public bool CanHostAnEngine => Version >= 2 && !IsManagedByADesktopProduct;
}

/// <summary>
/// Enumerates WSL distributions.
/// <para>
/// docs/ARCHITECTURE.md section 2.4. A distribution running its own <c>dockerd</c> is a host like
/// any other, and on a Windows machine it is the only way to run Linux containers — the native
/// engine runs Windows containers and nothing else.
/// </para>
/// <para>
/// <c>wsl.exe --list --verbose</c> rather than the <c>Lxss</c> registry keys, even though the
/// registry needs no subprocess: the registry records which distributions are installed and not
/// which are running, and it is an implementation detail Microsoft has never documented as an
/// interface. Listing does not start a distribution, so the subprocess costs nothing but its own
/// launch.
/// </para>
/// </summary>
public sealed class WslDistroReader(IProcessRunner? runner = null)
{
    readonly IProcessRunner _runner = runner ?? new SystemProcessRunner();

    /// <summary>
    /// Makes <c>wsl.exe</c> write UTF-8.
    /// <para>
    /// Without it every version of <c>wsl.exe</c> writes UTF-16LE, which .NET decodes through the
    /// console code page into text with a NUL after every character. Supported since WSL 0.64;
    /// <see cref="Decode"/> handles the older builds that ignore it.
    /// </para>
    /// </summary>
    static readonly Dictionary<string, string> Utf8Environment = new() { ["WSL_UTF8"] = "1" };

    /// <summary>
    /// Every distribution WSL knows about, or an empty list where WSL is not installed.
    /// <para>
    /// Absence is not an error here. Most Windows machines have no WSL, and the ones that do may
    /// have it installed with no distribution in it — both are ordinary states that must leave the
    /// rest of discovery alone.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<WslDistro>> ListAsync(CancellationToken ct = default)
    {
        // wsl.exe exists only on Windows, and probing for it anywhere else costs a process launch
        // on every start for an answer that is always the same.
        if (!OperatingSystem.IsWindows()) return [];

        try
        {
            var result = await _runner
                .RunAsync("wsl.exe", ["--list", "--verbose"], null, Utf8Environment, ct)
                .ConfigureAwait(false);

            // WSL exits non-zero when it has no distributions installed, and prints its usage when
            // the command is not understood. Neither is a failure worth surfacing.
            return result.ExitCode == 0 ? Parse(result.StandardOutput) : [];
        }
        catch (Exception)
        {
            // Not installed, not on PATH, or the probe timed out. A machine without WSL is the
            // common case, not an exceptional one.
            return [];
        }
    }

    /// <summary>
    /// Parse <c>wsl --list --verbose</c>.
    /// <para>
    /// The format is a fixed-width table with a marker column, and it is localised: the header row
    /// says NAME/STATE/VERSION in English and something else elsewhere, and the state is
    /// "Running"/"Stopped" in English and translated too. So neither the header nor the state word
    /// can be matched on. What is stable is the shape — a marker, a name, a state, a version — and
    /// the version, which is a digit in every locale.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<WslDistro> Parse(string output)
    {
        var distros = new List<WslDistro>();

        foreach (var raw in Decode(output).Split('\n'))
        {
            var line = raw.TrimEnd('\r').TrimEnd();
            if (line.Length == 0) continue;

            // The default distribution is marked with an asterisk in the first column.
            var isDefault = line.TrimStart().StartsWith('*');
            var body = line.TrimStart().TrimStart('*').Trim();

            var columns = body.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (columns.Length < 3) continue;

            // The last column is the version, and it is the only one that is a bare number in
            // every locale. A header row fails this and is skipped without being recognised as a
            // header, which is what makes the parser locale-independent.
            if (!int.TryParse(columns[^1], out var version)) continue;

            // The name is the FIRST column and the state is everything between it and the version.
            // Not the other way around: a localised state is not always one word — French reports
            // a running distribution as "En cours" — so counting back from the version to find the
            // name yields "Ubuntu En" on a French machine.
            //
            // WSL forbids whitespace in a distribution name, which is what makes the first column
            // unambiguous where the middle is not.
            var name = columns[0];
            var state = string.Join(' ', columns[1..^1]);

            if (name.Length == 0 || state.Length == 0) continue;

            distros.Add(new WslDistro(
                name,
                version,
                IsRunning: state.Equals("Running", StringComparison.OrdinalIgnoreCase),
                isDefault));
        }

        return distros;
    }

    /// <summary>
    /// Recover text from a build of <c>wsl.exe</c> that ignored <c>WSL_UTF8</c>.
    /// <para>
    /// Those write UTF-16LE, which arrives here decoded through a single-byte code page as each
    /// character followed by a NUL. Dropping the NULs restores it exactly, because a distribution
    /// name is drawn from the same character set as the file name it is stored under.
    /// </para>
    /// <para>
    /// Conditional rather than unconditional: real UTF-8 output has no NULs in it at all, so the
    /// presence of one is what distinguishes the two cases.
    /// </para>
    /// </summary>
    internal static string Decode(string output)
        => output.Contains('\0') ? output.Replace("\0", string.Empty) : output;
}
