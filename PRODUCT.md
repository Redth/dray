# Product

## Register

product

## Platform

web

> `web` is the impeccable register value for the UI layer: Dray's pages are Blazor components rendered
> in a WebView. It is **not** a website. The app shell is native on every platform (AppKit on macOS,
> WinUI on Windows, GTK4 on Linux) and the native platform conventions in `DESIGN.md` §9 are binding.

## Users

Developers who run containers as part of building something else. They are not container specialists
and they are not administering a fleet — they have between two and thirty containers on a laptop, a
couple of compose stacks per project, and a growing pile of images they know they should clean up.
Their context is a side window: they are in an editor or a terminal, something is broken, and they
switch to Dray to find out *why* — a port that isn't mapped, a container that exited 137, a stack
where one service never became healthy. They are back in the editor within ninety seconds.

Some also run a Kubernetes cluster on the same laptop — Docker Desktop's, OrbStack's, or a kind
or k3d cluster — to try a manifest before it goes somewhere that matters. A pod stuck in
`CrashLoopBackOff` is the same question as a container that exited 137, one layer up, and Dray
answers it in the same window. They are not cluster administrators, and Dray is not a cluster
console.

Some also keep a home server, a NAS, or a VPS with a Docker daemon on it. That is the same job one
hop away, and the architecture treats it as the same job — but it is not the job Dray is designed
around. The machine in front of them is.

The job to be done: **see the true state of my containers and change it, without leaving the keyboard
or losing the thread of what I was actually doing.**

Success is that a developer who has Docker Desktop installed opens Dray instead, out of preference,
because it answers the question faster.

## Product Purpose

Dray is a desktop application for managing Docker: containers, images, volumes, networks, registries,
and compose stacks — locally and on any host reachable through a Docker context — and the local
Kubernetes cluster that runs beside them.

Kubernetes support is a lens on the user's own workloads, not an admin console, and in v1 it
connects only to clusters measured as running on this machine. Dray does not flip an engine's own
Kubernetes switch, for the same reason it does not ship an engine. It will create a kind cluster
when asked, because a kind cluster is containers on the engine the user already has — a workload,
not a runtime — and it shows the command and what it will change before running it.
`docs/KUBERNETES.md` is the contract.

It manages the engine you have, and helps you get one when you have none. Dray does not ship a
runtime of its own and does not compete with Docker Desktop, Colima, OrbStack, Rancher Desktop, or a
bare `dockerd`. It connects to whichever of those is running, and to WSL2 distros on Windows, and to
remote hosts over SSH.

**Dray helps install what it drives, on every platform.** A developer with no engine — or with the
wrong one for the container they need — should not have to leave the app to find out what to install
or how. So Dray names what is missing and installs it: WSL and a distribution, an engine inside that
distribution, the Windows features Windows containers need, Docker Desktop, and on other platforms
their equivalents. The rules for doing so are the same everywhere:

- **Shown before it runs.** Every step displays the exact command, or the whole script when one runs
  as root, so the user can read it, copy it, run it themselves, or decide Dray is wrong.
- **Run only when asked.** Nothing installs on launch, on a timer, or as a side effect of something
  else. A step is a button the user presses.
- **Elevated in the open.** A step that needs administrator raises the system's own prompt and runs
  in its own visible window. Everything else runs inside Dray with its output on screen.
- **The vendor's own installer.** Dray installs Docker's engine with Docker's script, Docker Desktop
  with its own package, Ubuntu with `wsl --install`. It never repackages an engine or ships one.
- **Licences are said, not decided.** Where routes differ in cost — Docker Desktop needs a paid
  subscription in larger organisations, Docker Engine does not — both are offered and the difference
  is stated beside them.

**One engine at a time is the engine's rule, not Dray's.** Docker Desktop on Windows runs its Linux
engine or its Windows engine, never both. Dray shows each as its own host and switching to the other
switches Docker Desktop — after saying what stops.

Runtime ownership is deliberately behind an interface seam (`IContainerRuntime`) so that Apple's
`container` framework and, if it ever earns its place, machine lifecycle management can land later
without a rewrite.

## Positioning

**The Docker GUI for whichever engine you already have.**

