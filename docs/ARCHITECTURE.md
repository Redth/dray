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
| `ssh://` | Relay process — `ssh host -- docker system dial-stdio` (§2.4a) |
| `wsl://` | Relay process — `wsl.exe -d <distro> … dial-stdio` (§2.4a). Dray's own scheme |

The last two have no address on this machine, so they are reached by running a program that already
has the socket and speaking HTTP over its stdio. One transport serves both; §2.4a is the detail.

For `ssh://` this means the system `ssh` binary, so the user's `~/.ssh/config`, agent, jump hosts and
hardware keys work exactly as they already do. **Do not reimplement SSH config parsing.**

### 2.3 Contexts and hosts

Read `~/.docker/config.json` for `currentContext` and `~/.docker/contexts/meta/*/meta.json` for the
rest, honouring `DOCKER_HOST` and `DOCKER_CONTEXT` overrides. Contexts appear in the sidebar host
picker alongside any Dray-specific profiles. Every host carries its own connection state machine —
`Disconnected → Connecting → Connected → Degraded → Unreachable` — surfaced in the picker, so a dead
SSH host degrades one entry rather than hanging the app.

### 2.4 Windows: two engines, and a pipe name that means nothing

Windows is the only platform where "connect to the local engine" is an ambiguous instruction, and
the ambiguity is not cosmetic — the two answers do not share images, containers, volumes or
networks.

**Windows containers** run on this machine's own kernel through the Host Compute Service. They need
no WSL2 and no Linux VM — but Windows ships the platform, not a daemon. The `Containers` optional
feature is what makes containers possible; `dockerd.exe` is a separate install, and has been since
Microsoft retired the `DockerMsftProvider` package in 2023. The documented routes now are Docker
Desktop in Windows-container mode, Microsoft's `install-docker-ce.ps1` (which enables the feature and
installs Moby in one step), Mirantis Container Runtime, or containerd.

Isolation differs by SKU, which matters because it decides whether pause works. On Windows Server,
process isolation is the default and Hyper-V is needed only for `--isolation=hyperv`. On Windows 10
and 11 — Pro or Enterprise, never Home — Hyper-V is a prerequisite and containers get Hyper-V
isolation by default. Dray does not encode any of this: it reads `/info`'s `Isolation` and lets the
answer decide.

**Linux containers** need a Linux kernel, so they run in a virtual machine. Every product does the
same thing here and there is no clever alternative to copy: Docker Desktop uses WSL2, Rancher
Desktop uses WSL2, and `podman machine` uses WSL2 by default with Hyper-V as its other provider.

**The pipe name does not say which.** `//./pipe/docker_engine` is served by a native Windows
`dockerd`, by Docker Desktop in whichever mode it is switched to, *and* by podman's machine, which
forwards its Linux API to that exact name. So discovery offers the pipe and `/info`'s `OSType`
settles what was behind it. That is why `ContainerOs` is a probed capability rather than something
inferred from the endpoint, and why `Unknown` is a real value that must never collapse into Linux.

Dray installs none of this. It is a client (§2.1), so what it owes the user is an honest account of
the choice — which is what the first-run state says when nothing is found.

#### What actually differs under a Windows engine

Each of these is a branch on `RuntimeCapabilities.ContainerOs`, and each fails quietly rather than
loudly if it is missed:

| | Linux | Windows |
|---|---|---|
| Paths | `/etc/nginx` | `C:\inetpub`, and never a forward slash |
| Shell | `bash`, `ash`, `sh` | `pwsh.exe`, `powershell.exe`, `cmd.exe` |
| Directory listing | `ls -la`, parsed | archive only — `dir` is localised and unparseable |
| Exit codes | `128 + signal`; 137 is an OOM kill | NTSTATUS; 137 means nothing |
| CPU counter | nanoseconds | **100-nanosecond intervals**, same field, no marker |
| Memory | `usage` less `inactive_file` | `privateworkingset`; no limit reported |
| Process count | `pids_stats.current` | absent — `num_procs` is the CPU count, not a process count |
| Block I/O | `blkio_stats` | `storage_stats` |
| Default network driver | `bridge` | `nat` — `bridge` is rejected |
| Pause | always | Hyper-V isolation only |

The CPU one is the most dangerous, because reading Windows ticks as nanoseconds under-reports by a
factor of a hundred: a pegged container draws as a flat 1% rather than failing.

