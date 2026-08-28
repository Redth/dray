using Dray.Core.Engine;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Whether to tell someone about Apple's container engine.
/// <para>
/// The rule this protects: never point someone at a download that will not run on their machine.
/// Apple supports macOS 26 and later on Apple silicon, and says so plainly rather than as a soft
/// floor.
/// </para>
/// </summary>
public class AppleEngineTests
{
    [Fact]
    public void NothingToSayWhenItIsAlreadyInstalled()
    {
        var found = AppleEngine.Check(installed: true, isMacOS: true, isAppleSilicon: true, macOSMajor: 26);

        Assert.Equal(AppleEngineAvailability.Installed, found);
        Assert.Null(AppleEngine.Explain(found));
    }

    [Fact]
    public void SuggestedOnAMacThatCouldRunIt()
    {
        var found = AppleEngine.Check(installed: false, isMacOS: true, isAppleSilicon: true, macOSMajor: 26);

        Assert.Equal(AppleEngineAvailability.Available, found);
        Assert.Contains("not installed", AppleEngine.Explain(found), StringComparison.Ordinal);
    }

    [Fact]
    public void NeverSuggestedOnIntel()
    {
        // It is a virtualization framework, not an emulator.
        var found = AppleEngine.Check(installed: false, isMacOS: true, isAppleSilicon: false, macOSMajor: 26);

        Assert.Equal(AppleEngineAvailability.Unsupported, found);
        Assert.Null(AppleEngine.Explain(found));
    }

    [Fact]
    public void NeverSuggestedOffMacOS()
    {
        var found = AppleEngine.Check(installed: false, isMacOS: false, isAppleSilicon: true, macOSMajor: 0);

        Assert.Equal(AppleEngineAvailability.Unsupported, found);
        Assert.Null(AppleEngine.Explain(found));
    }

    [Fact]
    public void AnOlderMacOsIsToldWhyRatherThanLeftGuessing()
    {
        // Apple silicon, so the machine is the right shape — the OS is not. Saying nothing here
        // would leave someone wondering why the engine everyone mentions is missing.
        var found = AppleEngine.Check(installed: false, isMacOS: true, isAppleSilicon: true, macOSMajor: 15);

        Assert.Equal(AppleEngineAvailability.NeedsNewerMacOS, found);
        Assert.Contains("macOS 26", AppleEngine.Explain(found), StringComparison.Ordinal);
    }

    [Fact]
    public void TheVersionFloorIsTheOneAppleStates()
        => Assert.Equal(26, AppleEngine.MinimumMacOSMajor);
}
