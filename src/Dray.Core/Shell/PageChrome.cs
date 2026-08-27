namespace Dray.Core.Shell;

/// <summary>How an action is weighted, which decides its treatment in native chrome.</summary>
public enum ChromeActionKind
{
    /// <summary>Brand fill. At most one per page (DESIGN.md section 6).</summary>
    Primary,

    Secondary,

    /// <summary>
    /// Destroys something. Never rendered in the brand colour, always confirmed
    /// (PRODUCT.md: "destructive operations are typed, not clicked").
    /// </summary>
    Destructive,
}

/// <summary>A single command a page offers in the window's native chrome.</summary>
public sealed record ChromeAction(
    string Id,
    string Label,
    IconRef Icon,
    ChromeActionKind Kind = ChromeActionKind.Secondary,
    bool IsEnabled = true,
    string? Tooltip = null,
    string? Shortcut = null)
{
    public static ChromeAction Primary(string id, string label, IconRef icon, string? shortcut = null)
        => new(id, label, icon, ChromeActionKind.Primary, Shortcut: shortcut);

    public static ChromeAction Secondary(string id, string label, IconRef icon, string? shortcut = null)
        => new(id, label, icon, ChromeActionKind.Secondary, Shortcut: shortcut);

    public static ChromeAction Destructive(string id, string label, IconRef icon)
        => new(id, label, icon, ChromeActionKind.Destructive);
}

/// <summary>
/// The way back out of a detail page.
/// <para>
/// Its own slot rather than another <see cref="ChromeAction"/>, because it is not one. There is at
/// most one, it is navigation rather than a command, and every platform already agrees on where it
/// belongs: leading, not trailing with the actions. Modelling it as an action meant each host had
/// to special-case an id it should not have known about, and the web chrome rendered it as a
/// forward-pointing button on the far right — the opposite of what it does.
/// </para>
/// </summary>
/// <param name="Id">Raised through <c>InvokeAction</c> like any other, so pages handle it in one place.</param>
/// <param name="Label">What it goes back to, e.g. "All containers". Shown as a tooltip where space is tight.</param>
public sealed record ChromeBack(string Id, string Label);

/// <summary>A search field in the chrome. Absent means the page has no search.</summary>
public sealed record ChromeSearch(string Placeholder, string Text = "");

/// <summary>An option in a <see cref="ChromeFilter"/>.</summary>
public sealed record ChromeFilterOption(string Label, IconRef? Icon = null, string? Detail = null);

/// <summary>
/// A dropdown in the chrome. General on purpose: the host picker, a state filter and a stack
/// filter are all this. A chrome manager renders items a page supplies and never reaches for a
/// feature service to build its own — the mistake that grew Sherpa's Windows title bar to 780
/// lines and seven injected dependencies.
/// </summary>
public sealed record ChromeFilter(
    string Id,
    string Label,
    IReadOnlyList<ChromeFilterOption> Options,
    int SelectedIndex = 0)
{
    /// <summary>Value equality including <see cref="Options"/> — see the note on PageChrome.Equals.</summary>
    public bool Equals(ChromeFilter? other)
        => other is not null
           && Id == other.Id
           && Label == other.Label
           && SelectedIndex == other.SelectedIndex
           && Options.SequenceEqual(other.Options);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Label);
        hash.Add(SelectedIndex);
        foreach (var o in Options) hash.Add(o);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Everything a page wants from the window's native chrome, declared as one value.
/// <para>
/// A page states what it needs the way it states a page title; the host projects that onto
/// <c>NSToolbar</c>, <c>CommandBar</c> or <c>AdwHeaderBar</c>. Pages never imperatively mutate
/// native chrome, and hosts never contain route tables — see docs/NATIVE-SHELL.md section 2.1 for
/// the failure this replaces.
/// </para>
/// </summary>
public sealed record PageChrome(
    string Title,
    string? Subtitle = null,
    ChromeSearch? Search = null,
    IReadOnlyList<ChromeAction>? Actions = null,
    IReadOnlyList<ChromeFilter>? Filters = null,
    ChromeBack? Back = null)
{
    public static readonly PageChrome Empty = new(string.Empty);

    /// <summary>
    /// Whether the chrome has anything but its title.
    /// <para>
    /// A web toolbar with no controls is a second copy of the page heading, which is why detail
    /// pages looked like they had two titles. Native heads always draw their toolbar — the window
    /// has one whether Dray fills it or not — so this is only consulted by the web chrome.
    /// </para>
    /// </summary>
    public bool HasControls => Search is not null || ActionList.Count > 0 || FilterList.Count > 0;

    public IReadOnlyList<ChromeAction> ActionList => Actions ?? [];

    public IReadOnlyList<ChromeFilter> FilterList => Filters ?? [];

    /// <summary>
    /// Value equality including the collections.
    /// <para>
    /// A positional record compares <see cref="IReadOnlyList{T}"/> members by reference, so two
    /// chromes built from identical literals would compare unequal and every Blazor re-render
    /// would look like a change — re-raising <c>ChromeChanged</c> and thrashing the native
    /// toolbar. Comparing the sequences is what makes <c>DeclareChrome</c> idempotent.
    /// </para>
    /// </summary>
    public bool Equals(PageChrome? other)
        => other is not null
           && Title == other.Title
           && Subtitle == other.Subtitle
           && Search == other.Search
           && Back == other.Back
           && ActionList.SequenceEqual(other.ActionList)
           && FilterList.SequenceEqual(other.FilterList);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Title);
        hash.Add(Subtitle);
        hash.Add(Search);
        hash.Add(Back);
        foreach (var a in ActionList) hash.Add(a);
        foreach (var f in FilterList) hash.Add(f);
        return hash.ToHashCode();
    }

    /// <summary>
    /// The shape of the chrome this page needs, ignoring anything a host can change in place.
    /// <para>
    /// NSToolbar resists per-navigation rebuilds, so the macOS host builds a superset of items once
    /// and toggles visibility (docs/NATIVE-SHELL.md section 1.4). This signature changes only when
    /// the structure changes — not when a label, tooltip or enabled flag does — so it is the
    /// host's signal that a rebuild is genuinely required rather than an in-place update.
    /// </para>
    /// </summary>
    public string Signature =>
        string.Join(
            '|',
            new[] { Search is null ? "-" : "search", Back is null ? "-" : "back" }
                .Concat(ActionList.Select(a => $"a:{a.Id}:{a.Kind}"))
                .Concat(FilterList.Select(f => $"f:{f.Id}:{f.Options.Count}")));
}
