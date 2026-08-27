# Design

The visual contract for Dray. Every colour, size, and component decision in the app resolves to
something on this page. If a screen needs something that isn't here, the thing to change is this
document — then the code.

> **Why this document is binding.** Its predecessor project shipped 1,080 lines of `app.css` holding
> ~80 feature-scoped variables (`--sdk-item-bg`, `--suggestion-bg`, `--tool-indicator-icon`) alongside
> 253 hardcoded hex values in `.razor` files, and required every new page to paste its own copy of the
> modal CSS or dialogs would render broken. That is what happens without a north star. §11 describes
> the lint that makes this one enforceable rather than aspirational.

---

## 1. Theme

**One sentence of scene:** a developer at 9am with the blinds open, laptop on a desk next to a real
terminal, switching to Dray for ninety seconds to find out why a container exited 137 — and switching
back.

That scene forces the answer: **light is the default, dark is first-class, and the OS decides.** Dark
is not the default because "tools look cool dark" — most of these users are in a bright room, and the
window sits beside an editor that is already following the system theme. Dray follows the system
theme, always, with a manual override in Settings.

**Colour strategy: Restrained.** Tinted neutrals, one brand colour under 10% of pixels, semantic
colour reserved for meaning. This is a tool that displays hundreds of rows of other people's data;
the chrome must recede.

---

## 2. Colour

All colours are authored in OKLCH. Hex is shown only as a reference for native platform code that
cannot consume OKLCH (AppKit `NSColor`, GTK CSS fallback). **Never author hex in CSS or `.razor`.**

**The two tables below are generated** from `design/tokens.json` by `build/gen-tokens.mjs`; editing
them by hand is pointless, since CI regenerates and diffs them. Every ratio is computed by
`build/verify-contrast.mjs`, which also fails the build if a token drifts outside sRGB and would
silently clip.

### 2.1 Light

<!-- generated:palette-light:start -->
| Token | OKLCH | Hex | Contrast |
|---|---|---|---|
| `--bg` | `oklch(1.000 0.000 0)` | `#ffffff` | — |
| `--surface` | `oklch(0.972 0.004 41)` | `#f8f5f4` | 1.09:1 vs bg |
| `--surface-2` | `oklch(0.945 0.005 41)` | `#f0ecea` | 1.18:1 vs bg |
| `--line` | `oklch(0.890 0.006 41)` | `#ded9d8` | 1.39:1 vs bg |
| `--line-strong` | `oklch(0.638 0.012 41)` | `#928986` | 3.40:1 vs bg |
| `--ink` | `oklch(0.235 0.014 41)` | `#241c19` | 16.73:1 vs bg |
| `--muted` | `oklch(0.520 0.011 41)` | `#6f6764` | 5.53:1 vs bg |
| `--brand` | `oklch(0.550 0.150 41)` | `#b74c1f` | 5.18:1 vs bg |
| `--brand-hi` | `oklch(0.480 0.147 41)` | `#9e3701` | 6.99:1 vs bg |
| `--on-brand` | `oklch(1.000 0.000 0)` | `#ffffff` | 1.00:1 vs bg |
| `--accent` | `oklch(0.330 0.067 232)` | `#013b52` | 12.06:1 vs bg |
| `--on-accent` | `oklch(1.000 0.000 0)` | `#ffffff` | 1.00:1 vs bg |
| `--focus` | `oklch(0.550 0.150 41)` | `#b74c1f` | 5.18:1 vs bg |
| `--ok` | `oklch(0.520 0.136 152)` | `#037e3f` | 5.17:1 vs bg |
| `--warn` | `oklch(0.660 0.137 78)` | `#bf8603` | 3.17:1 vs bg |
| `--danger` | `oklch(0.415 0.165 13)` | `#8f0131` | 9.45:1 vs bg |
| `--on-danger` | `oklch(1.000 0.000 0)` | `#ffffff` | 1.00:1 vs bg |
| `--ok-tint` | `oklch(0.955 0.028 152)` | `#e3f6e7` | 1.13:1 vs bg |
| `--ok-ink` | `oklch(0.400 0.105 152)` | `#005729` | 8.77:1 vs bg |
| `--warn-tint` | `oklch(0.962 0.033 78)` | `#fff0da` | 1.12:1 vs bg |
| `--warn-ink` | `oklch(0.430 0.099 62)` | `#754100` | 8.35:1 vs bg |
| `--danger-tint` | `oklch(0.955 0.022 18)` | `#ffebea` | 1.15:1 vs bg |
| `--danger-ink` | `oklch(0.435 0.174 18)` | `#9a002a` | 8.72:1 vs bg |
| `--neutral-tint` | `oklch(0.945 0.005 41)` | `#f0ecea` | 1.18:1 vs bg |
| `--neutral-ink` | `oklch(0.520 0.011 41)` | `#6f6764` | 5.53:1 vs bg |
<!-- generated:palette-light:end -->

