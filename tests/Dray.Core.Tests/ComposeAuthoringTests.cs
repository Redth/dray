using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

public sealed class ComposeAuthoringTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("dray-new-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    static ImageDefaults Nginx => new(
        [new EnvVar("PATH", "/usr/local/sbin:/usr/bin"), new EnvVar("NGINX_VERSION", "1.27.4")],
        new Dictionary<string, string> { ["maintainer"] = "NGINX" },
        ["nginx", "-g", "daemon off;"],
        ["/docker-entrypoint.sh"],
        null,
        null);

    static ContainerInspect Web => new()
    {
        Id = "abc",
        Name = "/my web",
        Image = "nginx:1.27",
        State = DockerState.Running,
        Entrypoint = ["/docker-entrypoint.sh"],
        Command = ["nginx", "-g", "daemon off;"],
        Environment =
        [
            new EnvVar("PATH", "/usr/local/sbin:/usr/bin"),
            new EnvVar("NGINX_VERSION", "1.27.4"),
            new EnvVar("TZ", "UTC"),
            new EnvVar("DB_PASSWORD", "hunter2-correct"),
        ],
        Ports = [new ExposedPort(80, "tcp", [new PortBinding(8080, 80)]), new ExposedPort(443, "tcp", [])],
        Mounts =
        [
            new MountPoint(MountKind.Volume, "site-data", "/data", false),
            new MountPoint(MountKind.Volume, new string('a', 64), "/var/cache/nginx", false),
            new MountPoint(MountKind.Bind, "/srv/conf", "/etc/nginx/conf.d", true),
        ],
        Networks = [new NetworkAttachment("bridge", null, null, null, []), new NetworkAttachment("shared", null, null, null, [])],
        RestartPolicy = "unless-stopped",
        Labels = new Dictionary<string, string> { ["maintainer"] = "NGINX", ["team"] = "web" },
    };

    [Fact]
    public void OnlyWhatTheContainerSetIsWritten()
    {
        var stack = ComposeAuthoring.FromContainers([(Web, Nginx)]);
        var yaml = stack.Compose;
        var document = ComposeDocument.Parse(yaml);

        Assert.Null(document.SyntaxError);
        Assert.Equal("my-web", Assert.Single(document.Services).Name);

        // The image's own variables, command and labels stay the image's.
        Assert.DoesNotContain("NGINX_VERSION", yaml);
        Assert.DoesNotContain("PATH", yaml);
        Assert.DoesNotContain("daemon off", yaml);
        Assert.DoesNotContain("maintainer", yaml);

        Assert.Equal(["TZ=UTC", "DB_PASSWORD=${DB_PASSWORD}"], document.ItemsOf("my-web", "environment").Select(i => i.Value));
        Assert.Equal(["8080:80"], document.ItemsOf("my-web", "ports").Select(i => i.Value));
        Assert.Equal(["site-data:/data", "/srv/conf:/etc/nginx/conf.d:ro"], document.ItemsOf("my-web", "volumes").Select(i => i.Value));
        Assert.Equal(["shared"], document.ItemsOf("my-web", "networks").Select(i => i.Value));
        Assert.Contains("team: \"web\"", yaml);
    }

    [Fact]
    public void ASecretGoesToEnvAndNeverIntoTheComposeFile()
    {
        var stack = ComposeAuthoring.FromContainers([(Web, Nginx)]);

        Assert.DoesNotContain("hunter2-correct", stack.Compose);
        Assert.Equal("hunter2-correct", Assert.Single(stack.Environment).Value);
        Assert.Contains(stack.Notes, n => n.Contains("DB_PASSWORD", StringComparison.Ordinal));
    }

    [Fact]
    public void ExistingVolumesAndNetworksAreExternalSoTheirDataIsReused()
    {
        var yaml = ComposeAuthoring.FromContainers([(Web, Nginx)]).Compose;

        Assert.Contains("volumes:\n  site-data:\n    external: true", yaml);
        Assert.Contains("networks:\n  shared:\n    external: true", yaml);
        Assert.DoesNotContain(new string('a', 64), yaml);
    }

    [Fact]
    public void WithoutImageDefaultsEverythingIsKeptAndSaidSo()
    {
        var stack = ComposeAuthoring.FromContainers([(Web, null)]);

        Assert.Contains("NGINX_VERSION", stack.Compose);
        Assert.Contains(stack.Notes, n => n.Contains("could not say", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoContainersWithTheSameNameGetTwoServices()
    {
        var names = ComposeDocument.Parse(ComposeAuthoring.FromContainers([(Web, Nginx), (Web, Nginx)]).Compose).Services.Select(s => s.Name);

        Assert.Equal(["my-web", "my-web-2"], names);
    }

    [Fact]
    public void AConvertedRunLineLosesItsPlaceholderNameAndItsSecrets()
    {
        // composerize 1.7.6's actual output for:
        // docker run -d --name web -p 8080:80 -e TZ=UTC -e API_TOKEN=tok-123456 nginx:alpine
        const string converted = """
            name: <your project name>
            services:
              nginx:
                container_name: web
                ports:
                  - 8080:80
                environment:
                  - TZ=UTC
                  - API_TOKEN=tok-123456
                image: nginx:alpine
            """;

        var stack = ComposeAuthoring.FromConverted(converted);

        Assert.DoesNotContain("<your project name>", stack.Compose);
        Assert.DoesNotContain("tok-123456", stack.Compose);
        Assert.Contains("API_TOKEN=${API_TOKEN}", stack.Compose);
        Assert.Equal("tok-123456", Assert.Single(stack.Environment).Value);
    }

    [Theory]
    [InlineData("nginx:1.27-alpine", "nginx")]
    [InlineData("ghcr.io/owner/My App:2", "my-app")]
    [InlineData("/web_1", "web_1")]
    [InlineData("@@@", "app")]
    public void ServiceNamesAreComposeServiceNames(string from, string name)
        => Assert.Equal(name, ComposeAuthoring.ServiceName(from));

    [Fact]
    public void AStackFromAnImageQuotesItsPorts()
    {
        var yaml = ComposeAuthoring.FromImage("postgres:16", ["5432:5432"]).Compose;

        Assert.Contains("- \"5432:5432\"", yaml);
        Assert.Equal("postgres", Assert.Single(ComposeDocument.Parse(yaml).Services).Name);
    }

    [Fact]
    public void CreatingWritesTheFolderAndNeverOverAnExistingOne()
    {
        var stack = ComposeAuthoring.FromContainers([(Web, Nginx)]);

        var (folder, problem) = StackCreation.Create(_root, "shop", stack);
        Assert.Null(problem);
        Assert.True(File.Exists(Path.Combine(folder!, "compose.yaml")));
        Assert.Contains("DB_PASSWORD=hunter2-correct", File.ReadAllText(Path.Combine(folder!, ".env")));

        Assert.NotNull(StackCreation.Create(_root, "shop", stack).Problem);
        Assert.NotNull(StackCreation.Validate(_root, "shop", []));
        Assert.NotNull(StackCreation.Validate(_root, "Shop", []));
        Assert.NotNull(StackCreation.Validate(_root, "api", ["api"]));
        Assert.Null(StackCreation.Validate(_root, "api", []));
    }
}