The volume browser needs an image to mount a volume into, and there is no Windows equivalent of
alpine to fall back on — a Windows base image's tag has to match the host's build under process
isolation, so no single reference is right on every machine. Dray borrows a local image and, where
there is none, says what to pull instead of pulling a guess that would cost a gigabyte and then
refuse to run.

### 2.4a Reaching an engine that has no address here

A WSL2 distro running its own `dockerd` publishes a Unix socket that exists only inside that distro.
Windows has no path to it, and the Docker CLI has no scheme for it either.

Dray reaches it the way the Docker CLI reaches `ssh://`: by running a program that already has the
socket and speaking HTTP over its standard input and output. `docker system dial-stdio` is the
CLI's own hidden subcommand for exactly this — it connects to whatever `DOCKER_HOST` resolves to on
its side and copies bytes to its stdio.

```
wsl.exe -d Ubuntu -u root --exec docker system dial-stdio
ssh -o BatchMode=yes host -- docker system dial-stdio
```

One transport (`StdioRelayHandlerFactory`) serves both, which is why `ssh://` arrived with this work
rather than as a project of its own. It is built on `ManagedHandler` — the same hand-rolled HTTP/1.1
client the Unix-socket and named-pipe transports use — rather than on `SocketsHttpHandler`, because
exec and attach upgrade the connection and hand back the raw stream, which a pooling handler will
not do.

Three details are load-bearing:

- **`--exec`** runs the program directly instead of through a login shell. Not an optimisation: a
  login shell prints whatever the user's `.profile` prints, and those bytes land in the middle of an
  HTTP response. Anyone with a banner or a version manager's greeting would see connections fail to
  parse, intermittently, with no clue why.
- **`-u root`**, because `/var/run/docker.sock` is mode 660 and root-owned. A user who has not added
  themselves to the `docker` group would otherwise get a permission error that reads as the engine
  being absent.
- **stderr is drained and kept.** A pipe nobody reads fills, and a relay blocked writing to stderr
  stops relaying — the connection would hang with no explanation anywhere. It is also where the
  explanation lives: a distro with no `docker` binary produces an empty stream on stdout and the
  reason on stderr.

**The cost is one process per connection.** The handler does not pool, so every request not already
on an open connection starts a relay. It is the same bargain the Docker CLI makes for `ssh://`. It
matters less than it sounds like it should, because the expensive things here are the long-lived
ones — the event stream, a log follow, a stats stream — and each of those is a single connection
held open for as long as it is watched.

**Verified.** The transport was exercised end to end against a real engine on macOS, with `docker
system dial-stdio` standing in for the WSL launcher — the mechanism is identical and only the
program differs. Version and info probes, container and image lists, a hijacked exec with its exit
code, the archive endpoint streaming a tar, and a held-open stats stream all work through it.

Distro enumeration is `wsl.exe --list --verbose` with `WSL_UTF8=1`, which is the only way to get
UTF-8 out of it; builds that predate that variable write UTF-16LE, which arrives with a NUL after
every character and is recovered by dropping them. The table is localised — both the header and the
state word — so the parser matches on shape rather than on words: the name is the first column, the
version is the last and is a digit in every locale, and the state is whatever lies between. Reading
the name backwards from the version instead is wrong, and was: French reports a running distro as
"En cours", which made every distro on a French machine parse as `Ubuntu En`.

Distros belonging to a desktop product — `docker-desktop`, `rancher-desktop`, `podman-machine-*` —
are not offered. The storage-only ones run no engine at all, and the rest are already published on a
named pipe, so offering both routes would list one engine twice in the picker whose entire job is to
say which engine you are looking at.

> **Still to do.** Show which distro a container's bind mounts actually live in. The `/mnt/c` versus
> native-filesystem distinction is the top cause of "why is this so slow" on Windows and no GUI
> surfaces it today.

### 2.5 Compose

There is no viable .NET Compose library, and reimplementing the Compose spec is a trap. Dray shells
out to `docker compose` with `--progress json` / `--format json` and parses the stream, while reading
compose files with YamlDotNet for the editor, validation, and the service graph.

Running stacks are discovered from the `com.docker.compose.project`, `.service`, and
`.project.config_files` labels on containers, so a stack started from a terminal appears in Dray
automatically. User-added compose files are tracked separately so a stack that is *down* is still
visible — the thing Docker Desktop gets wrong.

### 2.6 What a real non-Docker engine actually does

Verified against podman 6.0.2 speaking API 1.44. These are the concrete gaps behind
`RuntimeCapabilities`, not hypotheticals.

