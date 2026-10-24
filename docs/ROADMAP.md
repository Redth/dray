# Roadmap

Nine phases. Each has a **demo** — the thing you can actually show when it's done — and **exit
criteria** that must hold before the next phase starts. Phases 1–7, including 6.5, are v1.

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
`PageChrome`, with the theme handoff verified in light and dark. Greyscale and both-theme checks
pass on `/gallery`.

**The Windows head is written but has not run.** It has a WinUI `NavigationView` sidebar built from
the manifest, a native toolbar row projected from `PageChrome`, `ContentDialog` dialogs with Blazor
bodies, a transparent WebView2 on Mica, and the token handoff read from WinUI's own theme
dictionaries. Every file compiles against the real Windows reference assemblies with warnings as
errors. Nothing has been seen on screen. docs/NATIVE-SHELL.md §5.4 lists what to verify first. The
GTK head is next.

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

> **The GTK4 head is deferred.** It cannot be run or verified on the development machine. The
> seams it needs — `NavigationManifest`, `PageChrome`, `IconRef`, `IShellBridge`, `IPlatformTheme`
> — are all in place and exercised by the other heads, so adding it later is additive rather than
> structural. The Windows head was written ahead of a Windows machine, so it could be finished in one
> session on one. It is the precedent for what "written but not run" has to come with: a compile
> check against the platform's real assemblies, and a written list of runtime claims to verify.

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
healthy.

**SSH and WSL2 both landed, as one piece of work.** Neither has an address on this machine, so both
are reached by running a program that already has the socket and speaking HTTP over its stdio —
`docker system dial-stdio`, which is the Docker CLI's own hidden subcommand for exactly this. One
transport serves both, which is why `ssh://` arrived alongside WSL rather than as a project of its
own (docs/ARCHITECTURE.md §2.4a).

The transport was first verified against a live engine on macOS with `dial-stdio` standing in for
the WSL launcher. **Then verified on Windows, against a real `wsl.exe`** — and that run found the bug
the stand-in could not: `DockerRawApi`, built during connect, refused relayed endpoints, so no WSL or
SSH host had ever been able to connect. Fixed, with a regression test. Against Ubuntu 26.04 running
Docker 29.9, connect, the container list and the event stream all work through the relay. A
container started from inside the distro appears in the store without a re-fetch. A request on a
warm distro costs about 175 ms. A failed relay now says why, from its stderr: Docker not installed,
installed but not running, or the distro gone.

Distro enumeration handles the two traps in `wsl --list --verbose`: it is UTF-16LE unless
`WSL_UTF8=1` is honoured, and the table is localised in both its header and its state word. The
parser matches on shape rather than words — and a test written from the French output caught a real
bug, because "En cours" is two words and reading the name backwards from the version made every
distro on a French machine parse as `Ubuntu En`.

Still outstanding for this phase: showing which distro a container's bind mounts live in (`/mnt/c`
versus the native filesystem, the top cause of "why is this so slow" on Windows).

**Demo:** connect to local Docker, a WSL2 distro, and a remote host over SSH; kill a container from a
terminal and watch the dashboard update with no refresh. *(Local Docker and a WSL2 distro are
verified live on Windows. SSH shares the WSL relay, raw-API fix included, but has not been run
against a real `ssh` yet.)*
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

**Push, tag, save and load** are done. An archive is streamed in both directions and written
beside its destination before being moved into place, so an interrupted save cannot leave a
half-written file where a whole one is expected. The controls are gated on the head as well as the
engine: an archive is a file on this machine, so the web head — which has no file dialog — offers
neither.

**The topology view** answers which containers share which network, as a grid rather than a node
diagram. The answer is a set of memberships; a diagram of it is mostly empty space, unreadable past
a dozen containers, and hides the thing worth seeing, which is a row with two marks in it. Each
cell carries the address that container has on that network, and a container reaching more than one
is marked and sorted to the top — that container is how traffic crosses between them.

**Phase 4 is complete.**

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

**Scale** is a stepper per service, running `up -d --no-recreate --scale`, so adding a replica does
not restart the containers already up.

**The dependency graph** is read from the compose file, because the engine keeps no record of
`depends_on` — compose uses it to decide start order and then forgets it. Layered left to right,
with a barycentre pass so edges do not cross for no reason, and a cycle drawn rather than dropped:
compose refuses to run one, and hiding the services involved would hide the reason.

**Watch mode** is offered only where it would do something — compose new enough to have the
subcommand, and a `develop.watch` block in the file. It is the one command here that never
finishes, so it has its own token and stops when the page goes away.

