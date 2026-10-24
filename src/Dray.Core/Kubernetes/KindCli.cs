using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Dray.Core.Engine;
using Dray.Core.Model;

namespace Dray.Core.Kubernetes;

/// <summary>
/// Which kubeconfig context a node container serves, measured from the container: the host port
/// it publishes for 6443 is the port in the context's API server address (docs/KUBERNETES.md §4).
/// Measured for kind — <c>127.0.0.1:63628->6443/tcp</c> on the container, <c>https://127.0.0.1:63628</c>
/// in the kubeconfig — which pairs one cluster with one container on one engine, no name involved.
/// </summary>
public static class ClusterPairing
{
    public static KubeContext? ContextFor(ContainerSummary node, IEnumerable<KubeContext> contexts)
    {
        var published = node.Ports.Where(p => p.ContainerPort == 6443).Select(p => p.HostPort).ToHashSet();
        if (published.Count == 0) return null;

        return contexts.FirstOrDefault(c => c.IsLocal
            && Uri.TryCreate(c.Server, UriKind.Absolute, out var uri)
            && published.Contains(uri.Port));
    }

    /// <summary>The node containers on the engine that serve <paramref name="context"/>, control plane first.</summary>
    public static IReadOnlyList<ContainerSummary> NodesOf(KubeContext context, IEnumerable<ContainerSummary> containers)
    {
        var all = containers.Where(c => c.ClusterNode is not null).ToList();
        var plane = all.FirstOrDefault(c => ContextFor(c, [context]) is not null);
        if (plane?.ClusterNode is not { } cluster) return [];

        // The workers publish nothing; they are the same tool's nodes for the same cluster name.
        return [.. all.Where(c => c.ClusterNode!.Tool == cluster.Tool && c.ClusterNode.Cluster == cluster.Cluster)
            .OrderBy(c => c.Id == plane.Id ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.Ordinal)];
    }
}

/// <summary>
/// The <c>kind</c> CLI, which Dray runs to create and delete a cluster on the engine the user is
/// connected to and to load an image into one (docs/KUBERNETES.md §7). Detected, never downloaded:
/// a binary an app fetches and runs is a supply-chain hole with a progress bar.
/// </summary>
public sealed class KindCli(IDockerConfigSource source)
{
    /// <summary>
    /// What kind accepts as a cluster name — it becomes container names and a context, so lower case,
    /// digits and hyphens.
    /// </summary>
    public static bool IsValidName(string name) => Regex.IsMatch(name, "^[a-z0-9]([-a-z0-9]{0,30}[a-z0-9])?$");

    /// <summary>
    /// Where kind is: on <c>PATH</c>, then where the package managers put it. The second half is
    /// the GUI PATH problem arriving early — a Finder-launched app's PATH has no
    /// <c>/opt/homebrew/bin</c>, which is exactly where <c>brew install kind</c> puts it (§7.1).
    /// </summary>
    public string? Find()
    {
        var exe = source.IsWindows ? "kind.exe" : "kind";
        var path = (source.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in path.Concat(KnownDirectories()))
        {
            var candidate = Path.Combine(dir, exe);
            if (source.FileExists(candidate)) return candidate;
        }

        return null;
    }

    internal IEnumerable<string> KnownDirectories()
    {
        var home = source.GetEnvironmentVariable(source.IsWindows ? "USERPROFILE" : "HOME") ?? "";

        if (source.IsWindows)
        {
            var local = source.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.Combine(home, "AppData", "Local");
            return [Path.Combine(local, "Microsoft", "WinGet", "Links"), Path.Combine(home, "go", "bin"), Path.Combine(home, "scoop", "shims")];
        }

        return ["/opt/homebrew/bin", "/usr/local/bin", "/home/linuxbrew/.linuxbrew/bin", Path.Combine(home, "go", "bin"), Path.Combine(home, ".local", "bin")];
    }

    /// <summary>The command that installs kind here, shown for the user to run — Dray does not run a package manager on its own.</summary>
    public string InstallCommand => source.IsWindows
        ? "winget install Kubernetes.kind"
        : source.IsMacOS ? "brew install kind" : "go install sigs.k8s.io/kind@latest";

