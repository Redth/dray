using Dray.Core.Shell;
using Xaml = Microsoft.UI.Xaml;

namespace Dray.WinUI;

/// <summary>
/// Projects the current <see cref="PageChrome"/> onto a row of native WinUI controls above the
/// WebView: the page title, its filters as drop-down buttons, an <c>AutoSuggestBox</c> for search, the
/// primary action as an accent button and the rest in a <c>CommandBar</c>, which folds them into
/// its overflow menu when the window is narrow. Back is in the title bar (<see cref="WinTitleBar"/>).
/// <para>
/// Like the macOS projector it knows nothing about any page — it renders what the page declared
/// (docs/NATIVE-SHELL.md section 2.1). It also uses the same rule for when to rebuild:
/// <see cref="PageChrome.Signature"/> changes with the chrome's shape, and everything else is
/// updated in place so a field being typed into is never torn down under the caret.
/// </para>
/// </summary>
public sealed class WinToolbarProjector : IDisposable
{
    readonly Xaml.Controls.Grid _host;
    readonly WinTitleBar _titleBar;
    readonly Xaml.UIElement _content;
    readonly IShellState _shell;
    readonly IPlatformTheme _theme;

    /// <summary>Last shape rendered. A sentinel no real signature can equal forces the first build.</summary>
    string _signature = " uninitialised";

    readonly Xaml.Controls.TextBlock _title;
    readonly Xaml.Controls.TextBlock _subtitle;
    readonly Xaml.Controls.StackPanel _filters;
    readonly Xaml.Controls.Grid _searchSlot;
    readonly Xaml.Controls.Grid _primarySlot;
    readonly Xaml.Controls.CommandBar _commands;

    readonly Dictionary<string, Xaml.Controls.Primitives.ButtonBase> _actionItems = [];
    readonly Dictionary<string, FilterMenu> _filterMenus = [];
    Xaml.Controls.AutoSuggestBox? _search;
    bool _disposed;

