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

**Never poll a list.** State comes from the Docker event stream into the entity store. A whole-list
re-fetch on a timer is a bug, not a fallback.

**Destructive operations are typed, not clicked.** Irreversible bulk operations require the user to
type the target's name. The brand colour never appears on a destructive action.

**Zero network dependencies at boot.** No CDN links, no webfonts, no remote assets. The app must
render correctly with the network unplugged.

## Commands

```bash
node build/gen-tokens.mjs              # regenerate tokens.css, Tokens.g.cs, DESIGN.md tables
node build/gen-icons.mjs               # regenerate sprite.svg + Icons.g.cs
node build/gen-tokens.mjs --check      # fail if any generated file is stale
node build/gen-icons.mjs --check
node build/verify-contrast.mjs         # contrast + gamut + stale-exception report
node build/lint-tokens.mjs             # raw colour / type / z-index literals

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
- `Dray.Core` references neither Docker nor a UI framework. `Dray.Docker` is one implementation of
  `IContainerRuntime`.
- Nullable enabled, warnings as errors, central package management via `Directory.Packages.props`.
- Records for DTOs, `Async` suffix on async methods.
- A page **declares** its native chrome via `PageChrome`; it never imperatively mutates a toolbar.
- Nav entries live once, in `NavigationManifest`. Never duplicate them into a head.
- Icons are `IconRef` values resolved per platform — never an SF Symbol string in shared code.
- Buttons have three weights (`Primary`/`Secondary`/`Ghost`) and a separate `Danger` tone. A
  destructive action in chrome is ghost + danger; the filled danger treatment belongs only to the
  committing button in a confirmation.
- Each app head links its own scoped-CSS bundle: `Dray.MacOS.styles.css`, `Dray.DevHost.styles.css`.
- The page `<h1>` is emitted by `PageChromeScope` into the content area, visually hidden. It cannot
  live in the toolbar: on native heads the toolbar has no DOM.

## Verifying UI work

Both themes, every time. Attach light and dark screenshots to any PR touching UI, plus a greyscale
check on state pills. "It looks right on my machine in dark mode" is half the work.

Use `Dray.DevHost` for fast component iteration and **MAUI DevFlow for anything about the native
shell** — the seam, the sidebar, theme handoff. A WebView has no devtools, so a failure there is
invisible without it; `docs/NATIVE-SHELL.md` §1.9 has the commands and §1.7 the exception handlers
that make errors surface at all.
