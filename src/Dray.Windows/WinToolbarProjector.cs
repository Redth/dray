using Dray.Core.Shell;
using Dray.Core.Theme;
using Xaml = Microsoft.UI.Xaml;

namespace Dray.WinUI;

/// <summary>
/// Projects the current <see cref="PageChrome"/> into the title bar, as native WinUI controls: the
/// page's title and its filters as drop-down buttons at the leading edge, and at the trailing edge
/// an <c>AutoSuggestBox</c> for search, the primary action as an accent button and the rest in a
/// <c>CommandBar</c>, which folds them into its overflow menu when the window is narrow. Back is in
/// the title bar too (<see cref="WinTitleBar"/>). The panels are <see cref="WinTitleBarToolbar"/>'s.
/// <para>
/// Actions are icons, with their label as the tooltip and the accessible name, as macOS's toolbar
/// draws them: Pull, Refresh and Delete are understood from the glyph, and a row of labelled
/// buttons is what made the title bar too narrow to hold them.
/// </para>
/// <para>
/// Like the macOS projector it knows nothing about any page — it renders what the page declared
/// (docs/NATIVE-SHELL.md section 2.1). It also uses the same rule for when to rebuild:
/// <see cref="PageChrome.Signature"/> changes with the chrome's shape, and everything else is
/// updated in place so a field being typed into is never torn down under the caret.
/// </para>
/// </summary>
public sealed class WinToolbarProjector : IDisposable
{
    readonly WinTitleBarToolbar _toolbar;
    readonly WinTitleBar _titleBar;
    readonly Xaml.FrameworkElement _content;
    readonly IShellState _shell;
    readonly IPlatformTheme _theme;

    /// <summary>Last shape rendered. A sentinel no real signature can equal forces the first build.</summary>
    string _signature = " uninitialised";

    readonly Xaml.Controls.TextBlock _title;
    readonly Xaml.Controls.TextBlock _subtitle;
    readonly Xaml.Controls.StackPanel _heading;
    readonly Xaml.Controls.StackPanel _filters;
    readonly Xaml.Controls.Grid _searchSlot;
    readonly Xaml.Controls.Grid _primarySlot;
    readonly Xaml.Controls.CommandBar _commands;

    readonly Dictionary<string, Xaml.Controls.Primitives.ButtonBase> _actionItems = [];
    readonly Dictionary<string, FilterMenu> _filterMenus = [];
    Xaml.Controls.AutoSuggestBox? _search;
    bool _disposed;

    public WinToolbarProjector(
        WinTitleBarToolbar toolbar, WinTitleBar titleBar, Xaml.FrameworkElement content, IShellState shell, IPlatformTheme theme)
    {
        _toolbar = toolbar;
        _titleBar = titleBar;
        _content = content;
        _shell = shell;
        _theme = theme;

        // Body-strong over a caption: the two lines fit the title bar's 48 with room above and below,
        // where the toolbar row's larger title did not.
        _title = new Xaml.Controls.TextBlock
        {
            Style = Resource<Xaml.Style>("BodyStrongTextBlockStyle"),
            TextTrimming = Xaml.TextTrimming.CharacterEllipsis,
            TextWrapping = Xaml.TextWrapping.NoWrap,
        };

        _subtitle = new Xaml.Controls.TextBlock
        {
            Style = Resource<Xaml.Style>("CaptionTextBlockStyle"),
            TextTrimming = Xaml.TextTrimming.CharacterEllipsis,
            TextWrapping = Xaml.TextWrapping.NoWrap,
            Visibility = Xaml.Visibility.Collapsed,
        };

        _heading = new Xaml.Controls.StackPanel
        {
            VerticalAlignment = Xaml.VerticalAlignment.Center,
            MaxWidth = HeadingMaxWidth,
        };
        _heading.Children.Add(_title);
        _heading.Children.Add(_subtitle);

        _filters = new Xaml.Controls.StackPanel
        {
            Orientation = Xaml.Controls.Orientation.Horizontal,
            Spacing = 8,
            Margin = new Xaml.Thickness(8, 0, 0, 0),
            VerticalAlignment = Xaml.VerticalAlignment.Center,
        };

        _searchSlot = new Xaml.Controls.Grid { VerticalAlignment = Xaml.VerticalAlignment.Center };
        _primarySlot = new Xaml.Controls.Grid { VerticalAlignment = Xaml.VerticalAlignment.Center };

        _commands = new Xaml.Controls.CommandBar
        {
            DefaultLabelPosition = Xaml.Controls.CommandBarDefaultLabelPosition.Collapsed,
            IsDynamicOverflowEnabled = true,
            OverflowButtonVisibility = Xaml.Controls.CommandBarOverflowButtonVisibility.Auto,
            VerticalAlignment = Xaml.VerticalAlignment.Center,
            Background = new Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };

        // Leading is the page and how it is narrowed; trailing is what can be done to it — the same
        // split the macOS toolbar makes.
        _toolbar.Lead.Children.Add(_heading);
        _toolbar.Lead.Children.Add(_filters);
        Xaml.Controls.Grid.SetColumn(_primarySlot, 1);
        Xaml.Controls.Grid.SetColumn(_commands, 2);
        _toolbar.Trail.Children.Add(_searchSlot);
        _toolbar.Trail.Children.Add(_primarySlot);
        _toolbar.Trail.Children.Add(_commands);

        _shell.ChromeChanged += OnChromeChanged;
        _theme.Changed += OnThemeChanged;
        _content.SizeChanged += OnContentSizeChanged;

        Apply(_shell.Chrome);
    }