- **Extra event verbs.** Podman emits `init`, `sync` and `cleanup`, which Docker does not. Anything
  consuming the stream must ignore unknown actions rather than assume a closed set.
- **`restart` is not causally ordered.** A restart was observed emitting `restart`, `start`, and
  *then* the `die` belonging to the instance that was replaced. Applying that `die` would leave a
  running container reading "Exited 137", so the store reconciles a restart from the API instead of
  trusting the stream for that one sequence.
- **Exit codes arrive twice**, as `exitCode` and `containerExitCode`. Reading either works.
- **Compose labels ride along on events**, so a stack is identifiable without a fetch.
- **No `system df`.** `Docker.DotNet.Enhanced` does not expose it either, so the disk-usage
  breakdown needs a direct call in phase 4.
- **Stats and BuildKit are absent or partial**, which is why `SupportsStats` and `SupportsBuildKit`
  are off for this flavor rather than assumed.

A leftover from an uninstalled engine is a recurring hazard on a real machine, not an edge case.
This one had `/var/run/docker.sock` dangling at a socket that no longer existed, *and*
`credsStore: osxkeychain` in `config.json` pointing at
`/Applications/OrbStack.app/.../docker-credential-osxkeychain` after OrbStack had been removed —
which makes every registry operation fail. Phase 6 must treat a missing credential helper as a
condition to explain, not an exception to propagate.

---

### 2.7 Two clients, on purpose

`Dray.Docker` talks to the engine through a typed client (`Docker.DotNet.Enhanced`) for everything
the client models, and through a small raw HTTP client (`DockerRawApi`) for two things it does not:

- **The Inspect tab's raw JSON.** A typed client models the fields it knows and drops the rest.
  Re-serialising its output would present Dray's vocabulary as the engine's, and the fields worth
  seeing when something is behaving strangely are precisely the ones the client has never heard of.
- **`system df`.** No typed binding exists. Without it the dashboard reported zeroes, which read as
  measurements rather than as the absence of one.

The raw client handles GET, returns text, and leaves every modelled call alone. It is not a second
implementation of the engine API and must not become one.

**Every raw path carries the API version.** Podman serves two different APIs on the same socket:
`/v1.44/system/df` returns the Docker-compatible `{"Images": [...]}`, and `/system/df` returns
podman's own `{"ImageUsage": {"Items": [...]}}`. An unversioned request therefore does not fail —
it deserialises cleanly into a valid-looking object full of zeroes. This cost an afternoon; the
version prefix is applied centrally so it cannot be forgotten per-call.

---

### 2.8 Reading a volume without running anything

The Engine API exposes storage only through containers. There is no endpoint that reads a volume,
so browsing one means mounting it into a container — which is the approach every competitor either
skips or implements by running a shell inside a helper image.

Dray creates the helper and **never starts it**. The archive endpoint (`GET/PUT /archive`) operates
on a container's filesystem including its mounts, and does not require the container to have run.
Verified against podman 6.0.2: a container created and never started serves both reads and writes
of a mounted volume's contents.

That buys three things:

- **Nothing is executed.** Browsing a volume cannot run code, in the helper or anywhere else.
- **The image does not matter.** No shell, no `ls`, no working entrypoint — so the helper is built
  from an image already on the host and opening a volume does not quietly pull from a registry.
  A `busybox` or `alpine` is preferred over the user's own images, matched on the *repository* and
  not the whole reference: `library/nginx:alpine` contains "alpine" and is somebody's web server.
- **Removal is safe.** The helper is removed with `RemoveVolumes: false`, emphatically — it exists
  to expose the volume and must never take it with it.

Helpers carry a `codes.redth.dray.helper` label and are:

- **excluded from the container list**, because a container the user did not create and cannot
  explain is one they will reasonably try to delete;
- **excluded from a volume's "used by"**, because otherwise looking inside a volume would report it
  as in use — the exact fact someone opens that screen to check;
- **swept at connect**, because a Dray that was killed rather than closed never reaches disposal,
  and the helpers otherwise accumulate one per volume ever opened.

Listing a directory is the same problem as §2.6's: the two engines disagree, and neither documents
it. Docker roots a directory tar at the requested directory's name (`etc/hosts` for `/etc`); podman
returns `/` and `/hosts`. Assuming either shape silently yields an **empty directory** rather than
an error, so the root prefix is read from the tar's own first entry.

---

### 2.9 CPU percentage is not computed the documented way

Docker's published formula for container CPU is:

