using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Compose's interpolation rules, pinned. These are not Dray's invention and getting one wrong
/// would make the annotation lie about what is going to happen — which is worse than not annotating.
/// </summary>
public class ComposeInterpolationTests
{
    static Dictionary<string, string> Vars(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    static Substitution One(string text, params (string, string)[] vars)
        => Assert.Single(ComposeInterpolation.Find(text, Vars(vars)));

    // ---------------------------------------------------------------- the forms

    [Fact]
    public void ABracedReferenceResolves()
    {
        var s = One("image: myapp:${TAG}", ("TAG", "1.2"));

        Assert.Equal("TAG", s.Name);
        Assert.Equal("1.2", s.Resolved);
        Assert.Equal(SubstitutionState.Resolved, s.State);
    }

    [Fact]
    public void AnUnbracedReferenceResolvesToo()
        => Assert.Equal("1.2", One("image: myapp:$TAG", ("TAG", "1.2")).Resolved);

    [Fact]
    public void AnUnbracedReferenceStopsAtTheFirstCharacterANameCannotHave()
    {
        // `$TAG/data` is the variable TAG followed by a literal path, not a variable called
        // "TAG/data" — getting this wrong would annotate the wrong span.
        var s = One("- $TAG/data", ("TAG", "v1"));

        Assert.Equal("TAG", s.Name);
        Assert.Equal(4, s.Length);
    }

    [Fact]
    public void ADoubledDollarIsALiteralAndNotAReference()
    {
        // How a compose file writes a literal `$`. Treating it as a variable would annotate the
        // inside of a shell command.
        Assert.Empty(ComposeInterpolation.Find("command: echo $$HOME", Vars()));
    }

    // ---------------------------------------------------------------- the quiet failure

    [Fact]
    public void AnUnsetVariableWithNoDefaultIsMissingAndResolvesToNothing()
    {
        // Compose substitutes an empty string and carries on. The image tag becomes "myapp:",
        // which fails much later in a message about something else entirely.
        var s = One("image: myapp:${TAG}");

        Assert.Equal(SubstitutionState.Missing, s.State);
        Assert.Equal("", s.Resolved);
        Assert.True(s.IsProblem);
    }

    [Fact]
    public void TheEmptyStringItSubstitutesIsVisibleInThePreview()
        => Assert.Equal("image: myapp:", ComposeInterpolation.Apply("image: myapp:${TAG}", Vars()));

    // ---------------------------------------------------------------- defaults

    [Fact]
    public void AnUnsetVariableTakesItsDefault()
    {
        var s = One("image: myapp:${TAG:-latest}");

        Assert.Equal("latest", s.Resolved);
        Assert.Equal(SubstitutionState.Defaulted, s.State);
        Assert.False(s.IsProblem);
    }

    [Fact]
    public void ColonDashTreatsAnEmptyValueAsUnset()
        => Assert.Equal("latest", One("${TAG:-latest}", ("TAG", "")).Resolved);

    [Fact]
    public void BareDashKeepsAnEmptyValue()
    {
        // The trap. Someone who writes `TAG=` in their .env and expects ${TAG-latest} to give
        // "latest" gets an empty tag. Dray shows what will happen, not what was meant.
        var s = One("${TAG-latest}", ("TAG", ""));

        Assert.Equal("", s.Resolved);
        Assert.Equal(SubstitutionState.Resolved, s.State);
    }

    [Fact]
    public void ASetVariableIgnoresItsDefault()
        => Assert.Equal("1.2", One("${TAG:-latest}", ("TAG", "1.2")).Resolved);

    // ---------------------------------------------------------------- required

    [Fact]
    public void ARequiredVariableThatIsUnsetStopsCompose()
    {
        // Louder than Missing, and easier to diagnose because Compose refuses to run and says so.
        var s = One("image: ${REGISTRY:?set REGISTRY first}/app");

        Assert.Equal(SubstitutionState.Required, s.State);
        Assert.True(s.IsProblem);
    }

    [Fact]
    public void ARequiredVariableThatIsSetIsFine()
        => Assert.Equal(SubstitutionState.Resolved, One("${REGISTRY:?nope}", ("REGISTRY", "ghcr.io")).State);

    // ---------------------------------------------------------------- the inverse

    [Fact]
    public void PlusUsesItsArgumentOnlyWhenTheVariableIsSet()
    {
        Assert.Equal("--debug", One("${DEBUG:+--debug}", ("DEBUG", "1")).Resolved);
        Assert.Equal("", One("${DEBUG:+--debug}").Resolved);
    }

    [Fact]
    public void PlusIsNeverAProblemEitherWay()
    {
        Assert.False(One("${DEBUG:+--debug}").IsProblem);
        Assert.False(One("${DEBUG:+--debug}", ("DEBUG", "1")).IsProblem);
    }

    // ---------------------------------------------------------------- positions

    [Fact]
    public void PositionsAreOneBasedAndPointAtTheDollar()
    {
        // An editor's decoration API wants 1-based line and column, and an annotation one
        // character off lands inside the previous token.
        var s = One("services:\n  web:\n    image: ${TAG}\n", ("TAG", "1"));

        Assert.Equal(3, s.Line);
        Assert.Equal(12, s.Column);
        Assert.Equal(6, s.Length);
    }

    [Fact]
    public void SeveralOnOneLineAreFoundInOrderWithDistinctColumns()
    {
        var found = ComposeInterpolation.Find("${A}-${B}", Vars(("A", "1"), ("B", "2")));

        Assert.Equal(["A", "B"], found.Select(s => s.Name));
        Assert.Equal([1, 6], found.Select(s => s.Column));
    }

    [Fact]
    public void PositionsSurviveWindowsLineEndings()
    {
        var s = One("a\r\nb\r\n${TAG}", ("TAG", "1"));

        Assert.Equal(3, s.Line);
        Assert.Equal(1, s.Column);
    }

    // ---------------------------------------------------------------- applying

    [Fact]
    public void ApplyRewritesEveryReferenceAndLeavesTheRestAlone()
    {
        const string yaml = "image: ${REG:-docker.io}/app:${TAG}\nports:\n  - ${PORT}:80\n";

        Assert.Equal(
            "image: docker.io/app:1.2\nports:\n  - 8080:80\n",
            ComposeInterpolation.Apply(yaml, Vars(("TAG", "1.2"), ("PORT", "8080"))));
    }

    [Fact]
    public void ApplyLeavesADoubledDollarAlone()
    {
        // Not substituted, and not collapsed either: Dray is previewing what Compose reads, and
        // Compose is the one that turns `$$` into `$`.
        Assert.Equal("echo $$HOME", ComposeInterpolation.Apply("echo $$HOME", Vars()));
    }

    [Fact]
    public void AFileWithNoReferencesIsUnchanged()
    {
        const string yaml = "services:\n  web:\n    image: nginx:alpine\n";

        Assert.Equal(yaml, ComposeInterpolation.Apply(yaml, Vars()));
        Assert.Empty(ComposeInterpolation.Find(yaml, Vars()));
    }

    [Fact]
    public void AnUnclosedBraceIsNotAReference()
    {
        // Malformed YAML should not become a malformed annotation on top of it.
        Assert.Empty(ComposeInterpolation.Find("image: ${TAG", Vars()));
    }
}
