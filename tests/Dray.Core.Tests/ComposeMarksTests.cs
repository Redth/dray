using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

public class ComposeMarksTests
{
    const string Compose = """
        services:
          db:
            image: postgres:${PG_TAG}
            environment:
              POSTGRES_PASSWORD: ${DB_PASSWORD}
              PLAIN: ${SHOWN}
              GONE: ${MISSING}
        """;

    [Fact]
    public void AValueIsShownBesideItsReference()
    {
        var marks = ComposeMarks.Annotations(Compose, [new EnvVar("PG_TAG", "16"), new EnvVar("SHOWN", "visible")]);

        Assert.Contains(marks, m => m.Text == "→ 16" && m.Kind == "ok");
        Assert.Contains(marks, m => m.Text == "→ visible");
    }

    [Fact]
    public void ASecretIsMaskedInlineWhetherGuessedFromItsNameOrMarked()
    {
        var marks = ComposeMarks.Annotations(Compose,
        [
            new EnvVar("DB_PASSWORD", "hunter2"),
            new EnvVar("SHOWN", "visible") { Marked = true },
        ]);

        Assert.DoesNotContain(marks, m => m.Text.Contains("hunter2", StringComparison.Ordinal));
        Assert.DoesNotContain(marks, m => m.Text.Contains("visible", StringComparison.Ordinal) || (m.Hover ?? "").Contains("visible", StringComparison.Ordinal));
        Assert.Equal(2, marks.Count(m => m.Text == "→ ••••••••"));
    }

    [Fact]
    public void AnUnsetVariableSaysWhatComposeWillDoWithIt()
    {
        var mark = Assert.Single(ComposeMarks.Annotations(Compose, []), m => m.Text == "→ empty" && m.Hover!.Contains("MISSING", StringComparison.Ordinal));
        Assert.Equal("missing", mark.Kind);
    }
}

public class ComposeEnvironmentProblemTests
{
    static PlacedProblem PlaceOne(string yaml, string stderr)
    {
        var config = new ComposeConfig(null, ComposeProblems.Parse(stderr), 15);
        return Assert.Single(config.Place([("/d/compose.yaml", ComposeDocument.Parse(yaml))]));
    }

    [Fact]
    public void AListOfMapsUnderEnvironmentIsUnderlinedWhereItIs()
    {
        const string yaml = "services:\n  web:\n    image: nginx\n    environment:\n      - FOO: bar\n";
        var placed = PlaceOne(yaml, "services.web.environment.[0]: unexpected type map[string]interface {}");

        // On the item itself, and in the file's words rather than Go's.
        Assert.Equal(5, placed.Range?.Line);
        Assert.Equal("services.web.environment.[0] cannot be a mapping here", placed.Problem.Message);
    }

    [Fact]
    public void AStringWhereAMappingBelongsIsUnderlinedOnItsKey()
    {
        const string yaml = "services:\n  web:\n    image: nginx\n    environment: FOO=bar\n";
        var placed = PlaceOne(yaml, "validating /d/compose.yaml: services.web.environment must be a mapping");

        Assert.Equal(4, placed.Range?.Line);
    }

    [Fact]
    public void AMisspelledKeyIsUnderlinedOnTheKey()
    {
        const string yaml = "services:\n  web:\n    image: nginx\n    environmnt:\n      FOO: bar\n";
        var placed = PlaceOne(yaml, "validating /d/compose.yaml: services.web additional properties 'environmnt' not allowed");

        Assert.Equal(4, placed.Range?.Line);
    }
}
