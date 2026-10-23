using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Dray.Core.Model;

/// <summary>A new stack's files: the compose file, and the <c>.env</c> beside it.</summary>
/// <param name="Environment">What goes in <c>.env</c> — the values the compose file refers to by <c>${NAME}</c>.</param>
/// <param name="Notes">What the user should know about how it was made — what was left out, and why.</param>
public sealed record AuthoredStack(string Compose, IReadOnlyList<EnvVar> Environment, IReadOnlyList<string> Notes);

/// <summary>
/// Writing a compose file for a stack that does not exist yet.
/// <para>
/// From a template, an image, a <c>docker run</c> line or containers already running. Every route
/// ends the same way: secrets go to <c>.env</c> and are referred to as <c>${NAME}</c>, never
/// written into the compose file, which is the file people commit and paste into issues.
/// </para>
/// <para>
/// From containers, the container's inspect record is the source — not a reconstructed command line
/// — with everything the image sets by itself taken away. Copying an image's own
/// <c>NGINX_VERSION</c> into a compose file would pin it there, and quietly override the next
/// image's value after an upgrade.
/// </para>
/// </summary>
public static class ComposeAuthoring
{
    public static AuthoredStack Blank() => new(
        """
        # A new stack. Each service is a container compose keeps running.
        # Ctrl+Space lists every key compose accepts; hover one for what it does.
        services:
          app:
            image: nginx:1.27-alpine
            restart: unless-stopped
            ports:
              - "8080:80"

        """,
        [],
        []);

    public static AuthoredStack FromImage(string image, IReadOnlyList<string> ports)
    {
        var service = new YamlMappingNode { { "image", Plain(image) }, { "restart", Plain("unless-stopped") } };
        if (ports.Count > 0) service.Add("ports", Sequence(ports.Select(Quoted)));

        var root = new YamlMappingNode { { "services", new YamlMappingNode { { ServiceName(image), service } } } };
        return new AuthoredStack(Emit(root), [], []);
    }

    /// <summary>
    /// Tidy composerize's output: drop the placeholder project name it writes, and move any secret
    /// out of the file into <c>.env</c>.
    /// </summary>
    public static AuthoredStack FromConverted(string yaml)
    {
        var stream = new YamlStream();

        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            return new AuthoredStack(yaml, [], [$"The conversion produced YAML that does not parse: {ex.Message}"]);
        }

        if (stream.Documents.FirstOrDefault()?.RootNode is not YamlMappingNode root) return new AuthoredStack(yaml, [], []);

        // The project is named by the stack's folder; a name: in the file would fight it.
        root.Children.Remove(new YamlScalarNode("name"));

        var environment = new List<EnvVar>();

        if (root.Children.TryGetValue(new YamlScalarNode("services"), out var services) && services is YamlMappingNode map)
        {
            foreach (var (_, body) in map.Children)
            {
                if (body is YamlMappingNode service) ExtractSecrets(service, environment);
            }
        }

