using AppKit;
using Dray.Core.Engine;
using Dray.Core.Model;
using Dray.Core.Shell;
using Foundation;

namespace Dray.MacOS;

/// <summary>
/// Dray in the menu bar: how many containers are running, and the things worth doing without
/// opening the window.
/// <para>
/// The count is the whole point. It is the one number a developer glances at all day — "is the
/// stack up?" — and answering it without a window is the difference between an app that is open
/// and an app that is running.
/// </para>
/// <para>
/// Built from the same <see cref="EntityStore"/> the UI reads, so it follows the event stream: a
/// container dying updates the menu bar without anything here polling for it.
/// </para>
/// </summary>
public sealed class MacStatusItem : IDisposable
{
    /// <summary>
    /// How many containers the menu lists before it stops. A menu bar item that unrolls forty
    /// rows is a worse way to find a container than the window it is trying to save you opening.
    /// </summary>
    const int MostListed = 10;

    readonly EngineManager _engine;
    readonly IShellState _shell;

    readonly NSStatusItem _item;

    // Menu targets are plain NSObjects, and without a strong reference the runtime collects them
    // and the items silently stop working — docs/NATIVE-SHELL.md section 1.5b, the hard way.
    readonly List<NSObject> _targets = [];

    bool _disposed;

    public MacStatusItem(EngineManager engine, IShellState shell)
    {
        _engine = engine;
        _shell = shell;

        _item = NSStatusBar.SystemStatusBar.CreateStatusItem(NSStatusItemLength.Variable);
        _item.Button.Image = NSImage.GetSystemSymbol("shippingbox", null);
        _item.Button.Image!.Template = true;
        _item.Button.ImagePosition = NSCellImagePosition.ImageLeading;

        Rebuild();

        _engine.Changed += OnChanged;
    }

    void OnChanged()
    {
        if (_disposed) return;

        NSApplication.SharedApplication.BeginInvokeOnMainThread(Rebuild);
    }

    void Rebuild()
    {
        if (_disposed) return;

        var containers = _engine.Store.Containers;
        var running = containers.Where(c => c.State == DockerState.Running).ToList();

        // No number when nothing is running: a zero beside the icon reads as a badge saying
        // something is wrong, and nothing running is the ordinary morning state.
        _item.Button.Title = running.Count > 0 ? $" {running.Count}" : "";
        _item.Button.ToolTip = Describe(running.Count, containers.Count);

        var menu = new NSMenu();
        _targets.Clear();

        menu.AddItem(new NSMenuItem(Describe(running.Count, containers.Count)) { Enabled = false });
        menu.AddItem(NSMenuItem.SeparatorItem);

        foreach (var container in running.Take(MostListed))
        {
            var item = new NSMenuItem(container.Name);
            var captured = container;

            var target = new Action(() => Open($"/containers/{captured.ShortId}"));
            item.Activated += (_, _) => target();
            _targets.Add(new Retainer(target));

            // Stop lives one level in, so the container's own row is a navigation and nothing in
            // this menu stops anything by being clicked slightly wrong.
            var submenu = new NSMenu();

            var open = new NSMenuItem("Open in Dray");
            open.Activated += (_, _) => Open($"/containers/{captured.ShortId}");
            submenu.AddItem(open);

            var stop = new NSMenuItem("Stop");
            stop.Activated += (_, _) => _ = _engine.PerformAsync(captured.Id, ContainerAction.Stop);
            submenu.AddItem(stop);

            var restart = new NSMenuItem("Restart");
            restart.Activated += (_, _) => _ = _engine.PerformAsync(captured.Id, ContainerAction.Restart);
            submenu.AddItem(restart);

            item.Submenu = submenu;
            menu.AddItem(item);
        }

        if (running.Count > MostListed)
        {
            menu.AddItem(new NSMenuItem($"and {running.Count - MostListed} more…") { Enabled = false });
        }

        menu.AddItem(NSMenuItem.SeparatorItem);

        var containersItem = new NSMenuItem("All containers");
        containersItem.Activated += (_, _) => Open("/containers");
        menu.AddItem(containersItem);

        var stacksItem = new NSMenuItem("Stacks");
        stacksItem.Activated += (_, _) => Open("/stacks");
        menu.AddItem(stacksItem);

        menu.AddItem(NSMenuItem.SeparatorItem);

        var quit = new NSMenuItem("Quit Dray", "q", (_, _) => NSApplication.SharedApplication.Terminate(null));
        menu.AddItem(quit);

        _item.Menu = menu;
    }

    static string Describe(int running, int total) => (running, total) switch
    {
        (0, 0) => "Dray — nothing here yet",
        (0, _) => $"Dray — nothing running, {total} stopped",
        (_, _) => $"Dray — {running} running of {total}",
    };

    /// <summary>
    /// Bring the window forward and go somewhere in it.
    /// <para>
    /// Activation first: the navigation lands in a window behind everything else otherwise, and
    /// the user is left looking at the menu bar wondering whether the click did anything.
    /// </para>
    /// </summary>
    void Open(string route)
    {
        // Activate(), not the obsolete ActivateIgnoringOtherApps: since macOS 14 an app asks to
        // come forward and the system decides, which is the behaviour a menu-bar click should have
        // anyway — it was a click on this app.
        NSApplication.SharedApplication.Activate();
        _shell.RequestNavigation(route);
    }

    /// <summary>Keeps a delegate alive for as long as the menu that calls it.</summary>
    sealed class Retainer(Action action) : NSObject
    {
        public Action Action { get; } = action;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _engine.Changed -= OnChanged;

        NSStatusBar.SystemStatusBar.RemoveStatusItem(_item);
        _targets.Clear();
    }
}