### 2.2 Dark

Runtime-overridden by the platform window colour where the OS exposes one (§9.1); these are the
fallbacks, not the truth.

<!-- generated:palette-dark:start -->
| Token | OKLCH | Hex | Contrast |
|---|---|---|---|
| `--bg` | `oklch(0.185 0.000 0)` | `#131313` | — |
| `--surface` | `oklch(0.235 0.004 41)` | `#201d1d` | 1.12:1 vs bg |
| `--surface-2` | `oklch(0.275 0.005 41)` | `#2a2726` | 1.26:1 vs bg |
| `--line` | `oklch(0.330 0.006 41)` | `#383433` | 1.52:1 vs bg |
| `--line-strong` | `oklch(0.520 0.010 41)` | `#6e6765` | 3.37:1 vs bg |
| `--ink` | `oklch(0.945 0.005 41)` | `#f0ecea` | 15.85:1 vs bg |
| `--muted` | `oklch(0.700 0.010 41)` | `#a49c9a` | 6.95:1 vs bg |
| `--brand` | `oklch(0.705 0.140 46)` | `#e6814f` | 6.77:1 vs bg |
| `--brand-hi` | `oklch(0.760 0.130 48)` | `#f49665` | 8.34:1 vs bg |
| `--on-brand` | `oklch(0.185 0.000 0)` | `#131313` | 1.00:1 vs bg |
| `--accent` | `oklch(0.680 0.100 228)` | `#4ba4c9` | 6.63:1 vs bg |
| `--on-accent` | `oklch(0.185 0.000 0)` | `#131313` | 1.00:1 vs bg |
| `--focus` | `oklch(0.705 0.140 46)` | `#e6814f` | 6.77:1 vs bg |
| `--ok` | `oklch(0.740 0.145 152)` | `#59c47c` | 8.57:1 vs bg |
| `--warn` | `oklch(0.800 0.145 82)` | `#ecb33c` | 9.86:1 vs bg |
| `--danger` | `oklch(0.625 0.215 12)` | `#eb3764` | 4.67:1 vs bg |
| `--on-danger` | `oklch(0.185 0.000 0)` | `#131313` | 1.00:1 vs bg |
| `--ok-tint` | `oklch(0.285 0.026 27.36)` | `#362523` | 1.29:1 vs bg |
| `--ok-ink` | `oklch(0.740 0.145 152)` | `#59c47c` | 8.57:1 vs bg |
| `--warn-tint` | `oklch(0.296 0.026 14.76)` | `#392729` | 1.33:1 vs bg |
| `--warn-ink` | `oklch(0.800 0.145 82)` | `#ecb33c` | 9.86:1 vs bg |
| `--danger-tint` | `oklch(0.264 0.039 2.16)` | `#351d23` | 1.20:1 vs bg |
| `--danger-ink` | `oklch(0.700 0.170 14)` | `#f56a7e` | 6.44:1 vs bg |
| `--neutral-tint` | `oklch(0.275 0.005 41)` | `#2a2726` | 1.26:1 vs bg |
| `--neutral-ink` | `oklch(0.700 0.010 41)` | `#a49c9a` | 6.95:1 vs bg |
<!-- generated:palette-dark:end -->

### 2.3 Status pills

Container state is the single most-read piece of information in the app, so it gets a dedicated
treatment: a **pale tint with dark ink**, never a saturated fill. This is what keeps a table of thirty
rows from looking like a bag of sweets, and it is why `--warn` never needs to carry white text.

