using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

public class VolumeSummaryTests
{
    static VolumeSummary Volume(string name, params string[] usedBy)
        => new() { Name = name, Driver = "local", UsedBy = usedBy };

    [Fact]
    public void AnAnonymousVolumeIsNotShownAsThoughItsNameWereChosen()
    {
        // The engine names these with 64 hex characters. Printing that as a name implies someone
        // picked it, and it crowds out every other column.
        var volume = Volume("06ae69ae6f466124e4e235e4b83c0fe9fc1ff2199979abacdb44176e9fdb9d15");

        Assert.True(volume.IsAnonymous);
        Assert.Equal("anonymous · 06ae69ae6f46", volume.DisplayName);
    }

    [Fact]
    public void ANamedVolumeKeepsItsName()
    {
        var volume = Volume("dray-data");

        Assert.False(volume.IsAnonymous);
        Assert.Equal("dray-data", volume.DisplayName);
    }

    [Fact]
    public void A64CharacterNameThatIsNotHexIsStillAName()
    {
        // Length alone is not the test — someone could genuinely name a volume this.
        Assert.False(Volume(new string('z', 64)).IsAnonymous);
    }

    [Fact]
    public void UppercaseHexIsNotTheEnginesAnonymousFormat()
        => Assert.False(Volume(new string('A', 64)).IsAnonymous);

    [Fact]
    public void InUseIsDrivenByWhoHoldsItRatherThanByALabel()
    {
        Assert.True(Volume("v", "dray-web").IsInUse);
        Assert.False(Volume("v").IsInUse);
    }

    [Fact]
    public void AComposeVolumeReportsItsStack()
    {
        var volume = new VolumeSummary
        {
            Name = "dray_pgdata",
            Driver = "local",
            Labels = new Dictionary<string, string> { ["com.docker.compose.project"] = "dray" },
        };

        Assert.Equal("dray", volume.Stack);
    }
}
