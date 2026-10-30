using Dray.Core.Engine;
using Dray.Core.Shell;

namespace Dray.Ui.Services;

/// <summary>
/// The engine picker, as a model every head draws: the macOS toolbar's leading menu, the Windows
/// title bar's drop-down, and the web head's sidebar corner.
/// <para>
/// Each head used to build this menu itself, from <see cref="EngineManager"/> and
/// <see cref="HostSwitcher"/> directly — the same list, the same sentence per engine and the same
/// "Hosts and setup…" entry, written three times. Now it is built here once, and a head knows how to
/// show a <see cref="ChromePicker"/> and nothing about engines.
/// </para>
/// </summary>
public sealed class EnginePicker : IChromePickerSource, IDisposable
{
    /// <summary>The item that opens the Hosts page rather than choosing an engine.</summary>
    public const string ManageId = "dray:hosts";

    readonly EngineManager _engine;
    readonly HostSwitcher _switcher;
    readonly IShellState _shell;

    public EnginePicker(EngineManager engine, HostSwitcher switcher, IShellState shell)
    {
        _engine = engine;
        _switcher = switcher;
        _shell = shell;
        Current = Build();
        _engine.Changed += OnEngineChanged;
    }

    public ChromePicker Current { get; private set; }

    public event Action<ChromePicker>? Changed;

    /// <summary>
    /// <see cref="EngineManager.Changed"/> fires for every container event as well as for host
    /// changes. Rebuilding is cheap; telling a native toolbar is not, so only a picker that differs
    /// is announced.
    /// </summary>
    void OnEngineChanged()
    {
        var next = Build();
        if (next.Equals(Current)) return;

        Current = next;
        Changed?.Invoke(next);
    }

    ChromePicker Build()
    {
        var selected = _engine.Selected;

        // The name alone. The corner is narrow on every head — on macOS it is the strip of toolbar
        // above the sidebar — and "Apple container · unreachable" pushed the picker into the
        // toolbar's overflow menu, which is the one place the control that says what every page is
        // about must never go. So the state is the glyph, and the words are in the tooltip and the
        // menu, where there is room for a sentence. Glyphs rather than a tint (DESIGN.md 2.4); and
        // every page already leads with the engine's state in words when it is anything but up.
        var title = selected?.Label ?? "No engine";

        var icon = (_engine.Transition, selected?.State) switch
        {
            ({ }, _) or (_, HostConnectionState.Connecting) => IconRef.Refresh,
            (_, HostConnectionState.Unreachable or HostConnectionState.Degraded) => IconRef.Warning,
            _ => IconRef.Host,
        };

        var tooltip = selected is null
            ? "Choose an engine, or set one up"
            : $"{selected.Label} — {_engine.Transition ?? _switcher.Describe(selected)}";

        var busy = _engine.Transition is not null;

        List<ChromePickerItem> items =
        [
            .. _engine.Hosts.Select(h => new ChromePickerItem(
                h.Id,
                $"{h.Label}  ·  {_switcher.Describe(h)}",
                IsChecked: selected?.Id == h.Id,

                // Not while Docker Desktop is switching, so a second switch cannot be started on top
                // of the first.
                IsEnabled: !busy)),
            new ChromePickerItem(ManageId, "Hosts and setup…", SeparatorBefore: _engine.Hosts.Count > 0, IsCommand: true),
        ];

        return new ChromePicker("engine", title, tooltip, icon, items);
    }

    public async Task<string?> ChooseAsync(string itemId)
    {
        if (itemId == ManageId)
        {
            _shell.RequestNavigation("/hosts");
            return null;
        }

        return await _switcher.ChooseAsync(itemId);
    }

    public void Dispose() => _engine.Changed -= OnEngineChanged;
}