    public WinToolbarProjector(
        Xaml.Controls.Grid host, WinTitleBar titleBar, Xaml.UIElement content, IShellState shell, IPlatformTheme theme)
    {
        _host = host;
        _titleBar = titleBar;
        _content = content;
        _shell = shell;
        _theme = theme;

        _title = new Xaml.Controls.TextBlock
        {
            Style = Resource<Xaml.Style>("SubtitleTextBlockStyle"),
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

        var heading = new Xaml.Controls.StackPanel
        {
            VerticalAlignment = Xaml.VerticalAlignment.Center,
            MaxWidth = 420,
        };
        heading.Children.Add(_title);
        heading.Children.Add(_subtitle);

        _filters = new Xaml.Controls.StackPanel
        {
            Orientation = Xaml.Controls.Orientation.Horizontal,
            Spacing = 8,
            Margin = new Xaml.Thickness(16, 0, 0, 0),
            VerticalAlignment = Xaml.VerticalAlignment.Center,
        };

        _searchSlot = new Xaml.Controls.Grid { VerticalAlignment = Xaml.VerticalAlignment.Center };
        _primarySlot = new Xaml.Controls.Grid
        {
            VerticalAlignment = Xaml.VerticalAlignment.Center,
            Margin = new Xaml.Thickness(8, 0, 0, 0),
        };

        _commands = new Xaml.Controls.CommandBar
        {
            DefaultLabelPosition = Xaml.Controls.CommandBarDefaultLabelPosition.Right,
            IsDynamicOverflowEnabled = true,
            OverflowButtonVisibility = Xaml.Controls.CommandBarOverflowButtonVisibility.Auto,
            VerticalAlignment = Xaml.VerticalAlignment.Center,
            Background = new Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };

        // Title | filters | flexible space | search | primary | commands. Leading is the page and
        // how it is narrowed; trailing is what can be done to it — the same split the macOS
        // toolbar makes, laid out the way a Windows command bar reads.
        _host.ColumnDefinitions.Add(new Xaml.Controls.ColumnDefinition { Width = Xaml.GridLength.Auto });
        _host.ColumnDefinitions.Add(new Xaml.Controls.ColumnDefinition { Width = Xaml.GridLength.Auto });
        _host.ColumnDefinitions.Add(new Xaml.Controls.ColumnDefinition { Width = new Xaml.GridLength(1, Xaml.GridUnitType.Star) });
        _host.ColumnDefinitions.Add(new Xaml.Controls.ColumnDefinition { Width = Xaml.GridLength.Auto });
        _host.ColumnDefinitions.Add(new Xaml.Controls.ColumnDefinition { Width = Xaml.GridLength.Auto });
        _host.ColumnDefinitions.Add(new Xaml.Controls.ColumnDefinition { Width = Xaml.GridLength.Auto });

        Place(heading, 0);
        Place(_filters, 1);
        Place(_searchSlot, 3);
        Place(_primarySlot, 4);
        Place(_commands, 5);

        _shell.ChromeChanged += OnChromeChanged;
        _theme.Changed += OnThemeChanged;

        Apply(_shell.Chrome);
    }

    void Place(Xaml.FrameworkElement element, int column)
    {
        Xaml.Controls.Grid.SetColumn(element, column);
        _host.Children.Add(element);
    }

    void OnChromeChanged(PageChrome chrome) => _host.DispatcherQueue.TryEnqueue(() => Apply(chrome));

    // Icons are stroked in the theme's ink, so a theme change is a change of shape as far as the
    // command bar is concerned.
    void OnThemeChanged() => _host.DispatcherQueue.TryEnqueue(() =>
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
                Width = 240,
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
    /// The page's main action, as Windows draws a main action: an accent button.
    /// <para>
    /// Text only. The accent fill is the brand and its text on-brand (<see cref="WinPalette"/>); an
    /// icon stroked in Dray's ink would be the one thing on the button in neither.
    /// </para>
    /// </summary>
    Xaml.Controls.Button BuildPrimary(ChromeAction action)
    {
        var id = action.Id;
        var button = new Xaml.Controls.Button
        {
            Content = action.Label,
            Style = Resource<Xaml.Style>("AccentButtonStyle"),
            IsEnabled = action.IsEnabled,
        };

        Xaml.Controls.ToolTipService.SetToolTip(button, action.Tooltip ?? action.Label);
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
    /// carried by the confirmation it opens (DESIGN.md section 2.5).
    /// </summary>
    Xaml.Controls.AppBarButton BuildCommand(ChromeAction action)
    {
        var id = action.Id;
        var button = new Xaml.Controls.AppBarButton
        {
            Label = action.Label,
            Icon = WinIcons.Create(action.Icon, _theme.Current),
            IsEnabled = action.IsEnabled,
        };

        Xaml.Controls.ToolTipService.SetToolTip(button, action.Tooltip ?? action.Label);
        DimWhenDisabled(button);
        button.Click += (_, _) => InvokeAction(id);
        return button;
    }

    /// <summary>
    /// A rendered icon does not follow the button's disabled foreground the way a glyph would, so a
    /// disabled command would otherwise keep a full-strength icon beside its greyed label.
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
        };
        Xaml.Controls.ToolTipService.SetToolTip(button, filter.Label);

        return new FilterMenu(button, items);
    }

    static string SelectedLabel(ChromeFilter filter)
        => filter.SelectedIndex >= 0 && filter.SelectedIndex < filter.Options.Count
            ? filter.Options[filter.SelectedIndex].Label
            : filter.Label;

    /// <summary>Everything that can change without disturbing the row's structure.</summary>
    void UpdateInPlace(PageChrome chrome)
    {
        foreach (var action in chrome.ActionList)
        {
            if (!_actionItems.TryGetValue(action.Id, out var item)) continue;

            item.IsEnabled = action.IsEnabled;
            if (item is Xaml.Controls.AppBarButton command) command.Label = action.Label;
            else if (item is Xaml.Controls.Button button) button.Content = action.Label;
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
        if (_search is not null) _search.TextChanged -= OnSearchTextChanged;
    }
}
