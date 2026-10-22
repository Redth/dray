using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Web.WebView2.Core;

namespace Dray.WinUI;

/// <summary>
/// What every WebView2 in Dray needs before it stops looking like a browser in a window.
/// <para>
/// The Windows counterpart of the macOS list in docs/NATIVE-SHELL.md section 1.3. WKWebView is
/// quiet by default; WebView2 is Edge, and arrives with Edge's furniture — a page context menu with
/// Back, Reload, Save as and Print, Ctrl+wheel zoom, swipe-to-go-back, a status bar on link hover,
/// F5, Ctrl+F and Ctrl+P. Each is a way for the user to discover they are in a web page, and a couple
/// of them (Reload, Back) break the app outright.
/// </para>
/// </summary>
public static class WebViewSetup
{
    /// <summary>
    /// Where WebView2 keeps its profile. Its default is beside the executable, which is read-only
    /// once the app is installed under Program Files — and WebView2 fails to start there with
    /// nothing on screen to say why.
    /// </summary>
    static string UserDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Dray", "WebView2");

    /// <summary>The page context menu items worth keeping: editing, and nothing that navigates.</summary>
    static readonly HashSet<string> KeptMenuItems = new(StringComparer.Ordinal)
    {
        "undo", "redo", "cut", "copy", "paste", "pasteAndMatchStyle", "selectAll", "emoji", "spellCheck",
    };

    /// <summary>
    /// Make the WebView truly transparent, so the window's backdrop is the ground the page is
    /// drawn on. For the shell's WebView only: a transparent pixel shows the window's backdrop
    /// directly, never the XAML beneath the control, so inside a dialog it would show the window
    /// behind the dialog.
    /// <para>
    /// WinUI's WebView2 does not hand a transparent <c>DefaultBackgroundColor</c> to the browser
    /// as it is. It substitutes the theme's <c>SolidBackgroundFillColorBase</c>, so the page sits on
    /// that opaque light or dark grey whatever it paints. The colour is resolved once, when
    /// the control creates its controller: overriding that one resource on the control before then
    /// is what makes transparent mean transparent. The brush of the same name has no effect, and
    /// setting either after initialisation is too late. docs/NATIVE-SHELL.md section 5.3.
    /// </para>
    /// </summary>
    public static void MakeTransparent(BlazorWebView view)
    {
        view.HandlerChanged += (_, _) =>
        {
            if (view.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.WebView2 webView)
                webView.Resources["SolidBackgroundFillColorBase"] = Microsoft.UI.Colors.Transparent;
        };
    }

    public static void Apply(BlazorWebView view)
    {
        view.BlazorWebViewInitializing += (_, e) =>
        {
            Directory.CreateDirectory(UserDataFolder);
            e.UserDataFolder = UserDataFolder;
        };

        view.BlazorWebViewInitialized += (_, e) => Configure(e);
    }

    static void Configure(BlazorWebViewInitializedEventArgs e)
    {
        var webView = e.WebView;

        // Transparent. On its own WinUI turns this into the theme's opaque base fill; the shell's
        // WebView is truly transparent only with MakeTransparent as well.
        webView.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;

        var core = webView.CoreWebView2;
        var settings = core.Settings;

        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsStatusBarEnabled = false;

        // F5, Ctrl+R, Ctrl+F, Ctrl+P and the rest of the browser's own shortcuts. Clipboard and
        // editing keys are not browser accelerators and keep working. Left on in Debug, where a
        // reload is a development tool rather than a way to lose the app's state.
        settings.AreBrowserAcceleratorKeysEnabled = IsDebug;

        // Dray never stores a secret (docs/CREDENTIALS.md). WebView2 would otherwise offer to save
        // the password typed into a registry login form into its own profile, on disk, beside
        // the app — a credential store Dray did not choose and cannot see.
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;

        // MAUI's BlazorWebView switches the default menus off before this event, which takes Cut,
        // Copy and Paste out of every field and means ContextMenuRequested is never raised. On
        // again, so the handler below can trim the menu rather than nothing showing at all.
        settings.AreDefaultContextMenusEnabled = true;
        core.ContextMenuRequested += OnContextMenuRequested;
    }

    /// <summary>
    /// The page's own menu stays — a row's context menu is drawn by the page and prevents this one
    /// — and on text, WebView2's is trimmed to editing.
    /// <para>
    /// Not switched off outright with <c>AreDefaultContextMenusEnabled</c>: that also takes Cut,
    /// Copy and Paste away from every text field, which is the one place a native app would have
    /// them.
    /// </para>
    /// </summary>
    static void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var target = e.ContextMenuTarget;
        if (!target.IsEditable && !target.HasSelection)
        {
            e.Handled = true;
            return;
        }

        var items = e.MenuItems;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            var keep = item.Kind == CoreWebView2ContextMenuItemKind.Separator
                || KeptMenuItems.Contains(item.Name)
                || (IsDebug && item.Name == "inspectElement");

            if (!keep) items.RemoveAt(i);
        }

        // Removing items leaves their separators behind: none may lead, trail or double up.
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].Kind != CoreWebView2ContextMenuItemKind.Separator) continue;

            var orphan = i == 0 || i == items.Count - 1
                || items[i - 1].Kind == CoreWebView2ContextMenuItemKind.Separator;
            if (orphan) items.RemoveAt(i);
        }

        if (items.Count == 0) e.Handled = true;
    }

    static bool IsDebug =>
#if DEBUG
        true;
#else
        false;
#endif
}
