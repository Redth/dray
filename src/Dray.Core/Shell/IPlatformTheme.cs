using Dray.Core.Theme;

namespace Dray.Core.Shell;

/// <summary>
/// What the host OS actually painted, so the WebView's token layer can match it exactly.
/// <para>
/// The WebView is transparent over the native window. If <c>--bg</c> is our generated fallback
/// rather than the real window colour, the seam between native chrome and web content shows as a
/// faint mismatch — the single most visible way a hybrid app gives itself away
/// (DESIGN.md section 9.1).
/// </para>
/// </summary>
public interface IPlatformTheme
{
    DrayTheme Current { get; }

    /// <summary>
    /// Token overrides as <c>name -&gt; CSS value</c>, without the <c>--</c> prefix.
    /// <para>
    /// Mostly colour: the roles where "what the OS chose" beats "what we chose" — the window
    /// ground, separators, the accent. It also carries <c>chrome-top</c>, the height of the
    /// native titlebar and toolbar the content sits under, because that is a fact only the host
    /// knows and CSS has to reserve space for it.
    /// </para>
    /// </summary>
    IReadOnlyDictionary<string, string> TokenOverrides();

    /// <summary>Raised when the OS appearance changes. Must repaint in the same frame.</summary>
    event Action? Changed;
}

/// <summary>
/// For hosts with no native window to match — the browser dev host. Leaves the generated palette
/// alone and lets prefers-color-scheme decide.
/// </summary>
public sealed class GeneratedPaletteTheme : IPlatformTheme
{
    public DrayTheme Current => DrayTheme.Light;

    public IReadOnlyDictionary<string, string> TokenOverrides() => new Dictionary<string, string>();

    public event Action? Changed
    {
        add { }
        remove { }
    }
}