| Pair | Light | Dark | Contrast |
|---|---|---|---|
| `--ok-tint` / `--ok-ink` | `#e3f6e7` / `#005729` | `--ok` @ 18% / `--ok` | 7.77:1 |
| `--warn-tint` / `--warn-ink` | `#fff0d7` / `#764100` | `--warn` @ 18% / `--warn` | 7.43:1 |
| `--danger-tint` / `--danger-ink` | `#ffe9e9` / `#9a002a` | `--danger` @ 18% / `--danger` | 7.48:1 |
| `--neutral-tint` / `--neutral-ink` | `#f0eeed` / `#5b5553` | `--surface-2` / `--muted` | 6.31:1 |

### 2.4 Container state vocabulary — binding

**Never colour alone.** Every state is tint + glyph + word. A greyscale screenshot must stay legible.

| Docker state | Word | Glyph | Pill |
|---|---|---|---|
| `running` (healthy or no healthcheck) | Running | ● filled circle | ok |
| `running` (health: starting) | Starting | ◐ half circle | warn |
| `running` (health: unhealthy) | Unhealthy | ▲ triangle | danger |
| `restarting` | Restarting | ↻ arrows | warn |
| `paused` | Paused | ‖ pause bars | warn |
| `created` | Created | ○ hollow circle | neutral |
| `exited` (code 0) | Exited | ■ square | neutral |
| `exited` (code ≠ 0) | Exited *code* | ■ square | danger |
| `dead` | Dead | ✕ cross | danger |
| *unreachable host* | Unreachable | ⚠ | neutral, row dimmed to 55% |

Exit codes get plain-language expansion inline: `137` → "Exited 137 · killed (out of memory)",
`143` → "Exited 143 · stopped", `139` → "Exited 139 · segmentation fault".

### 2.5 Two documented exceptions

Honest notes rather than fudged numbers:

1. **`--warn` never carries white text** (2.72:1). It is authored for icons, borders, and the
   `--warn-ink` pill pair only. There is no amber filled button anywhere in Dray.
2. **In dark mode, `--brand` and `--danger` separate by hue, not luminance** (1.45, below the 1.7
   guideline). On a dark ground both must clear 4.5:1, which compresses them into one luminance band.
   They are 34° apart in hue and unmistakable in colour. The compensating rule is binding: **in dark
   mode a destructive button is never a saturated fill** — it is `--danger-tint` background with
   `--danger` text, a `--danger` border, and a trash glyph. Only the brand gets a filled button.

---

## 3. Typography

**One family, and it is the platform's own.** This is the single largest contributor to "feels
native." A shared webfont makes the content area look like a website embedded in a native window,
which is exactly the seam we are trying to hide.

```css
--font-ui:
  /* macOS */      -apple-system, BlinkMacSystemFont, "SF Pro Text",
  /* Windows 11 */ "Segoe UI Variable Text", "Segoe UI",
  /* GNOME */      "Adwaita Sans", Cantarell, Inter,
                   system-ui, sans-serif;

--font-mono:
  /* macOS */      ui-monospace, "SF Mono", Menlo,
  /* Windows */    "Cascadia Mono", Consolas,
  /* Linux */      "JetBrains Mono", "Source Code Pro", monospace;
```

No webfonts are downloaded. Ever. The app must render correctly with the network unplugged.

`--font-mono` is not decoration — it is a semantic signal meaning *this string is an identifier you
might copy*: container IDs, image digests, paths, ports, env values, log output, exec output. Mono
text is always selectable even though the rest of the app is not (§7.3).

**Fixed rem scale, ratio 1.125.** No `clamp()`. Users view at consistent DPI in a window they resize;
fluid type that shrinks in a narrow pane looks broken, not responsive.

| Token | Size | Weight | Use |
|---|---|---|---|
| `--text-xs` | 0.6875rem / 11px | 500 | Table column headers, pill text, keyboard hints |
| `--text-sm` | 0.75rem / 12px | 400 | Table cells, secondary metadata, log lines |
| `--text-base` | 0.8125rem / 13px | 400 | Body, form labels, buttons — matches macOS control size |
| `--text-md` | 0.9375rem / 15px | 500 | Detail-pane section headings, dialog titles |
| `--text-lg` | 1.125rem / 18px | 600 | Page titles |
| `--text-xl` | 1.5rem / 24px | 600 | Dashboard stat values, empty-state headings |

13px base is deliberate. This is a dense native tool sitting next to an IDE, not a web page; 16px
body makes a container table look like a blog post.