Every other desktop tool in this space owns an engine and wants you on theirs — Docker Desktop,
OrbStack and Rancher each ship a runtime and a GUI welded together, so the GUI is really the shop
window for the runtime. Dray ships no runtime. It connects to Docker Desktop, OrbStack, Colima,
Rancher, a bare `dockerd`, a WSL2 distro or podman's compatible socket, probes what that engine
actually supports, and says plainly what it cannot do rather than failing at it. When there is no
engine yet, it installs whichever the user picks — and says what each costs — rather than steering
them to one.

That indifference is also why remote hosts cost almost nothing: a host is a host, so an engine
across the network is the same code path as the socket on this machine. Local is the job Dray is
built for; remote is what falls out of building it honestly.

## Brand Personality

**Workmanlike, warm, exacting.**

A dray is the flatbed cart that hauls the heavy load — brewery drays, dock freight, the unglamorous
vehicle that everything else depends on. The name sets the register: this is equipment, not a
platform. It has a job, it does it without ceremony, and it is built to be used every day for years.

Voice is plain and specific. Dray says "Container exited with code 137 (out of memory)" where other
tools say "Something went wrong." It never says "Oops." It never uses an exclamation mark. It states
what happened, what it means, and what the user can do about it, in that order.

Warmth comes from craft, not from cheer: real empty states that teach, error messages that name the
actual cause, a colour that feels like fired clay rather than another blue dashboard.

## Anti-references

- **Docker Desktop's dashboard.** Sluggish, Electron, whole-list refreshes, a marketing surface
  (Docker Hub promos, Scout upsells, extension marketplace) wedged into a tool people opened to
  restart a container.
- **Portainer.** Web-admin density with no editorial judgement — every field the API returns, in a
  table, with no sense of what matters. Dray shows the five facts that answer the question and puts
  the other ninety behind an Inspect tab.
- **Terminal-cosplay dev tools.** Near-black chrome, phosphor green, monospace headings, a fake CRT
  glow. Our users have a real terminal one keystroke away and don't need a costume of one.
- **The Linear/Vercel monoculture.** Cool graphite, subtle purple-blue accent, tasteful gradient
  glow on a dark hero. It is well-made and it is everywhere; adopting it means having no identity.
- **Any interface that encodes state in colour alone.** Green dot / red dot with no glyph fails
  ~8% of the men who will use this app.

## Design Principles

**Answer the question in the first screen.** Every list view leads with the fields that resolve the
reason someone opened it — state, health, ports, and how long it has been that way. Configuration is
one click deeper, never in the way.

**The event stream is the source of truth.** Dray subscribes to the Docker event stream and mutates
its store from it. There is no refresh button as the primary means of getting current data, and no
poll loop that redraws a whole list. If a container dies while the user is looking at it, the row
changes underneath them, immediately.

**Native chrome, shared content.** The window, sidebar, toolbar, menus, dialogs, and file pickers are
the platform's own. The content area is one Blazor codebase. The seam between them must be invisible:
matching background colour, matching accent, matching density, matching typeface. A user should not be
able to tell where AppKit stops and the WebView starts.

**Destructive operations are typed, not clicked.** Removing a volume, pruning images, or bringing down
a stack destroys data that may not be recoverable. These require an explicit confirmation naming what
will be lost, and irreversible bulk operations require the user to type the target's name.

**One vocabulary, everywhere.** Every list is the same list component. Every state pill is the same
pill. Every dialog is the same dialog. Consistency is the whole feature; novelty per screen is a bug.

## Accessibility & Inclusion

Target **WCAG 2.2 AA** across both themes.

- Body text ≥ 4.5:1, large text and UI boundaries ≥ 3:1, verified in light and dark. Placeholder and
  secondary text held to the same 4.5:1 as body — no "elegant" light grey.
- **Never colour alone.** Container state is a coloured pill *plus* a distinct glyph *plus* a word.
  A screenshot converted to greyscale must remain fully legible.
- Full keyboard operation: every action reachable without a pointer, a visible focus ring on every
  interactive element, focus trapped and restored around dialogs, and a command palette (⌘K / Ctrl+K)
  as the keyboard path to any command.
- `prefers-reduced-motion` honoured — every transition degrades to an instant state change or a
  crossfade. The live-updating log view and stats sparklines must remain usable with motion reduced.
- Respect the OS: theme, accent colour where the platform exposes one, reduce-transparency,
  increase-contrast, and the system text size.
