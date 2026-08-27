# Native shell — lifted from MAUI.Sherpa

Sherpa ships a working native sidebar and toolbar on macOS, Windows and GTK4 against the preview
MAUI platform packages. **The path is proven; Dray does not re-litigate it.** This document records
what to copy verbatim, what to copy with care, and the five places the pattern frays — each backed by
something you can open in that repo right now.

Reference: `/Users/redth/code/MAUI.Sherpa`, notably `src/MauiSherpa.MacOS/MacOSApp.cs`,
`src/MauiSherpa.MacOS/BlazorContentPage.cs`, `src/MauiSherpa.LinuxGtk/LinuxToolbarManager.cs`,
`src/MauiSherpa/Services/WindowsTitleBarManager.cs`.

---

## 1. The shape that works

One service in Core, three platform managers observing it. Keep this.

```
IToolbarService (Core)  ──event──▶  BlazorContentPage        (macOS · NSToolbar)
        ▲                       ├─▶  LinuxToolbarManager      (GTK4 · AdwHeaderBar)
        │                       └─▶  WindowsTitleBarManager    (WinUI · TitleBar)
   Blazor pages
```

### 1.1 macOS native sidebar — copy verbatim

`MacOSFlyoutPage` already does the work. A `FlyoutPage` with `FlyoutLayoutBehavior.Split`, a
`NavigationPage` detail wrapping the Blazor page, and four attached-property calls:

```csharp
MacOSFlyoutPage.SetUseNativeSidebar(flyoutPage, true);
MacOSFlyoutPage.SetSidebarItems(flyoutPage, items);            // hierarchical, SF Symbols, Tag = route
MacOSFlyoutPage.SetSidebarSelectionChanged(flyoutPage, item => …);
MacOSFlyoutPage.SelectSidebarItem(flyoutPage, predicate);      // native follows web
```

`MacOSSidebarItem` supports nested `Children`, which is how Sherpa gets Android / Apple / Secrets /
Tools groups. Dray's groups are Host, Workloads (Containers, Stacks), Resources (Images, Volumes,
Networks) and Settings.

### 1.2 Two-way route sync needs a reentrancy guard

Native selection drives the Blazor router, and Blazor route changes drive native selection. Without a
guard they ping-pong. Sherpa's fix is a plain bool and it is correct:

```csharp
MacOSFlyoutPage.SetSidebarSelectionChanged(flyoutPage, item => {
    if (_suppressSidebarSync) return;
    if (item.Tag is string route) blazorPage.NavigateToRoute(route);
});

void OnBlazorRouteChanged(string route) {
    _suppressSidebarSync = true;
    MacOSFlyoutPage.SelectSidebarItem(_flyoutPage, i => i.Tag as string == route);
    _suppressSidebarSync = false;
}
```

### 1.3 Making the seam invisible — the non-obvious parts

All four of these are needed for the WebView to sit inside native chrome convincingly. Sherpa learned
them the hard way; take them as given.

- **`ContentInsets = new Thickness(0, 52, 0, 0)`** on `MacOSBlazorWebView` so content clears the
  unified titlebar, plus `HideScrollPocketOverlay = true`.
- **Transparent WKWebView.** It paints an opaque background by default; kill it with KVC —
  `webView.SetValueForKey(NSObject.FromObject(false), new NSString("drawsBackground"))` — so the
  native window background shows through. This is what makes DESIGN.md §9.1 possible at all.
- **A native loading overlay, not a web one.** An `NSView` with `NSColor.WindowBackground` and an
  `NSProgressIndicator`, added above the WebView the moment the handler connects, with the WebView
  `Hidden` until Blazor signals ready — then fade in. Otherwise you get a dark flash on every launch.
  Keep Sherpa's **15-second safety timer** that reveals the WebView regardless; without it a Blazor
  startup exception leaves a permanently blank window.
- **A titlebar drag overlay.** The WKWebView swallows mouse events in the toolbar zone, so the window
  cannot be dragged by its titlebar. A transparent `NSView` over that strip restores it. Non-obvious,
  and users notice immediately.

### 1.4 NSToolbar: build a superset once, then toggle visibility

`NSToolbar` does not like being rebuilt per navigation. Sherpa builds every possible item once and
then shows/hides — and because Blazor initializes *before* the window handler is connected, the first
attempt finds `nsWindow.Toolbar == null` and must be retried. Keep both halves; they are real.

### 1.5 Keep native callback targets alive

Two GC traps, one per platform, both already solved:

