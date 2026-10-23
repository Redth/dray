using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Reading <c>/images/search</c>, which the two engines answer in two different shapes.
/// <para>
/// The podman fixture is captured verbatim from podman 6.0.2 on this machine. The Docker one is
/// the shape its API documents; it is asserted here so a reader built against the measured engine
/// cannot quietly stop working on the other, which would look like an empty registry rather than
/// a parsing failure.
/// </para>
/// </summary>
public class ImageSearchTests
{
    const string Podman = """
        [
          {"Index":"docker.io","Name":"docker.io/mcp/redis","Description":"Access to Redis database operations.","Stars":14,"Official":"","Automated":"","Tag":""},
          {"Index":"docker.io","Name":"docker.io/library/redis","Description":"Redis is the world's fastest data platform.","Stars":13612,"Official":"[OK]","Automated":"","Tag":""}
        ]
        """;

    const string Docker = """
        [
          {"description":"Redis is an open source key-value store.","is_official":true,"is_automated":false,"name":"redis","star_count":13612},
          {"description":"Bitnami Redis","is_official":false,"is_automated":true,"name":"bitnami/redis","star_count":250}
        ]
        """;

    [Fact]
    public void ReadsPodmansShape()
    {
        var results = ImageSearch.Parse(Podman);

        Assert.Equal(2, results.Count);
        Assert.Equal("docker.io/library/redis", results[1].Name);
        Assert.Equal(13612, results[1].Stars);
        Assert.True(results[1].IsOfficial);

        // "" is podman's "no", not a missing field.
        Assert.False(results[0].IsOfficial);
    }

    [Fact]
    public void ReadsDockersShape()
    {
        var results = ImageSearch.Parse(Docker);

        Assert.Equal(2, results.Count);
        Assert.Equal("redis", results[0].Name);
        Assert.Equal(13612, results[0].Stars);
        Assert.True(results[0].IsOfficial);
        Assert.False(results[1].IsOfficial);
    }

    [Fact]
    public void TheNameKeepsWhateverTheEngineGaveIt()
    {
        // Both pull. Rewriting podman's answer to Docker's short form would produce a reference
        // that works by luck rather than because the engine named it.
        Assert.Equal("docker.io/library/redis", ImageSearch.Parse(Podman)[1].Name);
        Assert.Equal("redis", ImageSearch.Parse(Docker)[0].Name);
    }

    [Theory]
    [InlineData("docker.io/library/redis", "redis")]
    [InlineData("docker.io/mcp/redis", "mcp/redis")]
    [InlineData("redis", "redis")]
    [InlineData("bitnami/redis", "bitnami/redis")]
    [InlineData("ghcr.io/redth/dray", "redth/dray")]
    public void TheDisplayNameDropsTheRegistryAndTheLibraryPrefix(string name, string expected)
        => Assert.Equal(expected, new ImageSearchResult(name).ShortName);

    [Fact]
    public void AnEngineThatDoesNotCountStarsSaysSoRatherThanZero()
    {
        // A fabricated zero would sort every result from that engine last.
        var results = ImageSearch.Parse("""[{"name":"redis"}]""");

        Assert.Equal(-1, Assert.Single(results).Stars);
    }

    [Fact]
    public void AnEntryWithNoNameIsNotAResult()
        => Assert.Empty(ImageSearch.Parse("""[{"description":"nothing to pull"}]"""));

    [Fact]
    public void NothingAtAllIsAnEmptyList()
    {
        Assert.Empty(ImageSearch.Parse(null));
        Assert.Empty(ImageSearch.Parse(""));
        Assert.Empty(ImageSearch.Parse("[]"));
    }

    [Fact]
    public void AResponseThatIsNotSearchResultsIsAFailureRatherThanNoResults()
    {
        // The distinction that matters on screen: "nothing matched" and "the engine broke" are
        // different sentences, and only one of them is worth retyping the search for.
        Assert.Throws<FormatException>(() => ImageSearch.Parse("{not json"));

        // A well-formed answer of the wrong shape genuinely has no results in it.
        Assert.Empty(ImageSearch.Parse("""{"message":"page not found"}"""));
    }