    void OnChromeChanged(PageChrome chrome) => _content.DispatcherQueue.TryEnqueue(() => Apply(chrome));

    // Icons are stroked in the theme's ink, so a theme change is a change of shape as far as the
    // command bar is concerned.
    void OnThemeChanged() => _content.DispatcherQueue.TryEnqueue(() =>
    {
        _signature = " theme";
        Apply(_shell.Chrome);
    });

    void Apply(PageChrome chrome)
    {
        if (_disposed) return;

        _title.Text = chrome.Title;
        _subtitle.Text = chrome.Subtitle ?? string.Empty;
        _subtitle.Visibility = string.IsNullOrEmpty(chrome.Subtitle) ? Xaml.Visibility.Collapsed : Xaml.Visibility.Visible;

        // Back is WinUI's back button, in the title bar, so a page that declares one gets the control
        // every Windows app has in that place rather than a second arrow in the toolbar.
        _titleBar.SetBack(chrome.Back);

        if (chrome.Signature != _signature)
        {
            _signature = chrome.Signature;
            Rebuild(chrome);
        }
        else
        {
            UpdateInPlace(chrome);
        }

        Measure();
        _toolbar.NotifyChanged();
        Fit();
    }

    void Rebuild(PageChrome chrome)
    {
        if (_search is not null) _search.TextChanged -= OnSearchTextChanged;

        _actionItems.Clear();
        _filterMenus.Clear();
        _filters.Children.Clear();
        _searchSlot.Children.Clear();
        _primarySlot.Children.Clear();
        _commands.PrimaryCommands.Clear();
        _search = null;

        foreach (var filter in chrome.FilterList)
        {
            var menu = BuildFilter(filter);
            _filterMenus[filter.Id] = menu;
            _filters.Children.Add(menu.Button);
        }

        if (chrome.Search is { } search)
        {
            _search = new Xaml.Controls.AutoSuggestBox
            {
                PlaceholderText = search.Placeholder,
                Text = search.Text,
                Width = SearchWidth,
                QueryIcon = new Xaml.Controls.SymbolIcon(Xaml.Controls.Symbol.Find),
            };
            _search.TextChanged += OnSearchTextChanged;
            _searchSlot.Children.Add(_search);
        }

        foreach (var action in chrome.ActionList)
        {
            if (action.Kind == ChromeActionKind.Primary && _primarySlot.Children.Count == 0)
            {
                var primary = BuildPrimary(action);
                _actionItems[action.Id] = primary;
                _primarySlot.Children.Add(primary);
                continue;
            }

            var item = BuildCommand(action);
            _actionItems[action.Id] = item;
            _commands.PrimaryCommands.Add(item);
        }

        _commands.Visibility = _commands.PrimaryCommands.Count > 0 ? Xaml.Visibility.Visible : Xaml.Visibility.Collapsed;
    }