        return new AuthoredStack(Emit(root), environment, environment.Count > 0 ? [SecretsNote(environment)] : []);
    }

    /// <summary>
    /// A compose file for containers that are running now.
    /// </summary>
    /// <param name="containers">Each container's inspect record, and its image's defaults where the engine could say.</param>
    public static AuthoredStack FromContainers(IReadOnlyList<(ContainerInspect Container, ImageDefaults? Image)> containers)
    {
        var services = new YamlMappingNode();
        var volumes = new YamlMappingNode();
        var networks = new YamlMappingNode();
        var environment = new List<EnvVar>();
        var notes = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (container, image) in containers)
        {
            var name = Unique(ServiceName(container.Name), names);
            var service = new YamlMappingNode { { "image", Plain(container.Image) } };

            if (image is null)
                notes.Add($"The engine could not say what {container.Image} sets by itself, so {name} keeps every variable and label the container has — some are the image's own.");

            if (Differs(container.Entrypoint, image?.Entrypoint) && container.Entrypoint.Count > 0)
                service.Add("entrypoint", Sequence(container.Entrypoint.Select(Plain)));

            if (Differs(container.Command, image?.Command) && container.Command.Count > 0)
                service.Add("command", Sequence(container.Command.Select(Plain)));

            if (container.WorkingDirectory is { Length: > 0 } wd && wd != image?.WorkingDirectory) service.Add("working_dir", Plain(wd));
            if (container.User is { Length: > 0 } user && user != image?.User) service.Add("user", Plain(user));

            // Only what the container set: a variable the image already sets to the same value is
            // the image's, and stays the image's. PATH is everybody's.
            var inherited = image?.Environment.ToHashSet() ?? [];
            var own = container.Environment.Where(e => !inherited.Contains(e) && e.Key != "PATH").ToList();

            if (own.Count > 0)
            {
                var env = new YamlMappingNode();

                foreach (var variable in own)
                {
                    if (variable.IsSecret)
                    {
                        env.Add(variable.Key, Plain($"${{{variable.Key}}}"));
                        if (environment.All(e => e.Key != variable.Key)) environment.Add(variable);
                    }
                    else
                    {
                        env.Add(variable.Key, Quoted(variable.Value));
                    }
                }

                service.Add("environment", env);
            }

            var ports = container.Ports
                .SelectMany(p => p.Bindings.Select(b => $"{b.HostPort}:{p.ContainerPort}" + (p.Protocol == "tcp" ? "" : "/" + p.Protocol)))
                .Distinct()
                .ToList();

            if (ports.Count > 0) service.Add("ports", Sequence(ports.Select(Quoted)));

            var mounts = new List<YamlNode>();
            var tmpfs = new List<YamlNode>();

            foreach (var mount in container.Mounts)
            {
                var ro = mount.ReadOnly ? ":ro" : "";

                switch (mount.Kind)
                {
                    case MountKind.Volume when mount.Source.Length > 0 && !IsAnonymous(mount.Source):
                        mounts.Add(Plain($"{mount.Source}:{mount.Destination}{ro}"));

                        // External: the volume exists, with the data in it. Without this compose
                        // would create a new, empty one named after the project.
                        if (!volumes.Children.ContainsKey(new YamlScalarNode(mount.Source)))
                            volumes.Add(mount.Source, new YamlMappingNode { { "external", Plain("true") } });
                        break;

                    case MountKind.Volume:
                        // Anonymous: the image declares the path and the engine made a volume for
                        // it. Compose makes its own, so naming it here would only pin a random id.
                        break;

                    case MountKind.Bind:
                        mounts.Add(Plain($"{mount.Source}:{mount.Destination}{ro}"));
                        break;

                    default:
                        tmpfs.Add(Plain(mount.Destination));
                        break;
                }
            }

            if (mounts.Count > 0) service.Add("volumes", Sequence(mounts));
            if (tmpfs.Count > 0) service.Add("tmpfs", Sequence(tmpfs));

            var attached = container.Networks.Select(n => n.Name).ToList();

            if (attached.Contains("host")) service.Add("network_mode", Plain("host"));
            else
            {
                var custom = attached.Where(n => n is not ("bridge" or "none")).ToList();

                if (custom.Count > 0)
                {
                    service.Add("networks", Sequence(custom.Select(Plain)));

                    foreach (var network in custom.Where(n => !networks.Children.ContainsKey(new YamlScalarNode(n))))
                        networks.Add(network, new YamlMappingNode { { "external", Plain("true") } });
                }
            }

            if (container.RestartPolicy is { Length: > 0 } restart and not "no") service.Add("restart", Plain(restart));

            var labels = container.Labels
                .Where(l => !l.Key.StartsWith("com.docker.compose.", StringComparison.Ordinal)
                    && image?.Labels.GetValueOrDefault(l.Key) != l.Value)
                .ToList();

            if (labels.Count > 0)
            {
                var map = new YamlMappingNode();
                foreach (var (key, value) in labels) map.Add(key, Quoted(value));
                service.Add("labels", map);
            }

            services.Add(name, service);
        }

        var root = new YamlMappingNode { { "services", services } };
        if (volumes.Children.Count > 0) root.Add("volumes", volumes);
        if (networks.Children.Count > 0) root.Add("networks", networks);

        if (environment.Count > 0) notes.Add(SecretsNote(environment));

        notes.Add("Deploying this creates new containers; the ones it was made from keep running until you remove them, and anything they publish will clash.");

        return new AuthoredStack(Emit(root), environment, notes);
    }

    /// <summary>A service name from a container or image name: compose allows letters, digits, dots, dashes and underscores.</summary>
    public static string ServiceName(string from)
    {
        var name = from.TrimStart('/');

        // An image reference: the last path segment, without its tag or digest.
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        name = name.Split('@')[0].Split(':')[0];

        var cleaned = new string([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? char.ToLowerInvariant(c) : '-')]).Trim('-', '.', '_');
        return cleaned.Length == 0 ? "app" : cleaned;
    }

    static void ExtractSecrets(YamlMappingNode service, List<EnvVar> environment)
    {
        if (!service.Children.TryGetValue(new YamlScalarNode("environment"), out var env)) return;

        switch (env)
        {
            case YamlSequenceNode list:
                for (var i = 0; i < list.Children.Count; i++)
                {
                    if (list.Children[i] is not YamlScalarNode { Value: { } entry } || entry.IndexOf('=') is not (var eq and > 0)) continue;

                    var variable = new EnvVar(entry[..eq], entry[(eq + 1)..]);
                    if (!variable.IsSecret) continue;

                    list.Children[i] = Plain($"{variable.Key}=${{{variable.Key}}}");
                    if (environment.All(e => e.Key != variable.Key)) environment.Add(variable);
                }

                break;

            case YamlMappingNode map:
                foreach (var key in map.Children.Keys.OfType<YamlScalarNode>().ToList())
                {
                    var variable = new EnvVar(key.Value ?? "", (map.Children[key] as YamlScalarNode)?.Value ?? "");
                    if (!variable.IsSecret) continue;

                    map.Children[key] = Plain($"${{{variable.Key}}}");
                    if (environment.All(e => e.Key != variable.Key)) environment.Add(variable);
                }

                break;
        }
    }

    static string SecretsNote(IReadOnlyList<EnvVar> secrets)
        => $"{string.Join(", ", secrets.Select(s => s.Key))} {(secrets.Count == 1 ? "looks" : "look")} secret, so the value goes in .env beside compose.yaml and the file refers to it by name.";

    /// <summary>The engine names an anonymous volume with a 64-character hex id; a person never does.</summary>
    static bool IsAnonymous(string volume) => volume.Length == 64 && volume.All(char.IsAsciiHexDigitLower);

    static bool Differs(IReadOnlyList<string> container, IReadOnlyList<string>? image)
        => image is null || !container.SequenceEqual(image);

    static string Unique(string name, HashSet<string> taken)
    {
        var candidate = name;
        for (var i = 2; !taken.Add(candidate); i++) candidate = $"{name}-{i}";
        return candidate;
    }

    static YamlScalarNode Plain(string value) => new(value);

    /// <summary>
    /// Double-quoted. For port mappings, which YAML 1.1 parsers read as base-60 numbers when
    /// unquoted, and for values, which would otherwise become booleans or numbers.
    /// </summary>
    static YamlScalarNode Quoted(string value) => new(value) { Style = ScalarStyle.DoubleQuoted };

    static YamlSequenceNode Sequence(IEnumerable<YamlNode> items) => new(items);

    static string Emit(YamlNode root)
    {
        var writer = new StringWriter { NewLine = "\n" };

        // Indented sequences — "ports:\n  - ..." — which is how compose files are written by hand
        // and in compose's own documentation. YamlDotNet's default puts the dash under the key.
        var emitter = new Emitter(writer, new EmitterSettings().WithIndentedSequences());
        new YamlStream(new YamlDocument(root)).Save(emitter, assignAnchors: false);

        // YamlStream ends every document with "...", which is legal and looks like a mistake.
        var text = writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.EndsWith("...\n", StringComparison.Ordinal)) text = text[..^4];

        return text;
    }
}

