using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Reading a compose file with a real parser, and pointing at places in it.
/// </summary>
public class ComposeDocumentTests
{
    [Fact]
    public void AMergeKeyCarriesItsDependencies()
    {
        // The case a line scanner could not see: the edge lives in the anchor, not under the service.
        const string yaml = """
            x-app: &app
              depends_on: [db]
            services:
              web:
                <<: *app
                image: web
              db:
                image: postgres
            """;

        var web = ComposeDocument.Parse(yaml).Services.Single(s => s.Name == "web");

        Assert.Equal(["db"], web.DependsOn);
    }

    [Fact]
    public void QuotedAndFlowStyleServicesAreServices()
    {
        const string yaml = """
            services: { "web": { image: nginx, depends_on: { db: { condition: service_healthy } } }, db: { image: postgres } }
            """;

        var document = ComposeDocument.Parse(yaml);

        Assert.Equal(["web", "db"], document.Services.Select(s => s.Name));
        Assert.Equal(["db"], document.Services[0].DependsOn);
    }

    [Fact]
    public void PositionsAreOneBasedLikeAnEditors()
    {
        const string yaml = "services:\n  web:\n    image: nginx\n";

        var web = Assert.Single(ComposeDocument.Parse(yaml).Services);

        Assert.Equal(new YamlRange(2, 3, 2, 6), web.Key);
    }

    [Fact]
    public void TextThatIsNotYamlSaysWhereAndHasNoServices()
    {
        const string yaml = "services:\n  web:\n    image: nginx\n   ports: [1]\n";

        var document = ComposeDocument.Parse(yaml);

        Assert.Empty(document.Services);
        var error = Assert.IsType<YamlSyntaxError>(document.SyntaxError);
        Assert.Equal(4, error.Range.Line);
        Assert.DoesNotContain("Idx:", error.Message);
    }

    [Fact]
    public void LocateFollowsKeysAndListItems()
    {
        const string yaml = """
            services:
              web:
                image: nginx
                depends_on:
                  - cache
                  - db
                ports:
                  - "8080:80"
            """;

        var document = ComposeDocument.Parse(yaml);

        Assert.Equal(3, document.Locate(["services", "web", "image"])!.Range.Line);
        Assert.Equal(6, document.Locate(["services", "web", "depends_on", "db"])!.Range.Line);
        Assert.Equal(8, document.Locate(["services", "web", "ports", "0"])!.Range.Line);
    }

    [Fact]
    public void AnUnknownIndexStopsAtTheList()
    {
        // compose names "some item of ports" as ports.[] — which one is not knowable from the text.
        const string yaml = "services:\n  web:\n    ports:\n      - target: x\n";

        var location = ComposeDocument.Parse(yaml).Locate(["services", "web", "ports", "[]", "target"])!;

        Assert.False(location.Exact);
        Assert.Equal(3, location.Range.Line);
    }

    [Fact]
    public void APathThatDoesNotStartInTheFileIsNotFound()
        => Assert.Null(ComposeDocument.Parse("services:\n  web: {}\n").Locate(["volumes", "data"]));

    [Fact]
    public void BuildsAndProfilesAreRead()
    {
        var services = ComposeDocument.Parse("services:\n  api:\n    build: .\n  tools:\n    image: alpine\n    profiles: [debug, ops]\n").Services;

        Assert.True(services[0].Builds);
        Assert.Empty(services[0].Profiles);
        Assert.Equal(["debug", "ops"], services[1].Profiles);
    }

    [Fact]
    public void NothingAtAllIsEmpty()
    {
        Assert.Same(ComposeDocument.Empty, ComposeDocument.Parse(null));
        Assert.Same(ComposeDocument.Empty, ComposeDocument.Parse("   \n"));
        Assert.Null(ComposeDocument.Empty.SyntaxError);
    }
}

/// <summary>
/// Reading compose's refusals. Every input string here was printed by compose 5.4.0 against a file
/// made to trigger it — captured, not composed from documentation, of which there is none.
/// </summary>
public class ComposeProblemsTests
{
    [Fact]
    public void AnUnknownKeyPointsAtTheKey()
    {
        var problem = Assert.Single(ComposeProblems.Parse(
            "validating /tmp/cfgprobe/compose.yaml: services.web additional properties 'imagee' not allowed\n"));

        Assert.Equal(ProblemSeverity.Error, problem.Severity);
        Assert.Equal("/tmp/cfgprobe/compose.yaml", problem.File);
        Assert.Equal(["services", "web", "imagee"], problem.KeyPath);
        Assert.Equal("services.web additional properties 'imagee' not allowed", problem.Message);
    }