    /// <summary>
    /// The page's main action, as Windows draws a main action: an accent button. The accent fill is
    /// the brand (<see cref="WinPalette"/>), so its icon is stroked on-brand rather than in ink.
    /// </summary>
    Xaml.Controls.Button BuildPrimary(ChromeAction action)
    {
        var id = action.Id;
        var icon = WinIcons.Create(action.Icon, _theme.Current, DrayColor.OnBrand);
        var button = new Xaml.Controls.Button
        {
            Content = icon,
            Style = Resource<Xaml.Style>("AccentButtonStyle"),
            IsEnabled = action.IsEnabled,
            Padding = new Xaml.Thickness(11, 7, 11, 7),
            VerticalAlignment = Xaml.VerticalAlignment.Center,
        };

        Label(button, action);
        void Sync() => icon.Opacity = button.IsEnabled ? 1.0 : 0.4;
        Sync();
        button.IsEnabledChanged += (_, _) => Sync();
        button.Click += (_, _) => InvokeAction(id);
        return button;
    }

    /// <summary>
    /// Run a toolbar action with the keyboard in the content it acts on.
    /// <para>
    /// A WinUI button takes focus when it is clicked, and an NSToolbar button does not. Left there,
    /// a dialog the page opens with <c>showModal()</c> focuses its own field inside a WebView that
    /// does not have the keyboard: typing goes nowhere and Escape does not close it. Focus moves
    /// first, so it is already in the page when the page responds. A native dialog the action
    /// opens still takes focus for itself when it appears.
    /// </para>
    /// </summary>
    void InvokeAction(string id)
    {
        _content.Focus(Xaml.FocusState.Programmatic);
        _shell.InvokeAction(id);
    }

    /// <summary>
    /// Every other action. A destructive one is drawn like the rest: quiet in chrome, with the weight
    /// carried by the confirmation it opens (DESIGN.md section 2.5). The label stays on the button,
    /// collapsed: it is what the overflow menu shows when the bar is too narrow.
    /// </summary>
    Xaml.Controls.AppBarButton BuildCommand(ChromeAction action)
    {
        var id = action.Id;
        var button = new Xaml.Controls.AppBarButton
        {
            Label = action.Label,
            Icon = WinIcons.Create(action.Icon, _theme.Current),
            IsEnabled = action.IsEnabled,

            // The compact width, as File Explorer's command bar spaces its icons. The template's
            // default leaves room for a label this bar never shows.
            Width = 40,
        };

        Label(button, action);
        DimWhenDisabled(button);
        button.Click += (_, _) => InvokeAction(id);
        return button;
    }

    /// <summary>The words an icon-only button no longer shows: its tooltip and its accessible name.</summary>
    static void Label(Xaml.Controls.Primitives.ButtonBase button, ChromeAction action)
    {
        Xaml.Controls.ToolTipService.SetToolTip(button, action.Tooltip ?? action.Label);
        Xaml.Automation.AutomationProperties.SetName(button, action.Label);
    }

    /// <summary>
    /// A rendered icon does not follow the button's disabled foreground the way a glyph would, so a
    /// disabled command would otherwise keep a full-strength icon.
    /// </summary>
    static void DimWhenDisabled(Xaml.Controls.AppBarButton button)
    {
        void Sync() => button.Icon?.SetValue(Xaml.UIElement.OpacityProperty, button.IsEnabled ? 1.0 : 0.4);

        Sync();
        button.IsEnabledChanged += (_, _) => Sync();
    }

