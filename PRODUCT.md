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

A meaningful minority also keep a home server, a NAS, or a VPS with a Docker daemon on it, reachable
over SSH. Today they manage it by SSHing in and typing `docker ps`. That is the same job, one hop away.

The job to be done: **see the true state of my containers and change it, without leaving the keyboard
or losing the thread of what I was actually doing.**

Success is that a developer who has Docker Desktop installed opens Dray instead, out of preference,
because it answers the question faster.

## Product Purpose

Dray is a desktop application for managing Docker: containers, images, volumes, networks, registries,
and compose stacks — locally and on any host reachable through a Docker context.

It manages the engine you already have. Dray does not ship a runtime, does not provision a VM, and
does not compete with Docker Desktop, Colima, OrbStack, Rancher Desktop, or a bare `dockerd`. It
connects to whichever of those is running, and to WSL2 distros on Windows, and to remote hosts over
SSH. Runtime ownership is deliberately behind an interface seam (`IContainerRuntime`) so that Apple's
`container` framework and, if it ever earns its place, machine lifecycle management can land later
without a rewrite.

## Positioning

**The Docker GUI that treats a remote host exactly like the local one.**

Every competitor is a front end for the daemon on this machine, with remote hosts bolted on or absent.
Dray's host switcher is the first control in the sidebar, and every screen behind it works the same
whether the engine is a Unix socket, a WSL2 distro, or an SSH tunnel to a box in a closet.

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
