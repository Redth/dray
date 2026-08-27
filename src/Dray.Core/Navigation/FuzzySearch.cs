namespace Dray.Core.Navigation;

/// <summary>How well one candidate matched, and where.</summary>
/// <param name="Value">The candidate itself.</param>
/// <param name="Score">Higher is better. Only meaningful relative to other results of one query.</param>
/// <param name="Highlights">
/// Indexes into the candidate's <i>label</i> that matched, so the UI can mark them. Empty when the
/// match came from text the user cannot see — highlighting nothing is the honest rendering of "this
/// matched on its image name", and inventing marks on the visible text would be a lie.
/// </param>
public sealed record SearchMatch<T>(T Value, int Score, IReadOnlyList<int> Highlights);

/// <summary>
/// Ranking anything against what has been typed.
/// <para>
/// Subsequence matching rather than substring, because that is what people expect of a palette:
/// "rsc" should find "Restart container". Scoring favours matches at the start of a word, so
/// "stop" ranks "Stop" above "Prune stopped".
/// </para>
/// <para>
/// Generic because the command palette is not the only list worth searching this way — an image
/// picker wants the same behaviour over a different type. The two things it needs from a candidate
/// are the text the user reads and the wider text worth matching against, so it asks for those and
/// nothing else.
/// </para>
/// </summary>
public static class FuzzySearch
{
    /// <summary>
    /// Rank candidates against a query, best first.
    /// </summary>
    /// <param name="label">
    /// The text the user actually sees. Matched first, and the only thing highlights point into.
    /// </param>
    /// <param name="haystack">
    /// Everything worth matching against, including what is not displayed — an image name, an id, a
    /// synonym. Should already be lowercase; callers building rows repeatedly are expected to
    /// compute it once rather than per keystroke.
    /// </param>
    /// <param name="tieBreak">
    /// Ordering for equal scores, so the list does not reshuffle between identical queries.
    /// </param>
    /// <returns>An empty query returns every candidate in its given order, scored zero.</returns>
    public static IReadOnlyList<SearchMatch<T>> Rank<T>(
        IEnumerable<T> candidates,
        string query,
        Func<T, string> label,
        Func<T, string> haystack,
        Func<T, string>? tieBreak = null)
    {
        var needle = query.Trim().ToLowerInvariant();

        if (needle.Length == 0)
            return [.. candidates.Select(c => new SearchMatch<T>(c, 0, []))];

        tieBreak ??= label;

        return
        [
            .. candidates
                .Select(c => Match(c, needle, label, haystack))
                .Where(m => m is not null)
                .Select(m => m!)
                .OrderByDescending(m => m.Score)
                .ThenBy(m => tieBreak(m.Value), StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>Score one candidate, or null when the query is not a subsequence of it.</summary>
    internal static SearchMatch<T>? Match<T>(
        T candidate, string needle, Func<T, string> label, Func<T, string> haystack)
    {
        var text = label(candidate);

        // Matched against the label first so the highlights land on what the user can see; the
        // wider haystack only decides whether the row appears at all.
        if (Subsequence(text.ToLowerInvariant(), needle, out var highlights) is { } score)
        {
            // A candidate whose label starts with the query is almost always the one meant.
            if (text.StartsWith(needle, StringComparison.OrdinalIgnoreCase)) score += 60;

            return new SearchMatch<T>(candidate, score, highlights);
        }

        // Not in the label, but perhaps in the image name or the id. Worth offering, ranked below
        // anything that matched visibly — a row that matched on text the user cannot see looks
        // like a bug if it outranks one that did.
        return Subsequence(haystack(candidate), needle, out _) is { } hidden
            ? new SearchMatch<T>(candidate, hidden - 200, [])
            : null;
    }

    /// <summary>
    /// Whether every character of <paramref name="needle"/> appears in order, and how good the fit
    /// is. Higher is better.
    /// </summary>
    internal static int? Subsequence(string haystack, string needle, out IReadOnlyList<int> highlights)
    {
        var positions = new List<int>(needle.Length);
        var score = 0;
        var at = 0;

        foreach (var c in needle)
        {
            var found = haystack.IndexOf(c, at);

            if (found < 0)
            {
                highlights = [];
                return null;
            }

            // A character beginning a word is worth far more than one in the middle: it is what
            // makes an acronym like "rsc" rank above an accidental scatter of the same letters.
            var atWordStart = found == 0 || haystack[found - 1] is ' ' or '-' or '_' or '·' or '/' or ':';
            score += atWordStart ? 12 : 2;

            // Consecutive characters are a stronger signal than scattered ones.
            if (positions.Count > 0 && found == positions[^1] + 1) score += 6;

            positions.Add(found);
            at = found + 1;
        }

        // A short haystack that matched is a tighter fit than a long one — "Stop" beats
        // "Prune stopped" for the query "stop".
        score += Math.Max(0, 40 - haystack.Length);

        highlights = positions;
        return score;
    }

    /// <summary>
    /// Split a label into runs, marking which are matched.
    /// <para>
    /// Rendered rather than computed in the component so the same segmentation is testable and so
    /// every list that highlights does it identically.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Text, bool Matched)> Segment(string label, IReadOnlyList<int> highlights)
    {
        if (highlights.Count == 0 || label.Length == 0) return [(label, false)];

        var marks = new HashSet<int>(highlights);
        var runs = new List<(string, bool)>();

        var start = 0;
        var current = marks.Contains(0);

        for (var i = 1; i <= label.Length; i++)
        {
            var next = i < label.Length && marks.Contains(i);

            if (i == label.Length || next != current)
            {
                runs.Add((label[start..i], current));
                start = i;
                current = next;
            }
        }

        return runs;
    }
}