    [Fact]
    public void AWrongTypeKeepsItsPath()
    {
        var problem = Assert.Single(ComposeProblems.Parse(
            "validating /tmp/cfgprobe/compose.yaml: services.web.healthcheck.test must be a string"));

        Assert.Equal(["services", "web", "healthcheck", "test"], problem.KeyPath);
    }

    [Fact]
    public void ASyntaxErrorCarriesItsOwnPosition()
    {
        var problem = Assert.Single(ComposeProblems.Parse(
            "failed to parse /tmp/cfgprobe/compose.yaml: go-yaml load error in parser (while parsing a block mapping) at L2.C3-L4.C4: did not find expected key"));

        Assert.Equal(new YamlRange(2, 3, 4, 4), problem.Range);
        Assert.Equal("did not find expected key", problem.Message);
    }

    [Fact]
    public void AScannerErrorIsASinglePoint()
    {
        // The scanner, unlike the parser, names one position. Found by saving a broken file from
        // the editor and reading what compose printed.
        var problem = Assert.Single(ComposeProblems.Parse(
            "failed to parse /tmp/dray-ui-probe/compose.yaml: go-yaml load error in scanner at L14.C12: mapping values are not allowed in this context"));

        Assert.Equal(new YamlRange(14, 12, 14, 13), problem.Range);
        Assert.Equal("/tmp/dray-ui-probe/compose.yaml", problem.File);
        Assert.Equal("mapping values are not allowed in this context", problem.Message);
    }

    [Fact]
    public void AnUndefinedDependencyPointsAtTheEdge()
    {
        var problem = Assert.Single(ComposeProblems.Parse(
            "service \"web\" depends on undefined service \"db\": invalid compose project"));

        Assert.Equal(["services", "web", "depends_on", "db"], problem.KeyPath);
    }

    [Fact]
    public void AnInterpolationFailureNamesTheField()
    {
        var required = Assert.Single(ComposeProblems.Parse(
            "error while interpolating services.web.image: required variable TAG is missing a value: set TAG"));

        Assert.Equal(["services", "web", "image"], required.KeyPath);
        Assert.Equal("required variable TAG is missing a value: set TAG", required.Message);

        var cast = Assert.Single(ComposeProblems.Parse(
            "error while interpolating services.web.ports.[].target: failed to cast to expected type: strconv.Atoi: parsing \"notanumber\": invalid syntax"));

        Assert.Equal(["services", "web", "ports", "[]", "target"], cast.KeyPath);
    }

    [Fact]
    public void EachDecodingFailureIsItsOwnProblem()
    {
        var problem = Assert.Single(ComposeProblems.Parse(
            "decoding failed due to the following error(s):\n\n'services[web].mem_limit' invalid size: 'lots'\n"));

        Assert.Equal(["services", "web", "mem_limit"], problem.KeyPath);
    }

    [Fact]
    public void WarningsOnASuccessfulRunAreWarnings()
    {
        const string stderr = """
            time="2026-10-22T20:26:46-04:00" level=warning msg="The \"TAG\" variable is not set. Defaulting to a blank string."
            time="2026-10-22T20:26:46-04:00" level=warning msg="/tmp/cfgprobe/compose.yaml: the attribute `version` is obsolete, it will be ignored, please remove it to avoid potential confusion"
            """;

        var problems = ComposeProblems.Parse(stderr);

        Assert.Equal(2, problems.Count);
        Assert.All(problems, p => Assert.Equal(ProblemSeverity.Warning, p.Severity));
        Assert.Equal("The \"TAG\" variable is not set. Defaulting to a blank string.", problems[0].Message);
        Assert.Equal(["version"], problems[1].KeyPath);
    }

    [Fact]
    public void AnythingElseIsStillReported()
    {
        // Unplaced, verbatim. An error Dray cannot locate is still one the user has to read.
        var problem = Assert.Single(ComposeProblems.Parse("invalid hostPort: 99999\n"));

        Assert.Equal("invalid hostPort: 99999", problem.Message);
        Assert.Empty(problem.KeyPath);
        Assert.Null(problem.Range);
    }

    [Fact]
    public void SilenceIsNoProblems() => Assert.Empty(ComposeProblems.Parse(""));
}

/// <summary>
/// Compose's verdict on a stack, placed in the file and safe to show.
/// </summary>
public class ComposeConfigTests
{
    static readonly Dray.Core.Engine.ComposeCommand Plugin = new("docker", ["compose"], "5.4.0");