There are no display sizes and no hero type. If a screen wants a 48px number, that screen is wrong.

---

## 4. Space, size, radius

Space is a 4px scale: `--s1` 4px, `--s2` 8px, `--s3` 12px, `--s4` 16px, `--s5` 24px, `--s6` 32px,
`--s7` 48px. Nothing between steps; nothing above `--s7` in product UI.

**Density is a user setting**, because a person with six containers and a person with sixty want
different things. It swaps one variable; nothing else in the system changes.

| | Comfortable (default) | Compact |
|---|---|---|
| `--row-h` | 36px | 28px |
| `--row-pad-y` | `--s2` | `--s1` |
| Table font | `--text-sm` | `--text-xs` |

Radius: `--r-sm` 4px (pills, inputs, buttons), `--r-md` 6px (panels, popovers), `--r-lg` 10px
(dialogs). Nothing is fully round except avatars and the state dot.

Elevation is borders and background steps, not shadow. Exactly two shadows exist, both for genuinely
floating layers: `--shadow-popover` and `--shadow-dialog`. Cards do not have shadows. Rows do not have
shadows.

**Z-index is a named scale**, never an arbitrary number:
`--z-sticky` 10 → `--z-dropdown` 20 → `--z-overlay` 30 → `--z-dialog` 40 → `--z-toast` 50 →
`--z-tooltip` 60.

---

## 5. Layout

```
┌──────────────┬──────────────────────────────────────────────┐
│              │  native toolbar — title, actions, search      │  ← platform chrome
│   native     ├──────────────────────────────────────────────┤
│   sidebar    │                                              │
│              │           Blazor content                     │  ← one codebase
│  host picker │           (list  ·  split  ·  full)          │
│  nav tree    │                                              │
│              │                                              │
└──────────────┴──────────────────────────────────────────────┘
```

Three content archetypes, and only three:

1. **List** — a virtualized table. Containers, images, volumes, networks, stacks, registries.
2. **Split** — list on the left, detail on the right, resizable and persisted. Used when the user
   moves between siblings frequently (containers, stacks).
3. **Full** — a single subject filling the pane. Container detail with its tab strip, the compose
   file editor, Settings.

Detail is a **pane, not a modal.** Modals are the lazy answer and they break the "back to the editor
in ninety seconds" loop.

Responsive behaviour is structural, not fluid: below 900px the split collapses to list-or-detail;
below 700px the sidebar collapses to the platform's own overlay behaviour. Type never scales.

**Long content scrolls inside its own container.** Log views, exec terminals, inspect JSON, and wide
tables get `overflow: auto` on themselves. The window body never scrolls horizontally.

---

## 6. Components

Every interactive component ships all seven states — default, hover, focus, active, disabled,
loading, error. Shipping four of them is how a tool starts feeling cheap.

**Button.** Three variants and no more. `primary` (brand fill, white text — one per view, and never
on a destructive action), `secondary` (surface-2 fill, `--line-strong` border), `ghost` (transparent,
hover fills). Destructive uses `danger` styling per §2.5. Height 28px, `--r-sm`, `--text-base`.

**DataTable.** The most important component in the app. Virtualized (never render 400 rows), sortable,
resizable and persisted columns, row selection with shift/cmd range, keyboard navigation, sticky
header, per-row and multi-select action bar, and a right-click context menu that uses the **platform's
native menu**, not a div. Every list screen uses this one component. There is no second table.

**StatePill.** §2.4, and it is the only thing allowed to render container state.

**Dialog.** The native `<dialog>` element, one shared component, with focus trap, restore, `Escape`,
and backdrop built in. **There is no per-page dialog CSS.** Native OS dialogs are used for anything
touching the file system, and for destructive confirmation on macOS (`NSAlert` as a sheet).

**LogView.** Virtualized, follow-tail with auto-detach on scroll-up, timestamps toggle, wrap toggle,
level and text filter, per-service colour keys for aggregated stack logs, copy and save-to-file.
Selectable text. ANSI colour honoured. This is where a lot of the app's perceived quality lives.

**Terminal.** `xterm.js`, vendored locally, bound to a hijacked Docker exec stream. Font is
`--font-mono`; the colour scheme is derived from Dray's tokens, not xterm's defaults.

