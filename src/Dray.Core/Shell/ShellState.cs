namespace Dray.Core.Shell;

/// <summary>
/// The single channel between Blazor content and native chrome.
/// <para>
/// Replaces MAUI.Sherpa's twenty-member <c>IToolbarService</c> with a declarative one: a page
/// hands over a whole <see cref="PageChrome"/> at once, rather than calling <c>SetItems</c>,
/// <c>SetSearch</c> and <c>SetFilters</c> in sequence and leaving hosts to reconcile partial state.
/// </para>
/// </summary>
public interface IShellState
{
    /// <summary>What the current page has declared. Never null; <see cref="PageChrome.Empty"/> at rest.</summary>
    PageChrome Chrome { get; }

    /// <summary>The route the Blazor router is currently showing.</summary>
    string Route { get; }

    /// <summary>The chrome changed. Hosts re-project; they rebuild only if the Signature changed.</summary>
    event Action<PageChrome>? ChromeChanged;

    /// <summary>The router moved. Native sidebars sync their selection from this.</summary>
    event Action<string>? RouteChanged;

    /// <summary>Native chrome asked to navigate. The Blazor router listens.</summary>
    event Action<string>? NavigationRequested;

    /// <summary>A chrome action fired, by id. The declaring page listens.</summary>
    event Action<string>? ActionInvoked;

    event Action<string>? SearchTextChanged;

    event Action<string, int>? FilterChanged;

    void DeclareChrome(PageChrome chrome);

    /// <summary>Called by the Blazor router after it navigates.</summary>
    void NotifyRouteChanged(string route);

    /// <summary>Called by native chrome — a sidebar selection, a menu item, a deep link.</summary>
    void RequestNavigation(string route);

    void InvokeAction(string actionId);

    void SetSearchText(string text);

    void SetFilter(string filterId, int selectedIndex);
}

/// <inheritdoc cref="IShellState"/>
public sealed class ShellState : IShellState
{
    // Native selection drives the router, and the router drives native selection. Without a guard
    // the two echo each other indefinitely. Sherpa solved this with a bool in its macOS head;
    // formalising it here means the three heads cannot each get it subtly wrong.
    // See docs/NATIVE-SHELL.md section 1.2.
    bool _syncingRoute;

    public PageChrome Chrome { get; private set; } = PageChrome.Empty;

    public string Route { get; private set; } = "/";

    public event Action<PageChrome>? ChromeChanged;
    public event Action<string>? RouteChanged;
    public event Action<string>? NavigationRequested;
    public event Action<string>? ActionInvoked;
    public event Action<string>? SearchTextChanged;
    public event Action<string, int>? FilterChanged;

    public void DeclareChrome(PageChrome chrome)
    {
        ArgumentNullException.ThrowIfNull(chrome);
        if (Chrome == chrome) return;

        Chrome = chrome;
        ChromeChanged?.Invoke(chrome);
    }

    public void NotifyRouteChanged(string route)
    {
        route = Normalize(route);
        if (Route == route) return;

        Route = route;

        // Native listeners select the matching sidebar row. If one of them echoes back through
        // RequestNavigation, the guard swallows it rather than starting a loop.
        _syncingRoute = true;
        try
        {
            RouteChanged?.Invoke(route);
        }
        finally
        {
            _syncingRoute = false;
        }
    }

    public void RequestNavigation(string route)
    {
        if (_syncingRoute) return;

        route = Normalize(route);
        if (Route == route) return;

        NavigationRequested?.Invoke(route);
    }

    public void InvokeAction(string actionId)
    {
        // A host may still be showing an action the current page no longer declares — a stale
        // toolbar item mid-navigation. Dropping it is correct; routing it to whichever page
        // happens to be listening is not.
        if (!Chrome.ActionList.Any(a => a.Id == actionId && a.IsEnabled)) return;

        ActionInvoked?.Invoke(actionId);
    }

    public void SetSearchText(string text)
    {
        text ??= string.Empty;
        if (Chrome.Search is null || Chrome.Search.Text == text) return;

        Chrome = Chrome with { Search = Chrome.Search with { Text = text } };
        SearchTextChanged?.Invoke(text);
    }

    public void SetFilter(string filterId, int selectedIndex)
    {
        var filter = Chrome.FilterList.FirstOrDefault(f => f.Id == filterId);
        if (filter is null) return;
        if (selectedIndex < 0 || selectedIndex >= filter.Options.Count) return;
        if (filter.SelectedIndex == selectedIndex) return;

        Chrome = Chrome with
        {
            Filters = Chrome.FilterList
                .Select(f => f.Id == filterId ? f with { SelectedIndex = selectedIndex } : f)
                .ToList(),
        };
        FilterChanged?.Invoke(filterId, selectedIndex);
    }

    /// <summary>Routes are compared with a leading slash and no trailing one, case-insensitively.</summary>
    static string Normalize(string route)
    {
        if (string.IsNullOrWhiteSpace(route)) return "/";

        route = route.Trim();
        if (!route.StartsWith('/')) route = "/" + route;
        if (route.Length > 1) route = route.TrimEnd('/');

        return route.ToLowerInvariant();
    }
}
