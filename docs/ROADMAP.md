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
- Run-from-image dialog: ports, volumes, env, network, restart policy, with the generated
  `docker run` shown live and copyable
- Exit-code plain-language expansion (DESIGN.md §2.4)

**Progress:** actions and logs are done and running against a live engine. Detail page has its tab
strip; Inspect, Files, Ports and Env are stubs. Exec, stats and the file browser remain.

**Demo:** find a container that died overnight, read why in the logs, exec in, fix it, restart —
without touching a terminal.
**Exit:** 400 containers scroll at 60fps · one stopping re-renders one row · logs keep up with a
noisy container · every action keyboard-reachable.

---

## Phase 4 — Images, volumes, networks

- **Images:** list with tag grouping and dangling filter, layer history, pull with per-layer progress,
  push, tag, remove, prune with a preview of exactly what will be reclaimed, save/load, run
- **Volumes:** list with real sizes, inspect, **browse contents** (via a helper container — the
  feature every competitor is missing), create, remove, prune, backup/restore to tar
- **Networks:** list, inspect, create, connect/disconnect, prune, and a simple topology view of which
  containers share which network
- Typed-confirmation for prune and bulk delete

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
