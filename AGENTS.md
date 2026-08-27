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
node build/gen-tokens.mjs --check      # fail if any generated file is stale
node build/verify-contrast.mjs         # contrast + gamut + stale-exception report
node build/lint-tokens.mjs             # raw colour / type / z-index literals

dotnet build                           # all projects
dotnet test                            # all tests
```

`src/Dray.Ui/wwwroot/css/tokens.css` and `src/Dray.Core/Theme/Tokens.g.cs` are **generated and
gitignored** — run the generator after a fresh clone, before building.

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

## Verifying UI work

Both themes, every time. Attach light and dark screenshots to any PR touching UI, plus a greyscale
check on state pills. "It looks right on my machine in dark mode" is half the work.