- macOS: `MenuActionHandler : NSObject` with `[Export("menuAction:")]` set as an `NSMenuItem.Target`,
  held in a `List<NSObject>` field. Managed targets are otherwise collected and menus silently die.
- GTK: a `List<Gtk.Widget> _retainedWidgets` for widgets removed from the header bar.

### 1.6 Five things that are not in Sherpa's docs, only its source

Each of these cost a debugging cycle here. Together they are the difference between a window that
works and one that shows a bare "unhandled error" banner with nothing in any log.

**Register `NativeSidebarFlyoutPageHandler` explicitly.** `MacOSFlyoutPage.SetUseNativeSidebar` is
inert on its own — the default `FlyoutPageHandler` has no `NSSplitViewController` to configure, so
the flag is read by nobody and you silently get MAUI's own flyout.

```csharp
builder.ConfigureMauiHandlers(h => h.AddHandler<FlyoutPage, NativeSidebarFlyoutPageHandler>());
```

**Call both BlazorWebView registrations.** `.AddMacOSBlazorWebView()` registers the platform
handler; `builder.Services.AddMauiBlazorWebView()` registers the services components resolve. With
only the first, the app boots and then dies on the first component that injects
`NavigationManager` — and the failure surfaces as an unobserved task exception, not a startup error.

**Swallow the activation exception.** `MacOSMauiApplication.ApplicationDidBecomeActive` calls
`IWindow.Activated()` unconditionally and MAUI throws when the window is already active — which it
is on every re-activation, including the first once the WebView takes focus. The throw crosses back
into Objective-C, aborts activation, and leaves the Blazor content dead:

```csharp
[Export("applicationDidBecomeActive:")]
public new void ApplicationDidBecomeActive(NSNotification n)
{
    try { base.ApplicationDidBecomeActive(n); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("already activated")) { }
}
```

**Resolve `NSColor`s inside the effective appearance.** AppKit's semantic colours are dynamic:
outside a drawing context they resolve against the *default* (light) appearance no matter what the
app is showing. Skip this and switching to dark repaints the pills and text but leaves every surface
on its light value — light-grey text on a white panel inside a dark window.

```csharp
NSApplication.SharedApplication.EffectiveAppearance.PerformAsCurrentDrawingAppearance(() => { … });
```

**Give the mount point a height.** `#app { height: 100% }`. Without it `.app { height: 100% }`
resolves against an auto-height parent, the shell stops at its content, and bare window background
shows below — a seam in exactly the place the whole approach is trying to hide one.

### 1.7 Install global exception handlers on day one

A WebView app has no visible stack trace. An unhandled exception renders as a bare banner with the
cause in a console nobody can open, and MAUI's own logging does not catch the paths that matter.
Three handlers in `Main` cover them, and they found every bug above:

```csharp
AppDomain.CurrentDomain.UnhandledException += …
TaskScheduler.UnobservedTaskException += …          // where Blazor DI failures land
ObjCRuntime.Runtime.MarshalManagedException += …    // managed exceptions crossing into Obj-C
```

Pair them with a `window.onerror` / `unhandledrejection` hook in `index.html` that prints into the
error banner, so a JS-side failure is visible in the window rather than only in a screenshot of it.

### 1.8 Persist sidebar width

Read it from `NativeSidebarFlyoutPageHandler.SplitViewController.SplitView.ArrangedSubviews[0]`,
save on `NSApplication.WillTerminateNotification`, and **cache the split view reference before
terminate** — the handler chain is already torn down by the time the notification fires.

---

## 1.9 Driving the app: MAUI DevFlow

There is no devtools in a WebView and no screen-recording permission in a headless session, so the
app is otherwise unobservable. DevFlow gives screenshots, logs and CDP access to the Blazor DOM.

Both packages are Debug-only and **neither self-starts** — without these two calls the agent never
listens:

```csharp
builder.AddMauiDevFlowAgent();        // on the MauiAppBuilder, not Services
builder.AddMauiBlazorDevFlowTools();
```

The port comes from `MauiDevFlowPort` in `Directory.Build.props`; the CLI defaults to 9223, so pass
it explicitly:

```bash
dotnet maui devflow -ap 9241 ui status
dotnet maui devflow -ap 9241 ui screenshot --output shot.png --overwrite
dotnet maui devflow -ap 9241 theme set dark
```

`dotnet maui devflow logs` reads a shared store and can return a *different* app's history — check
the log entries actually belong to this app before trusting them.

## 1.10 Environment gotchas that block a first build

None of these are Dray's code, and all four cost time:

