# Dray — agent instructions

A native desktop app for managing Docker: containers, images, volumes, networks, registries and
compose stacks, locally and on any host reachable through a Docker context.

**Read these before making changes.** They are contracts, not suggestions.

| Doc | What it governs |
|---|---|
| [`PRODUCT.md`](PRODUCT.md) | Who this is for, what it is not, the five design principles |
| [`DESIGN.md`](DESIGN.md) | Colour, type, spacing, components, motion, native integration. Binding. |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Project layout, runtime seam, state flow, the native↔web seam |
| [`docs/NATIVE-SHELL.md`](docs/NATIVE-SHELL.md) | What to lift from MAUI.Sherpa verbatim, and the five places it frays |
| [`docs/CREDENTIALS.md`](docs/CREDENTIALS.md) | Secret handling. Dray stores none; read before touching auth |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | Phases, demos, exit criteria |

## The rules that have teeth

**Colour and type come from `design/tokens.json`. Nowhere else.** Not a hex value in a `.razor`
file, not an `NSColor` literal in a head, not a `px` font-size. `node build/gen-tokens.mjs`
regenerates `tokens.css`, `Tokens.g.cs` and DESIGN.md's palette tables from that one source. CI runs
`build/lint-tokens.mjs` and fails on any raw literal. If a literal is genuinely unavoidable,
annotate the line `design-lint-ok: <reason>`.

**Contrast is verified, not asserted.** `node build/verify-contrast.mjs` recomputes all 42 pairs and
fails on regression, on a token drifting outside sRGB, *and* on a stale exception left behind after
a pair starts passing. Two exceptions are declared in `tokens.json`, each naming its compensating
control. Do not add a third without one.

**Never colour alone.** Container state is tint + glyph + word. A greyscale screenshot must stay
legible. See DESIGN.md §2.4 for the full vocabulary.

