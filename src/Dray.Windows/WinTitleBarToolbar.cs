using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Xaml = Microsoft.UI.Xaml;

namespace Dray.WinUI;

/// <summary>
/// The page's toolbar, in the title bar: the two panels <see cref="WinToolbarProjector"/> fills and
/// the title bar shows.
/// <para>
/// Two, not one row across the bar, because everything MAUI's title bar is given as content is a
/// passthrough region: clicks reach the controls instead of dragging the window. A row stretched
/// across the bar would leave nothing to drag the window by. So the page's heading and filters are
/// the title bar's content, hugging the leading edge, what can be done to the page is its trailing
/// content, and the space between them is still title bar.
/// </para>
/// <para>
/// One per app, shared by the title bar that shows the panels and the shell that fills them, either
/// of which may be built first, as <see cref="WinTitleBar"/> is.
/// </para>
/// </summary>
public sealed class WinTitleBarToolbar
{
    /// <summary>
    /// The page's title and filters, straight after the app's icon. A margin rather than padding,
    /// so the gap stays title bar the window can be dragged by.
    /// </summary>
    public Xaml.Controls.StackPanel Lead { get; } = Panel(new Xaml.Thickness(12, 0, 0, 0));

    /// <summary>
    /// Search, primary, then the commands. When the bar is short of room the projector narrows them
    /// in that order of giving way: the command bar folds into its overflow menu, then the search
    /// box narrows (<see cref="WinToolbarProjector"/>).
    /// </summary>
    public Xaml.Controls.Grid Trail { get; } = TrailGrid();

    /// <summary>Raised when a panel's contents change size, so MAUI lays the title bar out again.</summary>
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    static Xaml.Controls.Grid TrailGrid()
    {
        var grid = new Xaml.Controls.Grid { VerticalAlignment = Xaml.VerticalAlignment.Center, ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new Xaml.Controls.ColumnDefinition { Width = Xaml.GridLength.Auto });
        grid.ColumnDefinitions.Add(new Xaml.Controls.ColumnDefinition { Width = Xaml.GridLength.Auto });
        grid.ColumnDefinitions.Add(new Xaml.Controls.ColumnDefinition { Width = Xaml.GridLength.Auto });
        return grid;
    }

    static Xaml.Controls.StackPanel Panel(Xaml.Thickness margin) => new()
    {
        Margin = margin,
        Orientation = Xaml.Controls.Orientation.Horizontal,
        VerticalAlignment = Xaml.VerticalAlignment.Center,
        Spacing = 8,
    };
}

/// <summary>The title bar's content: the page's heading and filters (<see cref="WinTitleBarToolbar.Lead"/>).</summary>
public sealed class TitleBarPageLead : View
{
    public TitleBarPageLead() => HorizontalOptions = LayoutOptions.Start;
}

/// <summary>The title bar's trailing content: search and the page's actions (<see cref="WinTitleBarToolbar.Trail"/>).</summary>
public sealed class TitleBarPageActions : View
{
    public TitleBarPageActions() => HorizontalOptions = LayoutOptions.End;
}

public sealed class TitleBarPageLeadHandler() : TitleBarPanelHandler<TitleBarPageLead>(t => t.Lead);

public sealed class TitleBarPageActionsHandler() : TitleBarPanelHandler<TitleBarPageActions>(t => t.Trail);

/// <summary>
/// Hands MAUI one of the shared panels. MAUI sizes a handler's view by measuring it once, so when
/// the projector rebuilds a panel the virtual view is told to measure again; MAUI then recomputes the
/// title bar's passthrough regions from the new bounds.
/// </summary>
public abstract class TitleBarPanelHandler<TView>(Func<WinTitleBarToolbar, Xaml.Controls.Panel> panel)
    : ViewHandler<TView, Xaml.Controls.Panel>(ViewMapper) where TView : View
{
    WinTitleBarToolbar? _toolbar;

    protected override Xaml.Controls.Panel CreatePlatformView()
    {
        _toolbar = MauiContext!.Services.GetRequiredService<WinTitleBarToolbar>();
        return panel(_toolbar);
    }

    protected override void ConnectHandler(Xaml.Controls.Panel platformView)
    {
        base.ConnectHandler(platformView);
        if (_toolbar is not null) _toolbar.Changed += OnChanged;
    }

    protected override void DisconnectHandler(Xaml.Controls.Panel platformView)
    {
        if (_toolbar is not null) _toolbar.Changed -= OnChanged;
        base.DisconnectHandler(platformView);
    }

    void OnChanged() => (VirtualView as IView)?.InvalidateMeasure();
}
