using Dray.Core.Shell;
using Xunit;

namespace Dray.Core.Tests;

public class ShellStateTests
{
    static PageChrome Containers() => new(
        "Containers",
        Search: new ChromeSearch("Filter containers"),
        Actions: [ChromeAction.Primary("run", "Run…", IconRef.Play)],
        Filters: [new ChromeFilter("state", "State", [new("All"), new("Running"), new("Stopped")])]);

    // ---------------------------------------------------------------- route sync

    [Fact]
    public void RouterChangeNotifiesNativeChrome()
    {
        var shell = new ShellState();
        var seen = new List<string>();
        shell.RouteChanged += seen.Add;

        shell.NotifyRouteChanged("/containers");

        Assert.Equal(["/containers"], seen);
        Assert.Equal("/containers", shell.Route);
    }

    [Fact]
    public void NativeSelectionRequestsNavigation()
    {
        var shell = new ShellState();
        var requested = new List<string>();
        shell.NavigationRequested += requested.Add;

        shell.RequestNavigation("/images");

        Assert.Equal(["/images"], requested);
    }

    /// <summary>
    /// The ping-pong docs/NATIVE-SHELL.md section 1.2 exists to prevent: native selection drives
    /// the router, the router drives native selection. A head that echoes its selection back would
    /// otherwise loop forever.
    /// </summary>
    [Fact]
    public void NativeEchoDuringRouteSyncDoesNotLoop()
    {
        var shell = new ShellState();
        var requests = 0;

        // A well-meaning but naive host: on RouteChanged it selects the row, and its selection
        // handler calls RequestNavigation straight back.
        shell.RouteChanged += route => shell.RequestNavigation(route);
        shell.NavigationRequested += _ => requests++;

        shell.NotifyRouteChanged("/volumes");

        Assert.Equal(0, requests);
        Assert.Equal("/volumes", shell.Route);
    }

    [Fact]
    public void NavigatingToTheCurrentRouteIsANoOp()
    {
        var shell = new ShellState();
        shell.NotifyRouteChanged("/images");

        var requests = 0;
        shell.NavigationRequested += _ => requests++;
        shell.RequestNavigation("/images");

        Assert.Equal(0, requests);
    }

