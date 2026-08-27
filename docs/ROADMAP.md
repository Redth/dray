# Roadmap

Nine phases. Each has a **demo** — the thing you can actually show when it's done — and **exit
criteria** that must hold before the next phase starts. Phases 1–7 are v1.

Assumptions taken (see PRODUCT.md for the reasoning; say the word and any of these can change):
Dray is a **client**, not an engine host, with the runtime seam in place for Apple `container` later ·
identity is **Freight & Fired Clay** · this ships as a **public, open-source product** with real
release engineering.

**Local engines are the primary target.** Remote hosts over `ssh://` remain a first-class idea in
the architecture — every host is the same type behind the same picker — but the transport is
deferred rather than shipped in v1. Nothing about the design changes; only the order.

---

## Phase 0 — North star

Establish the contract before any UI exists, because retrofitting it is what went wrong last time.

**Status: done.**

- ✔ `PRODUCT.md`, `DESIGN.md`, `docs/ARCHITECTURE.md`, `docs/NATIVE-SHELL.md`, `AGENTS.md`, `README.md`
- ✔ `design/tokens.json` → generated `tokens.css`, `Tokens.g.cs`, and DESIGN.md's palette tables
- ✔ `build/verify-contrast.mjs` — 42 pairs, plus sRGB gamut and stale-exception checks
- ✔ `build/lint-tokens.mjs` — 5 rules over `.css`/`.razor`/`.cs`/`.html`/`.js`
- ✔ Repo scaffold: `Dray.slnx`, `Dray.Core`, `Dray.Core.Tests` (54 passing), central package
  management, CI with a design gate feeding a 3-OS build matrix

**Demo:** CI fails a PR that introduces `#ff0000` in a `.razor` file. *(Verified by negative test —
all five lint rules fire, and breaking one token value fails the contrast gate.)*
**Exit:** ✔ both gates green, `dotnet build` and `dotnet test` clean.

---

## Phase 1 — The shell

Three heads booting the same UI, with the seam already invisible. No Docker yet.

**This is a port, not a spike.** MAUI.Sherpa already ships a working native sidebar and toolbar on all
three heads against these preview packages. `docs/NATIVE-SHELL.md` records what to copy verbatim
(native sidebar attached properties, the two-way route sync guard, WKWebView transparency, the native
loading overlay, the titlebar drag overlay, NSToolbar superset-then-toggle, the GC traps) and the five
places that pattern frays. The work here is re-expressing the proven mechanics around
`NavigationManifest`, `PageChrome`, `IconRef` and generated tokens so the heads cannot drift.

- `Dray.Core`, `Dray.Ui` (RCL), and the three heads
- Native sidebar from `NavigationManifest`; `PageChrome` projection onto `NSToolbar` / `CommandBar` /
  `AdwHeaderBar`; `IShellBridge` per head
- Platform background + accent pushed into CSS before first paint; synchronous theme switching
- The component kit built against fixtures: `DataTable`, `StatePill`, `Dialog`, `EmptyState`,
  skeletons, toasts, command palette
- Vendored icon sprite, vendored xterm.js. Zero network calls at boot.

**Progress:** Core abstractions, the RCL, the component kit, `Dray.DevHost` and the **macOS head**
are done — native `NSOutlineView` sidebar and `NSToolbar` both projected from the manifest and from
`PageChrome`, with the theme handoff verified in light and dark. Windows and GTK heads are next.
Greyscale and both-theme checks pass on `/gallery`.

Since built on top of that:

- **Content sits in cards**, inset from the window on a margin *measured* from the system's own
  sidebar card rather than chosen — `--chrome-inset`, alongside the measured `--chrome-top`.
- **`PageChrome` gained a `Back` slot**, projected leading on a native toolbar and rendered beside
  the heading where there is no toolbar to hang it from.
- **Blazor's own overlays are restyled.** The unhandled-error bar and the reconnect modal both
  shipped as recognisably-Blazor furniture — a yellow strip and a grey box about "the server". They
  are now Dray cards, on Dray's tokens, in Dray's voice, with the stack trace behind a disclosure.

