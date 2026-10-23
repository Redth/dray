namespace Dray.Core.Model;

/// <summary>A port this host already publishes, and who publishes it.</summary>
public sealed record PublishedPort(int Port, string Protocol, string Owner);

/// <summary>What Dray knows about the host a file is about to be deployed to.</summary>
/// <param name="Published">Ports published by containers outside this stack.</param>
/// <param name="HostIsRemote">The engine is on another machine — relative paths will not mean what they say.</param>
public sealed record LintContext(IReadOnlyList<PublishedPort> Published, bool HostIsRemote);

/// <summary>Something Dray noticed in a compose file, and where.</summary>
public sealed record LintFinding(string Message, YamlRange Range);

/// <summary>
/// The checks only Dray can make.
/// <para>
/// The schema says whether a file is well-formed and compose says whether it will run; neither
/// knows what else is on the host. A port another container already publishes is valid YAML, a
/// valid service, and a deploy that fails at the last step. Each rule here uses something Dray
/// knows and they do not — the engine's containers, where the engine is — or names a choice that
/// is legal and worth seeing twice. They are warnings, never errors: compose is the authority on
/// what runs, and nothing here stops a deploy.
/// </para>
/// </summary>
public static class ComposeLint
{
    public static IReadOnlyList<LintFinding> Check(ComposeDocument document, LintContext context)
    {
        var findings = new List<LintFinding>();

        foreach (var service in document.Services)
        {
            var built = document.ItemsOf(service.Name, "build").Count > 0 || document.NodeAt("services", service.Name, "build") is not null;

            foreach (var image in document.ItemsOf(service.Name, "image"))
            {
                if (!built && Unpinned(image.Value) is { } message) findings.Add(new LintFinding(message, image.Range));
            }

            foreach (var port in document.ItemsOf(service.Name, "ports"))
            {
                if (HostPort(port) is not { } published) continue;

                var protocol = Protocol(port);
                var taken = context.Published.FirstOrDefault(p => p.Port == published && p.Protocol == protocol);

                if (taken is not null)
                {
                    findings.Add(new LintFinding(
                        $"Port {published} is already published on this host by {taken.Owner}. Deploying {service.Name} will fail to bind it.",
                        port.Range));
                }
            }

            foreach (var volume in document.ItemsOf(service.Name, "volumes"))
            {
                var source = Source(volume);
                if (source is null) continue;

                if (source.EndsWith("docker.sock", StringComparison.Ordinal) || source.EndsWith("docker_engine", StringComparison.Ordinal))
                {
                    findings.Add(new LintFinding(
                        $"{service.Name} mounts the engine's socket, which gives it control of every container on this host — and through them, the host.",
                        volume.Range));
                }
                else if (context.HostIsRemote && source is ['.', ..] or ['~', ..])
                {
                    findings.Add(new LintFinding(
                        $"{source} is resolved on this machine, but the engine is on another one. It will bind whatever is at that path there — an empty directory, if nothing is.",
                        volume.Range));
                }
            }
        }

        return findings;
    }

    /// <summary>An image reference with no version, or <c>:latest</c> — legal, and a different image on every pull.</summary>
    static string? Unpinned(string? image)
    {
        if (string.IsNullOrWhiteSpace(image) || image.Contains("${", StringComparison.Ordinal) || image.Contains('@', StringComparison.Ordinal))
            return null;

        // The tag is after the last colon of the last path segment; a colon before a slash is a
        // registry port, as in registry:5000/app.
        var last = image[(image.LastIndexOf('/') + 1)..];
        var colon = last.LastIndexOf(':');
        var tag = colon < 0 ? null : last[(colon + 1)..];

        return tag switch
        {
            null => $"{image} has no tag, so it means {image}:latest — whatever that is on the day it is pulled. Pin a version to deploy the same thing twice.",
            "latest" => $"{image} follows whatever was pushed last. Pin a version to deploy the same thing twice.",
            _ => null,
        };
    }

    /// <summary>
    /// The host port a ports entry publishes, or null when it publishes none Dray can name.
    /// <para>
    /// Short syntax: <c>8080:80</c>, <c>127.0.0.1:8080:80</c>, <c>8080:80/udp</c>. A bare
    /// <c>80</c> publishes a random port, and a range is left alone — either way there is no one
    /// port to compare. Long syntax: <c>published</c>.
    /// </para>
    /// </summary>
    internal static int? HostPort(YamlItem item)
    {
        var text = item.Value ?? item.Fields.GetValueOrDefault("published");
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (item.Value is null) return int.TryParse(text, out var published) ? published : null;

        var mapping = text.Split('/')[0];

        // An IPv6 host address is bracketed; take what follows it.
        if (mapping.StartsWith('[') && mapping.IndexOf(']') is var close and > 0) mapping = mapping[(close + 1)..].TrimStart(':');

        var parts = mapping.Split(':');
        if (parts.Length < 2) return null;

        var host = parts[^2];
        return host.Contains('-') ? null : int.TryParse(host, out var port) ? port : null;
    }

    static string Protocol(YamlItem item)
        => item.Value is { } text && text.Contains('/') ? text[(text.IndexOf('/') + 1)..] : item.Fields.GetValueOrDefault("protocol") ?? "tcp";

    /// <summary>A volume entry's source: before the first colon in short syntax, <c>source</c> in long.</summary>
    static string? Source(YamlItem item)
    {
        if (item.Value is not { } text) return item.Fields.GetValueOrDefault("source");

        var colon = text.IndexOf(':');

        // A Windows drive letter is a colon that is not a separator.
        if (colon == 1 && text.Length > 2 && char.IsAsciiLetter(text[0]) && text[2] is '\\' or '/') colon = text.IndexOf(':', 2);

        return colon < 0 ? null : text[..colon];
    }
}