~~**Phase 5 is complete.**~~ **It was not.** A re-read against the exit criteria found that a down
stack vanishes, because nothing tracks user-added files; that compose is never told which host to
target, so it follows whatever the CLI's context happens to be; that `SupportsCompose` was never
measured and reads false on every engine; and that build, recreate, per-service restart and exec,
and schema validation were never built. Phase 10 picks all of it up.

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

- **Registry search** lives in the pull dialog rather than in a browser of its own, because finding
  an image and pulling it is one thought. The engine does the searching — it already holds the
  registry configuration and the credentials — and the reader handles both engines' response
  shapes, which differ in field names and not merely in case.
- **Browse and pull by tag**, in the same dialog. One field, read as the CLI reads it: part of a name
  suggests repositories, a colon lists that repository's tags, and a host typed into it moves into a
  registry picker that offers Docker Hub, GitHub, Quay, Microsoft and Amazon ECR Public beside every
  registry the user has signed in to. Each tag shows its platforms with the engine's own first,
  its download size and its age; a filter, on by default, hides tags with no build for the
  engine's platform, and the chosen tag says before the download whether it runs here. Tags already
  on the engine are marked, and marked again when the registry's tag has moved since. Measured
  against Docker Desktop on Apple silicon: Hub search and tags, GHCR's 4,616 home-assistant tags
  read in five pages, MCR's 3,899-repository catalog, Quay through the engine's search, and an
  amd64-only `mssql/server` correctly flagged as having no arm64 build. Tags are read from the
  registry directly, because the Engine API has no endpoint for them (ARCHITECTURE.md §2.7).
- **Private images pull.** The engine never reads the credential store — the CLI hands it a
  credential on every pull — so before this a private image could not be pulled from Dray at all. A
  refused anonymous pull is now retried once with the helper's credential, and with none stored
  the refusal says what it probably means and where to sign in. Docker refuses a Hub repository it
  cannot see before the stream starts, which surfaced as a raw API exception; it is now reported
  in the stream like every other failure. Pulling by digest works: the digest used to be split on
  its colon into a repository called `nginx@sha256`.
- **Notifications** for pull, push, build and every compose command, and only when the window is
  not in front: a notification about something the user is watching is noise. The web head posts
  through the Notification API; the macOS half waits on phase 7's signing, because
  `UNUserNotificationCenter` needs an entitled bundle and raises without one.
- **The menu-bar item** shows how many containers are running and lists them, each with Open, Stop
  and Restart. It reads the same store the UI does, so it follows the event stream rather than
  polling.

- **Buildx builder selection** appears in the build dialog only when there is a choice to make —
  buildx installed, and a builder beyond the engine's own. Two genuinely different paths rather
  than a flag on one: the Engine API build needs nothing installed and streams the engine's own
  output, while buildx is a separate program with its own builders and its own cache, so choosing
  one means running it. A builder whose node reports an error is listed and disabled with the
  reason, because a builder pointing at an endpoint that no longer exists fails in a way nothing on
  screen otherwise explains.

  Two things here are measured rather than assumed. `docker buildx` answers "unknown command" on
  this machine while `docker-buildx` works — Homebrew installs the binary without wiring it into
  that Docker's plugin directory — so detection tries both. And `--load` is not optional: a buildx
  build with a container driver leaves its result in the build cache and nowhere else, so without
  it the build succeeds and the image never appears in the list, which reads as Dray losing it.

**Phase 6 is complete.**

**Demo:** ⌘K → "restart api" → Enter, without the window ever being focused on a list.
**Exit:** credentials never touch `config.json` in plaintext · palette covers 100% of commands.

---

## Phase 6.5 — Local Kubernetes

The cluster on this machine, beside the engine it runs on. A lens on the user's own workloads, not
an admin console. Design and the three settled decisions — in v1, two pickers, local clusters
only — are in [`docs/KUBERNETES.md`](KUBERNETES.md).

- **K0 spike** (KUBERNETES.md §10): run against kind on Docker Desktop's engine. Port-forward,
  v5 exec, watch/410, streaming lists, STATUS parity, pairing and the image diagnosis all measured;
  two first-draft claims corrected and one client deadlock found. Still to run: OrbStack, Docker
  Desktop's own Kubernetes, k3d, minikube, and anything inside the macOS head
- Kubeconfig discovery honouring `KUBECONFIG`; a cluster picker separate from the engine picker,
  defaulted by a measured pairing; Remote contexts listed with a reason and not connectable
- `Dray.Kubernetes` behind an `IClusterClient` seam in Core; `ClusterStore` fed by list-then-watch
  reflectors that relist by diffing on 410
- Workloads, Pods, Services and Events pages; the pod state vocabulary joins DESIGN.md §2.4
- Logs (the previous instance offered once a replacement runs), shell, port forwarding bound to loopback
- Scale, restart, delete — typed where it destroys something a controller will not recreate
- Node containers grouped under their cluster on the Containers page
- Create and delete a kind cluster on the selected engine: kind detected (Homebrew's path
  included) or its install command shown, creation streamed, the current-context switch and the
  1.3 GB node image stated before anything runs, deletion typed
