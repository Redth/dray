using Dray.Core.Engine;
using Dray.Core.Shell;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The names users give hosts: kept by Dray alone, applied to every host handed out, and never
/// allowed to stand in for the name the CLI uses.
/// </summary>
public class HostDisplayNameTests
{
    const string Socket = "unix:///Users/x/.docker/run/docker.sock";

    static DockerContextReader OneContext() => new(new FakeConfigSource()
        .WithConfig("desktop-linux")
        .WithContext("desktop-linux", Socket));

    [Fact]
    public async Task ANameAppliesToTheHostAndKeepsItsOwnName()
    {
        var names = new MemoryNames();
        await using var manager = new EngineManager(OneContext(), new FakeRuntimeFactory(), names: names);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        manager.SetDisplayName("desktop-linux", "  Laptop  ");

        var host = Assert.Single(manager.Hosts);
        Assert.Equal("Laptop", host.Label);
        Assert.Equal("desktop-linux", host.Name);
        Assert.Equal("Laptop", manager.Selected?.Label);
        Assert.Equal("Laptop", names.Saved["desktop-linux"]);
    }

    /// <summary>
    /// The name is applied where hosts are handed out, so a host whose state changes — reconnecting,
    /// being re-probed — keeps it. A name stored on the record would be lost the first time the
    /// manager replaced the record.
    /// </summary>
    [Fact]
    public async Task ANameSurvivesTheHostBeingReconnected()
    {
        var names = new MemoryNames { Saved = { ["desktop-linux"] = "Laptop" } };
        await using var manager = new EngineManager(OneContext(), new FakeRuntimeFactory(), names: names);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        await manager.SelectAsync("desktop-linux", TestContext.Current.CancellationToken);

        Assert.Equal("Laptop", manager.Selected?.Label);
        Assert.Equal("Laptop", Assert.Single(manager.Hosts).Label);
    }

    [Fact]
    public async Task BlankOrTheHostsOwnNameClearsIt()
    {
        var names = new MemoryNames { Saved = { ["desktop-linux"] = "Laptop" } };
        await using var manager = new EngineManager(OneContext(), new FakeRuntimeFactory(), names: names);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        manager.SetDisplayName("desktop-linux", "desktop-linux");
        Assert.Null(Assert.Single(manager.Hosts).DisplayName);
        Assert.Empty(names.Saved);

        manager.SetDisplayName("desktop-linux", "Laptop");
        manager.SetDisplayName("desktop-linux", "   ");
        Assert.Equal("desktop-linux", Assert.Single(manager.Hosts).Label);
    }

    [Fact]
    public async Task RenamingAHostThatIsNotThereDoesNothing()
    {
        var names = new MemoryNames();
        await using var manager = new EngineManager(OneContext(), new FakeRuntimeFactory(), names: names);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        manager.SetDisplayName("gone", "Somewhere");

        Assert.Empty(names.Saved);
        Assert.Equal(0, names.Saves);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("desktop-linux", null)]
    [InlineData(" Laptop ", "Laptop")]
    [InlineData("A name far too long to sit in any toolbar", "A name far too long to s")]
    public void Normalize(string? input, string? expected)
        => Assert.Equal(expected, HostDisplayName.Normalize(input, "desktop-linux"));

    [Fact]
    public void TheFileStoreRoundTripsAndSurvivesAMissingOrBrokenFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dray-names-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileHostNameStore(directory);
            Assert.Empty(store.Load());

            store.Save(new Dictionary<string, string> { ["desktop-linux"] = "Laptop", ["wsl:Ubuntu"] = "Ubuntu box" });
            var loaded = new FileHostNameStore(directory).Load();
            Assert.Equal("Laptop", loaded["desktop-linux"]);
            Assert.Equal("Ubuntu box", loaded["wsl:Ubuntu"]);

            File.WriteAllText(Path.Combine(directory, "host-names.json"), "{ not json");
            Assert.Empty(new FileHostNameStore(directory).Load());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    sealed class MemoryNames : IHostNameStore
    {
        public Dictionary<string, string> Saved { get; } = [];

        public int Saves { get; private set; }

        public IReadOnlyDictionary<string, string> Load() => new Dictionary<string, string>(Saved);

        public void Save(IReadOnlyDictionary<string, string> names)
        {
            Saves++;
            Saved.Clear();
            foreach (var (id, name) in names) Saved[id] = name;
        }
    }
}

/// <summary>
/// The picker model's equality and shape, which decide whether a head rebuilds its native control —
/// a rebuild on macOS flickers the whole toolbar, so "nothing changed" has to be detectable.
/// </summary>
public class ChromePickerTests
{
    static ChromePicker Picker(string title, bool firstChecked = true, params string[] ids) => new(
        "engine",
        title,
        $"{title} — Linux containers",
        IconRef.Host,
        [.. ids.Select((id, i) => new ChromePickerItem(id, id, IsChecked: i == 0 && firstChecked))]);

    [Fact]
    public void TwoPickersBuiltFromTheSameStateAreEqual()
        => Assert.Equal(Picker("Laptop", true, "a", "b"), Picker("Laptop", true, "a", "b"));

    [Fact]
    public void ADifferentCheckIsADifferentPickerButTheSameShape()
    {
        var before = Picker("Laptop", true, "a", "b");
        var after = Picker("Laptop", false, "a", "b");

        Assert.NotEqual(before, after);
        Assert.Equal(before.Signature, after.Signature);
    }

    [Fact]
    public void AHostAppearingChangesTheShape()
        => Assert.NotEqual(Picker("Laptop", true, "a").Signature, Picker("Laptop", true, "a", "b").Signature);
}
