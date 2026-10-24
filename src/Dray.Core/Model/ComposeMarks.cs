using Dray.Core.Shell;

namespace Dray.Core.Model;

/// <summary>
/// What a compose editor draws over its text: each <c>${VAR}</c>'s value beside it, and each problem
/// under it. One set of rules for every editor of a compose file — a stack's, and one being written
/// in the New stack window — so the two cannot come to disagree about what a reference means.
/// </summary>
public static class ComposeMarks
{
    /// <summary>
    /// Fixed width whatever the value's length, as the variables panel masks it: a mask that matched
    /// the length would leak it.
    /// </summary>
    const string Mask = "••••••••";

    /// <summary>
    /// Every <c>${VAR}</c> in <paramref name="text"/>, with what it resolves to drawn beside it — or a
    /// mask, for a variable that is secret by its name or by the user's say-so. A secret is no more
    /// readable inline in the YAML than it is in the panel beside it.
    /// </summary>
    public static IReadOnlyList<EditorAnnotation> Annotations(string text, IReadOnlyList<EnvVar> variables)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in variables) values[variable.Key] = variable.Value;

        var secret = variables.Where(v => v.IsSecret).Select(v => v.Key).ToHashSet(StringComparer.Ordinal);

        return
        [
            .. ComposeInterpolation.Find(text, values).Select(s => new EditorAnnotation(
                s.Line, s.Column, s.Length, Hint(s, secret.Contains(s.Name)), Kind(s.State), Hover(s, secret.Contains(s.Name)))),
        ];
    }

    /// <summary>A problem placed in a file, as the editor underlines it.</summary>
    /// <param name="source">Who said so, shown in the hover: <c>compose</c> or <c>dray</c>.</param>
    public static EditorProblem ToEditor(ComposeProblem problem, YamlRange range, string source)
    {
        // A zero-width range draws nothing. Widen it to the rest of the line's first word so the
        // squiggle is visible; Monaco clamps an end past the line to the line's end.
        var endColumn = range.EndLine == range.Line && range.EndColumn <= range.Column ? range.Column + 1 : range.EndColumn;

        return new EditorProblem(
            range.Line, range.Column, range.EndLine, endColumn,
            problem.Message,
            problem.Severity == ProblemSeverity.Error ? "error" : "warning",
            source);
    }

    /// <summary>
    /// What is drawn beside the reference. Short, because it sits inside a line of YAML — the
    /// explanation goes in the hover.
    /// </summary>
    static string Hint(Substitution s, bool secret) => s.State switch
    {
        SubstitutionState.Missing => "→ empty",
        SubstitutionState.Required => "→ will not run",
        _ when s.Resolved.Length == 0 => "→ empty",
        _ when secret => "→ " + Mask,
        _ => "→ " + (s.Resolved.Length > 40 ? s.Resolved[..40] + "…" : s.Resolved),
    };

    static string Kind(SubstitutionState state) => state switch
    {
        SubstitutionState.Resolved => "ok",
        SubstitutionState.Defaulted => "default",
        SubstitutionState.Required => "required",
        _ => "missing",
    };

    static string? Hover(Substitution s, bool secret) => s.State switch
    {
        // The quiet failure, stated plainly. Compose does not error on this — it substitutes an
        // empty string and carries on, so an image tag becomes `myapp:` and fails much later.
        SubstitutionState.Missing =>
            $"**{s.Name}** is not set.\n\nCompose substitutes an empty string and carries on, so "
            + "this line becomes whatever it reads without the variable. Add it to the variables.",

        SubstitutionState.Required =>
            $"**{s.Name}** is not set, and this reference requires it.\n\nCompose refuses to run "
            + "the stack until it has a value.",

        SubstitutionState.Defaulted =>
            $"**{s.Name}** is not set, so the default in this reference is used.",

        SubstitutionState.Resolved when secret =>
            $"**{s.Name}** is set, and secret, so its value is not shown here. Reveal it in the variables.",

        _ => null,
    };
}
