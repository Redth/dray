using Dray.Core.Model;
using Dray.Core.Shell;

namespace Dray.Core.Navigation;

/// <summary>What a palette entry does when chosen.</summary>
public enum CommandKind
{
    /// <summary>Go somewhere. Always safe, always available.</summary>
    Navigate,

    /// <summary>Act on something — start a container, prune images.</summary>
    Action,

    /// <summary>Jump to a specific container, image, volume, network or stack.</summary>
    Entity,
}

/// <summary>
/// One thing the palette can offer.
/// </summary>
/// <param name="Id">Stable identity, used to record what was chosen recently.</param>
/// <param name="Title">What the user reads. The thing being acted on comes first: "dray-web · Stop".</param>
/// <param name="Category">Groups entries and gives the row its second line.</param>
/// <param name="Keywords">
/// Extra text matched against but not displayed — an image name, a container id, a synonym. Lets
/// "postgres" find a container called <c>db</c>.
/// </param>
public sealed record Command(
    string Id,
    string Title,
    string Category,
    CommandKind Kind,
    IconRef Icon,
    string? Detail = null,
    string? Keywords = null,
    string? Shortcut = null,
    bool IsDestructive = false)
{
    /// <summary>Everything a search runs against, lowercased once at construction.</summary>
    public string Haystack { get; } =
        $"{Title} {Category} {Detail} {Keywords}".ToLowerInvariant();
}

/// <summary>One command and how well it matched.</summary>
public sealed record CommandMatch(Command Command, int Score, IReadOnlyList<int> Highlights);

