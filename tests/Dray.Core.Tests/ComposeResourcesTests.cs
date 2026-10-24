using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

public class ComposeResourcesTests
{
    const string File = """
        services:
          web:
            image: nginx
            volumes:
              - data:/srv
        volumes:
          data:
          cache:
            driver: local # the default, written out
            x-note: kept
        networks:
          front:
            driver: bridge
            internal: true
            ipam:
              config:
                - subnet: 172.28.0.0/16
                  gateway: 172.28.0.1
        """;

    static string Apply(string text, ComposeEdits.Result result)
    {
        Assert.Null(result.Declined);
        return ComposeChange.Apply(text, result.Changes);
    }

    [Fact]
    public void EveryVolumeAndNetworkIsListedOnItsLine()
    {
        var entries = ComposeResources.List(File);

        Assert.Equal(
            [(ComposeResourceKind.Volume, "data", 7), (ComposeResourceKind.Volume, "cache", 8), (ComposeResourceKind.Network, "front", 12)],
            entries.Select(e => (e.Kind, e.Name, e.Line)));
    }

    [Fact]
    public void ANetworkReadsBackWhatItDeclares()
    {
        var front = ComposeResources.Read(File, ComposeResourceKind.Network, "front")!;

        Assert.Equal("bridge", front.Driver);
        Assert.True(front.Internal);
        Assert.Equal("172.28.0.0/16", front.Subnet);
        Assert.Equal("172.28.0.1", front.Gateway);
    }

    [Fact]
    public void AVolumeWithNothingUnderItReadsAsTheDefaults()
    {
        var data = ComposeResources.Read(File, ComposeResourceKind.Volume, "data")!;
        Assert.Null(data.Driver);
        Assert.False(data.External);
    }

    [Fact]
    public void AddingToAFileWithoutTheSectionStartsIt()
    {
        const string text = "services:\n  web:\n    image: nginx\n";
        var added = Apply(text, ComposeResources.Add(text, new ComposeResource(ComposeResourceKind.Volume, "data") { Driver = "local" }));

        Assert.Equal("services:\n  web:\n    image: nginx\n\nvolumes:\n  data:\n    driver: local\n", added);
    }

    [Fact]
    public void AddingToAnExistingSectionFollowsItsIndent()
    {
        var added = Apply(File, ComposeResources.Add(File, new ComposeResource(ComposeResourceKind.Network, "back") { Internal = true }));

        var front = ComposeResources.Read(added, ComposeResourceKind.Network, "front")!;
        var back = ComposeResources.Read(added, ComposeResourceKind.Network, "back")!;
        Assert.Equal("172.28.0.0/16", front.Subnet);
        Assert.True(back.Internal);
        Assert.Contains("\n  back:\n    internal: true\n", added, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyEntryIsWrittenAsJustItsName()
    {
        const string text = "services:\n  web:\n    image: nginx\n";
        var added = Apply(text, ComposeResources.Add(text, new ComposeResource(ComposeResourceKind.Network, "front")));

        Assert.EndsWith("networks:\n  front:\n", added, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameAlreadyTakenIsDeclined()
    {
        Assert.NotNull(ComposeResources.Add(File, new ComposeResource(ComposeResourceKind.Volume, "cache")).Declined);
    }

    [Fact]
    public void EditingChangesOnlyWhatChangedAndKeepsCommentsAndUnknownKeys()
    {
        var cache = ComposeResources.Read(File, ComposeResourceKind.Volume, "cache")!;
        var edited = Apply(File, ComposeResources.Update(File, cache with { Driver = "nfs", Labels = [new("team", "ops")] }));

        Assert.Contains("    driver: nfs\n", edited, StringComparison.Ordinal);
        Assert.Contains("    x-note: kept\n", edited, StringComparison.Ordinal);
        Assert.Contains("team: ops", edited, StringComparison.Ordinal);

        // Everything outside the entry is byte for byte what it was.
        Assert.StartsWith(File[..File.IndexOf("  cache:", StringComparison.Ordinal)], edited, StringComparison.Ordinal);
        Assert.EndsWith(File[File.IndexOf("networks:", StringComparison.Ordinal)..], edited, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearingAFieldRemovesItsLines()
    {
        var front = ComposeResources.Read(File, ComposeResourceKind.Network, "front")!;
        var edited = Apply(File, ComposeResources.Update(File, front with { Internal = false, Subnet = null, Gateway = null }));

        Assert.DoesNotContain("internal", edited, StringComparison.Ordinal);
        Assert.DoesNotContain("ipam", edited, StringComparison.Ordinal);
        Assert.Contains("    driver: bridge\n", edited, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryWithNothingUnderItGainsItsFields()
    {
        var data = ComposeResources.Read(File, ComposeResourceKind.Volume, "data")!;
        var edited = Apply(File, ComposeResources.Update(File, data with { External = true }));

        Assert.Contains("  data:\n    external: true\n  cache:\n", edited, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEditThatChangesNothingChangesNothing()
    {
        var front = ComposeResources.Read(File, ComposeResourceKind.Network, "front")!;
        Assert.Equal(File, Apply(File, ComposeResources.Update(File, front)));
    }

    [Fact]
    public void AnInlineEntryIsDeclinedRatherThanReformatted()
    {
        const string text = "volumes:\n  data: { driver: local }\n";
        var data = ComposeResources.Read(text, ComposeResourceKind.Volume, "data")!;

        Assert.NotNull(ComposeResources.Update(text, data with { Driver = "nfs" }).Declined);
    }

    [Fact]
    public void AVolumeIsMountedAndANetworkJoinedFromTheService()
    {
        var mounted = Apply(File, ComposeEdits.AddVolumeMount(File, "web", "cache", "/cache"));
        Assert.Contains("      - data:/srv\n      - \"cache:/cache\"\n", mounted, StringComparison.Ordinal);

        var joined = Apply(mounted, ComposeEdits.JoinNetwork(mounted, "web", "front"));
        Assert.Contains("    networks:\n      - front\n", joined, StringComparison.Ordinal);
    }
}