**Demo:** the platform opens a window with a native sidebar and a container table, in light and dark,
and you cannot see where native chrome ends and the WebView begins.
**Exit:** OS theme toggle repaints in one frame · greyscale screenshot of the state pills is legible ·
app launches with networking disabled · no route, nav entry, icon or colour is declared in more than
one place.

> **Windows and GTK4 heads are deferred.** Neither can be run or verified on the development
> machine, so writing them now would produce untested code whose first real check is CI. The seams
> they need — `NavigationManifest`, `PageChrome`, `IconRef`, `IShellBridge`, `IPlatformTheme` — are
> all in place and exercised by the macOS head and `Dray.DevHost`, so adding a head later is
> additive rather than structural. Picked up when there is a machine to run them on.

---

## Phase 2 — Engine connection

> **Working without a live engine.** The development machine has no reachable Docker: no Docker
> Desktop or OrbStack installed, two declared contexts whose sockets are both dead, and a podman
> install with no machine. Provisioning one is the user's call, not a side effect of building.
>
> Almost all of this phase is testable regardless, because the interesting logic is not the HTTP
> calls: context discovery parses real files that exist on this machine, and the event pump, entity
> store, connection state machine and capability degradation are all driven through a transport
> seam that a fake can satisfy. The "no engine found" first-run state is the machine's actual
> state, so it gets verified for real rather than simulated. Only the transport itself stays
> unverified until an engine is available.

- `IContainerRuntime` + `Dray.Docker` over `Docker.DotNet.Enhanced`
- Context discovery, the host picker, per-host connection state machine
- `RuntimeEventPump` → `EntityStore`, with reconnect/backoff
- `RuntimeCapabilities` probing and graceful degradation
- WSL2 distro enumeration on Windows
- Dashboard: engine version, resource usage, disk-usage breakdown, live event log
- First-run: "No engine found" that names what Dray looked for and links to real options

**Progress:** endpoints, context discovery, capabilities, entity store, event pump, the Docker
transport and the host picker are done and running against a live engine. Killing a container from a
terminal updates the UI with no refresh, and the Refresh action is disabled while the stream is
healthy. Still outstanding: SSH tunnelling, WSL2 distro enumeration, and the disk-usage breakdown
(`system df` is not exposed by the client and needs a direct call).

**Demo:** connect to local Docker, a WSL2 distro, and a remote host over SSH; kill a container from a
terminal and watch the dashboard update with no refresh.
**Exit:** an unreachable host degrades one sidebar entry and nothing else · reconnect survives a
daemon restart · zero polling in the code path.

---

## Phase 3 — Containers

The core loop. If this phase is excellent, the app is worth using even with nothing else finished.

- List: virtualized, state pill + glyph + word, image, ports, uptime, CPU/memory sparkline, stack
  grouping, search, filters, multi-select
- Actions: start, stop, restart, pause, unpause, kill, remove, rename, copy ID
- Detail tabs: **Logs** (follow, timestamps, filter, wrap, ANSI, copy, save) · **Terminal** (exec,
  shell auto-detect) · **Files** (browse, download, upload) · **Stats** · **Inspect** (humanized +
  raw JSON) · **Ports** (click to open) · **Env** · **Mounts** · **Labels**
  — *all seven tabs are built; Mounts and Labels live inside Inspect rather than as tabs of their
  own, and Files has no download/upload yet*
- Run-from-image dialog: ports, volumes, env, network, restart policy, with the generated
  `docker run` shown live and copyable
- Exit-code plain-language expansion (DESIGN.md §2.4)

**Progress:** every detail tab is done and running against a live engine — Logs, Terminal, Stats,
Inspect, Files, Ports, Env — alongside the container actions.

- The file browser reads and writes real files in running and stopped containers through Monaco,
  preserving line endings and trailing newlines so an unedited save is byte-identical.
- The terminal is a real interactive shell through xterm.js and its own addons. The shell is
  *probed*, not assumed, so an Alpine image gets `ash` rather than a `bash` that dies on open, and
  stopped, paused and shell-less containers each say which they are.
- Stats stream from the engine rather than polling. CPU is computed from elapsed wall time, not
  Docker's published formula — see docs/ARCHITECTURE.md §2.9 for why that formula reports 398% for
  a single busy loop on podman.