/// <summary>
/// Ranking palette entries against what has been typed.
/// <para>
/// Subsequence matching rather than substring, because that is what people expect of a palette:
/// "rsc" should find "Restart container". Scoring favours matches at the start of a word, so
/// "stop" ranks "Stop" above "Prune stopped".
/// </para>
/// </summary>
public static class CommandSearch
{
    /// <summary>
    /// Rank commands against a query, best first. An empty query returns everything in its given
    /// order, which is how the palette shows its default list.
    /// </summary>
    public static IReadOnlyList<CommandMatch> Rank(IEnumerable<Command> commands, string query)
    {
        var needle = query.Trim().ToLowerInvariant();

        if (needle.Length == 0)
            return [.. commands.Select(c => new CommandMatch(c, 0, []))];

        return
        [
            .. commands
                .Select(c => Match(c, needle))
                .Where(m => m is not null)
                .Select(m => m!)
                .OrderByDescending(m => m.Score)

                // Ties broken by title so the list does not reshuffle between identical queries.
                .ThenBy(m => m.Command.Title, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// Score one command, or null when the query is not a subsequence of it.
    /// </summary>
    internal static CommandMatch? Match(Command command, string needle)
    {
        // Matched against the title first so the highlights land on what the user can see; the
        // wider haystack only decides whether the row appears at all.
        var titleScore = Subsequence(command.Title.ToLowerInvariant(), needle, out var highlights);

        if (titleScore is { } score)
        {
            // A command whose title starts with the query is almost always the one meant.
            if (command.Title.StartsWith(needle, StringComparison.OrdinalIgnoreCase)) score += 60;

            return new CommandMatch(command, score, highlights);
        }

        // Not in the title, but perhaps in the image name or the id. Worth offering, ranked below
        // anything that matched visibly — a row that matched on text the user cannot see looks
        // like a bug if it outranks one that did.
        return Subsequence(command.Haystack, needle, out _) is { } hidden
            ? new CommandMatch(command, hidden - 200, [])
            : null;
    }

    /// <summary>
    /// Whether every character of <paramref name="needle"/> appears in order, and how good the fit
    /// is. Higher is better.
    /// </summary>
    static int? Subsequence(string haystack, string needle, out IReadOnlyList<int> highlights)
    {
        var positions = new List<int>(needle.Length);
        var score = 0;
        var at = 0;

        foreach (var c in needle)
        {
            var found = haystack.IndexOf(c, at);
            if (found < 0)
            {
                highlights = [];
                return null;
            }

            // A character beginning a word is worth far more than one in the middle: it is what
            // makes an acronym like "rsc" rank above an accidental scatter of the same letters.
            var atWordStart = found == 0 || haystack[found - 1] is ' ' or '-' or '_' or '·' or '/' or ':';
            score += atWordStart ? 12 : 2;

            // Consecutive characters are a stronger signal than scattered ones.
            if (positions.Count > 0 && found == positions[^1] + 1) score += 6;

            positions.Add(found);
            at = found + 1;
        }

        // A short haystack that matched is a tighter fit than a long one — "Stop" beats
        // "Prune stopped" for the query "stop".
        score += Math.Max(0, 40 - haystack.Length);

        highlights = positions;
        return score;
    }
}

/// <summary>
/// Everything the palette can reach.
/// <para>
/// Built fresh on each open rather than registered ahead of time, because most of it is the user's
/// own containers and images — a registry populated at startup would be stale the moment anything
/// changed.
/// </para>
/// </summary>
public static class CommandCatalogue
{
    /// <summary>
    /// The navigation commands, one per place in the app.
    /// <para>
    /// Built from the same manifest the sidebar renders, so the palette cannot drift out of step
    /// with the nav — a route added in one place appears in both.
    /// </para>
    /// </summary>
    public static IEnumerable<Command> Navigation(bool includeDebug)
        => NavigationManifest.Leaves(includeDebug)
            .Where(node => node.Route is not null)
            .Select(node => new Command(
                $"go:{node.Route}",
                node.Title,
                "Go to",
                CommandKind.Navigate,
                node.Icon ?? IconRef.ChevronRight,
                Detail: node.Route));

    /// <summary>
    /// A jump to each container, plus the actions that apply to it right now.
    /// <para>
    /// State-filtered like everywhere else: offering Start on a running container through the
    /// palette would be the same mistake as offering it in the row, with less context to catch it.
    /// </para>
    /// </summary>
    /// <param name="canPause">
    /// Whether the engine can pause. The palette is where a user goes when they know what they
    /// want, so offering something the engine will refuse is worse here, not better.
    /// </param>
    public static IEnumerable<Command> ForContainers(IEnumerable<ContainerSummary> containers, bool canPause = true)
    {
        foreach (var container in containers)
        {
            var image = Humanize.ImageName(container.Image);

            yield return new Command(
                $"open:container:{container.Id}",
                container.Name,
                "Container",
                CommandKind.Entity,
                IconRef.Container,
                Detail: container.Status.Label,

                // The image and id are searchable without being shown: "postgres" should find a
                // container called "db".
                Keywords: $"{image} {container.ShortId} {container.Stack}");

            foreach (var action in ContainerActions.For(container.State, canPause))
            {
                yield return new Command(
                    $"do:{action}:{container.Id}",
                    $"{ContainerActions.Label(action)} {container.Name}",
                    "Container",
                    CommandKind.Action,
                    IconFor(action),
                    Detail: container.Status.Label,
                    Keywords: $"{image} {container.ShortId}",
                    IsDestructive: ContainerActions.IsDestructive(action));
            }
        }
    }

    public static IEnumerable<Command> ForStacks(IEnumerable<StackSummary> stacks)
        => stacks.Select(stack => new Command(
            $"open:stack:{stack.Name}",
            stack.Name,
            "Stack",
            CommandKind.Entity,
            IconRef.Stack,
            Detail: $"{stack.RunningCount}/{stack.ContainerCount} running",
            Keywords: string.Join(' ', stack.Services.Select(s => s.Name))));

    public static IEnumerable<Command> ForImages(IEnumerable<ImageSummary> images)
        => images.Select(image => new Command(
            $"open:image:{image.Id}",
            image.DisplayName,
            "Image",
            CommandKind.Entity,
            IconRef.Image,
            Detail: image.ShortId,
            Keywords: string.Join(' ', image.Tags.Select(t => t.Display))));

    public static IEnumerable<Command> ForVolumes(IEnumerable<VolumeSummary> volumes)
        => volumes.Select(volume => new Command(
            $"open:volume:{volume.Name}",
            volume.DisplayName,
            "Volume",
            CommandKind.Entity,
            IconRef.Volume,
            Detail: volume.IsInUse ? $"used by {Humanize.Names([.. volume.UsedBy])}" : "not in use",
            Keywords: volume.Name));

    static IconRef IconFor(ContainerAction action) => action switch
    {
        ContainerAction.Start or ContainerAction.Unpause => IconRef.Play,
        ContainerAction.Stop => IconRef.Stop,
        ContainerAction.Restart => IconRef.Restart,
        ContainerAction.Pause => IconRef.Pause,
        ContainerAction.Kill => IconRef.Warning,
        ContainerAction.Remove => IconRef.Trash,
        _ => IconRef.More,
    };
}
