namespace Dray.Core.Engine;

/// <summary>Why Apple's container engine is, or is not, an option on this machine.</summary>
public enum AppleEngineAvailability
{
    /// <summary>Installed and discovered. Nothing to suggest.</summary>
    Installed,

    /// <summary>Not this platform at all — Windows, Linux, or an Intel Mac.</summary>
    Unsupported,

    /// <summary>Apple silicon, but an older macOS than the engine supports.</summary>
    NeedsNewerMacOS,

    /// <summary>This machine could run it and does not have it.</summary>
    Available,
}

/// <summary>
/// Whether to tell someone about Apple's container engine.
/// <para>
/// Discovery finds it by looking for <c>container</c> on <c>PATH</c>, which is why both ways of
/// installing it work: Apple's own signed package puts it in <c>/usr/local/bin</c>, and Homebrew's
/// formula puts a wrapper in <c>/opt/homebrew/bin</c>. The engine is Apple's either way — Homebrew
/// is a delivery channel, not a different product.
/// </para>
/// <para>
/// The point of this type is the case discovery cannot cover: a Mac that could run it and has not
/// got it. Suggesting it there is useful; suggesting it on an Intel Mac or an older macOS is
/// pointing someone at a download that will not run, which is worse than saying nothing.
/// </para>
/// </summary>
public static class AppleEngine
{
    /// <summary>Apple supports macOS 26 and later, and says so plainly. Older is not a soft floor.</summary>
    public const int MinimumMacOSMajor = 26;

    public const string Documentation = "https://apple.github.io/container/documentation/";

    public const string Downloads = "https://github.com/apple/container/releases";

    /// <param name="installed">Whether discovery already found the engine.</param>
    /// <param name="isMacOS">Whether this is macOS at all.</param>
    /// <param name="isAppleSilicon">Apple silicon only — it is a virtualization framework, not an emulator.</param>
    /// <param name="macOSMajor">The major version, which is the whole requirement.</param>
    public static AppleEngineAvailability Check(
        bool installed, bool isMacOS, bool isAppleSilicon, int macOSMajor)
    {
        if (installed) return AppleEngineAvailability.Installed;
        if (!isMacOS || !isAppleSilicon) return AppleEngineAvailability.Unsupported;

        return macOSMajor >= MinimumMacOSMajor
            ? AppleEngineAvailability.Available
            : AppleEngineAvailability.NeedsNewerMacOS;
    }

    /// <summary>What to tell the user, or null where there is nothing worth saying.</summary>
    public static string? Explain(AppleEngineAvailability availability) => availability switch
    {
        AppleEngineAvailability.Available =>
            "This Mac can run Apple's own container engine, and it is not installed. It runs Linux "
            + "containers in lightweight virtual machines, and Dray drives it like any other engine.",

        AppleEngineAvailability.NeedsNewerMacOS =>
            $"Apple's container engine needs macOS {MinimumMacOSMajor} or later. This Mac is on an "
            + "older version, so it is not an option here.",

        _ => null,
    };
}