- `ImagePullBackOff` on an image built here, diagnosed from the pod's pull error and the engine's
  image list, with the load command shown before it runs

**Demo:** build `myapp:dev` in Dray, apply a manifest to a kind cluster from a terminal, and watch
the pod go to Can't pull image. Dray says the image is on the engine but not in the cluster and
shows the `kind load` command; run it, watch the pod go Running without a refresh, forward its port
and open it.
**Exit:** a pod deleted from a terminal changes its row with no refresh · a forced 410 relist
writes only what changed · `kubectl`'s current-context never moves unless the user pressed the
control that moves it · no Remote cluster is ever connected · pod pills legible in greyscale.

**Progress:** built, and run end to end against kind through the app — the web head in both themes
and the macOS head.

- **The engine layer.** `IClusterClient`, `ClusterStore` and `ClusterManager` in Core beside their
  Docker counterparts; `Dray.Kubernetes` over `KubernetesClient` 21.0.8 with a reflector that
  streams or lists, follows bookmarks, resumes quietly and relists by diffing on a 410 it learns of
  only through the watch's error callback.
- **The pages.** Workloads, Pods, Services and Events, worst first; pod and workload detail with
  Overview, Logs, Terminal and Inspect. The cluster and namespace pickers are toolbar menus on these
  pages — native ones on macOS. `LogView` and `ExecTerminal` take a source rather than being copied.
- **The join.** Node containers are a row per cluster above the Containers list. An image a pod
  cannot pull that is on the engine is diagnosed as never loaded, with `kind load` shown and run on
  request; the cluster's name comes off the node's providerID.
- **kind.** Created from a dialog that states the 1.3 GB node image and the current-context switch
  before anything runs; deleted, typed, only where its nodes are measured on the engine.

**The demo, as run:** `myapp:v2` built on Docker Desktop, deployed to kind, shown as Can't pull image
with the diagnosis "on Laptop, never loaded into this cluster"; Run it, and the pod went Running with
nothing restarted. A Service forwarded from its row answered on `localhost:8080`. A kind cluster was
created from the dialog and deleted from the Hosts page.

**Exit:** a watch changes rows with no refresh — scaling from the page showed the third pod arrive
and leave · a 410 relist writing nothing for unchanged data is a test · current-context moves only
when kind does, from the dialog that says it will · a remote context is refused with its reason, a
test · every pod state has a glyph of its own, a test, and a greyscale screenshot of the Pods page
reads by glyph and word alone.

**Still to do:** the Windows head is written and unbuilt; the explicit "Use in terminal" control
(KUBERNETES.md §6) is not built — selecting a cluster changes nothing in the terminal, which is the
rule, and the control to opt in is next; OrbStack, Docker Desktop's own Kubernetes, k3d and minikube
have not been run.

---

## Phase 7 — Ship

- macOS: hardened runtime, notarization, DMG, appcast auto-update, Homebrew cask
- Windows: MSIX + winget, unpackaged fallback
- Linux: Flatpak, AppImage, `.deb`
- Crash + opt-in telemetry, first-run onboarding, error taxonomy pass, full a11y audit,
  i18n plumbing (strings externalized; ship English)

**Note on macOS verification.** The macOS head builds clean, launches, runs without a crash
report, and shares every Razor component with the web head — one RCL, no per-head UI. It is driven
through MAUI DevFlow, which reaches both halves: `ui screenshot` captures the native window and
CDP reaches the Blazor DOM inside it. That is enough to verify behaviour on macOS rather than
assume it — the stack cards, their disclosure, the overflow menu collapsing when the window is
narrowed to 640px, the network topology grid, and the AppKit colours arriving as CSS custom
properties on `<html>` have all been checked there.

What DevFlow cannot show is the WebView's *pixels*. The WKWebView is transparent so the native
window colour shows through, and its layer does not composite into the window capture — so the
screenshot shows the real native sidebar over an empty content area. Everything inside that area is
verified through the DOM and through the web head, which renders the same markup. `ChromeSignatureTests` covers the part that is genuinely
native-only and invisible from the web head — whether the `NSToolbar` rebuilds when a page's action
set changes shape, which is what makes a Pause button disappear when the selected engine cannot
pause. Everything else on screen is the same markup the web screenshots show.

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

**Both "outstanding" items turned out not to be outstanding, and that is the lesson of this
phase.** Writing files and an attachable shell were both reported unsupported on the strength of
reading the subcommand list — never tested. `container` has `cp`, has a full `volume` subcommand,
and `exec -i` streams in both directions. Volumes, shells and file writing all work here, and are
now implemented and verified live.