    /// <summary>
    /// Captured from Docker 29 answering <c>/images/search?term=quay.io/prometheus</c>: no host on
    /// the names, a README for a description, and a zero for stars that Quay never sent.
    /// </summary>
    const string Quay = """
        [
          {"star_count":0,"is_official":false,"name":"prometheus-operator/prometheus-config-reloader","is_automated":false,"description":""},
          {"star_count":0,"is_official":false,"name":"prometheus/prometheus","is_automated":false,"description":"<h1 align=\"center\" style=\"border-bottom: none\">\n    <a href=\"https://prometheus.io\" target=\"_blank\"><img alt=\"Prometheus\" src=\"/documentation/images/prometheus-logo.svg\"></a><br>Prometheus\n</h1>\n\n<p align=\"center\">Visit <a href=\"https://prometheus.io\" target=\"_blank\">prometheus.io</a> for the full documentation,\nexamples and guides.</p>\n\n<div align=\"center\">\n\n[![CI](https://github.com/prometheus/prometheus/actions/workflows/ci.yml/badge.svg)](https://github.com/prometheus/prometheus/actions/workflows/ci.yml)\n"}
        ]
        """;

    [Fact]
    public void AnotherRegistrysResultsKeepTheirRegistry()
    {
        // Pulled as the engine gave it, "prometheus/prometheus" comes from Docker Hub — a different
        // image, from a different publisher, or nothing at all.
        var results = ImageSearch.Parse(Quay, "quay.io");

        Assert.Equal("quay.io/prometheus/prometheus", results[1].Name);
        Assert.Equal("prometheus/prometheus", results[1].ShortName);
    }

    [Fact]
    public void ANameThatAlreadyHasARegistryIsLeftAlone()
        => Assert.Equal("docker.io/library/redis", ImageSearch.Parse(Podman, "quay.io")[1].Name);

    [Fact]
    public void DockerHubResultsAreNotPrefixed()
        => Assert.Equal("redis", ImageSearch.Parse(Docker, "docker.io")[0].Name);

    [Fact]
    public void StarsFromAnotherRegistryAreUnknownNotZero()
        => Assert.All(ImageSearch.Parse(Quay, "quay.io"), r => Assert.Equal(-1, r.Stars));

    [Fact]
    public void AReadmeBecomesALineOfText()
    {
        var description = ImageSearch.Parse(Quay, "quay.io")[1].Description;

        Assert.Equal("Prometheus Visit prometheus.io for the full documentation, examples and guides.", description);
    }

    [Fact]
    public void AnEmptyDescriptionIsNone()
        => Assert.Null(ImageSearch.Parse(Quay, "quay.io")[0].Description);

    [Theory]
    [InlineData("Redis is the world's fastest data platform.", "Redis is the world's fastest data platform.")]
    [InlineData("**Bold** and `code` and __strong__", "Bold and code and strong")]
    [InlineData("Uses my_app and C# tooling", "Uses my_app and C# tooling")]
    [InlineData("# Heading\nBody", "Heading Body")]
    [InlineData("Tom &amp; Jerry", "Tom & Jerry")]
    public void PlainTextKeepsTheWords(string input, string expected)
        => Assert.Equal(expected, ImageSearch.Plain(input));

    [Fact]
    public void ReferenceStyleBadgesAreDropped()
    {
        // Measured: Quay's node-exporter README, as the engine passes it through.
        var text = ImageSearch.Plain("Node exporter [![CircleCI](https://circleci.com/x.svg)][circleci] [![Docker Repository on Quay][quay_image]][quay] Prometheus exporter for \\*NIX kernels.\n\n[quay]: https://quay.io/x");

        Assert.Equal("Node exporter Prometheus exporter for *NIX kernels.", text);
    }

    [Fact]
    public void ALongDescriptionIsCut()
        => Assert.EndsWith("…", ImageSearch.Plain(new string('a', 500)));

    [Fact]
    public void TheOfficialFilterIsTheOneBothEnginesAccept()
    {
        Assert.Equal("""{"is-official":["true"]}""", new ImageSearchFilters(OfficialOnly: true).ToQuery());
        Assert.Null(ImageSearchFilters.None.ToQuery());
    }
}
