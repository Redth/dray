using System.Text.RegularExpressions;

namespace Dray.Core.Engine;

/// <summary>
/// Creates the first ordinary account in a WSL distribution, which a distribution installed with
/// <c>--no-launch</c> does not have.
/// <para>
/// Ubuntu's own first run prompts for this in a console. Doing it from a form in Dray means the user
/// is never dropped into a terminal mid-setup — and Ubuntu's first run, when it does happen, finds
/// the account there and skips its prompt, because it looks for exactly what this creates: a uid of
/// 1000 or more with a real shell.
/// </para>
/// <para>
/// <b>The password is a secret</b> and is handled as one (docs/CREDENTIALS.md): it reaches
/// <c>chpasswd</c> on standard input, never on a command line where a process listing would show
/// it, and is never logged, stored or put in a message.
/// </para>
/// </summary>
public sealed partial class WslAccount(IProcessRunner? runner = null)
{
    readonly IProcessRunner _runner = runner ?? new SystemProcessRunner();

    static readonly Dictionary<string, string> Utf8 = new() { ["WSL_UTF8"] = "1" };

    /// <summary>
    /// The groups Ubuntu gives its first user, plus <c>docker</c> when the engine is already there.
    /// Each is added only where the distribution has it, so this works on more than Ubuntu.
    /// </summary>
    const string CreateScript =
        "set -e\n"
        + "groups=\n"
        + "for g in adm cdrom sudo dip plugdev docker; do getent group \"$g\" >/dev/null && groups=\"$groups${groups:+,}$g\"; done\n"
        + "useradd -m -s /bin/bash ${groups:+-G \"$groups\"} \"$1\"\n";

    /// <summary>The rule Ubuntu's first run uses, and the one <c>useradd</c> accepts everywhere.</summary>
    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$")]
    private static partial Regex ValidName();

    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    /// <summary>
    /// A suggestion for the name, from the Windows one — what Ubuntu prefills too. Lowercased, with
    /// anything a Linux name cannot hold removed.
    /// </summary>
    public static string Suggest(string windowsUserName)
    {
        var name = new string([.. windowsUserName.ToLowerInvariant().Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-')]);
        name = name.TrimStart('-', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return name.Length > 32 ? name[..32] : name;
    }

    /// <summary>
    /// Create the account, set its password, and make it the distribution's default user. Null on
    /// success, or a sentence saying what went wrong — which never contains the password.
    /// </summary>
    public async Task<string?> CreateAsync(string distro, string name, string password, CancellationToken ct = default)
    {
        if (!IsValidName(name))
            return "A Linux user name starts with a lowercase letter or an underscore, and holds only lowercase letters, digits, underscores and dashes.";

        if (password.Length == 0) return "Choose a password. sudo asks for it.";

        // The name goes in as a positional argument to the script rather than into its text, so
        // nothing about it is ever interpreted by the shell.
        var created = await _runner
            .RunLongAsync("wsl.exe", ["-d", distro, "-u", "root", "--exec", "sh", "-c", CreateScript, "sh", name], TimeSpan.FromSeconds(60), Utf8, ct)
            .ConfigureAwait(false);

        if (created.ExitCode != 0)
        {
            return WslDistroReader.Decode(created.StandardError).Contains("already exists", StringComparison.OrdinalIgnoreCase)
                ? $"{name} already exists in {distro}."
                : $"Could not create {name} in {distro}: {FirstLine(created.StandardError)}";
        }

        // chpasswd reads "name:password" lines from stdin. Through sh rather than --exec directly:
        // --exec searches a PATH without /usr/sbin, where chpasswd lives, and failed with
        // "execvpe(chpasswd) failed" on a real Ubuntu. The shell's own PATH has it.
        var set = await _runner
            .RunWithInputAsync("wsl.exe", ["-d", distro, "-u", "root", "--exec", "sh", "-c", "exec chpasswd"], $"{name}:{password}\n", ct)
            .ConfigureAwait(false);

        if (set.ExitCode != 0)
        {
            // Its error names the user, not the password — but it is not echoed regardless, because
            // nothing guarantees what a future chpasswd prints.
            return $"{name} was created, but its password could not be set. Run 'sudo passwd {name}' inside {distro} to set one.";
        }

        var made = await _runner
            .RunAsync("wsl.exe", ["--manage", distro, "--set-default-user", name], null, Utf8, ct)
            .ConfigureAwait(false);

        return made.ExitCode == 0
            ? null
            : $"{name} is ready, but could not be made the default user: {FirstLine(made.StandardError.Length > 0 ? made.StandardError : made.StandardOutput)}";
    }

    static string FirstLine(string text)
        => WslDistroReader.Decode(text).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "no reason given.";
}