The capability system exists to stop the UI offering what an engine cannot do. It is no protection
at all when the capability itself is a guess, and three of seven flags were guesses.

What is genuinely absent, having been run rather than read: pause, rename, manageable networks, log
timestamps and stream separation, exit codes, and any access to a *stopped* container's filesystem.
One engine bug found on the way: `container cp` into a mounted volume returns success and writes
nothing, so writes go through `exec` instead.

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

**Progress:** done, and verified against a live stack.

- **Ports, environment and mounts** are structured editors. `RunParser` stays as the paste path, so
  a line copied from a README still works.
- **The secret flag** persists as a container label — verified end to end: marking `LICENCE_BLOB`
  wrote `codes.redth.dray.env-secret` onto the container, `POSTGRES_PASSWORD` was correctly absent
  from it, and the Env tab masks both afterwards, one from the label and one from the guess.
- **`Combobox<T>` was built rather than vendored.** Tom Select, Choices.js and the Blazor suites
  were all measured first; the deciding argument was not size but that the ranking was already
  written, that a library replaces the element with a tree Blazor no longer owns, and that
  `lint-tokens.mjs` skips `wwwroot/lib` — so a vendored stylesheet's raw colours would walk past the
  one gate built to stop them. What a library would have given free was correct ARIA, and the
  palette did not have it; that gap is now closed in a file we own.
- **`PathInput` is a separate component**, because path completion is segment-scoped, ranks by
  prefix rather than subsequence, and treats a trailing `/` as "keep going".
- **The compose editor annotates every `${VAR}`** with what it resolves to, live, as the `.env`
  panel beside it is edited — before saving, because seeing what a value will do is the reason to
  type it.

`ComposeInterpolation` implements Compose's real rules, including the one that catches people out:
`${TAG:-latest}` replaces an empty value and `${TAG-latest}` keeps it. Bringing the test stack up
confirmed the semantics from the other side — compose refused to start at all with a `:?` variable
unset, which is exactly the state the annotation calls "will not run".

**Exit:** nobody has to know a mapping syntax to publish a port · a variable marked secret is masked
everywhere without exception · a `${VAR}` with nothing behind it is visible before deploy, not after.
**All three met.**

---

**Windows containers.** Done at the runtime layer, unverified until there is a machine to run it on.

Windows is the only platform where "the local engine" is ambiguous, and the ambiguity is not
cosmetic: Windows containers and Linux containers are separate engines that share no images,
containers, volumes or networks. Windows containers need neither WSL2 nor Docker Desktop — the
`Containers` feature and `dockerd.exe` are a complete install — while Linux containers need a Linux
kernel and therefore a VM, which is WSL2 for Docker Desktop, Rancher Desktop and `podman machine`
alike.

**The endpoint cannot be asked which it is.** `//./pipe/docker_engine` is served by a native Windows
daemon, by Docker Desktop in either mode, *and* by podman's machine, which forwards its Linux API to
that same name. So `ContainerOs` is probed from `/info`'s `OSType`, `Unknown` is a real value that
never collapses into Linux, and the Hosts page says which kind of container the selected engine runs
before it says anything else about it.

Ten things branch on it, each of which fails quietly rather than loudly if missed — paths, shells,
directory listing, exit codes, CPU units, memory field, process count, block I/O, network driver,
pause. The full table is in docs/ARCHITECTURE.md §2.4. Two are worth naming here:

- **CPU is reported in 100-nanosecond intervals**, in the same field Linux uses for nanoseconds,
  with nothing in the payload marking the difference. Read as nanoseconds it under-reports by a
  factor of a hundred — a pegged container draws as a flat 1% rather than visibly failing. The
  decision in §2.9 to divide by elapsed wall time rather than Docker's published formula paid for
  itself a third time here: the fix is a scale factor and nothing else.
- **Absences stay absent**, as they did for Apple. There is no process count on Windows —
  `num_procs` is the host's CPU count, not a process count — so `ContainerStats.ProcessCount` became
  nullable and the panel draws a dash. Zero would have described an empty container that is in fact
  running normally.

Pause follows isolation rather than being assumed: Windows can only freeze a container that has its
own utility VM, so it is offered under Hyper-V isolation and hidden under process isolation. An
engine that did not report its default is treated as unable, because a Pause button that always
fails is the outcome `ContainerAction.cs` calls worse than no button.

**Exit codes are the clearest case of why this could not be one shared table.** 137 is `128 +
SIGKILL` on Linux and almost always the OOM killer; on Windows it means nothing at all, and Windows
reports an NTSTATUS instead — which arrives signed through some paths and unsigned through others,
so both spellings of `0xC0000005` reach the same sentence.