    FilterMenu BuildFilter(ChromeFilter filter)
    {
        var flyout = new Xaml.Controls.MenuFlyout();
        var items = new List<Xaml.Controls.RadioMenuFlyoutItem>();
        var filterId = filter.Id;

        for (var i = 0; i < filter.Options.Count; i++)
        {
            var index = i;
            var item = new Xaml.Controls.RadioMenuFlyoutItem
            {
                Text = filter.Options[i].Label,
                GroupName = "dray-filter-" + filterId,
                IsChecked = i == filter.SelectedIndex,
            };
            item.Click += (_, _) => _shell.SetFilter(filterId, index);

            items.Add(item);
            flyout.Items.Add(item);
        }

        var button = new Xaml.Controls.DropDownButton
        {
            Content = SelectedLabel(filter),
            Flyout = flyout,
            VerticalAlignment = Xaml.VerticalAlignment.Center,
        };
        Xaml.Controls.ToolTipService.SetToolTip(button, filter.Label);

        return new FilterMenu(button, items);
    }

    static string SelectedLabel(ChromeFilter filter)
        => filter.SelectedIndex >= 0 && filter.SelectedIndex < filter.Options.Count
            ? filter.Options[filter.SelectedIndex].Label
            : filter.Label;

    /// <summary>Everything that can change without disturbing the toolbar's structure.</summary>
    void UpdateInPlace(PageChrome chrome)
    {
        foreach (var action in chrome.ActionList)
        {
            if (!_actionItems.TryGetValue(action.Id, out var item)) continue;

            item.IsEnabled = action.IsEnabled;
            if (item is Xaml.Controls.AppBarButton command) command.Label = action.Label;
            Label(item, action);
        }

        foreach (var filter in chrome.FilterList)
        {
            if (!_filterMenus.TryGetValue(filter.Id, out var menu)) continue;

            menu.Button.Content = SelectedLabel(filter);
            for (var i = 0; i < filter.Options.Count && i < menu.Items.Count; i++)
                menu.Items[i].IsChecked = i == filter.SelectedIndex;
        }

        // The search text is deliberately not written back: the field is the source of truth while
        // the user is typing, and assigning to it would fight the caret.
    }

