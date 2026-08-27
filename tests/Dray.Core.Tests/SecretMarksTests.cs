using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The secret marking, and the round trip through container labels that makes it survive.
/// <para>
/// The heuristic and the mark are both load-bearing, and these pin the boundary between them: the
/// guess is a floor that catches what nobody thought to mark, and the mark is an override that
/// catches what no name could reveal.
/// </para>
/// </summary>
public class SecretMarksTests
{
    // ---------------------------------------------------------------- the guess

    [Fact]
    public void AnUnmarkedVariableStillFallsToTheHeuristic()
    {
        // The case that motivated keeping the guess: a real stack read during this design carried
        // a JWT API key its own tool had stored as not-secret. Nobody marked it; the name does.
        Assert.True(new EnvVar("ABS_API_KEY", "eyJhbGciOi...").IsSecret);
        Assert.False(new EnvVar("TZ", "UTC").IsSecret);
    }

    [Fact]
    public void AnUnmarkedVariableIsNotReportedAsMarked()
    {
        // The UI words "Dray thinks this looks like a secret" differently from "you said so" —
        // one is worth correcting and the other is not.
        var guessed = new EnvVar("API_TOKEN", "abc");

        Assert.True(guessed.IsSecret);
        Assert.False(guessed.IsMarked);
    }

    // ---------------------------------------------------------------- the override

    [Fact]
    public void MarkingSecretBeatsANameThatLooksHarmless()
    {
        // What no heuristic can know. LICENCE_BLOB is a secret and does not look like one.
        var plain = new EnvVar("LICENCE_BLOB", "AAAA");

        Assert.False(plain.IsSecret);
        Assert.True((plain with { Marked = true }).IsSecret);
    }

    [Fact]
    public void MarkingPlainBeatsANameThatLooksAlarming()
    {
        var guessed = new EnvVar("API_TOKEN", "abc");

        Assert.True(guessed.IsSecret);
        Assert.False((guessed with { Marked = false }).IsSecret);
    }

    // ---------------------------------------------------------------- to labels

    [Fact]
    public void OnlyDecidedVariablesAreWrittenToLabels()
    {
        // Writing every key with its computed value would freeze today's heuristic onto the
        // container for ever, so a later improvement to the rule could never reach it.
        var labels = SecretMarks.ToLabels(
        [
            new EnvVar("LICENCE_BLOB", "x") { Marked = true },
            new EnvVar("BUILD_KEY_ID", "y") { Marked = false },
            new EnvVar("DATABASE_PASSWORD", "z"),
            new EnvVar("TZ", "UTC"),
        ]);

        Assert.Equal("LICENCE_BLOB", labels[SecretMarks.SecretLabel]);
        Assert.Equal("BUILD_KEY_ID", labels[SecretMarks.PlainLabel]);
    }

    [Fact]
    public void NothingDecidedWritesNoLabels()
    {
        // A container nobody marked anything on should carry no Dray labels at all.
        Assert.Empty(SecretMarks.ToLabels([new EnvVar("TZ", "UTC"), new EnvVar("API_TOKEN", "x")]));
    }

    [Fact]
    public void SeveralKeysShareOneLabel()
    {
        var labels = SecretMarks.ToLabels(
        [
            new EnvVar("A", "1") { Marked = true },
            new EnvVar("B", "2") { Marked = true },
        ]);

        Assert.Equal("A,B", labels[SecretMarks.SecretLabel]);
        Assert.False(labels.ContainsKey(SecretMarks.PlainLabel));
    }

    // ---------------------------------------------------------------- and back

    [Fact]
    public void AMarkSurvivesTheRoundTripThroughLabels()
    {
        EnvVar[] original =
        [
            new("LICENCE_BLOB", "x") { Marked = true },
            new("BUILD_KEY_ID", "y") { Marked = false },
            new("TZ", "UTC"),
        ];

        var labels = SecretMarks.ToLabels(original);

        // What comes back from the engine has no marks — they live only in the labels.
        var read = SecretMarks.Apply(original.Select(v => new EnvVar(v.Key, v.Value)), labels.ToDictionary());

        Assert.True(read.Single(v => v.Key == "LICENCE_BLOB").IsSecret);
        Assert.False(read.Single(v => v.Key == "BUILD_KEY_ID").IsSecret);

        // Untouched, so the heuristic still decides it.
        Assert.Null(read.Single(v => v.Key == "TZ").Marked);
    }

    [Fact]
    public void ContainersWithNoLabelsAreLeftToTheHeuristic()
    {
        var read = SecretMarks.Apply([new EnvVar("API_TOKEN", "x"), new EnvVar("TZ", "UTC")], null);

        Assert.All(read, v => Assert.Null(v.Marked));
        Assert.True(read[0].IsSecret);
    }

    [Fact]
    public void AKeyInBothListsIsTreatedAsSecret()
    {
        // Should not happen, but when the two disagree the safe reading is the one that hides
        // something unnecessarily rather than the one that reveals something.
        var labels = new Dictionary<string, string>
        {
            [SecretMarks.SecretLabel] = "TOKEN",
            [SecretMarks.PlainLabel] = "TOKEN",
        };

        Assert.True(Assert.Single(SecretMarks.Apply([new EnvVar("TOKEN", "x")], labels)).IsSecret);
    }

    [Fact]
    public void WhitespaceAroundKeysInALabelIsIgnored()
    {
        // Labels are editable by anyone with the engine; a hand-written one is likely spaced out.
        var labels = new Dictionary<string, string> { [SecretMarks.SecretLabel] = " A , B " };

        Assert.All(SecretMarks.Apply([new EnvVar("A", "1"), new EnvVar("B", "2")], labels),
            v => Assert.True(v.IsSecret));
    }

    [Fact]
    public void OtherLabelsOnTheContainerAreIgnored()
    {
        var labels = new Dictionary<string, string>
        {
            ["com.docker.compose.project"] = "shop",
            [SecretMarks.SecretLabel] = "TOKEN",
        };

        var read = SecretMarks.Apply([new EnvVar("TOKEN", "x"), new EnvVar("TZ", "UTC")], labels);

        Assert.True(read[0].IsSecret);
        Assert.Null(read[1].Marked);
    }
}
