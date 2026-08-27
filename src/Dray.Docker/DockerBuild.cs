using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Docker.DotNet;
using Docker.DotNet.Models;
using Dray.Core.Engine;
using Dray.Core.Model;

namespace Dray.Docker;

/// <summary>Building an image from a local context.</summary>
public static class DockerBuild
{
    /// <summary>
    /// Directories never worth sending.
    /// <para>
    /// A build context is tarred and uploaded whole, so a <c>node_modules</c> or a <c>.git</c> turns
    /// a two-second build into a two-minute one. A real <c>.dockerignore</c> is the proper answer
    /// and is honoured by neither Dray nor the engine here — this is a floor, not a substitute.
    /// </para>
    /// </summary>
    static readonly string[] NeverSend = [".git", "node_modules", "bin", "obj", ".vs", ".idea"];

    public static async IAsyncEnumerable<BuildProgress> RunAsync(
        DockerClient client,
        BuildRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Directory.Exists(request.ContextDirectory))
        {
            yield return new BuildProgress(string.Empty, $"No such directory: {request.ContextDirectory}");
            yield break;
        }

        var dockerfile = Path.Combine(request.ContextDirectory, request.Dockerfile);

        if (!File.Exists(dockerfile))
        {
            yield return new BuildProgress(
                string.Empty,
                $"No {request.Dockerfile} in {request.ContextDirectory}.");

            yield break;
        }

        // Written to disk rather than held in memory: a build context is routinely hundreds of
        // megabytes, and the engine streams it rather than needing it all at once.
        var archive = Path.Combine(Path.GetTempPath(), $"dray-build-{Guid.NewGuid():n}.tar");

        var channel = Channel.CreateUnbounded<BuildProgress>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        // Packaged before the stream opens. An iterator cannot yield from inside a catch, so the
        // failure is captured and reported on the next line rather than thrown at the caller.
        string? packagingFailure = null;

        try
        {
            CreateContext(request.ContextDirectory, archive);
        }
        catch (Exception ex)
        {
            packagingFailure = $"Could not package the build context: {ex.Message}";
        }

        if (packagingFailure is not null)
        {
            yield return new BuildProgress(string.Empty, packagingFailure);
            yield break;
        }

        var monitor = Task.Run(async () =>
        {
            try
            {
                await using var context = File.OpenRead(archive);

                await client.Images.BuildImageFromDockerfileAsync(
                    new ImageBuildParameters
                    {
                        Dockerfile = request.Dockerfile,
                        Tags = request.Tag is null ? null : [request.Tag],
                        NoCache = request.NoCache,
                        Pull = request.Pull ? "true" : null,

                        // The engine prunes intermediate containers itself; leaving them behind is
                        // how a machine ends up with two hundred exited build containers.
                        Remove = true,
                        ForceRemove = true,
                    },
                    context,
                    authConfigs: null,
                    headers: null,
                    new Progress<JSONMessage>(m =>
                    {
                        if (Map(m) is { } step) channel.Writer.TryWrite(step);
                    }),
                    ct).ConfigureAwait(false);

                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var step in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return step;
        }
        finally
        {
            await monitor.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            try
            {
                File.Delete(archive);
            }
            catch (IOException)
            {
                // The temp file outlives the build rather than the build failing over it.
            }
        }
    }

    /// <summary>Tar the context directory, skipping what is never worth uploading.</summary>
    static void CreateContext(string directory, string archivePath)
    {
        using var output = File.Create(archivePath);
        using var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: false);

        var root = Path.GetFullPath(directory);

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

            if (relative.Split('/').Any(segment => NeverSend.Contains(segment, StringComparer.Ordinal)))
                continue;

            writer.WriteEntry(file, relative);
        }
    }

    internal static BuildProgress? Map(JSONMessage m)
    {
        // The engine reports failures inside the stream rather than by failing the request, so a
        // build that could not resolve a base image otherwise looks like a success.
        if (!string.IsNullOrWhiteSpace(m.Error?.Message))
            return new BuildProgress(string.Empty, m.Error.Message);

        var text = m.Stream ?? m.Status;
        if (string.IsNullOrWhiteSpace(text)) return null;

        return new BuildProgress(text.TrimEnd('\n', '\r'));
    }
}