    /// <summary>
    /// Gives the trailing panel what the leading one leaves, as the window and the page change.
    /// <para>
    /// The page's title follows the title bar's own buttons and the app's icon, over the sidebar,
    /// rather than waiting for the content area's edge: the whole width of the bar is the toolbar's.
    /// MAUI's title bar sizes its trailing content to whatever it asks for, so without a limit a
    /// wide toolbar drew over the title and filters, and the command bar never overflowed. Short of
    /// room, the command bar narrows first, folding its commands into its menu but always keeping
    /// room for the menu's button, then the search box narrows to its minimum, then the page's title
    /// truncates. The primary action
    /// keeps its size.
    /// </para>
    /// <para>
    /// Run after layout rather than inside it, and against the leading panel's width as measured
    /// when the page's chrome last changed: measuring inside <c>LayoutUpdated</c> invalidated the
    /// layout it was reacting to, and a narrow window turned that into a layout cycle that took the
    /// app down.
    /// </para>
    /// </summary>
    void Fit() => _content.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
    {
        var lead = _toolbar.Lead;
        var trail = _toolbar.Trail;
        if (_disposed || lead.XamlRoot is null || trail.XamlRoot is null) return;

        // The trailing panel is end-aligned, so its right edge stays put however wide it is.
        var leadRight = X(lead) + _leadWidth;
        var trailRight = X(trail) + trail.ActualWidth;
        var room = Math.Max(0, Math.Floor(trailRight - leadRight - TitleBarGap));

        var search = _search is null ? 0 : SearchWidth;
        var parts = new[] { search, _primaryWidth, _commandsWidth }.Count(w => w > 0);
        var spacing = trail.ColumnSpacing * Math.Max(0, parts - 1);

        var commands = double.PositiveInfinity;
        var excess = search + _primaryWidth + _commandsWidth + spacing - room;
        if (excess > 0 && _commandsWidth > 0)
        {
            commands = Math.Max(OverflowButtonWidth, _commandsWidth - excess);
            excess -= _commandsWidth - commands;
        }

        if (excess > 0 && _search is not null)
        {
            var narrowed = Math.Max(MinSearchWidth, search - excess);
            excess -= search - narrowed;
            search = narrowed;
        }

        // Last, the page's title and subtitle, which truncate rather than run under the search box.
        var heading = excess > 0 ? Math.Max(MinHeadingWidth, _headingWidth - excess) : HeadingMaxWidth;

        var changed = false;
        if (Differs(_commands.MaxWidth, commands))
        {
            _commands.MaxWidth = commands;
            changed = true;
        }

        if (_search is not null && Differs(_search.Width, search))
        {
            _search.Width = search;
            changed = true;
        }

        if (Differs(_heading.MaxWidth, heading))
        {
            _heading.MaxWidth = heading;
            changed = true;
        }

        if (changed) _toolbar.NotifyChanged();
    });

    static bool Differs(double a, double b)
        => double.IsInfinity(a) != double.IsInfinity(b) || (!double.IsInfinity(a) && Math.Abs(a - b) >= 1);

    /// <summary>
    /// The natural widths <see cref="Fit"/> divides the room by, taken when the chrome changes: the
    /// leading panel unconstrained, because the title bar may have clipped it, and the command bar
    /// with every command showing.
    /// </summary>
    void Measure()
    {
        var unconstrained = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);

        _heading.MaxWidth = HeadingMaxWidth;
        _heading.Measure(unconstrained);
        _headingWidth = _heading.DesiredSize.Width;

        _leadWidth = 0;
        foreach (var child in _toolbar.Lead.Children)
        {
            if (child.Visibility != Xaml.Visibility.Visible) continue;
            child.Measure(unconstrained);
            _leadWidth += child.DesiredSize.Width + (_leadWidth > 0 ? _toolbar.Lead.Spacing : 0);
        }

        _primarySlot.Measure(unconstrained);
        _primaryWidth = _primarySlot.Children.Count > 0 ? _primarySlot.DesiredSize.Width : 0;

        _commands.MaxWidth = double.PositiveInfinity;
        _commands.Measure(unconstrained);
        _commandsWidth = _commands.Visibility == Xaml.Visibility.Visible ? _commands.DesiredSize.Width : 0;

        if (_search is not null) _search.Width = SearchWidth;
    }

    const double HeadingMaxWidth = 320;
    const double MinHeadingWidth = 80;
    const double SearchWidth = 220;
    const double MinSearchWidth = 140;

    /// <summary>The command bar's overflow button, which stays when every command has folded into it.</summary>
    const double OverflowButtonWidth = 48;

    double _headingWidth;
    double _primaryWidth;
    double _commandsWidth;

    void OnContentSizeChanged(object sender, Xaml.SizeChangedEventArgs e) => Fit();

    /// <summary>The leading panel's own width, as of the last change to the page's chrome.</summary>
    double _leadWidth;

    /// <summary>Space kept between the two panels, as title bar to drag the window by.</summary>
    const double TitleBarGap = 24;

    static double X(Xaml.UIElement element) => element.TransformToVisual(null).TransformPoint(default).X;


    void OnSearchTextChanged(Xaml.Controls.AutoSuggestBox sender, Xaml.Controls.AutoSuggestBoxTextChangedEventArgs e)
    {
        // Programmatic changes come back through here too; only the user's own typing is news.
        if (e.Reason == Xaml.Controls.AutoSuggestionBoxTextChangeReason.UserInput) _shell.SetSearchText(sender.Text);
    }

    static T? Resource<T>(string key) where T : class
        => Xaml.Application.Current.Resources.TryGetValue(key, out var value) ? value as T : null;

    sealed record FilterMenu(Xaml.Controls.DropDownButton Button, List<Xaml.Controls.RadioMenuFlyoutItem> Items);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _shell.ChromeChanged -= OnChromeChanged;
        _theme.Changed -= OnThemeChanged;
        _content.SizeChanged -= OnContentSizeChanged;
        if (_search is not null) _search.TextChanged -= OnSearchTextChanged;
    }
}
