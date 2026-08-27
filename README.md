# Dray

A native desktop app for managing Docker — containers, images, volumes, networks, registries and
compose stacks — that treats a remote host exactly like the local one.

> **Status: early.** Phase 0 (the design-system and build gates) is in place. The app itself is not
> yet built. See [`docs/ROADMAP.md`](docs/ROADMAP.md).

## What it is

Dray drives the Docker engine you already have — Docker Desktop, Colima, OrbStack, Rancher Desktop,
Podman's compatible socket, a bare `dockerd`, a WSL2 distro, or a remote host over SSH. It does not
ship a runtime and does not provision a VM.

Every competitor is a front end for *this machine's* daemon, with remote hosts bolted on or absent.
Dray's host switcher is the first control in the sidebar, and every screen behind it works the same
whether the engine is a Unix socket, a WSL2 distro, or an SSH tunnel to a box in a closet.

- **macOS** — AppKit shell, native sidebar and toolbar
- **Windows** — WinUI shell, WSL2 distro awareness
- **Linux** — GTK4 / libadwaita shell

Content is one Blazor codebase shared by all three heads. The seam between native chrome and the
WebView is meant to be invisible.

## Building

Requires the .NET 10 SDK (see `global.json`) and Node 22+ for the design-system build scripts.

```bash
node build/gen-tokens.mjs && dotnet build
```

The token files are generated and gitignored, so the generator runs first.

## Documentation

| Doc | Contents |
|---|---|
| [`PRODUCT.md`](PRODUCT.md) | Users, purpose, positioning, design principles |
| [`DESIGN.md`](DESIGN.md) | The visual contract — colour, type, components, native integration |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Projects, runtime seam, state flow |
| [`docs/NATIVE-SHELL.md`](docs/NATIVE-SHELL.md) | Native shell patterns and their failure modes |
| [`docs/CREDENTIALS.md`](docs/CREDENTIALS.md) | How secrets are handled — and why Dray stores none |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | Phases with demos and exit criteria |
| [`AGENTS.md`](AGENTS.md) | Contributor and agent rules |

## Licence

MIT
