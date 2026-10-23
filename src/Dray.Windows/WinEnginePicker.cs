using Dray.Core.Shell;
using Xaml = Microsoft.UI.Xaml;

namespace Dray.WinUI;

/// <summary>
/// The engine picker in the title bar, after the app's icon — the same corner macOS gives it in the
/// toolbar above the sidebar, so the control is in one place on every head.
/// <para>
/// It used to be the first item in the navigation pane. There it scrolled with the pane, collapsed to
/// an icon with the pane, and shared a list with places to go when it is not one. In the title bar it
/// is on screen whatever the pane is doing.
/// </para>
/// <para>
/// A <c>DropDownButton</c>, WinUI's own control for exactly this: a button that says what is chosen
/// and drops a menu to change it. It draws <see cref="ChromePicker"/> and builds nothing itself; the
/// menu is rebuilt from the model as it opens, so it is never stale. The name trims rather than
/// pushing the page's title along the bar.
/// </para>
/// </summary>
public sealed class WinEnginePicker(IChromePickerSource source, IPlatformTheme theme, IShellBridge bridge)
{
    /// <summary>Wide enough for a 24-character name at the title bar's size; anything longer trims.</summary>
    const double MaxWidth = 240;

    Xaml.Controls.DropDownButton? _button;
    readonly Xaml.Controls.TextBlock _title = new()
    {
        TextTrimming = Xaml.TextTrimming.CharacterEllipsis,
        VerticalAlignment = Xaml.VerticalAlignment.Center,
    };
    readonly Xaml.Controls.ContentPresenter _icon = new() { VerticalAlignment = Xaml.VerticalAlignment.Center };

    internal Xaml.FrameworkElement Build()
    {
        var content = new Xaml.Controls.StackPanel
        {
            Orientation = Xaml.Controls.Orientation.Horizontal,
            Spacing = 8,
        };
        content.Children.Add(_icon);
        content.Children.Add(_title);

        var menu = new Xaml.Controls.MenuFlyout { Placement = Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        menu.Opening += (_, _) => Fill(menu, source.Current);

        _button = new Xaml.Controls.DropDownButton
        {
            Content = content,
            Flyout = menu,
            MaxWidth = MaxWidth,
            VerticalAlignment = Xaml.VerticalAlignment.Center,
            Margin = new Xaml.Thickness(8, 0, 0, 0),

            // Quiet, like the Back and pane buttons beside it: chrome, not a call to action.
            Background = new Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Xaml.Thickness(0),
        };

        Apply(source.Current);
        source.Changed += picker => _button.DispatcherQueue.TryEnqueue(() => Apply(picker));
        theme.Changed += () => _button.DispatcherQueue.TryEnqueue(() => Apply(source.Current));

        return _button;
    }

    void Apply(ChromePicker picker)
    {
        if (_button is null) return;

        _title.Text = picker.Title;
        _icon.Content = WinIcons.Create(picker.Icon, theme.Current);
        Xaml.Controls.ToolTipService.SetToolTip(_button, picker.Tooltip);
        Xaml.Automation.AutomationProperties.SetName(_button, $"Engine: {picker.Tooltip}");
    }

    /// <summary>
    /// One radio item per engine — one is selected at a time, and the menu says which — then the way
    /// to the Hosts page.
    /// </summary>
    void Fill(Xaml.Controls.MenuFlyout menu, ChromePicker picker)
    {
        menu.Items.Clear();

        foreach (var item in picker.Items)
        {
            if (item.SeparatorBefore) menu.Items.Add(new Xaml.Controls.MenuFlyoutSeparator());

            var id = item.Id;
            Xaml.Controls.MenuFlyoutItem row = item.IsCommand
                ? new Xaml.Controls.MenuFlyoutItem()
                : new Xaml.Controls.RadioMenuFlyoutItem { GroupName = picker.Id, IsChecked = item.IsChecked };

            row.Text = item.Label;
            row.IsEnabled = item.IsEnabled;
            row.Click += (_, _) => _ = ChooseAsync(id);
            menu.Items.Add(row);
        }
    }

    async Task ChooseAsync(string itemId)
    {
        if (await source.ChooseAsync(itemId) is { } problem)
        {
            // Said where the user is likely to look next, and on the Hosts page if they go there.
            await bridge.NotifyAsync("Could not switch engine", problem, NoticeKind.Warning);
        }
    }
}