Since then the list itself has caught up: **virtualized** rows, **multi-select** with cmd/shift and
⌘A, a **bulk action bar** that says how many of the selection each action would actually reach,
**rename** with the engine's own name rules validated as you type, and **copy full ID**.

The list also has **opt-in CPU and memory columns** with sparklines. Opt-in because this is the one
feature whose cost scales with the number of rows: the engine has no bulk stats endpoint, so each
row is a held connection. `StatsHub` caps it at 24 concurrent streams and follows the filter.
**Download and upload** work in Files on hosts with a real file dialog.

**Run-from-image is done.** Four fields, not forty — a name, ports, environment, mounts — because
`docker run` has over a hundred flags and a form offering them all is one nobody can complete.
Reached from the Containers toolbar and from a Run action on each tagged image row. Every box takes
the syntax people already have in their shell history, `RunParser` reports the first unreadable line
while they are still looking at it, and a start that fails removes the container it just created
rather than leaving an orphan nobody knowingly made. Verified live on both engines, including the
port-conflict path: podman words it `proxy already running`, Docker Desktop `address already in
use`, the Linux daemon `port is already allocated` — none of them says "port", so all three are
matched and turned into one sentence.

**Demo:** find a container that died overnight, read why in the logs, exec in, fix it, restart —
without touching a terminal. **This now works end to end.**
**Exit:** 400 containers scroll at 60fps · one stopping re-renders one row · logs keep up with a
noisy container · every action keyboard-reachable.

---

## Phase 4 — Images, volumes, networks

- **Images:** list with tag grouping and dangling filter, layer history, pull with per-layer progress,
  push, tag, remove, prune with a preview of exactly what will be reclaimed, save/load, run
- **Volumes:** list with real sizes, inspect, **browse contents** (via a helper container — the
  feature every competitor is missing), create, remove, prune, backup/restore to tar
  — *browsing, reading and editing are done; create, remove, prune and backup remain*
- **Networks:** list, inspect, create, connect/disconnect, prune, and a simple topology view of which
  containers share which network
- Typed-confirmation for prune and bulk delete

**Progress:** all three pages are live against a real engine.

- **Images:** list with tag/dangling filters, sizes and what each is used by, remove, copy id, and
  **pull with real per-layer progress** — a row per layer rather than one averaged bar, because
  averaging produces a bar that jumps backwards whenever a new layer starts.
- **Networks:** a card per network showing the containers that share it with their addresses,
  create with an optional subnet, disconnect, remove. The engine's own networks are marked and
  cannot be removed, so Dray does not offer a button that always fails.
- **Volumes:** browse and edit as before, plus create and remove.
- **Prune** is one shared flow for all four kinds. It previews by naming exactly what would go,
  reports what deleting it actually frees — unique layers, not the sum of the sizes in the list —
  and requires the phrase to be typed.

Dray deliberately does not offer `image prune -a` from a button. "Delete every image no container
is currently using" reads as tidying and means re-pulling everything.

An image's **layer history** is on its detail page, with the build machinery stripped from each
instruction (`/bin/sh -c #(nop) CMD …` reads as `CMD …`) and long `RUN` lines clamped with
click-to-expand, so one enormous instruction cannot bury the twenty layers around it.

**Disk usage is on the dashboard.** `system df` was implemented in the runtime and nothing called
it; the panel now breaks the four kinds down with bars scaled to the largest rather than the total,
because images dwarf everything else and against the total the other three are invisible slivers —
exactly when someone is checking whether volumes have grown. An engine that cannot answer says so
instead of showing `0 B`.

**Still open:** push, tag, save/load, and the network topology view.

**Demo:** reclaim 20GB and know precisely what was deleted before confirming.
**Exit:** no destructive operation is one unconfirmed click · prune preview matches reality.

---

## Phase 5 — Compose stacks

- Discover running stacks from compose labels; track user-added compose files so *down* stacks stay visible
- up / down / restart / pull / build / recreate, with streamed progress
- Per-service: scale, restart, logs, exec, health
- Aggregated multiplexed logs with per-service colour keys
- Service dependency graph
- Compose file editor: syntax highlighting, schema validation, `.env` handling
- Watch mode where the engine supports it

