using Dray.Core.Engine;
using Dray.Core.Shell;

namespace Dray.Ui.Services;

/// <summary>
/// Choosing a host, wherever it is chosen from: the sidebar's picker, a row on the Hosts page, the
/// command palette.
/// <para>
/// Usually that is just a selection. When the host is one of Docker Desktop's engines and Docker
/// Desktop is not running it, choosing it means switching Docker Desktop — which stops the engine it
/// is running now, and every container on it. So that is asked first, in the same confirmation every
/// other stop uses, and only then done. One place, so the sidebar and the page cannot disagree about
/// when to ask.
/// </para>
/// </summary>
public sealed class HostSwitcher(EngineManager engine, IShellBridge shell)
{
    /// <summary>Choose <paramref name="hostId"/>. Null when it is now selected or the user declined; otherwise why not.</summary>
    public async Task<string?> ChooseAsync(string hostId)
    {
        if (engine.Transition is not null) return null;

        if (engine.Hosts.FirstOrDefault(h => h.Id == hostId) is not { } host) return null;

        // Starting it stops nothing, so it is not asked about. It starts in whichever mode it was
        // last in, and switches after if that is not this one — still nothing running to stop.
        if (engine.StartNeededFor(host)) return await engine.StartDesktopAsync(host.DesktopMode);

        if (engine.SwitchNeededFor(host) is { } mode && engine.Desktop.Mode is { } current)
        {
            var answer = await shell.ConfirmDestructiveAsync(new DestructiveConfirm(
                $"Switch Docker Desktop to {DockerDesktop.Label(mode)}?",
                $"Docker Desktop runs one engine at a time. Switching stops its {DockerDesktop.Label(current)} engine, "
                + "and every container running on it stops too. Nothing is deleted: switching back brings that "
                + "engine up again, and containers with a restart policy start with it.",
                "Switch"));

            return answer == ConfirmResult.Confirm ? await engine.SwitchDesktopAsync(mode) : null;
        }

        await engine.SelectAsync(hostId);
        return null;
    }
}