**A dialog is a native frame around web content.** Everything modal — a confirmation, a form, a
viewer — is a native title row, a Blazor body and a native button row. Only an inline menu (an
overflow `⋯`, a combobox list, a row's context menu) stays entirely in the page. A dialog that draws
its own title bar or button row in HTML is a bug on every head that has a native dialog; the web
head uses `<dialog>` and `showModal()`, never a hand-rolled overlay. DESIGN.md §2.4b, and
`docs/NATIVE-SHELL.md` §4 for each platform's frame.

**Text is not selectable by default.** `user-select: none` on the app, menus and dialogs — drag-
selecting chrome is the clearest tell that a window is a web page. Anything worth copying gets a
copy control instead; selection stays on only where a *range* is the unit of interest (logs,
terminal, editor, raw responses) and on form controls. DESIGN.md §2.6.

**Never poll a list.** State comes from the Docker event stream into the entity store. A whole-list
re-fetch on a timer is a bug, not a fallback.

**Destructive operations are typed, not clicked.** Irreversible bulk operations require the user to
type the target's name. The brand colour never appears on a destructive action.

**Dray never stores a secret.** Registry credentials go through the Docker credential helper
protocol, SSH stays with `ssh` and the agent, TLS material stays where the context put it. A
credential is never rendered, never logged, and never written to `config.json`.

**The colour lint reads comments too.** It scans for colour syntax anywhere in a file, prose
included, because teaching it to strip comments risks hiding a real literal in the one tool that
guards the palette. Write `rgb(…)` or `oklch(…)` in a comment and it will flag you — describe the
format in words instead, or annotate the line with `design-lint-ok: <reason>`.

**Zero network dependencies at boot.** No CDN links, no webfonts, no remote assets. The app must
render correctly with the network unplugged. The two third-party front-end libraries — Monaco for
the editor and xterm.js for the terminal — are **vendored** into `wwwroot/lib` by scripts in
`build/`, never referenced from a CDN. Opening a file or a shell inside a container must work on an
air-gapped machine, and the macOS WebView serves from a custom scheme where a remote script is a
CSP problem rather than a convenience.

**Don't reimplement a solved front-end problem.** Terminal emulation and code editing are both
deep: escape-sequence parsing, reflow, IME, character widths, tokenisation, undo. Dray uses the
libraries that do them and their own addons — the glue here resolves the palette and moves bytes,
and should stay that small.

## Commands

```bash
node build/gen-tokens.mjs              # regenerate tokens.css, Tokens.g.cs, DESIGN.md tables
node build/gen-icons.mjs               # regenerate sprite.svg + Icons.g.cs
node build/gen-tokens.mjs --check      # fail if any generated file is stale
node build/gen-icons.mjs --check
node build/verify-contrast.mjs         # contrast + gamut + stale-exception report
node build/lint-tokens.mjs             # raw colour / type / z-index literals

npm install                            # build-time tooling only; nothing here ships except Monaco
npm run vendor:monaco                  # refresh wwwroot/lib/monaco from node_modules
npm run check:monaco                   # fail if the vendored copy is stale or hand-edited
npm run vendor:xterm                   # refresh wwwroot/lib/xterm from node_modules
npm run check:xterm

dotnet build                           # all projects
dotnet test                            # all tests

dotnet run --project src/Dray.DevHost --launch-profile dray   # http://localhost:5199

# macOS head. `dotnet run` does not work for MAUI apps; run the built binary directly so
# stdout/stderr are visible.
dotnet build src/Dray.MacOS
src/Dray.MacOS/bin/Debug/net10.0-macos26.0/osx-arm64/Dray.app/Contents/MacOS/Dray.MacOS

# Drive and inspect the running app (Debug builds only). The CLI defaults to port 9223;
# Dray's agent is on MauiDevFlowPort from Directory.Build.props.
dotnet maui devflow -ap 9241 ui status
dotnet maui devflow -ap 9241 ui screenshot --output shot.png --overwrite
dotnet maui devflow -ap 9241 theme set dark
```

**`Dray.DevHost` runs the whole UI in a browser** with `ShellCapabilities.Web`, so components can
be built and reviewed in both themes without waiting on three native heads. It is never shipped.
`/gallery` renders every component against fixtures, including all seven interaction states — that
page is how UI changes get checked. Restart the host after a build: the scoped-CSS bundle is
content-hashed, so a running server keeps serving the previous one.

`tokens.css`, `sprite.svg`, `Tokens.g.cs` and `Icons.g.cs` are **generated and gitignored** — run
both generators after a fresh clone, before building. `Dray.Core` fails the build with a readable
message if either is absent.

## Conventions

- No XAML. UI is Blazor (`.razor`) in `Dray.Ui`, a Razor Class Library shared by all three heads —
  never MSBuild globs across projects (`docs/ARCHITECTURE.md` §1.1 explains why).
- `Dray.Core` references neither Docker nor a UI framework. `Dray.Docker` and `Dray.Apple` are two
  implementations of `IContainerRuntime`; a composite factory in the composition root dispatches on
  the endpoint's scheme. Adding an engine must not change a page — if it does, the seam is in the
  wrong place.
- **Engines differ, and the difference is data, not an exception.** When an engine cannot do
  something, add a `RuntimeCapabilities` flag set from something you measured against the running
  engine, and have the UI hide the control (DESIGN.md §8.1). Never catch the refusal and show an
  error the user could not have avoided, and never fill an absent value with a plausible one — an
  unreported size is not `0 B` and an unreported exit code is not `0` (DESIGN.md §8.2).
- Nullable enabled, warnings as errors, central package management via `Directory.Packages.props`.
- Records for DTOs, `Async` suffix on async methods.
- A page **declares** its native chrome via `PageChrome`; it never imperatively mutates a toolbar.
- Nav entries live once, in `NavigationManifest`. Never duplicate them into a head.
- Icons are `IconRef` values resolved per platform — never an SF Symbol string in shared code.
- Buttons have three weights (`Primary`/`Secondary`/`Ghost`) and a separate `Danger` tone. A
  destructive action in chrome is ghost + danger; the filled danger treatment belongs only to the
  committing button in a confirmation.
- Anything that reads `EngineManager.Store` must sit **inside** `<EngineScope>`, including
  `<PageChromeScope>` when the chrome shows a count. A value computed above the tag is captured by
  the child-content closure and never recomputed, so the screen silently goes stale.
- Each app head links its own scoped-CSS bundle: `Dray.MacOS.styles.css`, `Dray.DevHost.styles.css`.
- The page `<h1>` is emitted by `PageChromeScope` into the content area, visually hidden. It cannot
  live in the toolbar: on native heads the toolbar has no DOM.

## Verifying UI work

Both themes, every time. Attach light and dark screenshots to any PR touching UI, plus a greyscale
check on state pills. "It looks right on my machine in dark mode" is half the work.

**On the macOS head, a screenshot may not be available.** A window-image capture of the app renders
the AppKit sidebar and toolbar but leaves the content area blank — WKWebView composites out of
process — and DevFlow's CDP shim does not implement `Page.captureScreenshot`. Screen recording
permission is the only route to a composited image.

When it is not granted, verify through the layout the head actually computes, which is stronger
evidence than a picture for most of what matters here. Drive the running app with
`dotnet maui devflow webview Runtime evaluate -ap 9241 -- "<js>"` and check:

- `--chrome-top` and `--chrome-inset` on `:root` — the native insets are applied (52px / 8px).
- No `.toolbar` and no web sidebar in the DOM — AppKit is drawing both, not Blazor.
- `--brand` / `--accent` flip with `dotnet maui devflow theme set dark|light` — the theme handoff.
- A sweep for `scrollWidth > clientWidth` with `overflow-x: visible`, for transparent text, and for
  document-level horizontal scroll. The visually-hidden `<h1>` always reports as truncated; it is
  clipped to 1px on purpose and is the one expected hit.

Use `Dray.DevHost` for fast component iteration and **MAUI DevFlow for anything about the native
shell** — the seam, the sidebar, theme handoff. A WebView has no devtools, so a failure there is
invisible without it; `docs/NATIVE-SHELL.md` §1.9 has the commands and §1.7 the exception handlers
that make errors surface at all.
