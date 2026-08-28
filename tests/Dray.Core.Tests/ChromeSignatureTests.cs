using Dray.Core.Model;
using Dray.Core.Shell;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The signature the macOS toolbar projector rebuilds from.
/// <para>
/// This is native-only behaviour and there is no way to see it in the web head: on macOS the
/// toolbar is a real <c>NSToolbar</c>, and <c>MacToolbarProjector</c> rebuilds it only when this
/// string changes, updating in place otherwise. A signature that failed to change when the shape
/// did would leave the window showing the previous page's buttons — including a Pause button on an
/// engine that cannot pause, which is exactly what the capability work was meant to prevent.
/// </para>
/// </summary>
public class ChromeSignatureTests
{
    [Fact]
    public void LosingAnActionChangesTheSignature()
    {
        var withPause = new PageChrome("Containers", Actions:
        [
            ChromeAction.Secondary("stop", "Stop", IconRef.Stop),
            ChromeAction.Secondary("pause", "Pause", IconRef.Pause),
        ]);

        var without = new PageChrome("Containers", Actions:
            [ChromeAction.Secondary("stop", "Stop", IconRef.Stop)]);

        Assert.NotEqual(withPause.Signature, without.Signature);
    }

    [Fact]
    public void LosingSearchChangesTheSignature()
    {
        // The Volumes page drops its search box on an engine with no volumes. The native toolbar
        // has to actually remove the field, not leave it there disconnected.
        var searchable = new PageChrome("Volumes", Search: new ChromeSearch("Filter volumes", ""));
        var plain = new PageChrome("Volumes");

        Assert.NotEqual(searchable.Signature, plain.Signature);
    }

    [Fact]
    public void SwappingTheWholeActionSetChangesTheSignature()
    {
        // What switching from a Docker host to Apple's does to the Volumes page: three actions
        // become one.
        var docker = new PageChrome("Volumes", Actions:
        [
            ChromeAction.Primary("create", "Create…", IconRef.Plus),
            ChromeAction.Secondary("refresh", "Refresh", IconRef.Refresh),
            ChromeAction.Destructive("prune", "Prune unused", IconRef.Trash),
        ]);

        var apple = new PageChrome("Volumes", Actions:
            [ChromeAction.Secondary("hosts", "Switch engine…", IconRef.Host)]);

        Assert.NotEqual(docker.Signature, apple.Signature);
    }

    [Fact]
    public void AChangedKindChangesTheSignatureEvenWithTheSameId()
    {
        // Kind decides how macOS renders the item — a destructive action is drawn quietly. Two
        // shapes that differ only in kind must still rebuild.
        var secondary = new PageChrome("Images", Actions: [ChromeAction.Secondary("prune", "Prune", IconRef.Trash)]);
        var destructive = new PageChrome("Images", Actions: [ChromeAction.Destructive("prune", "Prune", IconRef.Trash)]);

        Assert.NotEqual(secondary.Signature, destructive.Signature);
    }

    [Fact]
    public void OnlyTheTitleChangingDoesNotForceARebuild()
    {
        // Navigating between two containers changes the title and nothing else. Rebuilding the
        // whole toolbar for that makes the buttons flicker on every navigation — which is why the
        // signature exists rather than comparing the chrome wholesale.
        var first = new PageChrome("dray-web", Subtitle: "Running",
            Actions: [ChromeAction.Secondary("stop", "Stop", IconRef.Stop)]);

        var second = new PageChrome("dray-redis", Subtitle: "Running",
            Actions: [ChromeAction.Secondary("stop", "Stop", IconRef.Stop)]);

        Assert.Equal(first.Signature, second.Signature);
    }

    [Fact]
    public void ADisabledActionDoesNotForceARebuildEither()
    {
        // Refresh is disabled while the event stream is healthy and enabled when it is not. That
        // is an in-place update of an existing item, not a new toolbar.
        var enabled = new PageChrome("Containers", Actions:
            [ChromeAction.Secondary("refresh", "Refresh", IconRef.Refresh)]);

        var disabled = new PageChrome("Containers", Actions:
            [ChromeAction.Secondary("refresh", "Refresh", IconRef.Refresh) with { IsEnabled = false }]);

        Assert.Equal(enabled.Signature, disabled.Signature);
    }

    // ---------------------------------------------------------------- the real thing

    [Fact]
    public void AContainersToolbarLosesPauseOnAnEngineThatCannotPause()
    {
        // The end-to-end version, built from the same Core call the detail page's chrome is built
        // from. On macOS these become NSToolbar items, so an engine change has to reshape the
        // window's own toolbar rather than leaving a Pause button that would always fail.
        static PageChrome ToolbarFor(bool canPause) => new(
            "web",
            Actions:
            [
                .. ContainerActions
                    .For(DockerState.Running, canPause)
                    .Select(a => ChromeAction.Secondary($"do:{a}", ContainerActions.Label(a), IconRef.Play)),
            ]);

        var docker = ToolbarFor(canPause: true);
        var apple = ToolbarFor(canPause: false);

        Assert.Contains(docker.ActionList, a => a.Label == "Pause");
        Assert.DoesNotContain(apple.ActionList, a => a.Label == "Pause");

        Assert.NotEqual(docker.Signature, apple.Signature);
    }
}
