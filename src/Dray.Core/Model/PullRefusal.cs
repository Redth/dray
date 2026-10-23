namespace Dray.Core.Model;

/// <summary>
/// Reading a failed pull for the one failure Dray can do something about: a registry that wants to
/// know who is asking.
/// </summary>
public static class PullRefusal
{
    /// <summary>
    /// Whether the engine's error is a registry refusing an anonymous request.
    /// <para>
    /// Matched on the registry's own words, which the engine passes through: Docker Hub says "pull
    /// access denied … requested access to the resource is denied", GHCR and most others say
    /// "unauthorized", and a registry that wants basic auth says "no basic auth credentials". A
    /// missing repository on Docker Hub reads exactly like a private one — the registry will not say
    /// which — so this is "might need a credential", and the retry is what finds out.
    /// </para>
    /// </summary>
    public static bool NeedsCredential(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;

        // Not "denied" alone: "permission denied" is the engine's own filesystem talking, and
        // telling someone to sign in to fix a full or read-only disk sends them the wrong way.
        return error.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || error.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            || error.Contains("access to the resource is denied", StringComparison.OrdinalIgnoreCase)
            || error.Contains("denied: ", StringComparison.OrdinalIgnoreCase) && !error.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            || error.Contains("authentication required", StringComparison.OrdinalIgnoreCase)
            || error.Contains("no basic auth credentials", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The refusal, with what it means when Dray has no credential to offer: either the repository is
    /// not there, or it is private — and which of those is not something the registry will say.
    /// </summary>
    public static string Explain(string error, string registry)
        => $"{registry} would not hand this over without signing in. Either there is no such repository, "
            + $"or it is private — sign in to {registry} on the Registries page and pull again. ({error.Trim()})";
}
