using System.Text.RegularExpressions;

namespace Dray.Core.Model;

public enum ProblemSeverity
{
    Error,
    Warning,
}

/// <summary>
/// One thing compose objected to, and where in the file it was.
/// </summary>
/// <param name="Message">Compose's own words, with the file path it prefixes removed.</param>
/// <param name="File">The file compose named, when it named one.</param>
/// <param name="Path">The key path compose named, e.g. <c>services, web, ports</c>. Empty when none.</param>
/// <param name="Range">
/// A position compose gave directly — only YAML syntax errors carry one. Everything else names a
/// path, which <see cref="ComposeDocument.Locate"/> turns into a position in the user's text.
/// </param>
public sealed record ComposeProblem(
    string Message,
    ProblemSeverity Severity,
    string? File = null,
    IReadOnlyList<string>? Path = null,
    YamlRange? Range = null)
{
    public IReadOnlyList<string> KeyPath => Path ?? [];
}

/// <summary>
/// Reading what <c>docker compose config</c> says when it refuses a file.
/// <para>
/// Every pattern here was captured from compose 5.4.0 against a file built to trigger it, not
/// written from documentation — there is no documentation of these strings. Anything that matches
/// none of them is still reported, verbatim and unplaced, because an error Dray cannot locate is
/// still an error the user needs to read.
/// </para>
/// </summary>
public static partial class ComposeProblems
{
    /// <summary>
    /// Split compose's stderr into problems. Warnings arrive as logrus lines even on a run that
    /// succeeds; the error, when there is one, is whatever else it printed.
    /// </summary>
    public static IReadOnlyList<ComposeProblem> Parse(string? output)
    {
        var problems = new List<ComposeProblem>();
        if (string.IsNullOrWhiteSpace(output)) return problems;

        var rest = new List<string>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;

            if (LogLine().Match(line) is { Success: true } log)
            {
                var message = Unescape(log.Groups["msg"].Value);
                var severity = log.Groups["level"].Value is "warning" or "warn" or "info"
                    ? ProblemSeverity.Warning
                    : ProblemSeverity.Error;

                problems.Add(Classify(message, severity));
                continue;
            }

            rest.Add(line.Trim());
        }

        // "decoding failed due to the following error(s):" heads a list, one bad field per line.
        // The heading says nothing the lines below it do not, so each line is its own problem.
        if (rest.Count > 1 && rest[0].StartsWith("decoding failed", StringComparison.Ordinal))
        {
            foreach (var line in rest.Skip(1)) problems.Add(Classify(line, ProblemSeverity.Error));
        }
        else if (rest.Count > 0)
        {
            problems.Add(Classify(string.Join(' ', rest), ProblemSeverity.Error));
        }

