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

### 1.4 NSToolbar: rebuild only when the shape changes

`NSToolbar` does not like being rebuilt per navigation — it flickers, and it silently drops the
search field's native subscription (§1.5). Sherpa's answer is a hardcoded superset of every action
in the app, built once and then shown/hidden. That works, but it means the head carries a list of
every page's commands.

Dray does not need the superset, because `PageChrome.Signature` says exactly when a rebuild is
required: it changes when the chrome's *structure* changes and not when a label, tooltip, enabled
flag or filter selection does. So navigation between two similarly-shaped pages does not rebuild at
all, and everything else updates in place. `MacToolbarProjector` is the whole implementation and it
knows nothing about any page.

Two mechanics still apply either way:

- **Attached properties first, `ToolbarItems` last.** Every mutation triggers a `RefreshToolbar`,
  and only the final one should see complete state. Set `MacOSToolbar.SetSearchItem` and
  `SetContentLayout` before touching `ToolbarItems`, or macOS inserts and removes items mid-build.
- **Retry the first attach.** Blazor declares chrome before the window handler is connected, so the
  first attempt finds `nsWindow.Toolbar == null`.

Native toolbar items are not all `ToolbarItem`: search and menus are `MacOSSearchToolbarItem` and
`MacOSMenuToolbarItem`, attached through `MacOSToolbar.SetContentLayout` with
`MacOSToolbarLayoutItem.Item / .Menu / .Search / .FlexibleSpace`.

### 1.5 The search field's event dies after the first refresh

Upstream, `ToolbarHandler.CleanupSearchItem` unsubscribes from the native `NSSearchField` change
event on every `RefreshToolbar`, but only re-subscribes when macOS inserts a *new* toolbar item.
After the first insertion the managed `TextChanged` event is silently dead — search stops working
with no error anywhere. Subscribe to the native field directly instead, after two dispatch hops (the
item must be inserted before `NSSearchToolbarItem` exists to find):

```csharp
NSNotificationCenter.DefaultCenter.AddObserver(
    NSTextField.TextDidChangeNotification, …, searchToolbarItem.SearchField);
```

### 1.5b Keep native callback targets alive

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

---

## 4. Dialogs — binding

**Rule: every pop-up that is not an inline menu is a native frame around web content.**

An inline menu — an overflow `⋯`, a combobox list, a context menu attached to a row — stays in the
page. It belongs to the control it came from, it moves when that control moves, and pulling it into
a native window would make it a window that happens to be shaped like a menu. Everything else — a
confirmation, a form, a viewer, anything modal — is a dialog and follows this rule.

### 4.1 The three parts

A dialog is three regions and the middle one is the only web:

| Region | Owned by | Default | Overridable |
|---|---|---|---|
| **Title row** | native | the title as a native label | yes — a template, for a dialog whose header needs controls (a segmented switch, a path, a state pill) |
| **Body** | Blazor | the `ChildContent` | it is the content; there is no default |
| **Button row** | native | Cancel + one confirm, in the platform's order and with the platform's default-button and destructive semantics | yes — a template, for a dialog that needs a third button or a left-aligned one |

The defaults exist so the common case is one line. The templates exist so the uncommon case does
not need a second dialog system. **A dialog that does not use them — that draws its own title bar
and its own buttons in HTML — is a bug**, on every head that has a native dialog.

### 4.2 Why, specifically

This is the lesson Sherpa paid for. Sherpa has both halves and neither is this pattern:

- `DialogService` uses `NSAlert` on macOS and `UIAlertController` on Mac Catalyst. Native, correct,
  keyboard-perfect — and unable to hold anything richer than a text field. Every dialog that needed
  a form went elsewhere.