    static Dray.Core.Engine.ComposeTarget Target => new(["--context", "nas"], new Dictionary<string, string>());

    [Fact]
    public async Task AnAcceptedStackKeepsItsModelAndItsWarnings()
    {
        var runner = new Fakes.RecordingProcessRunner { ExitCode = 0 };
        runner.Output.Add(new("name: web", IsError: false));
        runner.Output.Add(new("services:", IsError: false));
        runner.Output.Add(new("time=\"x\" level=warning msg=\"The \\\"TAG\\\" variable is not set. Defaulting to a blank string.\"", IsError: true));

        var config = await new Dray.Core.Engine.ComposeCli(runner, () => new Dictionary<string, string>())
            .ConfigAsync(Plugin, Target, "web", ["/s/compose.yaml"], "/s", TestContext.Current.CancellationToken);

        Assert.True(config.IsValid);
        Assert.Equal("name: web\nservices:\n", config.Yaml);
        Assert.Equal(ProblemSeverity.Warning, Assert.Single(config.Problems).Severity);
        Assert.Equal(["--context", "nas", "compose", "-p", "web", "-f", "/s/compose.yaml", "config"], runner.Streams[0].Arguments);
    }

    [Fact]
    public async Task ARefusedStackHasNoModel()
    {
        var runner = new Fakes.RecordingProcessRunner { ExitCode = 1 };
        runner.Output.Add(new("validating /s/compose.yaml: services.web additional properties 'imagee' not allowed", IsError: true));

        var config = await new Dray.Core.Engine.ComposeCli(runner, () => new Dictionary<string, string>())
            .ConfigAsync(Plugin, Target, "web", [], "/s", TestContext.Current.CancellationToken);

        Assert.False(config.IsValid);
        Assert.Null(config.Yaml);
        Assert.Equal(["services", "web", "imagee"], Assert.Single(config.Problems).KeyPath);
    }

    [Fact]
    public void AnUnreportedExitIsNotAcceptance()
        => Assert.False(new ComposeConfig("services: {}", [], ExitCode: null).IsValid);

    [Fact]
    public void ProblemsLandInTheFileAndOnTheLineTheyAreAbout()
    {
        var base_ = ComposeDocument.Parse("services:\n  web:\n    imagee: nginx\n");
        var overrides = ComposeDocument.Parse("services:\n  web:\n    depends_on: [db]\n");

        var config = new ComposeConfig(null,
        [
            new ComposeProblem("additional properties 'imagee' not allowed", ProblemSeverity.Error, "/s/compose.yaml", ["services", "web", "imagee"]),

            // No file named: found in whichever file has the path — here, the override.
            new ComposeProblem("web depends on db", ProblemSeverity.Error, Path: ["services", "web", "depends_on", "db"]),

            new ComposeProblem("in a file this stack does not have", ProblemSeverity.Error, "/elsewhere/x.yaml", ["services"]),
            new ComposeProblem("invalid hostPort: 99999", ProblemSeverity.Error),
        ], 1);

        var placed = config.Place([("/s/compose.yaml", base_), ("/s/compose.override.yaml", overrides)]);

        Assert.Equal(4, placed.Count);
        Assert.Equal((0, 3), (placed[0].FileIndex, placed[0].Range!.Line));
        Assert.Equal((1, 3), (placed[1].FileIndex, placed[1].Range!.Line));

        // Unplaceable, but still listed: the user has to read them either way.
        Assert.Null(placed[2].FileIndex);
        Assert.Null(placed[3].Range);
    }

    [Fact]
    public void SecretsAreMaskedWhereverTheyAppear()
    {
        const string yaml = """
            services:
              db:
                environment:
                  POSTGRES_PASSWORD: hunter2-long
                  POSTGRES_USER: app
                  DATABASE_URL: postgres://app:s3cr3t@db/app
                image: postgres:16
              api:
                command: ["serve", "--token", "tok-123456"]
            """;

        var config = new ComposeConfig(yaml, [], 0);

        var shown = config.Redact([new EnvVar("API_TOKEN", "tok-123456")])!;

        // From .env, by value — it was substituted into a command line, nowhere near a key.
        Assert.DoesNotContain("tok-123456", shown);

        // In the file itself, by the key's name, and by a URL carrying credentials.
        Assert.DoesNotContain("hunter2-long", shown);
        Assert.DoesNotContain("s3cr3t", shown);

        // And nothing else.
        Assert.Contains("POSTGRES_USER: app", shown);
        Assert.Contains("image: postgres:16", shown);
        Assert.Contains("POSTGRES_PASSWORD: " + ComposeConfig.Mask, shown);
    }
}
