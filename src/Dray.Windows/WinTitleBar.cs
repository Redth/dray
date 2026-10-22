using Dray.Core.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Xaml = Microsoft.UI.Xaml;

namespace Dray.WinUI;

/// <summary>
/// Back and the pane toggle, at the leading edge of the title bar, the way Settings and File
/// Explorer place them.
/// <para>
/// They used to be the <c>NavigationView</c>'s own, in a row above the sidebar items: Back appeared
/// only on a detail page, so every item below it moved down 40 pixels when one opened and back up
/// when it closed, under the pointer. In the title bar nothing in the sidebar moves. They are the
/// same two controls, in WinUI's own styles for them, so they look and behave as they did.
/// </para>
/// <para>
/// One per app, shared by the title bar that shows the buttons and the shell that owns what they
/// act on, either of which may be built first.
/// </para>
/// </summary>
public sealed class WinTitleBar(IShellState shell)
{
    Xaml.Controls.Button? _back;
    Xaml.Controls.Button? _toggle;
    Xaml.Controls.NavigationView? _nav;
    ChromeBack? _backChrome;

    /// <summary>The page's back, or null to hide the button.</summary>
    public void SetBack(ChromeBack? back)
    {
        _backChrome = back;
        ApplyBack();
    }

    /// <summary>The pane the toggle opens and closes. Its own toggle is hidden; this one replaces it.</summary>
    public void Attach(Xaml.Controls.NavigationView nav)
    {
        _nav = nav;
        nav.IsPaneToggleButtonVisible = false;
        nav.PaneOpened += (_, _) => ApplyToggle();
        nav.PaneClosed += (_, _) => ApplyToggle();
        ApplyToggle();
    }

    internal Xaml.FrameworkElement Build()
    {
        _back = new Xaml.Controls.Button
        {
            Style = Resource<Xaml.Style>("NavigationBackButtonNormalStyle"),
            VerticalAlignment = Xaml.VerticalAlignment.Center,
        };
        _back.Click += (_, _) =>
        {
            if (_backChrome is { } back) shell.InvokeAction(back.Id);
        };

        _toggle = new Xaml.Controls.Button
        {
            Style = Resource<Xaml.Style>("PaneToggleButtonStyle"),
            VerticalAlignment = Xaml.VerticalAlignment.Center,
        };
        _toggle.Click += (_, _) =>
        {
            if (_nav is { } nav) nav.IsPaneOpen = !nav.IsPaneOpen;
        };

        var row = new Xaml.Controls.StackPanel
        {
            Orientation = Xaml.Controls.Orientation.Horizontal,
            VerticalAlignment = Xaml.VerticalAlignment.Center,
        };
        row.Children.Add(_back);
        row.Children.Add(_toggle);

        ApplyBack();
        ApplyToggle();
        return row;
    }

    void ApplyBack()
    {
        if (_back is null) return;

        _back.Visibility = _backChrome is null ? Xaml.Visibility.Collapsed : Xaml.Visibility.Visible;

        // Named for where it goes, not for the direction it points.
        var label = _backChrome?.Label ?? "Back";
        Xaml.Controls.ToolTipService.SetToolTip(_back, label);
        Xaml.Automation.AutomationProperties.SetName(_back, label);
    }

    void ApplyToggle()
    {
        if (_toggle is null) return;

        // WinUI's own wording for its own toggle.
        var label = _nav?.IsPaneOpen == false ? "Open Navigation" : "Close Navigation";
        Xaml.Controls.ToolTipService.SetToolTip(_toggle, label);
        Xaml.Automation.AutomationProperties.SetName(_toggle, label);
    }

    static T? Resource<T>(string key) where T : class
        => Xaml.Application.Current.Resources.TryGetValue(key, out var value) ? value as T : null;
}

/// <summary>The title bar's leading content: a placeholder MAUI turns into <see cref="WinTitleBar"/>'s buttons.</summary>
public sealed class TitleBarNavigation : View;

public sealed class TitleBarNavigationHandler() : ViewHandler<TitleBarNavigation, Xaml.FrameworkElement>(ViewMapper)
{
    protected override Xaml.FrameworkElement CreatePlatformView()
        => MauiContext!.Services.GetRequiredService<WinTitleBar>().Build();
}