**Progress:** stacks are discovered from the labels compose puts on containers, so one brought up
from a terminal appears without being registered — verified against a real stack on this machine.

- **Per-service view** with each service's worst replica setting its state, because a service with
  three containers where one has crashed is not healthy.
- **up / down / restart / pull** through the compose CLI, streamed line by line. Compose is a
  separate binary, so it is *detected*: where it is absent the page says so rather than offering
  buttons that fail on click. `up` always runs detached — the containers must outlive the button.
- **Aggregated logs**, colour-keyed per service with each key a toggle, merged in arrival order
  rather than by timestamp: containers disagree about the clock by milliseconds, and sorting
  shuffles one service's lines into the middle of another's stack trace. Streamed from the engine
  rather than `compose logs`, so it works on a host with no compose installed.
- **Compose file editing** in the same Monaco editor the container and volume browsers use.

The service key colours are generated tokens (`--key-1`…`--key-8`), contrast-verified in both
themes like every other colour, and deliberately *not* the semantic palette — reusing danger red
for a service called `api` would make every one of its lines look like an error.

**Still open:** scale, the service dependency graph, and watch mode.

**Demo:** open a project's stack, bring it up, watch one service fail its healthcheck, read that
service's logs in the aggregated view, fix the compose file, recreate just that service.
**Exit:** a stack started from a terminal appears automatically · a down stack is still listed.

---

## Phase 6 — Registries, build, palette

- Registry management: add/remove, login through the platform credential helper (never plaintext)
- Docker Hub / GHCR search and browse, pull by tag
- Build from a Dockerfile with buildx builder selection and streamed output
- Command palette reaching every command; global search across all entity types
- Notifications for long operations, menu-bar/tray item with running count and quick actions

**Progress:**

- **The command palette** (⌘K) reaches navigation, every container and stack, and the actions that
  apply to each right now — state-filtered, so it never offers Start on a running container.
  Subsequence ranking, so "rsc" finds "Restart container"; hidden keywords, so "postgres" finds a
  container called `db`; and destructive entries are marked in the palette itself, since it is the
  one place an action runs without its row on screen.
- **Registries** read from the real Docker config and speak the credential-helper protocol.
  Signing in hands the token straight to the system store — the same one the `docker` command uses
  — and Dray keeps no copy. The `list` subcommand is used rather than `get`, so a token is never in
  memory just to render a table. A helper named in the config but missing from PATH is shown as a
  first-class state with the fix, not as "authentication failed".
- **Build from a Dockerfile**, with the context tarred locally and the engine's output streamed
  verbatim. Step counting handles both engines' formats — Docker's `Step 3/12 :` and podman's
  `STEP 3/12:`.

**Still open:** Docker Hub / GHCR search and browse, push, buildx builder selection, notifications
for long operations, and the menu-bar item.

**Demo:** ⌘K → "restart api" → Enter, without the window ever being focused on a list.
**Exit:** credentials never touch `config.json` in plaintext · palette covers 100% of commands.

---

## Phase 7 — Ship

- macOS: hardened runtime, notarization, DMG, appcast auto-update, Homebrew cask
- Windows: MSIX + winget, unpackaged fallback
- Linux: Flatpak, AppImage, `.deb`
- Crash + opt-in telemetry, first-run onboarding, error taxonomy pass, full a11y audit,
  i18n plumbing (strings externalized; ship English)

**Demo:** a stranger installs Dray from a link and manages a container in under a minute.
**Exit:** WCAG 2.2 AA verified in both themes · install → useful in under 60s on all three platforms.

---

## Later

**Phase 8 — Apple `container`.** Implement `IContainerRuntime` a second time against Apple's
containerization framework on macOS 26+. This is the payoff for the seam: it should be a new project
and a host-picker entry, with no page changes.

**Progress:** done, and verified live against `container` 1.3.0. `Dray.Apple` is a new project, a new
host-picker entry, and **not one page was rewritten** — the seam held.

- **Not Docker-shaped, and not pretended otherwise.** There is no HTTP API and no compatibility
  socket, so `AppleRuntime` drives the CLI with `--format json`. The wire types in `AppleWire.cs`
  were captured from the running engine rather than read from a specification, because there is not
  much of one.
