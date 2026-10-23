namespace Dray.Core.Engine;

/// <summary>
/// Where Dray keeps the little it keeps.
/// <para>
/// Each platform's own place, not .NET's single answer: <c>SpecialFolder.ApplicationData</c> is
/// <c>~/.config</c> on macOS, which is a Linux convention a Mac app has no business following.
/// Nothing here is a secret — CREDENTIALS.md is the rule, and nothing Dray writes is in scope of it.
/// </para>
/// </summary>
public static class DrayPaths
{
    /// <summary>
    /// The folder, or <c>DRAY_CONFIG_DIR</c> when set — so a development run or a test drives a
    /// throwaway copy rather than the one the installed app uses.
    /// </summary>
    public static string ConfigDirectory
    {
        get
        {
            if (Environment.GetEnvironmentVariable("DRAY_CONFIG_DIR") is { Length: > 0 } overridden) return overridden;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Application Support", "Dray");
            if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dray");

            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, "dray");
        }
    }

    /// <summary>What to suggest when Dray first asks where stacks should live.</summary>
    public static string SuggestedStacksFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Stacks");
}
