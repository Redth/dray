namespace Dray.Core.Engine;

/// <summary>
/// What an engine's background service is doing, and whether Dray can say anything about it.
/// </summary>
/// <param name="Running">
/// Whether the engine is answering. False is a real answer — the CLI is installed and the service
/// behind it is not up — and it is the one worth acting on.
/// </param>
/// <param name="Version">The service's own version, when it reports one separately from the CLI's.</param>
/// <param name="Detail">A sentence for the user. Where the install lives, or why it is not running.</param>
public sealed record EngineServiceState(bool Running, string? Version = null, string? Detail = null);

/// <summary>
/// An engine whose background service Dray can start and stop.
/// <para>
/// Optional, and asked for with a type test rather than a capability flag, because unlike the
/// capabilities this is not a property of the connection — it is a property of the engine being on
/// <i>this</i> machine. Dray cannot start a service over SSH, and should not pretend it can.
/// </para>
/// <para>
/// Apple's <c>container</c> is the case that motivated it: the CLI answers <c>--version</c>
/// whether or not its API server is up, so an engine that looks installed can fail every call, and
/// the fix is one command the user has no reason to know.
/// </para>
/// </summary>
public interface IEngineService
{
    /// <summary>What the service is doing right now.</summary>
    Task<EngineServiceState> ServiceStatusAsync(CancellationToken ct = default);

    /// <summary>Start it. Returns null on success, or a sentence explaining the refusal.</summary>
    Task<string?> StartServiceAsync(CancellationToken ct = default);

    /// <summary>
    /// Stop it. Returns null on success, or a sentence explaining the refusal.
    /// <para>
    /// Destructive in the sense that everything running under it stops with it, so the UI confirms
    /// before calling this — the same rule as any other stop.
    /// </para>
    /// </summary>
    Task<string?> StopServiceAsync(CancellationToken ct = default);
}