- **A composite factory** dispatches on the endpoint's scheme, and `DockerContextReader` finds the
  CLI on `PATH`. Apple's host sorts last and never claims the current slot: a machine with both
  should open where the user's existing containers are.
- **The event pump grew a polling fallback.** This engine has no `events` subcommand at all, so
  `SupportsEvents` is false and the pump lists and *diffs* every two seconds. Diffing rather than
  resetting is load-bearing — a reset twice a second would clear every pending action and fire the
  change highlight on rows nobody touched. That in turn needed `ContainerSummary.SameAs`, because
  `Ports` is a list and record equality would have reported a change on every single tick.
- **What this engine cannot do is reported, not caught.** Seven new capability flags — pause, shell,
  rename, volumes, networks, stopped-file access, log metadata — each measured against the running
  engine. The UI reads them and hides what would fail: no Pause button, no Rename, no Terminal tab,
  and the Volumes, Networks and Stacks pages explain themselves instead of showing an empty list
  that reads as "none yet". The Hosts page lists all of it in one panel.
- **Absences stay absent.** There is no exit code anywhere in this engine's output, so a stopped
  container has none — a zero would read as "finished cleanly" for a container that crashed. Image
  size is unreported rather than `0 B`, and containers-per-image is unknown rather than `nothing`.
  Tests assert each of these gaps so closing one has to be deliberate.
- **Measured, not assumed:** `container stats --no-stream` takes ~2.2s per call, so the sampler waits
  out the *remainder* of its period rather than compounding; `container logs` merges the container's
  stderr into its own stdout, so every line is `StdOut` and the CLI's own stderr is dropped rather
  than blamed on the container; `cpuUsageUsec` updates coarsely, so one busy core reads 65–105%
  across samples. `CpuUsage.Percent` needed no change — dividing by elapsed wall time paid for
  itself a second time.
- 30 tests in `Dray.Apple.Tests`, all against JSON the real CLI emitted.

**Two bugs the second engine flushed out, both pre-existing and both on Docker too:**

- **The containers list never updated.** `Containers.razor` hoisted `var visible = Visible;` *above*
  `<EngineScope>`, so the child-content closure captured a snapshot taken when the page rendered and
  never recomputed it. A container stopped from a terminal left the row reading "Running"
  indefinitely — on every engine, not just Apple. The chrome had the same problem one level up, so
  the toolbar's "5 running" was stale as well. Both now sit inside `EngineScope`, which documents
  the rule.
- **The file browser called an unreadable folder empty.** A listing with zero entries *and* a reason
  attached rendered as "Nothing here. Hidden files are shown, so this really is empty" — stating as
  fact the one thing the engine had explicitly declined to tell us. It now shows the reason.

**Verified on both heads.** Web: screenshots in light and dark across every screen. macOS: driven
live through DevFlow's CDP bridge against the running app on the Apple engine — native insets
(`--chrome-top: 52px`, `--chrome-inset: 8px`), no web toolbar or sidebar in the DOM, the theme
handoff flipping `--brand` / `--accent` with the system theme, the Terminal tab absent, row actions
reduced to Start/Restart/Remove with no Pause or Rename, the three capability pages explaining
themselves, and a container stopped from a terminal reaching the list through polling with no
refresh. A layout sweep over every screen in dark mode found no overflow, no clipping, no
horizontal scroll and no invisible text.

Still outstanding: writing files into a container (the CLI copies paths, not streams) and an
attachable shell (its `exec` is a terminal command). Both are reported as unsupported rather than
half-implemented.

## Phase 8.5 — Composing a container is the hard part

The run dialog and the stack editor are where someone *authors* something, and authoring is
different from browsing: it is the one place where a text box is the lazy answer. Three multiline
textareas ask the user to be a parser. This phase replaces them with editors that know what they
are editing.

**Ports.** A row — host, container, protocol — and an Add button, producing a chip list. Each chip
removable, each conflict caught as it is added rather than on submit. `RunParser` does not go away:
pasting `8080:80/udp` into the host field splits it across the fields, so a line from a README still
works and the tested code still earns its keep.

**Environment, with an explicit secret flag.** Key, value, and a checkbox that marks the value
hidden everywhere it is ever shown — the detail tab, the inspect panel, a copy action.