| Symptom | Cause | Fix |
|---|---|---|
| `NU1103` naming only feeds you can see | A machine-level config disabled nuget.org; `<clear/>` on `packageSources` does not undo that | `<disabledPackageSources><clear /></disabledPackageSources>` |
| `NU1507` with central package management | More than one feed configured | One feed, or package source mapping |
| `CS0246` on every platform type | Platform packages ship only a `net10.0-macos26.0` lib, so the bare `net10.0-macos` TFM references nothing | Target `net10.0-macos26.0` |
| `requires Xcode 26.0. The current version is 26.6` | The SDK pack pins an exact Xcode and rejects newer ones too | `<ValidateXcodeVersion>false</ValidateXcodeVersion>` |
| `IL1015: Unrecognized command-line option: 'Support/dotnetup/…'` | The SDK appends an unquoted link-attributes path; `dotnetup` installs under `~/Library/Application Support` | Requote `_ExtraTrimmerArgs` before `PrepareForILLink` (see `Dray.MacOS.csproj`) |

---

## 2. Five places the pattern frays

Everything above is worth copying. These five are what "improve on its shortcomings" means concretely.

### 2.1 The same routing table exists three times

`AppleRoutes` and `GoogleRoutes` — the sets deciding which routes show an identity picker — are
copy-pasted **verbatim into all three platform managers**: `BlazorContentPage.cs`,
`LinuxToolbarManager.cs`, and `WindowsTitleBarManager.cs`. Adding a route means remembering three
files, and nothing fails if you forget.

**Dray:** a page *declares* its chrome, the way it declares `<PageTitle>`. Hosts project; hosts never
decide.

```razor
<PageChrome Title="Containers"
            Search="Filter containers"
            Actions="@(new[]{ ChromeAction.Primary("run", "Run…", Icons.Play) })" />
```

### 2.2 The sidebar is declared twice

`MacOSApp.cs` builds a `List<MacOSSidebarItem>`; `MainLayout.razor` builds the same tree again as
`<NavLink>` markup. Two hand-maintained lists that must stay identical, with no test that they do.

**Dray:** one `NavigationManifest` in Core. The native sidebar and the web fallback nav both render
from it. Adding an entry is one line in one file.

### 2.3 SF Symbol names leak into the shared contract

`public record ToolbarAction(string Id, string Label, string SfSymbol, bool IsPrimary = true)` — the
cross-platform model hardcodes an Apple concept. The cost lands on GTK, which keeps a hand-written
`IconMap` translating `"arrow.clockwise"` → `"view-refresh-symbolic"`, *plus* a `CustomIconMap` of
pre-rendered PNGs in light and dark variants, *plus* entries for Font Awesome names like `"fa-cog"`
because pages sometimes pass those instead. Three naming schemes in one dictionary is the tell.

**Dray:** an `IconRef` enum-like abstraction. One table maps each `IconRef` to an SF Symbol, a Fluent
glyph, an Adwaita icon name, and a sprite id — generated from one source, so a missing mapping is a
compile error rather than a blank button on Linux.

### 2.4 Hardcoded colour crosses the native boundary

`WindowsTitleBarManager` opens with `BgDark = #1e1a2e`, `Accent = #8b5cf6` — a purple title bar,
while `app.css` sets `--accent-primary: #4299e1`, a blue. The Windows chrome and the web content are
literally different brands, and the token system stops at the WebView edge.

**Dray:** `tokens.json` generates `tokens.css` *and* `Tokens.g.cs`. Native chrome and CSS read the
same values, so they cannot disagree. This is the specific failure DESIGN.md §11 exists to prevent.

### 2.5 Chrome managers know about features

`WindowsTitleBarManager` is 780 lines and takes seven services, including `IAppleIdentityService` and
`IGoogleIdentityService`. `LinuxToolbarManager` takes nine. Feature logic has migrated into platform
chrome code, which is why the same logic then had to be written three times.

**Dray:** a chrome manager takes `IShellBridge` and the current `PageChrome`. Nothing else. If chrome
needs a dropdown of hosts, the page supplies the items; the manager renders them.

---

## 3. What this means for the plan

Phase 1 is **port and tighten**, not spike. The sidebar, toolbar, theme handoff and WebView
integration are known-good; the work is re-expressing them around `NavigationManifest`, `PageChrome`,
`IconRef` and generated tokens so the three heads cannot drift.

Budget the real unknowns instead — they are Docker-side, not shell-side: multiplexed exec streams
over a hijacked connection, `ssh://` context transport, and keeping a virtualized 400-row table at
60fps under a live event stream.