        return problems;
    }

    static ComposeProblem Classify(string text, ProblemSeverity severity)
    {
        // failed to parse /x/compose.yaml: go-yaml load error in parser (while parsing a block
        // mapping) at L2.C3-L4.C4: did not find expected key
        // failed to parse /x/compose.yaml: go-yaml load error in scanner at L14.C12: mapping
        // values are not allowed in this context
        if (Syntax().Match(text) is { Success: true } syntax)
        {
            // The parser reports a range; the scanner reports a single point, which is drawn as
            // the one character it names.
            var line = int.Parse(syntax.Groups["l1"].Value);
            var column = int.Parse(syntax.Groups["c1"].Value);

            var range = syntax.Groups["l2"].Success
                ? new YamlRange(line, column, int.Parse(syntax.Groups["l2"].Value), int.Parse(syntax.Groups["c2"].Value))
                : new YamlRange(line, column, line, column + 1);

            return new ComposeProblem(syntax.Groups["msg"].Value, severity, syntax.Groups["file"].Value, Range: range);
        }

        // validating /x/compose.yaml: services.web additional properties 'imagee' not allowed
        if (Validating().Match(text) is { Success: true } validating)
        {
            var path = Dotted(validating.Groups["path"].Value);
            var message = validating.Groups["msg"].Value;

            // The rejected key is named in the sentence, not the path. Pointing at it rather than at
            // its parent is the difference between "somewhere in web" and the exact typo.
            if (Additional().Match(message) is { Success: true } additional)
                path = [.. path, additional.Groups["key"].Value];

            return new ComposeProblem($"{validating.Groups["path"].Value} {message}", severity, validating.Groups["file"].Value, path);
        }

        // service "web" depends on undefined service "db": invalid compose project
        if (Undefined().Match(text) is { Success: true } undefined)
        {
            var service = undefined.Groups["svc"].Value;
            var dependency = undefined.Groups["dep"].Value;

            return new ComposeProblem(
                $"{service} depends on {dependency}, which is not a service in this stack",
                severity,
                Path: ["services", service, "depends_on", dependency]);
        }

        // error while interpolating services.web.image: required variable TAG is missing a value
        if (Interpolating().Match(text) is { Success: true } interpolating)
        {
            return new ComposeProblem(
                interpolating.Groups["msg"].Value, severity, Path: Dotted(interpolating.Groups["path"].Value));
        }

        // 'services[web].mem_limit' invalid size: 'lots'
        if (Decoding().Match(text) is { Success: true } decoding)
        {
            return new ComposeProblem(
                $"{decoding.Groups["path"].Value} {decoding.Groups["msg"].Value}",
                severity,
                Path: Bracketed(decoding.Groups["path"].Value));
        }

        // /x/compose.yaml: the attribute `version` is obsolete, it will be ignored, ...
        if (Attribute().Match(text) is { Success: true } attribute)
        {
            return new ComposeProblem(
                attribute.Groups["msg"].Value, severity, attribute.Groups["file"].Value, [attribute.Groups["key"].Value]);
        }

        return new ComposeProblem(text, severity);
    }

    /// <summary><c>services.web.ports.[].target</c> → services, web, ports, [], target.</summary>
    static List<string> Dotted(string path) => [.. path.Split('.', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary><c>services[web].mem_limit</c> → services, web, mem_limit.</summary>
    static List<string> Bracketed(string path)
        => [.. path.Replace("[", ".", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal)
            .Split('.', StringSplitOptions.RemoveEmptyEntries)];

    static string Unescape(string value) => value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

    [GeneratedRegex(@"level=(?<level>\w+)\s+msg=""(?<msg>(?:[^""\\]|\\.)*)""", RegexOptions.CultureInvariant)]
    private static partial Regex LogLine();

    [GeneratedRegex(@"^failed to parse (?<file>.+?): .*? at L(?<l1>\d+)\.C(?<c1>\d+)(?:-L(?<l2>\d+)\.C(?<c2>\d+))?: (?<msg>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Syntax();

    [GeneratedRegex(@"^validating (?<file>.+?): (?<path>\S+) (?<msg>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Validating();

    [GeneratedRegex(@"additional propert(?:y|ies) '(?<key>[^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex Additional();

    [GeneratedRegex(@"^service ""(?<svc>[^""]+)"" depends on undefined service ""(?<dep>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex Undefined();

    [GeneratedRegex(@"^error while interpolating (?<path>\S+): (?<msg>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Interpolating();

    [GeneratedRegex(@"^'(?<path>[^']+)' (?<msg>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Decoding();

    [GeneratedRegex(@"^(?<file>/.+?|[A-Za-z]:\\.+?): (?<msg>the attribute `(?<key>[^`]+)` .+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();
}

/// <summary>A problem, the file of the stack it belongs to, and where in that file.</summary>
/// <param name="FileIndex">Which of the files given to <see cref="ComposeConfig.Place"/>; null when none.</param>
/// <param name="Range">Where in that file; null when the file is known but the place is not.</param>
public sealed record PlacedProblem(ComposeProblem Problem, int? FileIndex, YamlRange? Range);

/// <summary>
/// What <c>docker compose config</c> made of a stack: the effective model as compose will run it,
/// and everything it objected to.
/// </summary>
/// <param name="Yaml">
/// The normalised model — every file merged, every variable substituted, every default filled in.
/// Null when compose refused. Holds the values of the stack's secrets, so it is shown only through
/// <see cref="Redact"/>.
/// </param>
/// <param name="ExitCode">Null when the run reported none; see <see cref="Engine.ComposeOutput.ExitCode"/>.</param>
public sealed partial record ComposeConfig(string? Yaml, IReadOnlyList<ComposeProblem> Problems, int? ExitCode)
{
    /// <summary>Compose accepted the stack. Only a reported zero counts.</summary>
    public bool IsValid => ExitCode == 0;

    public const string Mask = "••••••";

    /// <summary>
    /// Each problem, placed in the stack's files — <paramref name="files"/> in the order compose
    /// was given them.
    /// <para>
    /// A problem compose attributed to a file goes to that file. One with no file — an
    /// interpolation failure, an undefined dependency — goes to the first file whose text has the
    /// path it names — exactly if any file has it exactly — because with an override in play the
    /// key can live in either. A problem that
    /// lands nowhere is kept, unplaced, so it is still listed: an error Dray cannot locate is still
    /// one the user has to read.
    /// </para>
    /// </summary>
    public IReadOnlyList<PlacedProblem> Place(IReadOnlyList<(string Path, ComposeDocument Document)> files)
    {
        var placed = new List<PlacedProblem>();

        foreach (var problem in Problems)
        {
            if (problem.File is { } named)
            {
                var index = IndexOf(files, named);

                placed.Add(index is { } i
                    ? new PlacedProblem(problem, i, problem.Range ?? Locate(files[i].Document, problem))
                    : new PlacedProblem(problem, null, null));

                continue;
            }

            // An exact match anywhere beats a near one earlier: `services.web` is in every file of
            // a stack with an override, and only one of them has the `depends_on` being reported.
            var found = Enumerable.Range(0, files.Count)
                .Select(i => (Index: i, Location: problem.KeyPath.Count > 0 ? files[i].Document.Locate(problem.KeyPath) : null))
                .Where(f => f.Location is not null)
                .OrderBy(f => f.Location!.Exact ? 0 : 1)
                .FirstOrDefault();

            placed.Add(found.Location is { } location
                ? new PlacedProblem(problem, found.Index, location.Range)
                : new PlacedProblem(problem, null, null));
        }

        return placed;
    }

    static YamlRange? Locate(ComposeDocument document, ComposeProblem problem)
        => problem.Range ?? (problem.KeyPath.Count > 0 ? document.Locate(problem.KeyPath)?.Range : null);

    static int? IndexOf(IReadOnlyList<(string Path, ComposeDocument Document)> files, string path)
    {
        for (var i = 0; i < files.Count; i++)
        {
            if (SamePath(files[i].Path, path)) return i;
        }

        return null;
    }

    static bool SamePath(string a, string b) => string.Equals(
        Path.GetFullPath(a), Path.GetFullPath(b),
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// The effective model with every secret masked, for display.
    /// <para>
    /// <c>compose config</c> substitutes every variable, so its output carries the database password
    /// from <c>.env</c> in plain text. "A credential is never rendered" (CREDENTIALS.md) is not
    /// waived for a read-only view: two rules apply. A value of any variable that is secret — marked
    /// so, or named like one — is masked wherever it appears; and any key named like a secret has
    /// its value masked, which catches a password written into the file itself.
    /// </para>
    /// </summary>
    public string? Redact(IEnumerable<EnvVar> variables)
    {
        if (Yaml is not { } yaml) return null;

        // Longest first, so a secret that contains another is masked whole rather than in part.
        // Very short values are skipped: masking every "1" in the file would hide the file.
        foreach (var value in variables
            .Where(v => v.IsSecret && v.Value.Length >= 4)
            .Select(v => v.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(v => v.Length))
        {
            yaml = yaml.Replace(value, Mask, StringComparison.Ordinal);
        }

        var lines = yaml.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (KeyedValue().Match(lines[i]) is not { Success: true } match) continue;

            var key = match.Groups["key"].Value;
            var value = match.Groups["value"].Value.Trim().Trim('\'', '"');

            if (value.Length > 0 && value != Mask && new EnvVar(key, value).IsSecret)
                lines[i] = match.Groups["lead"].Value + Mask;
        }

        return string.Join('\n', lines);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<lead>\s*(?:-\s+)?(?<key>[A-Za-z_][A-Za-z0-9_.-]*)(?::\s+|=))(?<value>.+)$")]
    private static partial System.Text.RegularExpressions.Regex KeyedValue();
}
