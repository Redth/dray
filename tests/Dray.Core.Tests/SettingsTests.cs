using Dray.Core.Engine;
using Dray.Core.Kubernetes;
using Dray.Core.Navigation;
using Dray.Core.Settings;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>Settings: saved as they change, and followed at once by the sidebar, the palette and the cluster.</summary>
public class SettingsTests
{
    sealed class MemoryStore : ISettingsStore
    {
        public DraySettingsData Saved { get; private set; } = new();

        public int Saves { get; private set; }

        public DraySettingsData Load() => Saved;

        public void Save(DraySettingsData settings)
        {
            Saves++;
            Saved = settings;
        }
    }

    [Fact]
    public void EverythingIsShownUntilTheUserSaysOtherwise()
    {
        var settings = new DraySettings(new MemoryStore());

        Assert.True(settings.KubernetesEnabled);
        Assert.True(settings.IsShown("/kubernetes/pods"));
    }

    [Fact]
    public void AChangeIsSavedAndAnnouncedOnceAndARepeatIsNeither()
    {
        var store = new MemoryStore();
        var settings = new DraySettings(store);
        var changes = 0;
        settings.Changed += () => changes++;

        settings.SetShown("/kubernetes/events", false);
        settings.SetShown("/kubernetes/events", false);
        settings.SetKubernetes(false);
        settings.SetKubernetes(false);

        Assert.Equal(2, changes);
        Assert.Equal(2, store.Saves);
        Assert.Equal(["/kubernetes/events"], store.Saved.HiddenRoutes);
        Assert.False(store.Saved.Kubernetes);

        settings.SetShown("/kubernetes/events", true);
        Assert.Empty(store.Saved.HiddenRoutes);
    }

    [Fact]
    public void TheFileStoreRoundTripsAndForgivesABrokenFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dray-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            new FileSettingsStore(directory).Save(new DraySettingsData { Kubernetes = false, HiddenRoutes = ["/kubernetes/services"] });

            var loaded = new FileSettingsStore(directory).Load();
            Assert.False(loaded.Kubernetes);
            Assert.Equal(["/kubernetes/services"], loaded.HiddenRoutes);

            File.WriteAllText(Path.Combine(directory, "settings.json"), "{ nope");
            Assert.True(new FileSettingsStore(directory).Load().Kubernetes);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AHiddenPageLeavesTheSidebarAndThePalette()
    {
        var group = Assert.Single(NavigationManifest.Visible(false, true, ["/kubernetes/events"]), n => n.Title == "Kubernetes");
        Assert.DoesNotContain(group.Children, c => c.Route == "/kubernetes/events");

        var palette = CommandCatalogue.Navigation(false, true, ["/kubernetes/events"]).Select(c => c.Detail).ToList();
        Assert.DoesNotContain("/kubernetes/events", palette);
        Assert.Contains("/kubernetes/pods", palette);
        Assert.Contains(NavigationManifest.SettingsRoute, palette);
    }

    [Fact]
    public void WithEveryKubernetesPageHiddenTheGroupGoesToo()
        => Assert.DoesNotContain(
            NavigationManifest.Visible(false, true, [.. NavigationManifest.KubernetesPages.Select(p => p.Route!)]),
            n => n.Title == "Kubernetes");

    /// <summary>Off means nothing read and nothing connected — and it follows the switch both ways without a restart.</summary>
    [Fact]
    public async Task TheClusterFollowsTheKubernetesSwitch()
    {
        var settings = new DraySettings(new MemoryStore());
        settings.SetKubernetes(false);

        var factory = new ClusterManagerTests.FakeFactory();
        await using var manager = new ClusterManager(ClusterManagerTests.Reader(), factory, settings: settings);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(manager.Enabled);
        Assert.Empty(manager.Contexts);
        Assert.Empty(factory.Created);

        settings.SetKubernetes(true);
        Assert.True(await WaitFor(() => manager.Selected?.State == HostConnectionState.Connected));

        settings.SetKubernetes(false);
        Assert.True(await WaitFor(() => manager.Selected is null && manager.Contexts.Count == 0));
    }

    static async Task<bool> WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(5, TestContext.Current.CancellationToken);
        return condition();
    }
}
