using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Edits are insertions. The test that matters is the first: every byte that was in the file is
/// still in it, which is how a comment survives every edit Dray makes.
/// </summary>
public class ComposeEditsTests
{
    const string File = """
        # The shop. Do not expose the database.
        name: shop

        services:
          web:   # the front end
            image: nginx:1.27
            ports:
              - "8080:80"   # public
            environment:
              TZ: UTC

          db:
            image: postgres:16
            # environment comes from .env

        volumes:
          pgdata: {}
        """;

    static string Apply(ComposeEdits.Result result, string text = File)
    {
        Assert.Null(result.Declined);
        return result.Splice!.ApplyTo(text);
    }

    static void EveryOriginalLineSurvives(string before, string after)
    {
        var remaining = after.Split('\n').ToList();

        foreach (var line in before.Split('\n'))
        {
            Assert.Contains(line, remaining);
            remaining.Remove(line);
        }
    }

    [Fact]
    public void AddingAPortKeepsEveryCommentAndEveryLine()
    {
        var after = Apply(ComposeEdits.AddPort(File, "web", "8443:443"));

        EveryOriginalLineSurvives(File, after);
        Assert.Contains("      - \"8080:80\"   # public\n      - \"8443:443\"\n    environment:", after);
    }

    [Fact]
    public void APortOnAServiceWithoutPortsCreatesTheListAtTheServicesIndent()
    {
        var after = Apply(ComposeEdits.AddPort(File, "db", "5432:5432"));

        // At the service's own indent, right after its last setting; the comment below it stays.
        EveryOriginalLineSurvives(File, after);
        Assert.Contains("    image: postgres:16\n    ports:\n      - \"5432:5432\"\n    # environment comes from .env\n", after);
        Assert.Equal(["5432:5432"], ComposeDocument.Parse(after).ItemsOf("db", "ports").Select(p => p.Value));
    }

    [Fact]
    public void AVariableUsesTheFormTheServiceAlreadyUses()
    {
        var map = Apply(ComposeEdits.AddEnvironment(File, "web", "LOG_LEVEL", "debug"));
        Assert.Contains("      TZ: UTC\n      LOG_LEVEL: \"debug\"\n", map);

        const string list = "services:\n  web:\n    environment:\n      - TZ=UTC\n";
        Assert.Contains("      - TZ=UTC\n      - \"LOG_LEVEL=debug\"\n", Apply(ComposeEdits.AddEnvironment(list, "web", "LOG_LEVEL", "debug"), list));
    }

    [Fact]
    public void AServiceWithNoEnvironmentStartsTheMapForm()
    {
        var after = Apply(ComposeEdits.AddEnvironment(File, "db", "PGTZ", "UTC"));

        Assert.Contains("    image: postgres:16\n    environment:\n      PGTZ: \"UTC\"\n", after);
        Assert.Equal(["PGTZ=UTC"], ComposeDocument.Parse(after).ItemsOf("db", "environment").Select(i => i.Value));
    }

