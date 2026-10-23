using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Reading folders as compose projects. Every naming rule here was checked against compose 5.4.0
/// by running <c>docker compose config</c> in a folder of that name.
/// </summary>
public sealed class StackFolderTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("dray-stacks-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Stack(string folder, string compose = "services:\n  a:\n    image: alpine\n", string file = "compose.yaml")
    {
        var path = Path.Combine(_root, folder);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, file), compose);
        return path;
    }

    [Theory]
    [InlineData("My_Stack.v2", "my_stackv2")]
    [InlineData("-Lead Dash", "leaddash")]
    [InlineData("Ünïcode App", "ncodeapp")]
    public void TheFolderNameIsNormalisedTheWayComposeDoesIt(string folder, string project)
        => Assert.Equal(project, StackFolder.Read(Stack(folder))!.Project);

    [Fact]
    public void TheFilesOwnNameWinsOverTheFolder()
        => Assert.Equal("web", StackFolder.Read(Stack("x", "name: web\nservices: {}\n"))!.Project);

    [Fact]
    public void TheEnvFileWinsOverBoth()
    {
        var path = Stack("x", "name: fromfile\nservices: {}\n");
        File.WriteAllText(Path.Combine(path, ".env"), "COMPOSE_PROJECT_NAME=fromenv\n");

        Assert.Equal("fromenv", StackFolder.Read(path)!.Project);
    }

    [Fact]
    public void AnInterpolatedNameIsLeftToCompose()
        => Assert.Equal("x", StackFolder.Read(Stack("x", "name: ${PROJECT}\nservices: {}\n"))!.Project);

    [Fact]
    public void TheOverrideIsIncludedBecausePassingFileTurnsItsDiscoveryOff()
    {
        var path = Stack("x");
        File.WriteAllText(Path.Combine(path, "compose.override.yaml"), "services: {}\n");

        Assert.Equal(["compose.yaml", "compose.override.yaml"], StackFolder.Read(path)!.Files.Select(Path.GetFileName));
    }

    [Fact]
    public void ComposeFileInTheEnvChoosesTheFiles()
    {
        var path = Stack("x");
        File.WriteAllText(Path.Combine(path, "prod.yaml"), "services: {}\n");
        File.WriteAllText(Path.Combine(path, ".env"), $"COMPOSE_FILE=compose.yaml{Path.PathSeparator}prod.yaml\n");

        Assert.Equal(["compose.yaml", "prod.yaml"], StackFolder.Read(path)!.Files.Select(Path.GetFileName));
    }

    [Fact]
    public void TheOlderFileNameIsStillAStack()
        => Assert.Equal("docker-compose.yml", Path.GetFileName(StackFolder.Read(Stack("x", file: "docker-compose.yml"))!.Files[0]));

    [Fact]
    public void AFolderWithoutAComposeFileIsNotAStack()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        Assert.Null(StackFolder.Read(Path.Combine(_root, "empty")));
    }

    [Fact]
    public void ScanningFindsNestedStacksButNeverLooksInsideOne()
    {
        Stack("web");
        Stack(Path.Combine("group", "api"));

        // A stack's own data folder with something compose-shaped in it — another tool's example,
        // a vendored project. It belongs to `web` and is not a stack of its own.
        Stack(Path.Combine("web", "data", "vendor"));
        Stack(Path.Combine(".hidden", "secret"));

        var found = StackFolder.Scan(_root);

        Assert.Equal(["web", "api"], found.Select(d => d.Project));
    }

    [Fact]
    public void AMissingRootIsNoStacks() => Assert.Empty(StackFolder.Scan(Path.Combine(_root, "nope")));
}
