namespace Dray.Core.Model;

/// <summary>
/// Remembering which environment variables the user marked, on the container itself.
/// <para>
/// The marking has to survive a restart and has to be visible to whoever looks at the container
/// next, so it lives in container labels rather than in a database on Dray's side. Anyone running
/// <c>inspect</c> sees exactly what Dray sees, which is the same reason compose keeps its own
/// metadata there.
/// </para>
/// <para>
/// <b>Labels are set at creation and never change.</b> That is the engine's rule, not a choice
/// here, and it has a consequence the UI must be honest about: marking a variable on a container
/// that already exists cannot be written back. Dray treats that as a view preference for the
/// session rather than pretending to have changed the container.
/// </para>
/// </summary>
public static class SecretMarks
{
    /// <summary>Keys the user marked secret, comma separated.</summary>
    public const string SecretLabel = "codes.redth.dray.env-secret";

    /// <summary>
    /// Keys the user marked <i>not</i> secret, comma separated.
    /// <para>
    /// A second label rather than a cleverer encoding of one, because both lists are then plainly
    /// readable in <c>docker inspect</c> — which is the whole point of putting them there.
    /// </para>
    /// </summary>
    public const string PlainLabel = "codes.redth.dray.env-plain";

    /// <summary>
    /// The labels to create a container with, given what the user marked.
    /// <para>
    /// Only variables the user actually decided about are recorded. Writing every key with its
    /// computed value would freeze today's heuristic onto the container for ever, so a later
    /// improvement to the rule could never apply to it.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> ToLabels(IEnumerable<EnvVar> variables)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);

        var secret = variables.Where(v => v.Marked is true).Select(v => v.Key).ToList();
        var plain = variables.Where(v => v.Marked is false).Select(v => v.Key).ToList();

        if (secret.Count > 0) labels[SecretLabel] = string.Join(',', secret);
        if (plain.Count > 0) labels[PlainLabel] = string.Join(',', plain);

        return labels;
    }

    /// <summary>
    /// Apply a container's labels to the variables read from it, so a mark made when it was created
    /// still shows now.
    /// </summary>
    public static IReadOnlyList<EnvVar> Apply(
        IEnumerable<EnvVar> variables, IDictionary<string, string>? labels)
    {
        var secret = Split(Label(labels, SecretLabel));
        var plain = Split(Label(labels, PlainLabel));

        return
        [
            .. variables.Select(v => v with
            {
                // A key in neither list keeps a null mark and falls back to the heuristic. A key
                // somehow in both is treated as secret: when the two disagree, the safe reading is
                // the one that hides something unnecessarily rather than reveals something.
                Marked = secret.Contains(v.Key) ? true
                    : plain.Contains(v.Key) ? false
                    : null,
            }),
        ];
    }

    static string? Label(IDictionary<string, string>? labels, string key)
        => labels is not null && labels.TryGetValue(key, out var value) ? value : null;

    static HashSet<string> Split(string? value)
        => value is null
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