**One engine at a time, and Dray switches it.** This paragraph used to say Docker Desktop ran both
daemons at once and that the picker's switch stopped nothing. Measured, it does not: in Windows mode
the Linux pipe does not answer, and after a switch the Windows pipe returns 500. So Dray drives the
mode through Docker Desktop's own CLI (`docker desktop engine ls/use`, `status`, `start`, all with
`--format json`). Each engine is still its own host. Choosing the inactive one asks first — the
switch stops every container on the other engine — then switches Docker Desktop and selects the
engine that comes up: 11 seconds to Linux, 3 back, shown as in progress in the picker and on the
Hosts page. `docker_engine` and the CLI's `default` context are hidden as aliases once their engine ID
matches the Docker Desktop engine they point at. The picker is now in the Windows sidebar, and the
choice survives a restart.

**Guided setup that finishes the job.** PRODUCT.md now says Dray helps install engines on every
platform, under the same rules as before — shown first, run only when asked, elevated in the open —
using the vendor's own installer. On Windows that made the Linux track a complete route rather than a
checklist that stopped at "you have a distribution": WSL → Ubuntu (`--no-launch`, not elevated) →
your account, created from a native form → Docker Engine via Docker's script, as root, shown whole →
connected. Docker Desktop is offered beside it as the other route, installed with winget, started or
switched. Steps that do not need Dray elevated run inside it with their output streamed onto the
page. **Verified on this machine:** Ubuntu installed and Docker Engine installed through Dray's own
steps, and Dray connected to the engine they produced. Account creation was verified on a throwaway
distribution — groups, default user, and a password `sudo` accepts.

Every check is non-elevated, so `Unknown` is a common answer rather than a failure. What makes that
safe is that every offered command is idempotent — DISM against an enabled feature says so and
changes nothing — so Dray never has to guess in order to offer a step. An engine answering overrides
every inference: a machine where both kinds already work is shown no checklist at all.

**The CLI follows the picker.** "Use this host in my terminal" writes or reuses a Docker context and
switches to it, which covers `docker compose`, buildx and everything else for free — they resolve
their endpoint the same way `docker` does, where `DOCKER_HOST` would only cover one shell. Verified
against the live CLI on this machine: it reused the existing `desktop-linux` context rather than
creating a duplicate, which is the behaviour that would otherwise litter `docker context ls` with a
Dray-prefixed twin of every engine.

A `wsl://` host gets no button, because its socket has no Windows-side address — the panel says
where the command already works instead of offering one that cannot.

**Since then, verified on this machine:**

- **The setup UI was clicked through.** In the Windows head (Release, via UI Automation): the
  picker, choosing a WSL engine, and the Docker Desktop switch with its confirmation. In DevHost
  (driven over CDP): Start Docker in a distro, the account dialog, and Keep it running. This found
  that **every setup button had always been disabled**: `Running="_running"` binds the literal text
  to a string parameter.
- **An engine in WSL stopped whenever nothing was attached.** WSL ends a distribution about fifteen
  seconds after its last client, and containers do not count. Setup now offers
  `instanceIdleTimeout=-1` once such an engine answers; measured to keep the distribution and its
  containers up.
- **Compose acted on the CLI's context, not the selected engine,** and `SupportsCompose` was never
  set. It now targets the selected engine, and runs inside the distribution for a WSL engine.
  Verified: a stack in Ubuntu stopped and started through Dray, and its compose file opened from
  Windows.
- **Install help on macOS and Linux exists** (Colima, OrbStack and Docker Desktop on macOS; Docker
  Engine through polkit on Linux), as has a host picker in the web head.

**What is not done:**

- **Nothing here has run on a Linux desktop.** The Linux setup plan is unit-tested and reviewed in
  `/gallery`, but no step has executed there.
- **On a Mac, the head builds and runs against Docker Desktop**, and running it found two bugs in
  the sidebar picker: the sidebar selects its first row as it loads, which is the picker, so its
  menu's modal loop ran inside the app's launch and the window never appeared; and its title never
  changed, because the sidebar ignores a new list once it has loaded. Both are fixed. Opening the
  picker's menu with a click has not been driven — synthetic clicks need an Accessibility grant
  the test machine did not have — and no Mac setup step (Colima, OrbStack, starting Docker
  Desktop) has executed yet.
- **A Mac app opened from the Finder could not find `docker`** — launchd's `PATH` has none of the
  engines' commands — so compose, buildx, credential helpers and Apple's `container` were all
  missing outside a terminal launch. Fixed in `MacSearchPath`; verified by detecting compose under
  launchd's `PATH` before and after.
- **The Windows head was not driven with DevFlow**, because another worktree's Debug instance held
  its port and log file. It was driven in a Release build through UI Automation and screen capture.