```
cpuDelta    = cpu_stats.cpu_usage.total_usage - precpu_stats.cpu_usage.total_usage
systemDelta = cpu_stats.system_cpu_usage      - precpu_stats.system_cpu_usage
percent     = cpuDelta / systemDelta * online_cpus * 100
```

Dray does not use it, because the multiply is only correct if `system_cpu_usage` is summed across
cores. Docker's is. **Podman's is not** — it advances at roughly one CPU-second per wall second
however many cores exist, so the multiply over-reports by exactly the core count.

Measured on a four-core host, against a container running a single `while true; do :; done`:

| | reading |
|---|---|
| ground truth (`cpuDelta` ÷ wall time) | **97.2%** |
| `cpuDelta / systemDelta * 100` | 99.5% |
| Docker's formula, with `* online_cpus` | **398.1%** |

So Dray divides by **elapsed wall time** instead:

```
percent = cpuDelta_ns / (elapsed_seconds * 1e9) * 100
```

That needs no agreement between engines about what a counter means, and it is the definition of the
number anyway: CPU-seconds consumed per second of real time, where 100% is one core fully used and a
container on two of four cores reads 200% — which is what `docker stats` shows and what people
cross-check against.

The interval comes from the engine's own `read` and `preread` timestamps, not from when Dray
received the two samples: a sample delayed in a queue must not read as a busier container.

`CpuUsage.Percent` in `Dray.Core` holds this, with the measured case as a test.

---

### 2.10 One thing the event stream does not report

`docs/ARCHITECTURE.md` §3 says the event stream is the source of truth and nothing writes to the
store speculatively. There is exactly one exception, and it is not a design choice — it is an engine
gap.

**Podman emits no event for a rename.** Docker emits `container rename` with the new name in the
actor attributes; podman emits nothing at all, verified by watching `/events` across a rename and
capturing zero events. A store waiting for that event shows the old name until something unrelated
happens to refresh the list.

So `EngineManager.RenameAsync` writes the new name to the store itself once the engine has returned
success. Nothing is being guessed: the user typed the name, the engine accepted it, and the write
records a fact rather than predicting one. On Docker the matching event arrives afterwards and
applying it is idempotent — `EntityStore.Rename` ignores a rename to the name already held.

This is the whole exception. Every other state change still comes from the stream.

---

### 2.11 An engine that is not Docker-shaped at all

§2.6 catalogues an engine that *impersonates* Docker and gets details wrong. Apple's `container`
does not impersonate anything. Verified against version 1.3.0 on macOS 26.

There is no HTTP API, no compatibility socket, and no shared field name. The only machine-readable
surface is the CLI with `--format json`, so `Dray.Apple` drives processes. `EndpointScheme` gained
`AppleContainer` because everything above the seam addresses an engine by endpoint, and a second
engine should not need a second way of being addressed. A composite factory dispatches on the
scheme; `DockerContextReader` finds the CLI by walking `PATH` and offers the host last, so a machine
with both opens on the Docker-compatible engine where the user's containers already are.

**Four claims in the first version of this section were wrong**, and it is worth recording why:
they were made by reading the subcommand list rather than by running anything. `container` has a
`cp` and a full `volume` subcommand, and `exec -i` streams in both directions perfectly well — so
shells, volume management and writing files into a container all work here, and an earlier
`AppleRuntime` refused all three. The capability system exists precisely to stop a UI offering what
an engine cannot do; it is no protection at all when the capability itself is a guess. Every flag
below is now set from something that was executed.

**Three absences are real, and are reported rather than filled in.**

- **No event stream.** There is no `events` subcommand. `SupportsEvents` is false and
  `RuntimeEventPump` polls — see §3.1.
- **No exit codes, anywhere.** `ls` and `inspect` both report only `state: "stopped"`. A container
  that ran `exit 7` and one that finished cleanly are indistinguishable. `ExitCode` is therefore
  always null on this engine. Writing a zero would be worse than writing nothing: "Exited (0)" is a
  claim, and it would be wrong for exactly the containers someone is investigating.
- **No health checks.** The concept does not exist.

**What was measured, because none of it is guessable:**

- `container stats --no-stream` takes about **2.2 seconds** per call. The sampler waits out the
  remainder of a 2.5-second period rather than adding a fixed delay, or the cadence compounds.
