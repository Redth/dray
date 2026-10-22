using Dray.Core.Engine;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Reading <c>wsl --list --verbose</c>.
/// <para>
/// The transcripts here are the real format, including its two traps: the output is UTF-16LE
/// unless <c>WSL_UTF8</c> is honoured, and the header and state words are localised. Neither can
/// be checked on this machine, so both are pinned here instead.
/// </para>
/// </summary>
public class WslDistroTests
{
    const string EnglishOutput = """
          NAME                   STATE           VERSION
        * Ubuntu                 Running         2
          Debian                 Stopped         2
          docker-desktop         Running         2
          docker-desktop-data    Stopped         2
          Legacy                 Stopped         1
        """;

    [Fact]
    public void ReadsNameStateAndVersion()
    {
        var distros = WslDistroReader.Parse(EnglishOutput);

        Assert.Equal(5, distros.Count);

        var ubuntu = distros[0];
        Assert.Equal("Ubuntu", ubuntu.Name);
        Assert.Equal(2, ubuntu.Version);
        Assert.True(ubuntu.IsRunning);
        Assert.True(ubuntu.IsDefault);

        Assert.False(distros[1].IsRunning);
        Assert.False(distros[1].IsDefault);
    }

    /// <summary>
    /// The header is skipped because its last column is not a number, not because it was
    /// recognised as a header. That is what makes the parser work in a locale whose header reads
    /// NOM / ÉTAT / VERSION.
    /// </summary>
    [Fact]
    public void SkipsTheHeaderWithoutMatchingOnItsWords()
    {
        var french = """
              NOM             ÉTAT            VERSION
            * Ubuntu          En cours        2
              Debian          Arrêté          2
            """;

        var distros = WslDistroReader.Parse(french);

        Assert.Equal(2, distros.Count);
        Assert.Equal("Ubuntu", distros[0].Name);
        Assert.Equal(2, distros[0].Version);
    }

    /// <summary>
    /// A localised state word is not "Running", so it reads as stopped. That is the right failure:
    /// running-ness only decides sort order and a caption, while the version — which is a digit
    /// everywhere — decides whether the distro is offered at all.
    /// </summary>
    [Fact]
    public void ALocalisedStateDegradesToStoppedRatherThanDroppingTheDistro()
    {
        var distros = WslDistroReader.Parse("""
              NOM             ÉTAT            VERSION
            * Ubuntu          En cours        2
            """);

        var ubuntu = Assert.Single(distros);
        Assert.False(ubuntu.IsRunning);
        Assert.True(ubuntu.CanHostAnEngine);
    }

    /// <summary>
    /// A build of wsl.exe that ignored WSL_UTF8 writes UTF-16LE, which arrives with a NUL after
    /// every character. Dropping them restores the text exactly.
    /// </summary>
    [Fact]
    public void RecoversTextFromUtf16Output()
    {
        var utf16ish = string.Concat(EnglishOutput.Select(c => c + "\0"));

        var distros = WslDistroReader.Parse(utf16ish);

        Assert.Equal(5, distros.Count);
        Assert.Equal("Ubuntu", distros[0].Name);
    }

    /// <summary>Real UTF-8 output has no NULs, so it must pass through untouched.</summary>
    [Fact]
    public void LeavesUtf8OutputAlone()
        => Assert.Equal(EnglishOutput, WslDistroReader.Decode(EnglishOutput));

    [Theory]
    [InlineData("Ubuntu", 2, true)]
    [InlineData("Debian", 2, true)]

    // WSL 1 has no kernel, no cgroups and no namespaces. No engine runs on it, so offering one
    // would be offering a host that cannot work.
    [InlineData("Legacy", 1, false)]

    // Reached through the pipe each product publishes, so offering the distro as well would list
    // one engine twice in the picker.
    [InlineData("docker-desktop", 2, false)]
    [InlineData("docker-desktop-data", 2, false)]
    [InlineData("rancher-desktop", 2, false)]
    [InlineData("podman-machine-default", 2, false)]
    [InlineData("podman-machine-work", 2, false)]
    public void DecidesWhichDistrosAreWorthOffering(string name, int version, bool expected)
        => Assert.Equal(expected, new WslDistro(name, version, IsRunning: true, IsDefault: false).CanHostAnEngine);

    [Fact]
    public void IgnoresBlankAndMalformedLines()
    {
        var distros = WslDistroReader.Parse("\r\n  \r\nnot a table row\r\n  Ubuntu   Running   2\r\n");

        Assert.Equal("Ubuntu", Assert.Single(distros).Name);
    }

    /// <summary>WSL prints nothing but a header when no distribution is installed.</summary>
    [Fact]
    public void HandlesAnEmptyInstall()
        => Assert.Empty(WslDistroReader.Parse("  NAME   STATE   VERSION\r\n"));
}