    [Fact]
    public void ANewServiceGoesAtTheEndOfServicesNotTheEndOfTheFile()
    {
        var after = Apply(ComposeEdits.AddService(File, "cache", "redis:7-alpine", ["6379:6379"]));

        EveryOriginalLineSurvives(File, after);
        Assert.Equal(["web", "db", "cache"], ComposeDocument.Parse(after).Services.Select(s => s.Name));
        Assert.True(after.IndexOf("cache:", StringComparison.Ordinal) < after.IndexOf("volumes:", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyFileGetsAServicesBlock()
    {
        var after = Apply(ComposeEdits.AddService("", "web", "nginx:1.27"), "");

        Assert.Equal("web", Assert.Single(ComposeDocument.Parse(after).Services).Name);
    }

    [Fact]
    public void LineEndingsAreTheFilesOwn()
    {
        var crlf = File.Replace("\n", "\r\n", StringComparison.Ordinal);
        var after = Apply(ComposeEdits.AddPort(crlf, "web", "8443:443"), crlf);

        Assert.DoesNotContain("\n", after.Replace("\r\n", "", StringComparison.Ordinal));
    }

    [Fact]
    public void AKeyWithNothingUnderItGetsItsFirstItem()
    {
        const string text = "services:\n  web:\n    image: nginx\n    ports:\n";
        var after = Apply(ComposeEdits.AddPort(text, "web", "80:80"), text);

        Assert.Equal(["80:80"], ComposeDocument.Parse(after).ItemsOf("web", "ports").Select(p => p.Value));
    }

    [Fact]
    public void ABlockScalarDoesNotSwallowTheInsertion()
    {
        const string text = "services:\n  web:\n    image: nginx\n    command: |\n      echo one\n      echo two\n\n  db:\n    image: postgres\n";
        var after = Apply(ComposeEdits.AddPort(text, "web", "80:80"), text);

        var document = ComposeDocument.Parse(after);
        Assert.Equal(["80:80"], document.ItemsOf("web", "ports").Select(p => p.Value));
        Assert.Equal("echo one\necho two\n", document.ItemsOf("web", "command").Single().Value);
    }

    [Fact]
    public void InlineBlocksAreDeclinedRatherThanRewritten()
    {
        const string text = "services: { web: { image: nginx } }\n";

        Assert.NotNull(ComposeEdits.AddService(text, "db", "postgres").Declined);
        Assert.NotNull(ComposeEdits.AddPort(text, "web", "80:80").Declined);
    }

    [Fact]
    public void ADuplicateIsDeclined()
    {
        Assert.NotNull(ComposeEdits.AddService(File, "web", "nginx").Declined);
        Assert.NotNull(ComposeEdits.AddEnvironment(File, "web", "TZ", "x").Declined);
        Assert.NotNull(ComposeEdits.AddPort(File, "nope", "80:80").Declined);
    }
}

public class ComposeLintTests
{
    static IReadOnlyList<LintFinding> Lint(string yaml, bool remote = false, params PublishedPort[] published)
        => ComposeLint.Check(ComposeDocument.Parse(yaml), new LintContext(published, remote));

    [Theory]
    [InlineData("nginx", true)]
    [InlineData("nginx:latest", true)]
    [InlineData("nginx:1.27", false)]
    [InlineData("registry:5000/app", true)]
    [InlineData("registry:5000/app:2", false)]
    [InlineData("nginx@sha256:abc", false)]
    [InlineData("nginx:${TAG}", false)]
    public void AnUnpinnedImageIsNamed(string image, bool flagged)
        => Assert.Equal(flagged, Lint($"services:\n  web:\n    image: {image}\n").Count == 1);

    [Fact]
    public void AnImageThatIsBuiltHereIsNotUnpinned()
        => Assert.Empty(Lint("services:\n  web:\n    build: .\n    image: myapp\n"));

    [Fact]
    public void APortAnotherContainerHoldsIsFlaggedOnItsLine()
    {
        var findings = Lint(
            "services:\n  web:\n    image: nginx:1\n    ports:\n      - \"127.0.0.1:8080:80\"\n      - target: 443\n        published: 8443\n",
            published: [new PublishedPort(8080, "tcp", "blog-web-1 (blog)"), new PublishedPort(8443, "udp", "x")]);

        var finding = Assert.Single(findings);
        Assert.Equal(5, finding.Range.Line);
        Assert.Contains("blog-web-1 (blog)", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("8080:80", 8080)]
    [InlineData("8080:80/udp", 8080)]
    [InlineData("0.0.0.0:9000:9000", 9000)]
    [InlineData("80", null)]
    [InlineData("8000-8010:8000-8010", null)]
    public void HostPortsAreReadFromEveryShortForm(string mapping, int? port)
        => Assert.Equal(port, ComposeLint.HostPort(new YamlItem(mapping, new YamlRange(1, 1, 1, 2), new Dictionary<string, string>())));

    [Fact]
    public void TheEngineSocketIsNamed()
        => Assert.Single(Lint("services:\n  agent:\n    image: x:1\n    volumes:\n      - /var/run/docker.sock:/var/run/docker.sock\n"));

    [Fact]
    public void ARelativePathIsOnlyAProblemWhenTheEngineIsElsewhere()
    {
        const string yaml = "services:\n  web:\n    image: x:1\n    volumes:\n      - ./site:/usr/share/nginx/html\n      - data:/data\n";

        Assert.Empty(Lint(yaml, remote: false));
        Assert.Equal(5, Assert.Single(Lint(yaml, remote: true)).Range.Line);
    }
}
