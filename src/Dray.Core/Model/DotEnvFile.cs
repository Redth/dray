namespace Dray.Core.Model;

/// <summary>
/// The <c>.env</c> beside a compose file.
/// <para>
/// <b>Nothing in the compose file refers to it.</b> Compose loads <c>.env</c> from the project
/// directory automatically and uses it to interpolate <c>${VAR}</c> anywhere in the YAML. That is
/// the whole mechanism, and it is worth being precise about because it is constantly confused with
/// <c>env_file:</c>, which is a different thing entirely:
/// </para>
/// <list type="bullet">
/// <item><b><c>.env</c> in the project directory</b> — automatic, undeclared, and interpolates into
/// the <i>compose file itself</i>. An image tag, a port, a volume path.</item>
/// <item><b><c>env_file:</c> on a service</b> — declared in the YAML, and sets that
/// <i>container's</i> environment. The compose file never sees those values.</item>
/// </list>
/// <para>
/// Dray edits the first. The second is the service's own business and lives in the YAML.
/// </para>
/// </summary>
public static class DotEnvFile
{
    /// <summary>The filename, fixed by Compose. Not configurable, and not worth pretending it is.</summary>
    public const string FileName = ".env";

    /// <summary>
    /// Read a <c>.env</c>.
    /// <para>
    /// Deliberately lenient. This file is hand-edited far more often than it is generated, and a
    /// line Dray cannot read should cost that line rather than the file — the other twenty
    /// variables still work, and refusing to show any of them helps nobody.
    /// </para>
    /// </summary>
    public static IReadOnlyList<EnvVar> Parse(string? text)
    {
        var variables = new List<EnvVar>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');

            if (line.Length == 0 || line.StartsWith('#')) continue;

            // `export KEY=value` is valid here: the file doubles as something people source in a
            // shell, and Compose accepts the prefix.
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();

            var split = line.IndexOf('=');
            if (split <= 0) continue;

            var key = line[..split].TrimEnd();
            if (key.Length == 0 || key.Any(char.IsWhiteSpace)) continue;

            // Last one wins, which is what Compose does. Replacing rather than appending keeps the
            // editor showing the value that will actually be used.
            if (!seen.Add(key)) variables.RemoveAll(v => v.Key == key);

            variables.Add(new EnvVar(key, Unquote(line[(split + 1)..].Trim())));
        }

        return variables;
    }

    /// <summary>
    /// Strip one matching pair of quotes.
    /// <para>
    /// Only a matching pair, and only the outermost: a value that legitimately starts with a quote
    /// but does not end with one is not quoted, it is a value that starts with a quote.
    /// </para>
    /// </summary>
    static string Unquote(string value)
    {
        if (value.Length < 2) return value;

        var first = value[0];
        if (first is not ('"' or '\'') || value[^1] != first) return value;

        var inner = value[1..^1];

        // Escapes are a double-quote feature; a single-quoted value is literal, as in a shell.
        return first == '"'
            ? inner.Replace("\\n", "\n", StringComparison.Ordinal)
                   .Replace("\\\"", "\"", StringComparison.Ordinal)
            : inner;
    }

    /// <summary>
    /// Write a <c>.env</c> back.
    /// <para>
    /// Quotes only when the value needs it — a file full of unnecessary quotes is one the user
    /// stops hand-editing, and hand-editing it is the normal case.
    /// </para>
    /// </summary>
    public static string Serialize(IEnumerable<EnvVar> variables)
        => string.Join('\n', variables.Select(v => $"{v.Key}={Quote(v.Value)}")) + "\n";

    static string Quote(string value)
    {
        if (value.Length == 0) return value;

        var needsQuotes = value.Any(c => c is ' ' or '\t' or '#' or '\n' or '"')
                          || value[0] is '\'' or '"';

        if (!needsQuotes) return value;

        return '"' + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + '"';
    }
}