- **Windows Server's engine script** (`install-docker-ce.ps1`) is the command Microsoft documents. It
  has not been run on a Server machine.
- Showing which distro a bind mount lives in (`/mnt/c` versus native) is still outstanding from Phase 2.

---

**Phase 9 — considered, not committed.** Machine lifecycle (creating the VM rather than driving it) ·
`docker scout` vulnerability surfacing · Swarm · remote Kubernetes clusters. Local clusters are v1
(Phase 6.5); remote ones carry exec credential plugins, the GUI `PATH` problem and production-grade
stakes, and are deferred on purpose. Each of these is a product decision, not a backlog item.

---

## Phase 10 — Stacks you author, not just observe

Phase 5 made stacks visible. This phase makes them something you create, edit, deploy and keep,
on this machine and on a remote host, without Dray ever owning the files.

The design borrows from two web UIs that do this well and avoids what their issue trackers say goes
wrong. **Dockge**: files on disk in a folder per stack so the CLI keeps working, Save separate from
Deploy, live output that replays to whoever joins late, one operation per stack at a time.
**Dockhand**: managed / adopted / observed provenance, status and health as separate values,
a redeploy popover that shows the exact command, `-H` on the command line rather than `DOCKER_HOST`
in the environment, lint while typing with `compose config` as the authority, a delete that previews
exactly what goes. **What both get wrong**, and Dray must not: regenerating YAML from an object on
every form edit (comments, quotes and tags lost — Dockge's largest bug cluster), any exited
container marking the whole stack exited (Dockge's most-upvoted bug), polling `compose ls` on a
timer, read-only stacks for anything the tool did not create, one compose file per stack, and
storing registry or git credentials.

### 10.0 — Foundations

- **One pure function builds every compose command line**, tested exhaustively. The host goes on
  the command line — `--context <name>` for a context, otherwise `-H` plus the TLS flags — never in
  the environment, where it would leak into compose's own interpolation and into services that read
  `DOCKER_HOST`. The process gets a minimal environment for the same reason: a shell variable
  overrides an `--env-file` value.
- **Streamed commands report an exit code.** Failure is the exit code, not a search for "error" in
  stderr.
- **`SupportsCompose` is measured**: a compose CLI on this machine *and* an engine it can drive.
  Apple's `container` is not one.
- **Compose's own parser for meaning.** `docker compose config --format json` answers what the
  stack *is* — services, ports, includes, overrides, profiles, interpolation — so the graph and every
  derived view read the model compose will actually run. YAML positions, for editor markers and
  surgical edits, come from a real parser rather than line scanning.

**Exit:** a stack on a non-current host deploys to that host · scaling a service that fails says so
from the exit code.

**Progress:** done. Compose runs name their host (`--context`, or `-H` with TLS flags), get an
allow-listed environment, and end with an exit code; `SupportsCompose` is measured. YamlDotNet
replaced both line scanners — 28 existing graph and watch tests passed on it unchanged, and a
service built from a merge key now has its edges. `compose config` is the authority, and its
refusals are read by patterns captured from compose 5.4.0, one of which (the scanner's
single-point position) was only found by saving a broken file from the editor.

Pulled forward from 10.2, because it is what made the editor worth using: the Compose tab is a
workspace with every file of the stack as a tab, the effective config beside them (secrets masked),
compose's verdict rechecked on every save, and its problems drawn on their lines and listed under
the editor. The schema check is `monaco-yaml` with the Compose Specification's schema, so a
misspelt key is underlined while typing and again, by compose, after saving.

### 10.1 — A registry, and the whole lifecycle

- **Three sources, merged:** a `stacks.json` of known stacks (host, project, working directory,
  files, env files, profiles — paths, never values), compose labels on containers, and stack folders
  the user adds, scanned recursively and watched rather than polled. A stack that is down, or
  written and never deployed, is still listed.
- **The stacks folder is asked for on first use**, suggesting `~/Stacks`, never assumed.
- **Provenance.** *Managed* — Dray created the folder. *Adopted* — files edited where they are and
  never deleted by Dray, prefilled from `config_files` / `working_dir` when every container agrees.
  *Observed* — labels only, with per-container fallback actions rather than none.
- **Status:** running · partial · restarting · stopped · down · draft. A container that exited 0 is
  a completed one-shot, not a failure. Health is a separate value, never folded into status. Tint,
  glyph and word, as everywhere.
- **Stack actions:** Deploy through a popover that shows the exact command, with presets (only
  changed · recreate all · pull and recreate); start, stop, restart; pull and update, which brings up
  only a stack that was already running; build; down, typed only when it takes volumes with it;
  **Forget** (drop from Dray, touch nothing) separate from **Delete** (preview of exactly which
  files and volumes go, typed name, adopted files never deleted).
- **Per-service actions:** start, stop, restart, recreate (`up -d --no-deps --force-recreate`),
  pull, scale, logs, and exec with a replica picker.
- **One operation per stack**, with its output kept so leaving the page and coming back shows it.
- `COMPOSE_FILE`, `COMPOSE_PROFILES` and `COMPOSE_PROJECT_NAME` in the stack's `.env` are honoured.

**Exit:** a down stack is still listed · deleting an adopted stack cannot remove its files · one
exited migration container does not turn a stack red.

**Progress:** done, and driven end to end in a browser against Docker Desktop: a stacks folder of
two drafts, one deployed through the dialog with the pull preset, stopped, brought down — still
listed as Down, with Deploy as its next action — and the other deleted by typing its name, folder
and all. **All three exit criteria met.**

- **Three sources, one list** (`StackCatalog`): labels, `stacks.json` (paths only — a test asserts
  no value is ever written), and the stacks folder, scanned without descending into a stack's own
  data and watched rather than polled. A stack is remembered the moment its containers are seen with
  their files on this machine, so one brought up from a terminal stays listed after a down too.
- **Compose's own naming rules**, measured rather than read: `My_Stack.v2` is `my_stackv2`,
  `.env`'s `COMPOSE_PROJECT_NAME` beats the file's `name:`, and an override is passed explicitly
  because `-f` turns compose's discovery of it off.
- **A stack whose files are elsewhere still has real controls.** Measured: `start`, `stop`,
  `restart` and `down` work with nothing but `-p`, from any directory; `pull` and anything that
  creates do not. So those stacks get the four that work and no Deploy.
- **Every action is one builder** (`StackCommands`), so the dialog's command preview is the
  command that runs, and each step of a multi-step action announces itself in the output.
- **The first-use question** is a card on the Stacks page with `~/Stacks` suggested, not a folder
  created unasked.

A **Build** action appears for any stack or service with a `build:` section. Not done here: the
Windows head, which does not exist yet. A `DRAY_CONFIG_DIR` override keeps development runs out of
the installed app's registry.

### 10.2 — Authoring

- **New stack:** blank, from an image, from a `docker run` line, from running containers (their
  inspect data, not a guessed command line), or by adopting a folder. A native dialog frame.
- **Every file in the stack is a tab** — base, override, includes, `.env` — plus a read-only
  *effective config* and a profile picker.
- **Validation in three layers:** the compose-spec schema, vendored, for completion, hover and
  errors in Monaco; lint rules that use what Dray already knows (a port another stack already
  publishes, a relative bind mount on a remote host, a socket mount, `:latest`); and `compose config`
  on save and before deploy as the authority.
- **Drift without polling.** `compose config --hash` per service against each container's
  `com.docker.compose.config-hash` gives "edited, not deployed". A snapshot of the last deployed
  effective config — compose files only, never `.env` — gives a pending-changes diff, and a hash of
  bind-mounted config files catches an edited `nginx.conf` the YAML never mentions.
- **Quick actions, not a form editor.** Add a service, a port, a variable — as splices into the text
  that leave every other byte alone.

**Exit:** a comment survives every edit Dray makes · an invalid file is caught before Deploy, not by
it · a deployed stack shows which services the edit will recreate.

**Progress:** done, driven end to end in a browser against Docker Desktop in both themes. **All
three exit criteria met.**

- **New stack** from a template, an image, a `docker run` line (composerize, vendored — Dockge's
  choice of library) or running containers. From containers, the inspect record is the source with
  the image's own defaults subtracted: copying `NGINX_VERSION` into a compose file would pin it and
  override the next image's value. A new runtime call, `GetImageDefaultsAsync`, answers that, and
  answers "unknown" on an engine that cannot say — in which case everything is kept and the dialog
  says so. Existing volumes and networks come across as `external`, so the data is reused rather
  than recreated empty. A secret goes to `.env` and the file says `${NAME}`. The generated file was
  checked with `docker compose config`.
- **Drift is compose's own definition.** `compose config --hash` equals the
  `com.docker.compose.config-hash` label for an unedited service, and differs for exactly the
  edited one — measured. The Services tab marks the edited service, says what Deploy will do, and
  the Deploy dialog says it again for the options chosen.
- **Deploy asks compose first.** A file compose refuses never reaches `up`: its problems are listed
  with a way to the line.
- **Quick actions are insertions** — add a service, port or variable — computed against the
  editor's unsaved text and landing in it unsaved, selected, one undo. They decline rather than
  reformat when a block is written inline. Review shows any edit in Monaco's diff editor before
  saving.
- **Dray's lint**: a port another container already publishes, an unpinned image, the engine's
  socket mounted, a relative path on a remote engine. Advice, labelled as Dray's, never part of
  compose's verdict.

- **Profiles** are chips on the stack's page that write `COMPOSE_PROFILES` into its `.env` — one
  line, comments kept — because compose reads that for every command, a terminal's included.
  Measured: honoured with `-f` given and from any directory.
- **An edited mounted file is a change.** Compose's hash does not cover files a service binds in,
  so Dray keeps SHA-256 digests of them at each deploy it runs (`history.json`, digests only) and
  marks a service whose `nginx.conf` moved as "File changed". Deploy then adds
  `up -d --no-deps --force-recreate` for exactly those services — verified: the edit is picked up,
  and the drift clears.

### 10.3 — Inspect and operate

- **Overview:** published ports as links, the project's networks, volumes and images, combined
  stats from the stats stream, and `x-dray: { urls, icon }` read from the file.
- **Combined logs on the shared `LogView`**, picking up replicas that start after the page opens.
- **Image updates:** registry digest against local, per platform, through the existing credential
  helpers. Checked when a stack is opened and on demand, with an optional daily check, and never at
  boot.
- **History** of the operations Dray ran: command, exit code, duration.

**Progress:** done, driven live against Docker Desktop in both themes.

- **Overview** is the stack's first tab: `x-dray` links and icon (http and https only; the icon a
  local file inlined, never fetched), every published port as a link — at this machine for a local
  engine, at its host for a remote one — combined CPU and memory from the stats stream, and the
  project's own networks and volumes by compose's labels.
- **Image updates** are asked of the engine (`/distribution/{name}/json`), which has the registry's
  address and its network path. Measured: the registry digest equals the local `RepoDigests` entry
  while the tag stands still. Anonymous first, the stored credential only when refused — fetching
  one can prompt — and every unknown says why: built here, pinned, private and signed out. Checked
  when the stack is opened and on demand. A multi-platform index moves when any platform is
  rebuilt, so the pill says "tag has moved", never "newer version".
- **Logs follow the stack**: a replica scaled up, or restarted, while the page is open is picked up
  from the event stream and read from where it began — verified by scaling a service from a
  terminal with the page open.
- **History** is a tab: each operation's outcome, duration and commands, copyable.
- **A store bug it surfaced:** a container first seen at `create` kept no ports after starting,
  because `start` was patched in place and ports are bound at start. It now fetches once in that
  case — which also fixes the ports column on the Containers page.

Not done: the optional daily update check, and moving the aggregated view onto the shared
`LogView` — it still has its own renderer, now with the follow behaviour it lacked.

### 10.4 — Remote hosts

- **SSH runs compose on the host** (`ssh host docker compose …`), and reads and writes the files
  there. The files stay where the containers' `working_dir` labels already say they are, and a
  relative bind mount means what it says.
- **TCP keeps local files**, and the lint flags every relative bind mount, because the remote daemon
  silently creates an empty directory for a path it cannot see.

**Progress:** done, and driven end to end against a real SSH host — a container running `sshd`
and the Docker CLI with its own filesystem, reached through the user's `ssh` with a throwaway
config, so the files under test existed only on the far side.

- **A stack whose files are on the host is the host's.** When a stack's containers name files that
  are not on this machine, Dray asks the host — one `test -f` per stack, once — and where they are
  there, marks it *on the host* and remembers it so it stays listed when down.
- **Compose runs there**: `ssh host -- 'cd <dir> && env -u COMPOSE_… docker compose …'`, shown before
  it runs exactly as it will run. Check, drift and deploy all go the same way: verified, a variable
  added in the editor was written to the host's file over SSH (comment kept), compose there said
  Valid, the drift said the service would be recreated, and the deployed container carried it.
- **Files are read and written there** through `IStackFiles` — the editor, the `.env` panel, the
  graph and the overview no longer know which machine they are on. Writes go through a temporary
  file and `mv`, so a dropped connection leaves the old file.
- **Two bugs the real host flushed out.** SSH hosts had never connected: the raw API client threw
  for any SSH endpoint, claiming it was "tunnelled to a local socket first" when nothing tunnelled
  it. It now uses the same `docker system dial-stdio` relay as the typed client. And the host's
  compose probe was cached as cancelled when its first caller was, which made every later run on the
  host cancel before starting.
- **TCP** keeps local files; the relative-path lint applies to it and not to a stack on the host.

Not done: scanning a stacks folder *on* the host for stacks that have never run there, and creating
a new stack on it — a remote stack appears once it has containers.

### 10.5 — Git, desktop-shaped

A stack folder inside a repository shows its branch, uncommitted changes and ahead/behind, with
*pull and redeploy*. System `git`, the user's own credentials, nothing stored. An optional command
wrapper per stack (`op run --`, sops) keeps secrets in the user's tooling rather than in Dray's.

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
