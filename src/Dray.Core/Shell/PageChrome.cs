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
/// One row of a <see cref="ChromeMenu"/>: a command, a submenu of them, or a separator.
/// </summary>
/// <param name="Id">Raised through <c>InvokeAction</c> when chosen. A row with children is never chosen itself.</param>
/// <param name="Children">A submenu. The row then opens it rather than doing anything.</param>
public sealed record ChromeMenuItem(
    string Id,
    string Label,
    IconRef? Icon = null,
    IReadOnlyList<ChromeMenuItem>? Children = null,
    bool IsSeparator = false)
{
    public static readonly ChromeMenuItem Separator = new("-", string.Empty, IsSeparator: true);

    public IReadOnlyList<ChromeMenuItem> ChildList => Children ?? [];

    /// <summary>Whether choosing this row runs something: not a separator, and not a submenu's own row.</summary>
    public bool IsCommand => !IsSeparator && ChildList.Count == 0;

    /// <summary>This row and every row under it.</summary>
    public IEnumerable<ChromeMenuItem> SelfAndDescendants() => ChildList.SelectMany(c => c.SelfAndDescendants()).Prepend(this);

    /// <summary>Value equality including <see cref="Children"/> — see the note on PageChrome.Equals.</summary>
    public bool Equals(ChromeMenuItem? other)
        => other is not null
           && Id == other.Id
           && Label == other.Label
           && Icon == other.Icon
           && IsSeparator == other.IsSeparator
           && ChildList.SequenceEqual(other.ChildList);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Label);
        hash.Add(IsSeparator);
        foreach (var c in ChildList) hash.Add(c);
        return hash.ToHashCode();
    }

    /// <summary>Its place in a menu's shape: which rows, in which order, nested how.</summary>
    internal string Shape => IsSeparator ? "-" : ChildList.Count == 0 ? Id : $"{Id}({string.Join(',', ChildList.Select(c => c.Shape))})";
}

/// <summary>
/// A menu of commands in the chrome — where a filter is a choice of one, this is a list of things to
/// do. The stacks page's folders are one: each folder with what can be done to it, then a way to
/// add another.
/// <para>
/// Native menus are built once and not updated (upstream on macOS, by design on Windows), so every
/// row is part of the chrome's <see cref="PageChrome.Signature"/>: a menu whose rows change is
/// rebuilt. Only its own label is updated in place.
/// </para>
/// </summary>
public sealed record ChromeMenu(string Id, string Label, IconRef Icon, IReadOnlyList<ChromeMenuItem> Items)
{
    public bool Equals(ChromeMenu? other)
        => other is not null
           && Id == other.Id
           && Label == other.Label
           && Icon == other.Icon
           && Items.SequenceEqual(other.Items);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Label);
        foreach (var i in Items) hash.Add(i);
        return hash.ToHashCode();
    }

    /// <summary>The commands in it, at any depth.</summary>
    public IEnumerable<ChromeMenuItem> Commands => Items.SelectMany(i => i.SelfAndDescendants()).Where(i => i.IsCommand);
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
    ChromeBack? Back = null,
    IReadOnlyList<ChromeMenu>? Menus = null)
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
    public bool HasControls => Search is not null || ActionList.Count > 0 || FilterList.Count > 0 || MenuList.Count > 0;

    public IReadOnlyList<ChromeAction> ActionList => Actions ?? [];

    public IReadOnlyList<ChromeFilter> FilterList => Filters ?? [];

    public IReadOnlyList<ChromeMenu> MenuList => Menus ?? [];

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
           && FilterList.SequenceEqual(other.FilterList)
           && MenuList.SequenceEqual(other.MenuList);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Title);
        hash.Add(Subtitle);
        hash.Add(Search);
        hash.Add(Back);
        foreach (var a in ActionList) hash.Add(a);
        foreach (var f in FilterList) hash.Add(f);
        foreach (var m in MenuList) hash.Add(m);
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
                .Concat(FilterList.Select(f => $"f:{f.Id}:{f.Options.Count}"))
                .Concat(MenuList.Select(m => $"m:{m.Id}({string.Join(',', m.Items.Select(i => i.Shape))})")));
}