- `CreateProfileDialog.razor`, `EditProfileDialog.razor` and `ReleaseNotesDialog.razor` are the
  elsewhere: a `.dialog-overlay` div, a `.dialog-header` with an `<h2>` and a close button, a
  `.dialog-body`, and a footer of HTML buttons. Rich, and wrong in the ways a web overlay is always
  wrong in a native window — Escape, focus trapping, focus restore, the top layer and Tab order are
  all hand-rolled, which is how Sherpa ended up intercepting every Tab keypress in the app.

The buttons are the part people notice. A native window's dialog has its confirm button where the
platform puts it, with the platform's keyboard defaults — Return commits, Escape cancels, the
destructive one is marked the platform's way — and it looks like every other dialog on the machine.
An HTML button row gets all three wrong at once, and gets them wrong differently on each platform.

The title row is the part people notice second: on macOS a dialog attached to a document is a
**sheet**, and a sheet with a web-drawn title is a rectangle that slid out of the toolbar.

### 4.3 What each head does

| Head | Frame | Notes |
|---|---|---|
| macOS | `NSAlert` with an accessory view for the body, or an `NSPanel` sheet with a hosted WebView for a body that is more than a form | `BeginSheet` on the key window, never `RunModal`, unless there is no key window |
| Windows | `ContentDialog` — `Title`, `Content`, `PrimaryButtonText` / `CloseButtonText` | its three regions are this pattern already |
| Linux | `AdwMessageDialog`, or `AdwWindow` with `modal` for a hosted body | |
| Web | `<dialog>` + `showModal()` | the only head that draws its own frame, because there is no other frame to use. `showModal()` is what buys focus trapping, focus restore, Escape and the top layer — see `Dialog.razor`. Hand-rolling any of those is the Sherpa bug. |

`ConfirmDestructiveAsync` in `IShellBridge` is this pattern for the case that needs no body at all,
and `MacShellBridge` shows the shape: a sheet on the key window, an accessory view when the decision
needs one (type-to-confirm), the confirm button disabled until it is safe to press.

### 4.5 As built

`IShellBridge.ShowDialogAsync(DialogRequest)` is the whole surface. A request names a title, a
Blazor component for the body, its parameters, and a button row; the result is the id of the button
pressed, or null for a dismissal.

| | |
|---|---|
| Contract | `Dray.Core/Shell/DialogRequest.cs`. Button *order* lives here too, with tests — AppKit adds buttons right-to-left and the browser lays them out left-to-right, and that is one sorting rule with a parameter rather than two implementations. |
| Web | `WebDialogService` + `DialogHost`, over `<dialog>` and `showModal()`. |
| macOS | `MacDialogSheet` — an `NSAlert` sheet whose `AccessoryView` is `MacDialogBody`, a second `BlazorWebView` built from the app's own service provider and hosting `DialogSurface`. |

Three things about the macOS half that were learned by running it:

- `IMauiContext` is **not** in the app's service collection. It is created per window, so the body
  takes it from `Application.Current.Windows[0].Handler.MauiContext`; asking DI throws.
- The sheet attaches to the key window, then the main window, then MAUI's own. Falling through to
  `RunModal` because the app happened to be in the background blocks the main thread until someone
  dismisses it — a hang, not a dialog.
- The accessory view's size has to be set on the `NSView`, not only on the MAUI element, and it has
  to be decided before the body renders: an alert lays its accessory view out once, from the frame
  the view already has. That is why `DialogSize` is three named sizes rather than a measurement.

The body declares nothing about chrome. `DialogSurface` renders the component and nothing else — no
title, no buttons — which is the rule in one file.

### 4.4 Consequences for how a feature is written

- Anything that would be a modal in a web app is asked for through the shell, not built in the page.
- A dialog's body is written once, in Blazor, and hosted by whichever frame the head provides.
- A dialog that only shows something — raw JSON, a manifest, a certificate — still follows this
  rule. "Read-only" is not a reason to draw a web title bar; it is a reason the button row is one
  button.
- Nothing about the frame is in the shared code. The page says what it wants — a title, a body, a
  set of choices — and the head decides what that is made of, exactly as `PageChrome` does for the
  toolbar.