/// <summary>Putting a new stack on disk.</summary>
public static class StackCreation
{
    /// <summary>Why <paramref name="name"/> cannot be a new stack here, or null when it can.</summary>
    public static string? Validate(string stacksFolder, string name, IEnumerable<string> existing)
    {
        if (name.Length == 0) return "A stack needs a name.";
        if (!StackFolder.IsValidProjectName(name)) return $"Compose would call this {StackFolder.NormalizeProjectName(name)}: lower case letters, digits, dashes and underscores.";
        if (existing.Contains(name, StringComparer.Ordinal)) return $"There is already a stack called {name}.";
        if (Directory.Exists(Path.Combine(stacksFolder, name))) return $"{Path.Combine(stacksFolder, name)} already exists.";
        return null;
    }

    /// <summary>
    /// Write the stack's folder: <c>compose.yaml</c>, and <c>.env</c> when there is anything for it.
    /// Never over an existing folder.
    /// </summary>
    /// <returns>The folder, or why it could not be made.</returns>
    public static (string? Folder, string? Problem) Create(string stacksFolder, string name, AuthoredStack stack)
    {
        var folder = Path.Combine(stacksFolder, name);

        try
        {
            // CreateDirectory succeeds on an existing folder, so the check is ours to make.
            if (Directory.Exists(folder)) return (null, $"{folder} already exists.");

            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "compose.yaml"), stack.Compose);

            if (stack.Environment.Count > 0)
                File.WriteAllText(Path.Combine(folder, DotEnvFile.FileName), DotEnvFile.Serialize(stack.Environment));

            return (folder, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"Could not create {folder}: {ex.Message}");
        }
    }
}
