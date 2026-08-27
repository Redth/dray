namespace Dray.Docker;

/// <summary>
/// Turning the engine's timestamps into <see cref="DateTimeOffset"/>.
/// <para>
/// The client deserialises these into <see cref="DateTime"/>, and which <see cref="DateTimeKind"/>
/// comes back depends on what the engine sent: podman's stats carry a local offset, container
/// creation times arrive as UTC, and some fields have no zone at all. Constructing a
/// <c>DateTimeOffset</c> with a zero offset from a <c>Local</c> value throws outright, which is how
/// this was found — a stats stream that died on its first sample.
/// </para>
/// </summary>
internal static class DockerTime
{
    /// <summary>The moment, or null when the engine sent nothing.</summary>
    public static DateTimeOffset? From(DateTime value)
        => value == default ? null : Resolve(value);

    /// <summary>The moment, falling back to now when the engine sent nothing.</summary>
    public static DateTimeOffset FromOrNow(DateTime value)
        => value == default ? DateTimeOffset.UtcNow : Resolve(value);

    static DateTimeOffset Resolve(DateTime value) => value.Kind switch
    {
        // Already carries an offset; let the framework use it.
        DateTimeKind.Local => new DateTimeOffset(value),

        DateTimeKind.Utc => new DateTimeOffset(value, TimeSpan.Zero),

        // No zone. The Engine API is UTC throughout, so saying so is accurate rather than a guess —
        // and reading it as local time would shift every timestamp by the user's offset.
        _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero),
    };
}