**EmptyState.** Teaches, never says "Nothing here." Every empty state names the thing, says why it
might be empty, and offers the one action that fixes it. "No containers running. Start one from an
image, or bring up a compose stack." with both as buttons.

**Skeletons, not spinners,** for anything with known shape. A spinner in the middle of a content area
is a placeholder for a design decision nobody made. Spinners are legitimate only for indeterminate
work in a button or toolbar.

---

## 7. Motion

150–250ms, ease-out. `--ease: cubic-bezier(0.22, 1, 0.36, 1)`. No bounce, no elastic, no orchestrated
page-load sequence — the app loads into a task.

Motion conveys **state change only**: a row's state pill crossfading when a container stops, a detail
pane sliding in, a toast arriving, a disclosure expanding. Never decoration, never on scroll.

The one signature moment: when the event stream reports a state transition, the affected row's pill
crossfades and the row background pulses `--brand` at 6% for 400ms, then settles. It tells the user
"this changed while you were looking" without moving anything. That is the whole personality budget.

`prefers-reduced-motion: reduce` replaces every transition with an instant change, and replaces the
row pulse with a 1.5s static tint. It is not optional and it is not a nice-to-have.

---

## 8. Interaction rules

**Keyboard first.** ⌘K/Ctrl+K opens the command palette, which can reach every command in the app.
`/` focuses search. `↑↓` move selection, `Enter` opens, `Space` previews. Every destructive shortcut
requires confirmation.

**Text selection.** The predecessor applied `user-select: none` globally and re-enabled it on a
growing allowlist, which broke selection on every new surface until someone noticed. Dray inverts it:
selection is **on** by default and disabled only on the specific chrome elements where drag-select
feels wrong — sidebar rows, toolbar, table headers, buttons, tabs.

**Copy is everywhere.** Every ID, digest, port, path, and env value has a click-to-copy affordance
on hover. This is the single most common thing a user wants from a Docker GUI.

**Never poll a list.** State comes from the Docker event stream. A manual refresh exists as a
fallback, in the toolbar overflow, not as the primary affordance.

---

## 9. Native integration

This section is what separates Dray from an Electron app with a nice theme.

### 9.1 The seam must be invisible

The Blazor content sits inside a native window. At the boundary, these must match exactly:

- **Background.** The WebView is transparent and the native window background shows through. On
  macOS `--bg` is overridden at runtime from `NSColor.windowBackgroundColor` /
  `NSColor.controlBackgroundColor`; on Windows from the Mica/`ApplicationBackdrop` resolved colour;
  on GTK from the Adwaita `@window_bg_color`. The table values in §2 are the fallbacks, not the truth.
- **Accent.** Where the OS exposes a user accent colour (macOS `NSColor.controlAccentColor`, Windows
  `UISettings.GetColorValue(Accent)`), selection highlight uses it. `--brand` remains the app's
  identity — icon, splash, primary buttons — but the *selected row* follows the user's OS preference,
  because that is what every native list on their machine does.
- **Theme changes propagate synchronously.** Switching the OS appearance must repaint native chrome
  and WebView content in the same frame. A visible two-step flash is a bug, not a limitation.
- **System settings are honoured:** reduce-transparency, increase-contrast, and the system text-size
  preference all map onto tokens.

### 9.2 What is native, per platform

| | macOS (AppKit) | Windows | Linux (GTK4) |
|---|---|---|---|
| Sidebar | `NSSplitViewController` + `NSOutlineView`, SF Symbols, translucent material | `NavigationView` | `AdwNavigationSplitView` |
| Toolbar | `NSToolbar` with real items, unified titlebar | Custom title bar + `CommandBar` | `AdwHeaderBar` |
| Menus | Real menu bar, full `NSMenu` | System menu / accelerators | App menu |
| Context menus | `NSMenu` | Native flyout | `GtkPopoverMenu` |
| Dialogs | `NSAlert` sheets, `NSOpenPanel`/`NSSavePanel` | Native pickers | `GtkFileChooserNative`, `AdwMessageDialog` |
| Notifications | `UNUserNotificationCenter` | Toast notifications | `GNotification` |
| Menu bar item | `NSStatusItem` — running count, quick start/stop | System tray | Tray where the DE supports it |
| Icons | SF Symbols in native chrome; the bundled set inside the WebView | Fluent icons | Adwaita icons |

