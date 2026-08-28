namespace Dray.Core.Model;

/// <summary>
/// Whether a line carries ANSI escapes.
/// <para>
/// The cheap half of colouring logs. Turning escapes into markup is a real parser's job — the
/// grammar has 8-bit and 24-bit colour as well as the sixteen everyone remembers, and one that is
/// slightly wrong corrupts the text rather than the colour — but deciding whether a line needs that
/// parser at all is a scan for one byte, and almost no lines do.
/// </para>
/// </summary>
public static class AnsiText
{
    /// <summary>The escape that starts every sequence.</summary>
    public const char Escape = '\u001b';

    public static bool Contains(string? text) => text is not null && text.Contains(Escape);

    /// <summary>
    /// The line without its escapes, for anywhere that takes text rather than markup — the
    /// clipboard, a filter, a search.
    /// <para>
    /// Filtering against the raw line would let a search for "31" match the red in
    /// <c>ESC[31m</c>, and copying it would put bytes on the clipboard that paste as gibberish
    /// anywhere but a terminal.
    /// </para>
    /// </summary>
    public static string Strip(string? text)
    {
        if (text is null) return string.Empty;
        if (!Contains(text)) return text;

        var clean = new System.Text.StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != Escape)
            {
                clean.Append(text[i]);
                continue;
            }

            // CSI: ESC [ … final-byte, where the final byte is @ through ~. Anything else is left
            // alone rather than guessed at — an escape this does not understand is text.
            if (i + 1 >= text.Length || text[i + 1] != '[') continue;

            var end = i + 2;
            while (end < text.Length && text[end] is not (>= '@' and <= '~')) end++;

            i = end;
        }

        return clean.ToString();
    }
}
