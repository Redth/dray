# Architecture

## 1. Shape

```
Dray.sln
├── src/
│   ├── Dray.Core/            net10.0    domain, state store, services, nav manifest — no UI, no Docker
│   ├── Dray.Docker/          net10.0    IContainerRuntime implemented over the Docker Engine API
│   ├── Dray.Ui/              net10.0    Razor Class Library — ALL Blazor pages, components, wwwroot
│   ├── Dray.App/             MAUI       shared app model + the Windows head
│   ├── Dray.MacOS/           net10.0-macos      AppKit head
│   ├── Dray.LinuxGtk/        net10.0            GTK4 head
│   └── Dray.Cli/             net10.0    optional `dray` CLI over the same Core
└── tests/
    ├── Dray.Core.Tests/
    └── Dray.Docker.Tests/    integration, gated on a reachable engine
```

`Dray.Core` never references Docker or a UI framework. `Dray.Docker` is one implementation of the
runtime interfaces. This is the seam that lets Apple's `container` framework land later as
`Dray.AppleContainer` without touching a page.

### 1.1 Three deliberate departures from MAUI.Sherpa

Sherpa is the reference for how to stand these heads up. Three of its structural choices should not
be repeated, each backed by something visible in that repo today.

**Shared UI is a Razor Class Library, not MSBuild globs.** Sherpa's Linux head pulls the shared UI in
with `<Compile Include="..\MauiSherpa\**\*.cs" Exclude="...Platforms\**;...WindowsTitleBarManager.cs;
...WindowsCertificateService.cs" />` plus a parallel `<RazorComponent Include>`, while the macOS head
uses a *different* set of globs and `<BundleResource Include="..\MauiSherpa\wwwroot\**">`. Three heads,
three hand-maintained exclusion lists, guaranteed drift — and static assets that need a bespoke
`BundleResource` rule per platform. An RCL gives all three heads the same components and flows
`wwwroot` through the standard static-web-assets pipeline for free.

**Zero network dependencies at boot.** Sherpa's `index.html` pulls Font Awesome from cdnjs, xterm.js
and its fit addon from jsdelivr, and highlight.js plus two theme stylesheets from jsdelivr. A desktop
tool for managing local infrastructure must work on a plane, behind a corporate proxy, and inside a
strict WebView CSP. Everything is vendored into the RCL.

**One dialog, one place.** Sherpa's own AGENTS.md states: *"Each Blazor page defines its own
`.modal-overlay`, `.modal`, ... CSS in a `<style>` block. These are NOT global styles. New pages MUST
include modal CSS or modals will render inline without overlay/positioning."* That is a defect
promoted to a documented convention. Dray has one `<Dialog>` component over the native `<dialog>`
element with focus trap, restore, and `Escape` built in, styled globally.

---

## 2. The runtime layer

### 2.1 Client, not host

Dray drives an engine that is already running. It does not ship a runtime, provision a VM, or manage a
WSL2 distro's lifecycle. It connects to Docker Desktop, Colima, OrbStack, Rancher Desktop, Podman's
Docker-compatible socket, a bare `dockerd`, a WSL2 distro, or a remote host — through the same API.

```csharp
public interface IContainerRuntime
{
    RuntimeCapabilities Capabilities { get; }      // compose? buildx? swarm? stats?
    IContainerApi Containers { get; }
    IImageApi      Images { get; }
    IVolumeApi     Volumes { get; }
    INetworkApi    Networks { get; }
    ISystemApi     System { get; }
    IAsyncEnumerable<RuntimeEvent> WatchAsync(CancellationToken ct);
}
```

`RuntimeCapabilities` matters more than it looks: Podman's Docker-compatible socket, a rootless
daemon, and an old Synology engine all answer a different subset of the API. Screens ask the
capability rather than assuming, and degrade to a clear explanation instead of an exception dialog.

### 2.2 Transport

`Docker.DotNet.Enhanced` (the maintained `testcontainers` fork of the dormant `dotnet/Docker.DotNet`;
the upstream package has had no development for years) plus `Docker.DotNet.Enhanced.X509` for
TLS-authenticated remote daemons.

| Context scheme | Transport |
|---|---|
| `unix://` | Unix domain socket — macOS, Linux |
| `npipe://` | Named pipe — `//./pipe/dockerDesktopLinuxEngine`, `//./pipe/docker_engine` |
| `tcp://` + TLS | `X509` handler using the context's own cert material |
| `ssh://` | SSH tunnel to the remote socket |

For `ssh://`, prefer shelling out to the system `ssh` binary so the user's `~/.ssh/config`, agent,
jump hosts, and hardware keys work exactly as they already do. An in-process SSH.NET tunnel is the
fallback where no `ssh` binary exists. **Do not reimplement SSH config parsing.**

### 2.3 Contexts and hosts

Read `~/.docker/config.json` for `currentContext` and `~/.docker/contexts/meta/*/meta.json` for the
rest, honouring `DOCKER_HOST` and `DOCKER_CONTEXT` overrides. Contexts appear in the sidebar host
picker alongside any Dray-specific profiles. Every host carries its own connection state machine —
`Disconnected → Connecting → Connected → Degraded → Unreachable` — surfaced in the picker, so a dead
SSH host degrades one entry rather than hanging the app.

### 2.4 WSL2 on Windows