**Icons: a bundled SVG sprite, not an icon font, and not from a CDN.** Font Awesome loaded over the
network in a desktop app is an offline failure and a first-paint stall. The sprite ships in the RCL,
is tree-shaken, and is styled with `currentColor`.

### 9.3 Native chrome is a projection of page state

The native sidebar and the web nav must never be two hand-maintained lists that drift apart. Both
render from one `NavigationManifest` in `Dray.Core`. Similarly, a page **declares** its toolbar
through a `PageChrome` record — the way it declares `<PageTitle>` — and the host projects that onto
`NSToolbar` / `CommandBar` / `AdwHeaderBar`. Pages never imperatively mutate native chrome.

---

## 10. Identity

**Name.** Dray — the flatbed dray cart that hauls the load. Equipment, not a platform.

**Mark.** A flatbed cart silhouette in profile, reduced to a horizontal deck line, two wheels, and a
single stacked crate, drawn on a 24px grid with a 2px stroke so it survives being a 16px menu-bar
template image. Monochrome (a template image on macOS); the terracotta appears only in the full app
icon, never in chrome.

**Anti-brief, stated plainly:** no whale, no shipping container, no cargo ship, no hexagon-and-node
network diagram, no blue. The category reflex is Docker-blue-and-whale; the reflex one tier down is
terminal-black-and-phosphor-green. Both are refused, on purpose.

**Voice.** See PRODUCT.md. Concretely: sentence case everywhere including buttons and headings. No
exclamation marks. No "Oops." No "Are you sure?" — say what will be destroyed. Errors state cause,
meaning, and remedy in that order.

---

## 11. Enforcement

A north star nobody checks becomes decoration. Three gates run in CI on every PR, before any build.

**1. `node build/verify-contrast.mjs`** recomputes all 42 declared pairs from `design/tokens.json`
and fails on:

- any pair below its target ratio;
- any token that has drifted **outside sRGB** and would silently clip to a colour nobody chose —
  this caught nine such values on the first run, whose real rendered hex differed from what was
  authored;
- any **stale exception**: an exception declared for a pair that now passes is an error, so an
  exception cannot quietly outlive its reason.

Separation rules (two saturated fills a user must never confuse) are satisfied by *either* a
luminance ratio *or* ≥60° of hue distance. Large hue separation on the blue-yellow axis survives the
common colour vision deficiencies at matched lightness, so demanding both would be a false
constraint — and keeping the rule honest is what makes the one real exception below visible.

**2. `node build/gen-tokens.mjs --check`** fails if any generated file is stale. Tokens have one
source, `design/tokens.json`, which generates:

| Output | Consumer |
|---|---|
| `src/Dray.Ui/wwwroot/css/tokens.css` | the WebView |
| `src/Dray.Core/Theme/Tokens.g.cs` | native chrome — `NSColor`, `Windows.UI.Color`, `Gdk.RGBA` |
| the palette tables in §2 of this file | humans |

`NSColor` and CSS therefore cannot disagree. Both generated files are gitignored; run the generator
after a fresh clone. `Dray.Core` fails the build with a readable message if `Tokens.g.cs` is absent.

**3. `node build/lint-tokens.mjs`** fails on any raw colour or type literal in hand-written UI
source — `#rrggbb`, `rgb()`/`hsl()`, CSS named colours, `px` font-sizes, and magic `z-index` values.
An unavoidable literal must carry a `design-lint-ok: <reason>` annotation. This is the specific rule
that would have prevented 253 stray hex values in the predecessor.

`Dray.Core.Tests` additionally asserts in C# that every `DrayColor` resolves in both themes, that the
two themes are not the same block emitted twice, and that ink clears AA on its ground.

**And by review, on every UI PR:** light and dark screenshots, plus a greyscale check confirming §2.4
still holds.

### Token tiers

- **Primitive** — `clay-500`, `coal-900`. Named for the colour, never for a feature. No component
  may reference one.
- **Semantic** — `--bg`, `--ink`, `--brand`, `--ok-tint`. What components use.
- **Component** — added only when a component genuinely cannot express itself in semantic tokens,
  and always defined *as* a semantic token.

A component token named for a feature (`--stack-card-bg`) is the failure mode this document exists to
prevent. If you are about to write one, the answer is a missing semantic role, not a new variable.
