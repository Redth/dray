using Dray.Core.Model;

namespace Dray.Core.Engine;

/// <summary>How buildx was found on this machine.</summary>
/// <param name="LeadingArguments">
/// What goes before the subcommand. Empty for the standalone binary, <c>["buildx"]</c> for the
/// plugin form.
/// </param>
public sealed record BuildxCommand(string Executable, IReadOnlyList<string> LeadingArguments, string Version)
{
    public string Display => LeadingArguments.Count == 0
        ? Executable
        : $"{Executable} {string.Join(' ', LeadingArguments)}";

    public IReadOnlyList<string> With(params string[] arguments) => [.. LeadingArguments, .. arguments];
}

/// <summary>
/// buildx, as a separate program.
/// <para>
/// Separate because it is: buildx is a CLI plugin with its own release cycle, and a machine can
/// have Docker without it, buildx without the plugin wiring, or several builders configured
/// against endpoints that no longer exist. None of that is visible over the Engine API, which is
/// why Dray's own builds go through the API and this exists alongside rather than underneath.
/// </para>
/// </summary>
public sealed class BuildxCli(IProcessRunner? runner = null)
{
    readonly IProcessRunner _runner = runner ?? new SystemProcessRunner();

    /// <summary>
    /// The ways buildx ships, best first.
    /// <para>
    /// The plugin form first because it shares the CLI's context and configuration. The standalone
    /// binary is the fallback and is not hypothetical — on the machine this was written on,
    /// <c>docker buildx</c> answers "unknown command" while <c>docker-buildx</c> works, because
    /// Homebrew installs the binary without wiring it into that Docker's plugin directory.
    /// </para>
    /// </summary>
    static readonly (string Executable, string[] Leading)[] Candidates =
    [
        ("docker", ["buildx"]),
        ("docker-buildx", []),
        ("buildx", []),
    ];

    BuildxCommand? _found;
    bool _probed;

    /// <summary>
    /// Find a working buildx, or null. Probed once — this shells out, and the answer does not
    /// change while the app is running.
    /// </summary>
    public async Task<BuildxCommand?> DetectAsync(CancellationToken ct = default)
    {
        if (_probed) return _found;

        foreach (var (executable, leading) in Candidates)
        {
            try
            {
                var result = await _runner
                    .RunAsync(executable, [.. leading, "version"], null, ct)
                    .ConfigureAwait(false);

                if (result.ExitCode != 0) continue;

                var version = result.StandardOutput
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();

                if (string.IsNullOrWhiteSpace(version)) continue;

                _found = new BuildxCommand(executable, leading, version);
                break;
            }
            catch (Exception)
            {
                // Not installed, not on PATH, or not executable. Try the next form.
            }
        }

        _probed = true;
        return _found;
    }

    /// <summary>
    /// The builders this machine has configured.
    /// <para>
    /// <c>--format json</c> rather than the default table: the table is column-aligned with node
    /// rows indented under their builder, and reading it back is guesswork the moment a name is
    /// long enough to run into the next column.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<BuildxBuilder>> ListAsync(
        BuildxCommand command, CancellationToken ct = default)
    {
        var result = await _runner
            .RunAsync(command.Executable, command.With("ls", "--format", "json"), null, ct)
            .ConfigureAwait(false);

        // buildx exits 0 and prints the builders even when one of them is unreachable — the error
        // is on the node, not on the command. So the output is read either way.
        return Buildx.Parse(result.StandardOutput);
    }

    /// <summary>
    /// Run a build on a named builder, streaming its output.
    /// <para>
    /// <c>--load</c>, always. A buildx build with a container driver leaves its result in the build
    /// cache and nowhere else, so without this the build succeeds and the image never appears in
    /// the list — which reads as Dray losing it.
    /// </para>
    /// </summary>
    public IAsyncEnumerable<ComposeOutput> BuildAsync(
        BuildxCommand command,
        BuildRequest request,
        string builder,
        CancellationToken ct = default)
    {
        List<string> arguments = [.. command.LeadingArguments, "build", "--builder", builder, "--load", "--progress", "plain"];

        if (request.Dockerfile is { Length: > 0 } dockerfile && dockerfile != "Dockerfile")
        {
            arguments.Add("--file");
            arguments.Add(dockerfile);
        }

        if (request.Tag is { Length: > 0 } tag)
        {
            arguments.Add("--tag");
            arguments.Add(tag);
        }

        if (request.NoCache) arguments.Add("--no-cache");
        if (request.Pull) arguments.Add("--pull");

        arguments.Add(".");

        // Run from the context directory and pass "." rather than the path: buildx resolves
        // .dockerignore relative to the context, and a context given as an absolute path from
        // somewhere else finds a different one — or none.
        return _runner.StreamAsync(command.Executable, arguments, request.ContextDirectory, ct);
    }
}