    /// <summary>
    /// What kind needs in order to aim at <paramref name="host"/>: it drives the <c>docker</c> CLI,
    /// so that CLI's <c>DOCKER_HOST</c> — the same variable Compose is given — and a PATH that has
    /// a <c>docker</c> on it, which a GUI app's PATH may not.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment(DockerHost host)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOCKER_HOST"] = DockerCliContext.CliEndpoint(host.Endpoint),
        };

        var path = source.GetEnvironmentVariable("PATH") ?? "";
        var extra = KnownDirectories().Where(d => !path.Split(Path.PathSeparator).Contains(d));
        env["PATH"] = string.Join(Path.PathSeparator, new[] { path }.Concat(extra).Where(p => p.Length > 0));

        return env;
    }

    public IAsyncEnumerable<string> CreateAsync(string kind, string name, IReadOnlyDictionary<string, string> env, CancellationToken ct = default)
        => RunAsync(kind, ["create", "cluster", "--name", name, "--wait", "90s"], env, ct);

    public IAsyncEnumerable<string> DeleteAsync(string kind, string name, IReadOnlyDictionary<string, string> env, CancellationToken ct = default)
        => RunAsync(kind, ["delete", "cluster", "--name", name], env, ct);

    public IAsyncEnumerable<string> LoadAsync(string kind, string image, string cluster, IReadOnlyDictionary<string, string> env, CancellationToken ct = default)
        => RunAsync(kind, ["load", "docker-image", image, "--name", cluster], env, ct);

    /// <summary>
    /// Run it and hand back each line of its output as it comes — stdout and stderr together, in
    /// arrival order, because kind reports its progress on stderr. A non-zero exit throws with the
    /// last lines it printed, which are where kind says what went wrong.
    /// </summary>
    static async IAsyncEnumerable<string> RunAsync(string kind, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, [EnumeratorCancellation] CancellationToken ct)
    {
        var info = new ProcessStartInfo(kind)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        foreach (var (key, value) in env) info.Environment[key] = value;

        using var process = Process.Start(info) ?? throw new ClusterException($"Could not start {kind}.");
        var lines = System.Threading.Channels.Channel.CreateUnbounded<string>();
        var tail = new Queue<string>();

        async Task Pump(StreamReader reader)
        {
            while (await reader.ReadLineAsync(ct) is { } line) await lines.Writer.WriteAsync(line, ct);
        }

        var pumps = Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));
        _ = pumps.ContinueWith(_ => lines.Writer.TryComplete(), TaskScheduler.Default);

        await using var cancel = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });

        await foreach (var line in lines.Reader.ReadAllAsync(ct))
        {
            // kind draws spinners with escape sequences; the words are what a dialog can show.
            var text = AnsiText.Strip(line).Trim();
            if (text.Length == 0) continue;

            tail.Enqueue(text);
            if (tail.Count > 6) tail.Dequeue();
            yield return text;
        }

        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new ClusterException(tail.Count > 0 ? string.Join(" ", tail) : $"kind exited {process.ExitCode}.");
    }
}

/// <summary>
/// <c>kubectl config use-context</c>, behind the one control that moves the terminal's cluster
/// (docs/KUBERNETES.md §6). Selecting a cluster in Dray never does this; the user asks, sees the
/// command, and it runs.
/// </summary>
public sealed class KubectlCli(IDockerConfigSource source)
{
    public string? Find()
    {
        var exe = source.IsWindows ? "kubectl.exe" : "kubectl";
        var path = (source.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var known = new KindCli(source).KnownDirectories();

        // Docker Desktop ships one under its own resources on macOS, which is where a machine with
        // no Homebrew kubectl has one.
        IEnumerable<string> extra = source.IsMacOS ? ["/Applications/Docker.app/Contents/Resources/bin"] : [];

        foreach (var dir in path.Concat(known).Concat(extra))
        {
            var candidate = Path.Combine(dir, exe);
            if (source.FileExists(candidate)) return candidate;
        }

        return null;
    }

    public static string UseContextCommand(string context) => $"kubectl config use-context {context}";

    public async Task<string?> UseContextAsync(string kubectl, string context, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo(kubectl)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "config", "use-context", context }) info.ArgumentList.Add(arg);

        using var process = Process.Start(info) ?? throw new ClusterException($"Could not start {kubectl}.");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return process.ExitCode == 0 ? null : (await stderr).Trim() is { Length: > 0 } why ? why : $"kubectl exited {process.ExitCode}.";
    }
}