- `cpuUsageUsec` is cumulative with no previous sample, and updates coarsely — one saturated core
  reads between 65% and 105% across consecutive samples. `CpuUsage.Percent` needed no change:
  dividing by elapsed wall time (§2.9) works on an engine that shares no counter semantics with
  Docker at all, which is the second time that decision has paid for itself.
- `container logs` **merges the container's stderr into its own stdout** — verified by discarding
  the CLI's stderr and watching a line written to the container's stderr still arrive. So every line
  is `StdOut`, and the CLI's own stderr is dropped rather than labelled as container output. There
  is no timestamps flag either, so `LogLine.Timestamp` is null rather than stamped with the moment
  Dray read the line.
- **A container's id is its name.** There is no 64-hex id. `ContainerSummary.HasDistinctId` exists
  so the UI does not render the name with its last few characters cut off and call it a short id.
- The image descriptor's `size` is the **manifest's** size — 9218 bytes for alpine. Reported as
  unreported rather than as a nine-kilobyte image.

**What is genuinely missing here** is pause, rename, manageable networks, log metadata, and any
access to a *stopped* container's filesystem — that last one measured: `cp` and `exec` both refuse a
container that is not running. `RuntimeCapabilities` carries a flag for each and the UI reads them,
so `ContainerActions.For` filters pause, Rename is absent, the log toolbar drops two toggles wired
to nothing, and the Networks page explains itself rather than showing an empty list that reads as
"none yet".

**Two more things this engine does that Docker does not have to think about.**

- **`container cp` into a mounted volume silently does nothing.** It returns exit code 0 and writes
  no file — verified by copying one in, getting success, and finding the directory still empty.
  `WriteFileAsync` therefore pipes bytes into `cat` inside the container instead, which works for
  volumes and ordinary paths alike. A file editor that discards a save without saying so is the
  worst failure this application could have, and this is the second silent-success bug this engine
  has produced (see the version-prefix one in §2.7).
- **A volume attaches to one virtual machine at a time.** A volume here is an ext4 disk image, so a
  volume a running container holds cannot also be opened for browsing — which Docker allows. The
  engine reports it as "The storage device attachment is invalid", a sentence about nothing the user
  did, so Dray says which container is holding it instead.

The principle is the one `ContainerAction.cs` already states for state filtering: *an action offered
in the wrong state is either a no-op the user does not understand or an error the engine has to
reject, and it teaches the user the UI is guessing.* A capability the engine lacks is the same
mistake with a different cause.

**Not one page was rewritten to add this engine.** That is the claim the seam was drawn to support,
and it now has a second implementation behind it rather than an argument.

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

### 3.1 When there is no event stream

Apple's `container` (§2.11) has no `events` subcommand, so `SupportsEvents` is false and the pump
falls back to listing on an interval. Two things about that fallback are load-bearing.

**It diffs; it does not reset.** `EntityStore.Reset` clears every pending action and re-announces
every row, so calling it twice a second would flash the list and fire the change highlight
constantly — a signal that exists to show what just happened would come to mean nothing. Instead
each poll compares row by row and writes only what actually differs, so an idle engine produces zero
store changes and the UI does not repaint at all.

**That comparison cannot be `==`.** `ContainerSummary` is a record and `Ports` is a list, so two
summaries built from two identical engine responses compare unequal *by reference*, every time. A
poll loop using record equality would therefore find that everything changed on every tick — which
is precisely the flashing the diff was meant to avoid, arriving through the back door.
`ContainerSummary.SameAs` compares the fields a row renders, and a test asserts the trap by handing
the pump fresh instances of unchanged data.

The fallback is worse than a stream and is meant to look it: a change is noticed up to one interval
late, and a container created and destroyed between two polls is never seen. The Hosts page says so
under "Event stream" rather than leaving the user to notice.

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

- **Dray invents no secret store.** Registry credentials go through the Docker credential helper
  protocol so Dray and the CLI share one store; SSH keys stay with `ssh` and the agent; TLS material
  stays where the context put it. Full design, including the resolution order and the
  missing-helper failure mode, is in [`docs/CREDENTIALS.md`](CREDENTIALS.md).
- A named-but-missing helper is a supported state, not an exception — an uninstalled engine takes
  its bundled helper with it and leaves `credsStore` pointing at nothing, which breaks every
  registry operation including anonymous pulls.
- Dray's own secrets, if a feature ever needs one, go to Keychain / Credential Manager / libsecret.
  Note Sherpa's finding that ad-hoc Debug signing rotates the macOS code signature each rebuild and
  invalidates Keychain items — Debug builds need a fallback.
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