`EnvVar.IsSecret` is currently *derived*, from the variable's name and from values carrying inline
credentials. That heuristic stays as the floor and the flag becomes the override, because both parts
are load-bearing and each catches what the other misses:

- The heuristic catches what nobody thought to mark. Reading a real stack through Dockhand's API
  during this design turned up a JWT API key stored with `isSecret: false` — a manual flag nobody
  had set. Dray's name rule would have masked that one on sight.
- The flag catches what no heuristic can know. `LICENCE_BLOB` is a secret and does not look like
  one; `BUILD_KEY_ID` looks like one and is not.

**Where the flag lives** is the design question, and the answer is the engine, not Dray. At create
time the marked keys are written as a container label — the same place compose keeps its own
metadata — so the marking survives restarts, is visible to anyone running `inspect`, and needs no
database on Dray's side. Labels are immutable after creation, so toggling the flag on a container
that already exists cannot be written back; that case is honest about being a local view preference
rather than pretending to change the container.

**Mounts, by type.** A type selector first, because the three are genuinely different things and a
single `source:destination` box hides that:

- **Volume** — a combobox of the volumes that exist, plus creating one inline.
- **Bind** — a host path with completion as you type and a real folder picker.
  `IShellBridge.PickFolderAsync` is already there and already used by the file browser.
- **tmpfs** — a size, and no source at all.

The destination completes too, from the image's declared volumes and from a running container's
filesystem when there is one to read — Dray can already enumerate a container's directories, and
this is the same call.

**The image field becomes a real combobox**: local images ranked first, then registry search. That
merges with Phase 6's outstanding Docker Hub / GHCR search rather than being a second search box —
the same dropdown, with local results above remote ones.

### The stack's `.env`

Verified against a real Dockhand-managed stack: the convention is a `.env` beside `compose.yaml` in
the stack's directory, and **nothing in the YAML refers to it**. That is not Dockhand's invention —
Compose loads `.env` from the project directory automatically and uses it to interpolate `${VAR}`
anywhere in the compose file. Dray knows that directory already: compose writes it onto every
container as `com.docker.compose.project.working_dir`.

Two mechanisms get conflated constantly and Dray should not conflate them:

| | reads | affects |
|---|---|---|
| `.env` in the project directory | automatic, no YAML | `${VAR}` interpolation **in the compose file** |
| `env_file:` on a service | declared in YAML | that **container's** environment |

So: an environment panel beside the compose editor, writing the stack's `.env`, with the same
key/value/secret rows the run dialog uses.

**And the compose editor shows the substitutions inline.** Monaco decorations render the resolved
value in muted text after each `${VAR}`, so a stack file reads as what it will actually become. A
variable with nothing behind it gets a warning: Compose substitutes an **empty string** for an
undefined variable and carries on, which is the quietest way a stack has to break — an image tag
that becomes `myapp:` or a port that becomes `:80`. Showing that before it is deployed is the whole
point of the annotation.

**Exit:** nobody has to know a mapping syntax to publish a port · a variable marked secret is masked
everywhere without exception · a `${VAR}` with nothing behind it is visible before deploy, not after.

---

**Phase 9 — considered, not committed.** Machine lifecycle (creating the VM rather than driving it) ·
`docker scout` vulnerability surfacing · Swarm · Kubernetes. Kubernetes and pods are explicitly out of
v1 scope. Each of these is a product decision, not a backlog item.

---

## Sequencing notes

**The preview packages are a settled risk.** `Microsoft.Maui.Platforms.MacOS` and `.Linux.Gtk4` are
preview, and that is fine — Sherpa runs on them in production today. Phase 1 ports known-good
mechanics rather than proving them.

**The real unknowns are Docker-side, and they sit in phases 2–3.** Budget for them there: multiplexed
stdout/stderr over a hijacked exec connection, `ssh://` context transport, and holding a virtualized
400-row table at 60fps under a live event stream. Those are the things worth spiking early if
anything is.

**Phases 3–5 are independently shippable.** Containers alone is a useful app. Do not hold a release
waiting for compose.

**Windows and Linux heads must build in CI from Phase 1**, even while macOS leads on polish. A head
that isn't built for three months is a head that doesn't work.