    [Theory]
    [InlineData("containers", "/containers")]
    [InlineData("/Containers", "/containers")]
    [InlineData("/containers/", "/containers")]
    [InlineData("  /containers  ", "/containers")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    public void RoutesAreNormalizedBeforeComparison(string input, string expected)
    {
        var shell = new ShellState();
        shell.NotifyRouteChanged(input);
        Assert.Equal(expected, shell.Route);
    }

    [Fact]
    public void NormalizationPreventsARedundantNavigation()
    {
        // A native sidebar carrying "/Containers" and a router reporting "/containers" are the
        // same place. Without normalization this fires a pointless navigation on every sync.
        var shell = new ShellState();
        shell.NotifyRouteChanged("/containers");

        var requests = 0;
        shell.NavigationRequested += _ => requests++;
        shell.RequestNavigation("/Containers/");

        Assert.Equal(0, requests);
    }

    // ---------------------------------------------------------------- chrome

    [Fact]
    public void DeclaringChromeRaisesOnceAndIsIdempotent()
    {
        var shell = new ShellState();
        var raised = 0;
        shell.ChromeChanged += _ => raised++;

        shell.DeclareChrome(Containers());
        shell.DeclareChrome(Containers()); // equal by value — a re-render, not a change

        Assert.Equal(1, raised);
    }

    [Fact]
    public void StaleActionFromNativeChromeIsDropped()
    {
        // A toolbar can still be showing the previous page's items mid-navigation. Routing that
        // click to whichever page happens to be listening would fire the wrong command.
        var shell = new ShellState();
        shell.DeclareChrome(Containers());

        var fired = new List<string>();
        shell.ActionInvoked += fired.Add;

        shell.InvokeAction("prune-images"); // not declared by this page
        shell.InvokeAction("run");

        Assert.Equal(["run"], fired);
    }

    [Fact]
    public void DisabledActionsDoNotFire()
    {
        var shell = new ShellState();
        shell.DeclareChrome(new PageChrome("Containers", Actions:
            [ChromeAction.Primary("run", "Run…", IconRef.Play) with { IsEnabled = false }]));

        var fired = 0;
        shell.ActionInvoked += _ => fired++;
        shell.InvokeAction("run");

        Assert.Equal(0, fired);
    }

    [Fact]
    public void SearchTextRoundTripsIntoChrome()
    {
        var shell = new ShellState();
        shell.DeclareChrome(Containers());

        var seen = new List<string>();
        shell.SearchTextChanged += seen.Add;
        shell.SetSearchText("nginx");

        Assert.Equal(["nginx"], seen);
        Assert.Equal("nginx", shell.Chrome.Search!.Text);
    }

    [Fact]
    public void SearchTextIsIgnoredWhenThePageHasNoSearch()
    {
        var shell = new ShellState();
        shell.DeclareChrome(new PageChrome("Dashboard"));

        var fired = 0;
        shell.SearchTextChanged += _ => fired++;
        shell.SetSearchText("nginx");

        Assert.Equal(0, fired);
    }

    [Fact]
    public void FilterSelectionRoundTripsIntoChrome()
    {
        var shell = new ShellState();
        shell.DeclareChrome(Containers());

        var seen = new List<(string, int)>();
        shell.FilterChanged += (id, i) => seen.Add((id, i));
        shell.SetFilter("state", 1);

        Assert.Equal([("state", 1)], seen);
        Assert.Equal(1, shell.Chrome.FilterList[0].SelectedIndex);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(99)]
    public void OutOfRangeFilterIndexIsRejected(int index)
    {
        var shell = new ShellState();
        shell.DeclareChrome(Containers());

        var fired = 0;
        shell.FilterChanged += (_, _) => fired++;
        shell.SetFilter("state", index);

        Assert.Equal(0, fired);
        Assert.Equal(0, shell.Chrome.FilterList[0].SelectedIndex);
    }

    [Fact]
    public void UnknownFilterIsIgnored()
    {
        var shell = new ShellState();
        shell.DeclareChrome(Containers());

        var fired = 0;
        shell.FilterChanged += (_, _) => fired++;
        shell.SetFilter("nonexistent", 1);

        Assert.Equal(0, fired);
    }

    // ---------------------------------------------------------------- signature

    /// <summary>
    /// NSToolbar resists per-navigation rebuilds, so the signature tells the host when a rebuild is
    /// genuinely needed. It must be stable across changes a host can apply in place.
    /// </summary>
    [Fact]
    public void SignatureIgnoresInPlaceChanges()
    {
        var a = Containers();
        var b = a with
        {
            Title = "Containers (3)",
            Subtitle = "docker-desktop",
            Search = a.Search! with { Text = "nginx" },
            Actions = [a.ActionList[0] with { IsEnabled = false, Tooltip = "No engine" }],
            Filters = [a.FilterList[0] with { SelectedIndex = 2 }],
        };

        Assert.Equal(a.Signature, b.Signature);
    }

    [Fact]
    public void SignatureChangesWhenTheChromeShapeChanges()
    {
        var a = Containers();

        Assert.NotEqual(a.Signature, (a with { Search = null }).Signature);
        Assert.NotEqual(a.Signature, (a with { Actions = [] }).Signature);
        Assert.NotEqual(a.Signature, (a with { Filters = [] }).Signature);

        var extraOption = a with
        {
            Filters = [a.FilterList[0] with { Options = [.. a.FilterList[0].Options, new("Paused")] }],
        };
        Assert.NotEqual(a.Signature, extraOption.Signature);
    }

    [Fact]
    public void EmptyChromeHasAStableSignature()
        => Assert.Equal(PageChrome.Empty.Signature, new PageChrome("Anything").Signature);
}