Enumerate distros with `wsl.exe --list --verbose --quiet`, detect an engine socket inside each, and
offer them as hosts. Docker Desktop's WSL integration is exposed through
`//./pipe/dockerDesktopLinuxEngine`; a distro running its own `dockerd` is reached by running the
Docker CLI inside that distro or by relaying its Unix socket. Show which distro a container's bind
mounts actually live in — the `/mnt/c` versus native-filesystem distinction is the top cause of
"why is this so slow" on Windows, and no GUI surfaces it today.

### 2.5 Compose

There is no viable .NET Compose library, and reimplementing the Compose spec is a trap. Dray shells
out to `docker compose` with `--progress json` / `--format json` and parses the stream, while reading
compose files with YamlDotNet for the editor, validation, and the service graph.

Running stacks are discovered from the `com.docker.compose.project`, `.service`, and
`.project.config_files` labels on containers, so a stack started from a terminal appears in Dray
automatically. User-added compose files are tracked separately so a stack that is *down* is still
visible — the thing Docker Desktop gets wrong.

---

## 3. State: the event stream is the source of truth

The core rule from PRODUCT.md, made concrete.

```
Docker /events  ──▶  RuntimeEventPump  ──▶  EntityStore  ──▶  Blazor (observable)
                          │                     ▲
   initial list ──────────┘                     │
   /containers/{id}/stats (visible rows only) ──┘
```

- One `/events` subscription per connected host, reconnecting with backoff.
- A cold list call on connect seeds the store; after that, events mutate it. No list is ever re-fetched
  wholesale on a timer.
- Stats are expensive: subscribe **only** for rows currently visible plus the open detail pane, into a
  fixed ring buffer for sparklines. Unsubscribe on scroll-out.
- Log and exec streams are per-view and disposed with the view. Docker's multiplexed stdout/stderr
  framing is decoded in `Dray.Docker`, not in the UI.

Blazor components subscribe to the store, not to a service, and re-render only the rows that changed.
A 400-container list must not re-render because one container stopped.

---

## 4. The native ↔ web seam

Sherpa's `IToolbarService` carries twenty members and stringly-typed action IDs, and its sidebar items
are declared twice — once in `MacOSApp.cs` as `MacOSSidebarItem` records and again in
`MainLayout.razor` as `<NavLink>` markup. Two lists that must stay identical and nothing enforces it.

Worse, the *same* routing table (`AppleRoutes` / `GoogleRoutes`, deciding which routes show an
identity picker) is copy-pasted verbatim into all three platform managers. Adding a route means
remembering three files, and nothing fails if you forget.

The mechanics of Sherpa's shell are sound and Dray lifts them wholesale — see `docs/NATIVE-SHELL.md`
for what to copy and the five places the pattern frays. What changes is the direction of control.
Native chrome becomes a **projection of page state**:

```csharp
// Dray.Core — one manifest, consumed by native sidebars and the web fallback nav alike
public sealed record NavNode(string Route, string Title, IconRef Icon, ...);
public static class NavigationManifest { public static IReadOnlyList<NavNode> Nodes { get; } }

// A page declares its chrome the way it declares <PageTitle>
<PageChrome Title="Containers"
            Search="Filter containers"
            Actions="@(new[]{ ChromeAction.Primary("run", "Run…", Icons.Play) })" />
```

The host projects `PageChrome` onto `NSToolbar`, `CommandBar`, or `AdwHeaderBar`. Pages never call
"add a toolbar button." Adding a nav entry is one line in one file and every platform picks it up.

A chrome manager's constructor takes `IShellBridge` and the current `PageChrome`, and nothing else.
Sherpa's `WindowsTitleBarManager` grew to 780 lines and seven injected services — including
`IAppleIdentityService` and `IGoogleIdentityService` — which is precisely why the same feature logic
then had to be written three times.

`IShellBridge` covers the rest of the native surface — `ConfirmDestructiveAsync`, `PickFileAsync`,
`SaveFileAsync`, `RevealInFileManagerAsync`, `Notify`, `SetBadge`, `OpenExternal` — each head
implementing it with the platform's real controls.

**Theme flows the other way**, and it must be synchronous: the head resolves the platform's window
background and accent colour and pushes them into CSS custom properties before first paint, then
again on every appearance change. That is what makes the seam invisible (DESIGN.md §9.1).

---

## 5. Security

- **Never write registry credentials to `config.json` in plaintext.** Use the platform credential
  helpers (`docker-credential-osxkeychain`, `-wincred`, `-secretservice`) over their documented
  stdin/stdout protocol, so Dray and the Docker CLI share one credential store.
- Dray's own secrets (remote host profiles, SSH passphrase hints) go to macOS Keychain / Windows
  Credential Manager / libsecret. Note Sherpa's finding that ad-hoc Debug signing rotates the macOS
  code signature each rebuild and invalidates Keychain items — Debug builds need a file fallback.
- **The Docker socket is root-equivalent.** Any action that would grant escalated access — a
  privileged container, a bind mount of `/`, mounting the socket itself — is called out in the UI at
  the point of the action, not buried in an advanced tab.
- Container output is untrusted. Log lines, labels, image names, and inspect JSON are rendered as
  text, never as markup. ANSI is parsed into styles, not into HTML from the container.

---

## 6. Distribution

| Platform | Format |
|---|---|
| macOS | Signed + notarized `.app` in a `.dmg`; Sparkle-style appcast for updates; Homebrew cask |
| Windows | MSIX + winget; unpackaged installer fallback |
| Linux | Flatpak (primary), AppImage, `.deb` |

Sherpa's build already solves the macOS and Linux packaging targets and its
`build/DedupeNativeReferences.targets` workaround for the Apple SDK's parallel `InstallNameTool`
crash on duplicate native libraries — import both patterns rather than rediscovering them.
