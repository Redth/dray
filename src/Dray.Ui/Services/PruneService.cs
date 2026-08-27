using Dray.Core.Engine;
using Dray.Core.Model;
using Dray.Core.Shell;

namespace Dray.Ui.Services;

/// <summary>
/// Preview a prune, confirm it by typing, then run it.
/// <para>
/// Shared because four pages offer this and the sequence has to be identical every time. PRODUCT.md
/// is specific: destructive operations are typed, not clicked, and the preview has to match what
/// actually happens. A page that assembled its own confirmation would eventually assemble a weaker
/// one.
/// </para>
/// </summary>
public sealed class PruneService(EngineManager engine, IShellBridge shell)
{
    /// <summary>
    /// Ask, then do.
    /// </summary>
    /// <returns>
    /// What happened, or null when the user cancelled or there was nothing to remove. Callers show
    /// the outcome; cancelling is not an outcome worth reporting.
    /// </returns>
    public async Task<PruneOutcome?> RunAsync(PruneKind kind, CancellationToken ct = default)
    {
        PrunePreview preview;

        try
        {
            preview = await engine.PreviewPruneAsync(kind, ct);
        }
        catch (Exception ex)
        {
            return PruneOutcome.Failed(ex.Message);
        }

        if (preview.IsEmpty)
        {
            // Not a failure and not worth a dialog: saying so in place is the whole answer.
            return PruneOutcome.NothingToDo(kind);
        }

        var answer = await shell.ConfirmDestructiveAsync(
            new DestructiveConfirm(
                Title(preview),
                Body(preview),
                $"Remove {preview.Items.Count} {preview.Noun}",

                // Typed, because this is irreversible and reaches things the user did not name
                // individually. The phrase says what is about to happen rather than "DELETE".
                preview.ConfirmationPhrase),
            ct);

        if (answer != ConfirmResult.Confirm) return null;

        try
        {
            var result = await engine.PruneAsync(kind, ct);
            return PruneOutcome.Succeeded(result);
        }
        catch (Exception ex)
        {
            return PruneOutcome.Failed(ex.Message);
        }
    }

    static string Title(PrunePreview preview)
        => $"Remove {preview.Items.Count} unused {preview.Noun}?";

    /// <summary>
    /// The body names what goes. A count is not a preview — the point is that the user can see the
    /// thing they forgot about before it is deleted.
    /// </summary>
    static string Body(PrunePreview preview)
    {
        const int shown = 12;

        var lines = preview.Items.Take(shown).Select(i => $"  {i}");
        var listed = string.Join('\n', lines);

        if (preview.Items.Count > shown)
            listed += $"\n  …and {preview.Items.Count - shown} more";

        var reclaim = preview.ReclaimedBytes > 0
            ? $"\n\nThis frees {Humanize.Bytes(preview.ReclaimedBytes)}."
            : string.Empty;

        return $"This cannot be undone.\n\n{listed}{reclaim}";
    }
}

/// <summary>What a prune did, in terms a page can put on screen.</summary>
public sealed record PruneOutcome(bool Ok, string Message)
{
    public static PruneOutcome Succeeded(PruneResult result)
    {
        var noun = result.Removed == 1 ? Singular(result.Kind) : Plural(result.Kind);

        var freed = result.ReclaimedBytes > 0
            ? $", freeing {Humanize.Bytes(result.ReclaimedBytes)}"
            : string.Empty;

        return new PruneOutcome(true, $"Removed {result.Removed} {noun}{freed}.");
    }

    public static PruneOutcome NothingToDo(PruneKind kind)
        => new(true, $"No unused {Plural(kind)} to remove.");

    public static PruneOutcome Failed(string message) => new(false, message);

    static string Singular(PruneKind kind) => kind switch
    {
        PruneKind.Images => "image",
        PruneKind.Containers => "container",
        PruneKind.Volumes => "volume",
        _ => "network",
    };

    static string Plural(PruneKind kind) => kind switch
    {
        PruneKind.Images => "images",
        PruneKind.Containers => "containers",
        PruneKind.Volumes => "volumes",
        _ => "networks",
    };
}
