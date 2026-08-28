namespace Dray.Core.Model;

/// <summary>What a <c>${VAR}</c> resolved to, and whether that is good news.</summary>
public enum SubstitutionState
{
    /// <summary>The variable is set. The compose file will read as shown.</summary>
    Resolved,

    /// <summary>Unset, but the reference carries a default. Harmless and worth showing.</summary>
    Defaulted,

    /// <summary>
    /// Unset, with no default.
    /// <para>
    /// <b>Compose substitutes an empty string and carries on.</b> That is the quietest way a stack
    /// has to break: an image tag becomes <c>myapp:</c>, a port becomes <c>:80</c>, a path becomes
    /// a bare slash. Nothing errors until something much later does, in a message about something
    /// else. Showing it before deploy is the entire point of the annotation.
    /// </para>
    /// </summary>
    Missing,

    /// <summary>
    /// Unset, and the reference used <c>:?</c> — Compose refuses to run at all. Louder than
    /// <see cref="Missing"/>, and easier to diagnose because it says so.
    /// </summary>
    Required,
}

/// <summary>One <c>${VAR}</c> found in a compose file, and what it will become.</summary>
/// <param name="Line">1-based, because that is what an editor's decoration API wants.</param>
/// <param name="Column">1-based, at the leading <c>$</c>.</param>
/// <param name="Length">Characters of the whole reference, including the braces.</param>
public sealed record Substitution(
    string Name,
    int Line,
    int Column,
    int Length,
    string Resolved,
    SubstitutionState State)
{
    /// <summary>Worth interrupting someone over: it will not run, or it will run wrong.</summary>
    public bool IsProblem => State is SubstitutionState.Missing or SubstitutionState.Required;
}

/// <summary>
/// Finding the <c>${VAR}</c> references in a compose file and resolving them.
/// <para>
/// The forms are Compose's own, and the difference between two of them is a real trap: with
/// <c>:-</c> an empty value is replaced by the default, and with <c>-</c> an empty value is kept.
/// Someone who sets <c>TAG=</c> and expects <c>${TAG-latest}</c> to give <c>latest</c> gets an empty
/// tag instead. Dray shows what will actually happen rather than what was intended.
/// </para>
/// </summary>
public static class ComposeInterpolation
{
    /// <summary>
    /// Every reference in the text, in order, resolved against the given variables.
    /// </summary>
    public static IReadOnlyList<Substitution> Find(string? text, IReadOnlyDictionary<string, string> variables)
    {
        var found = new List<Substitution>();
        if (string.IsNullOrEmpty(text)) return found;

        var line = 1;
        var column = 1;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\n')
            {
                line++;
                column = 1;
                continue;
            }

            if (c != '$')
            {
                column++;
                continue;
            }

            // `$$` is an escaped dollar and not a reference at all — it is how a compose file
            // writes a literal `$`, and treating it as a variable would annotate a shell command.
            if (i + 1 < text.Length && text[i + 1] == '$')
            {
                i++;
                column += 2;
                continue;
            }

            if (Read(text, i) is not { } reference)
            {
                column++;
                continue;
            }

            found.Add(Resolve(reference, line, column, variables));

            // A reference cannot span lines, so the column simply advances past it.
            column += reference.Length;
            i += reference.Length - 1;
        }

        return found;
    }

    /// <summary>The raw text of a reference starting at <paramref name="start"/>, or null.</summary>
    static Reference? Read(string text, int start)
    {
        if (start + 1 >= text.Length) return null;

        // The unbraced form, `$VAR`. Ends at the first character a name cannot contain.
        if (text[start + 1] != '{')
        {
            var end = start + 1;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;

            return end == start + 1
                ? null
                : new Reference(text[(start + 1)..end], null, null, end - start);
        }

        var close = text.IndexOf('}', start + 2);
        if (close < 0) return null;

        var body = text[(start + 2)..close];
        var length = close - start + 1;

        // Order matters: `:-` has to be tested before `-`, or the two-character operator is read
        // as the one-character one with a leading colon in the name.
        foreach (var op in Operators)
        {
            var at = body.IndexOf(op, StringComparison.Ordinal);
            if (at <= 0) continue;

            return new Reference(body[..at], op, body[(at + op.Length)..], length);
        }

        return body.Length == 0 ? null : new Reference(body, null, null, length);
    }

    /// <summary>Longest first, so <c>:-</c> is never read as <c>-</c>.</summary>
    static readonly string[] Operators = [":-", ":?", ":+", "-", "?", "+"];

    static Substitution Resolve(
        Reference reference, int line, int column, IReadOnlyDictionary<string, string> variables)
    {
        var set = variables.TryGetValue(reference.Name, out var value);
        value ??= string.Empty;

        // The `:` variants treat an empty value as unset; the bare ones do not. This is the
        // distinction that catches people out, so it is the one thing this method is really for.
        var treatsEmptyAsUnset = reference.Operator?.StartsWith(':') == true;
        var absent = !set || (treatsEmptyAsUnset && value.Length == 0);

        return reference.Operator switch
        {
            ":-" or "-" when absent => new Substitution(
                reference.Name, line, column, reference.Length,
                reference.Argument ?? "", SubstitutionState.Defaulted),

            ":?" or "?" when absent => new Substitution(
                reference.Name, line, column, reference.Length,
                reference.Argument ?? "", SubstitutionState.Required),

            // `+` is the inverse: the argument replaces the value when the variable IS set, and
            // an unset one yields nothing. Neither outcome is a problem, so both are Resolved.
            ":+" or "+" => new Substitution(
                reference.Name, line, column, reference.Length,
                absent ? "" : reference.Argument ?? "", SubstitutionState.Resolved),

            _ when set => new Substitution(
                reference.Name, line, column, reference.Length, value, SubstitutionState.Resolved),

            _ => new Substitution(
                reference.Name, line, column, reference.Length, "", SubstitutionState.Missing),
        };
    }

    /// <summary>
    /// Substitute everything, producing the file Compose will actually read.
    /// <para>
    /// Used for the preview rather than for anything that runs: Compose does its own interpolation
    /// and Dray must never hand it a pre-substituted file, or a value containing a <c>$</c> would
    /// be interpolated twice.
    /// </para>
    /// </summary>
    public static string Apply(string? text, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        var result = new System.Text.StringBuilder(text.Length);
        var found = Find(text, variables);

        // Walked by offset rather than by line and column, so the two agree by construction.
        var offsets = Offsets(text, found);
        var at = 0;

        for (var i = 0; i < found.Count; i++)
        {
            result.Append(text, at, offsets[i].Start - at);
            result.Append(found[i].Resolved);
            at = offsets[i].Start + found[i].Length;
        }

        result.Append(text, at, text.Length - at);
        return result.ToString();
    }

    /// <summary>Character offsets for substitutions expressed in lines and columns.</summary>
    static IReadOnlyList<(int Start, int Length)> Offsets(string text, IReadOnlyList<Substitution> found)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') lineStarts.Add(i + 1);
        }

        return [.. found.Select(s => (lineStarts[s.Line - 1] + s.Column - 1, s.Length))];
    }

    sealed record Reference(string Name, string? Operator, string? Argument, int Length);
}
