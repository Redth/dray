# Kubernetes

Dray shows the local Kubernetes cluster beside the Docker engine it runs on, and can create one on
that engine when there is none. This document is the contract for that work: what it is for, what
it deliberately is not, and the seams it needs.

**Status: built, and run against kind.** `Dray.Kubernetes`, the four pages, the diagnosis and the
kind helper are in, and every behaviour below marked *measured* was run — first in the K0 spike
(§10), then again through the app itself on kind 0.33.0 / Kubernetes 1.37.0 on Docker Desktop's
engine: the web head in both themes and the macOS head. `Dray.Kubernetes.Tests` holds seven of the
claims against a live cluster. What has still not been reached — OrbStack, Docker Desktop's own
Kubernetes, k3d, minikube, Rancher Desktop, exec credential plugins, and the Windows head — is marked
*read, not run*, and stays a hypothesis until something executes it. Two claims in the first draft
were wrong, and both were caught only by running them (§10.1).

---

## 1. Four decisions

These are settled. Changing one is a product decision, not an implementation detail.

1. **Kubernetes is in v1.** It lands as Phase 6.5 in `docs/ROADMAP.md`, before Ship.
2. **Two pickers.** A Docker engine and a cluster are independent: someone can be looking at
   containers on `desktop-linux` while their pods run in a kind cluster, or in no cluster at all.
   The engine picker is the window's top-left control on every page (DESIGN.md §5). The cluster
   picker is a menu in the Kubernetes pages' toolbar, beside the namespace filter: the engine scopes
   every page and a cluster only those four, and the strip above the macOS sidebar has room for one
   control, not two. They are *paired* only when the pairing is measured (§4) — and selecting one
   never moves the other.
3. **Local clusters first.** v1 connects only to clusters measured as local (§3). Remote contexts
   are listed with a plain reason and are not connectable. Remote support is Phase 9 material.
4. **Dray can create a kind cluster** on the engine it is connected to (§7). A kind cluster is a
   set of containers on the user's own engine — a workload, closer to a compose stack than to a
   runtime — so this does not cross the line `PRODUCT.md` draws around shipping an engine.

---

## 2. What it is for

The user in `PRODUCT.md` does not administer a cluster. They run one on their laptop — Docker
Desktop's, OrbStack's, Rancher Desktop's, or a kind, k3d or minikube cluster — to try a manifest or
a chart before it goes somewhere that matters. Their question is the same one Dray already answers
for containers, one layer up:

> My pod is `CrashLoopBackOff` / `ImagePullBackOff` / `Pending`. Why?

So the feature is a **lens on your own workloads**, not an admin console. The first screen answers
that question; everything else is one click deeper or absent.

### What it is not

- **Not an engine provisioner.** Dray does not flip Docker Desktop's, OrbStack's or Rancher
  Desktop's Kubernetes switch — those are the engine's own settings, and the empty state says
  where they are. It does not install k3s or create a VM. The kind cluster in §7 is the one thing
  it creates, and it creates it on the engine the user already has.
- **Not Lens, Headlamp or k9s.** No RBAC editors, CRD browsers, Helm catalogues, node
  cordon/drain, multi-cluster dashboards or fleet views. Those tools exist and are good at it.
- **Not a manifest authoring tool in v1.** Apply, edit-in-place and Helm are later, and when they
  come they shell out to `kubectl` and `helm` the way Compose shells out to `docker compose`
  (`docs/ARCHITECTURE.md` §2.5). Reimplementing either is the same trap.

---

## 3. Local, measured

"Local" decides whether v1 connects at all, and later decides how much friction a destructive
action carries. It is measured, never inferred from a context name — `docker-desktop`, `orbstack`,
`kind-*` and `k3d-*` are conventions, and a convention is a guess.

A cluster is **Local** when either holds:

- **The API server resolves to a loopback address.** Resolve the kubeconfig `server` host and
  check the result, rather than matching the string: `kubernetes.docker.internal` is loopback only
  because a hosts-file entry says so. *Measured for kind:* `https://127.0.0.1:63628`. *Read, not
  run:* Docker Desktop, OrbStack, Rancher Desktop, Colima and k3d do the same.
- **Its nodes are containers on a connected engine** — see §4, which is the same measurement.

Everything else is **Remote**. Remote contexts appear on the Hosts page with their reason ("API
server at 10.4.0.12 is not on this machine") and are not offered in the cluster picker.

*Known gap:* minikube with a VM driver (hyperkit, qemu, Hyper-V) publishes its API server on a
private VM subnet, which this rule classifies as Remote. Whether a VM on a host-only network should
count is an open question (§11); it should not be resolved by matching the name `minikube`.

---

## 4. Pairing a cluster with an engine

Pairing is what lets Dray say things no cluster-only tool can (§8). Three signals, strongest first.
All three were measured against kind:

- **The published port.** The kubeconfig `server` port is the host port the node container
  publishes for 6443 — `127.0.0.1:63628->6443/tcp` on the container, `https://127.0.0.1:63628` in
  the kubeconfig. That pairs a cluster with one specific container on one specific engine, with no
  name involved.
- **Node identity.** `spec.providerID` is `kind://docker/dray-spike/dray-spike-control-plane` — the
  provider, the cluster and the container name.
- **Container labels.** `io.x-k8s.kind.cluster=<name>` and `io.x-k8s.kind.role=control-plane`.
  *Read, not run:* k3d labels with `k3d.cluster`, minikube's docker driver with
  `name.minikube.sigs.k8s.io`.

Pairing is what the Containers page and the Hosts page read: it is how Dray knows which node
containers to show under which cluster, and which kind cluster it may delete (§7.3). Selecting a
cluster never changes the engine, nor the other way round.

### Node containers are not the user's containers

A kind node appears on the Containers page as `dray-spike-control-plane`, running a 1.3 GB
`kindest/node` image the user never pulled by name — a container they did not knowingly create and
cannot explain, the same problem `docs/ARCHITECTURE.md` §2.8 solves for volume helpers. So node
containers (`ContainerSummary.ClusterNode`, from the labels above) are out of the plain list and in a
row per cluster above it — "kind cluster dray-spike · 1 node · running · context kind-dray-spike" —
that opens the cluster. Removing the cluster is §7.3's typed action.

---

## 5. What the user sees

A **Kubernetes** group in the sidebar, present while the kubeconfig has a local cluster — connected
or not, so its pages can say why not. `NavNode.RequiresCluster` sits beside `DebugOnly`, and
`NavigationManifest.Visible` filters on it, so the entries live once in the manifest; each head
refills its sidebar only when the group comes or goes, not on every watch event. With no cluster at
all, the Hosts page's Kubernetes section is where making one starts.

**It can be turned off.** Settings has a Kubernetes switch, and a switch per page. Off, Dray does
not read the kubeconfig or connect to a cluster at all — no watches running for someone who has a
kubeconfig and does not want a second thing watching it — and nothing about Kubernetes is in the
window: not the sidebar group, not the Hosts page's section, not the row of node containers, which
go back to being containers in the list. A page turned off leaves the sidebar and the palette and
still opens from a link. Both apply as they are changed, on every head, without a restart.

| Page | Leads with | Actions |
|---|---|---|
| **Workloads** | Deployments, StatefulSets, DaemonSets and Jobs, each with ready x/y and its worst pod's state; pods nested beneath | Scale, restart, delete |
| **Pods** | State and reason, restarts and when, age; the workload and namespace under the name | Logs, shell, forward a port, delete |
| **Services** | Type, ports, and how to reach it from this machine | Forward a port, open in browser |
| **Events** | Warnings first, each linked to its object | — |

Workloads is to Kubernetes what Stacks is to Compose: the unit someone thinks in, with its worst
member setting its state, because a Deployment with one crash-looping replica of three is not
healthy.

**Namespaces.** The default view is every namespace the user can list, with `kube-*` namespaces
behind a toggle. That prefix is reserved for the system by Kubernetes itself, so filtering on it is
a rule, not a guess. kind also creates `local-path-storage`, which is not `kube-*` and is shown.
Namespaces are a filter, never a mode — no page goes blank because the wrong one is selected.

### 5.1 Pod state vocabulary

Now binding, in DESIGN.md §2.4 beside the container table. Same rule: tint + glyph + word,
greyscale-legible, and a test holds every pod state to a glyph of its own.

The input is the STATUS `kubectl get pods` computes — not `status.phase`, which reads `Running` for
a pod whose only container is crash-looping. Users cross-check against kubectl, so Dray reproduces
its algorithm. **Measured:** a C# port of kubectl's `printPod` agreed with `kubectl get pods` on all
six spike pods — Running, CrashLoopBackOff twice, Completed, and two Deployment replicas.

**One deliberate departure: a crash loop does not flicker.** kubectl's STATUS for a crash-looping
pod alternates. Measured on the same two pods within one minute: `Error` and `OOMKilled` in the
moment after each exit, `CrashLoopBackOff` during the back-off between. Rendering that faithfully
would flip a pill between three states on every restart, and the change highlight would fire each
time — a signal about nothing. So **Crash loop** is resolved from the restart count and the last
termination, whichever half of the cycle the pod is in, and the last reason rides in the detail.
Every other row matches kubectl exactly.

| Pod | Word | Glyph | Tone |
|---|---|---|---|
| `Running`, all containers ready | Running | ● | ok |
| `Running`, not all ready | Not ready *x/y* | ◑ | warn |
| `Pending` | Pending | ○ | neutral |
| `ContainerCreating`, `PodInitializing`, `Init:n/m` | Starting | ◐ | warn |
| restarted, and the last exit was a failure | Crash loop · *last reason* | ↯ | danger |
| `ImagePullBackOff`, `ErrImagePull` | Can't pull image | ⤓ | danger |
| `Completed` | Completed | ■ | neutral |
| `Error` or `OOMKilled`, no restarts | Exited *code* · *reason* | ■ | danger |
| `Terminating` | Terminating | ◌ | warn |
| `Evicted` | Evicted | ✕ | danger |
| `Unknown` (node lost) | Node lost | ⚠ | neutral, row dimmed |

Crash loop is `↯` rather than `↻`, which is Restarting, and Not ready is `◑` rather than Starting's
`◐`: §2.4's narrow-width exception drops the word and keeps the glyph, so two states sharing one
would be the same state at that width.

**Kubernetes reports the reason; Dray does not infer it.** Measured: the OOM pod's
`lastState.terminated` carries `exitCode: 137, reason: OOMKilled`; the failing pod's carries
`exitCode: 3, reason: Error`. The reason is used verbatim — "Crash loop · out of memory" — rather
than read back out of the exit code. `ContainerStatusVocabulary.ExplainExitCode` remains the
fallback for a code with no reason attached.

### 5.2 Logs

Per container, reusing `LogView`. Measured against a crash-looping pod:

- **During back-off, the ordinary log already is the dead instance's.** With no container running,
  the kubelet serves the last terminated one: the plain request and `previous=true` returned the
  same lines with the same nanosecond timestamps. So the log opens on what the user needs without
  any special case, and "Previous instance" is offered only once a replacement is running and has
  a predecessor to show. The first draft said to open on the previous instance first; that would
  have shown the same output under a different label.
- **stdout and stderr arrive merged**, with no stream marker — the same situation as Apple's
  `container` (`docs/ARCHITECTURE.md` §2.11). Every line is `StdOut`; stream colouring is not
  offered rather than guessed.
- **Timestamps are the runtime's**, RFC 3339 with nanoseconds, one per line.
- **Follow streams**: one held-open request, a line as each is written.

Workload logs merge their pods the way stack logs merge services — arrival order, a colour key per
pod.

### 5.3 Shell

Reuses `IExecSession` and the terminal. Measured:

- **`v5.channel.k8s.io` negotiates** against 1.37, and is the protocol to use. Its one addition
  matters: a client can **close stdin**, a two-byte frame (`255`, then the stdin channel). Under
  `v4` the same command — anything that reads to end of input — hangs indefinitely.
- **The exit code arrives on the status channel** as a `Status` object: `reason: NonZeroExitCode`,
  with the code in `details.causes[].message`. It is parsed from there, not from the message text.
- **Full duplex works** with separate stdin and stdout streams: three write-then-echo round trips
  over one `cat`, each write issued while a read was pending, as a terminal always has.

Shell detection follows the pod's node OS (`status.nodeInfo.operatingSystem`, measured `linux`), as
`RuntimeCapabilities.ContainerOs` does. A distroless container has no shell, and the page says that
rather than failing on connect. Ephemeral debug containers (`kubectl debug`) are later.

### 5.4 Port forwarding

For local development, "how do I reach it" is the question a Service page exists to answer, so a
forward is a first-class object the user can see and stop — not a side effect of a click.

- Binds **127.0.0.1 only**. Never `0.0.0.0`: a forward is a tunnel into the cluster, and exposing
  it to the network is not something a click should do.
- Uses the remote port locally when it is free, and the next free port otherwise, and shows which.
- Lives as long as Dray does, and is listed where the user can stop it.
- Where the cluster already publishes the Service on this machine — a NodePort or LoadBalancer on
  an engine that maps them to localhost — the page shows that address and no forward is needed.
  Which engines do that is measured, not looked up. kind does not, without extra port mappings.

**Measured:** ten concurrent HTTP requests through one forward in 139 ms, all 200, one WebSocket
per TCP connection; and one TCP connection held for 30 seconds — twenty echoed round trips with a
20-second idle gap in the middle — with nothing lost. The transport is solid. The client's helper
for it is not; §9.2 has the trap.

### 5.5 Destructive actions

Deleting a pod a controller owns is a restart in practice; it gets the ordinary confirmation.
Deleting a bare pod, a workload, or scaling to zero is typed, per `PRODUCT.md`. So is deleting a
cluster (§7). When Remote arrives (Phase 9), every destructive action against it is typed.

### 5.6 Secrets

A Secret's keys and sizes are listed; its values are never rendered. Copying a value is a control,
the value goes to the clipboard, and nothing about it is logged. This is the same rule as registry
credentials, not a Kubernetes exception to it.

---

## 6. The terminal and `kubectl`

The Docker picker offers to make the terminal agree with the screen (`docs/ARCHITECTURE.md`
§2.4b). Kubernetes is different in a way that matters: `kubectl`'s current-context is the cluster
every terminal's next `delete` lands on.

**Dray never changes `kubectl`'s current-context implicitly.** Selecting a cluster in the picker
changes what Dray shows and nothing else. "Use in terminal", on the Hosts page, is the one control
that sets it: it shows `kubectl config use-context <name>` first, runs it on the second click with
the `kubectl` found on PATH or where Homebrew and Docker Desktop put one, and is offered for Local
clusters only. Measured: kubectl moved, Dray's own selection did not. A user who
selects a cluster to look at it must not find their next `kubectl delete` aimed somewhere new.

Creating a cluster is the one action that moves it as a side effect, because kind does (§7) — and
the confirmation for that action says so before it runs.

---

## 7. Creating a cluster

When there is no local cluster, the empty state offers two routes and recommends neither blindly:

- **The engine's own**, where it has one — Docker Desktop, OrbStack and Rancher Desktop each have a
  Kubernetes switch in their settings. Dray names where it is and does not flip it: the setting
  belongs to the engine. On engines whose cluster shares the engine's image store, this is the
  smoother route, and the empty state says why — nothing has to be loaded (§8).
- **A kind cluster on this engine**, created by Dray. Works on any Docker-compatible engine running
  Linux containers, and is the same everywhere.

### 7.1 What Dray runs

Dray drives the `kind` CLI the way it drives `docker compose`: a separate program, detected rather
than assumed, output streamed. It never downloads kind itself — a binary fetched by an app and run
without a signature check is a supply-chain hole with a progress bar.

- **kind is found**, on `PATH` and at the package managers' own locations. This is the GUI `PATH`
  problem arriving early: a Finder-launched app's `PATH` has no `/opt/homebrew/bin`, which is
  exactly where `brew install kind` puts it. Local-first deferred the problem for credential
  plugins; it did not defer it for this.
- **When kind is absent**, the empty state shows the platform's install command — `brew install
  kind`, `winget install Kubernetes.kind`, the distro package on Linux — and runs it only when
  asked, in the open, the pattern Windows setup established (`docs/ARCHITECTURE.md` §2.4b).
- **Creation** is `kind create cluster --name <name> --wait 60s`, aimed at the selected engine by
  the same environment Compose is given, so the cluster lands on the engine the user is looking at
  and not on whatever their default context is. *Read, not run:* kind's docker provider honours
  `DOCKER_HOST`; the spike created on the default engine only.

### 7.2 What the confirmation says

Before anything runs, the dialog states what will happen, because three of them are not obvious:

- **The first cluster downloads the node image** — `kindest/node`, 1.3 GB on disk, measured.
- **kind makes the new cluster `kubectl`'s current-context.** Measured: `Set kubectl context to
  "kind-dray-spike"`. kind has no flag to prevent it — its only option is `--kubeconfig`, which
  writes somewhere `kubectl` does not look. Dray does not quietly undo it afterwards: that would be
  Dray writing a kubeconfig, which it does only from the one control that says so (§6). The
  dialog says it instead, and the user decides.
- **The kubeconfig gains a client key.** Measured: kind writes `client-key-data` inline into
  `~/.kube/config`. That is kind's file and kind's credential; Dray reads it in memory like any
  other (§9.5) and stores nothing.

Measured: creation took 14 seconds end to end on this machine, CNI and storage class included.
Whether the node image was already cached was not recorded, so a first-ever create should be
expected to take longer by however long 1.3 GB takes to arrive.

Measured again from the app: the dialog created `kind-dev` on Docker Desktop's engine, Dray read
the new context and connected to it, and `kubectl`'s current-context moved to it, as the dialog
says it will.

### 7.3 Deleting it

`kind delete cluster --name <name>`, typed: the user types the cluster's name, and the dialog lists
what goes with it — every workload, and every persistent volume in kind's local-path storage, which
lives inside the node container and nowhere else. Offered only for a kind cluster whose node
containers are measured on the connected engine (§4), because that is the cluster Dray can be sure
of the contents of.

Measured from the app: the node container and the context went, and Dray reconnected to the
cluster that was left. kind also **leaves `kubectl`'s current-context unset** when the deleted
cluster was current — the next `kubectl` command fails with "current-context is not set" until the
user picks one. Dray does not pick one for them, for the reason in §6.

Dray offers to create and delete **kind** clusters only. k3d and minikube clusters it finds are
shown and used; creating them is one more tool's flags to track for no difference the user would
feel.

---

## 8. Where Docker and Kubernetes meet

This is the reason to build it in Dray rather than install Lens. The pairing in §4 lets Dray answer
the most common local-cluster failure precisely.

**`ImagePullBackOff` on an image built here.** Measured end to end: build `myapp:dev` on the
engine, deploy it to kind, and the pod's events say

> Failed to pull image "myapp:dev": failed to pull and unpack image "docker.io/library/myapp:dev":
> failed to resolve reference "docker.io/library/myapp:dev": pull access denied, repository does
> not exist or may require authorization

then `kind load docker-image myapp:dev` (0.35 seconds for a 2 MB image), and both replicas were
Running within twenty seconds, with no restart of anything.

So the diagnosis is built from two facts Dray already has:

- **The pod's pull failure**, from its events — primary, immediate, and verbatim.
- **Whether the engine has that image**, matched on the **normalised reference**: the cluster
  spells it `docker.io/library/myapp:dev` where Docker spells it `myapp:dev`.

Then:

- The engine has it and the stores are **separate** → "`myapp:dev` was built on this engine but has
  not been loaded into dray-spike", with `kind load docker-image` shown first and run only when
  asked.
- The stores are **shared** and the tag is `:latest` or the policy is `Always` → the cluster is
  trying a registry for an image it already has. Dray says so and names `imagePullPolicy`.
- The engine does not have it → a registry problem, and the event text is shown as is.

**Two things the first draft got wrong here**, both measured:

- **A node's `status.images` lags.** It did not list `myapp:dev` 0.3 seconds after the load, and did
  by the next check about fifty seconds later — it moves at the kubelet's node-status interval. As
  the primary signal it would have reported "not loaded" for up to a minute after the user had
  fixed it. It is a secondary hint only.
- **Digests do not match across the two stores.** Docker reports the image as `sha256:ac78…`; the
  node's runtime as `sha256:4dfd…`, with `kind load` adding an `import-<date>@sha256:97fe…` alias.
  So "the cluster is running an older build than the one you just made" cannot be detected from the
  API, and Dray does not claim to. Matching is by reference, and says so.

---

## 9. Architecture

### 9.1 A sibling seam, not another engine

Kubernetes is **not** an `IContainerRuntime`. It has no image store, volumes or networks in
Docker's sense, and pods have desired state and owners that containers do not. Forcing it through
that interface would put Kubernetes conditionals on every Docker page — the seam in the wrong place,
which `AGENTS.md` names as the failure mode.

```
src/
├── Dray.Core/          + IClusterClient, ClusterCapabilities, PodSummary, WorkloadSummary,
│                         ServiceSummary, ClusterEvent, ClusterStore, KindCli — no Kubernetes reference
└── Dray.Kubernetes/    + IClusterClient over KubernetesClient; kubeconfig discovery; reflectors
```

`ClusterStore` is its own store, keyed by kind, namespace and UID — `EntityStore` is
container-shaped and stays that way. Pages that read it sit inside a `<ClusterScope>`, for the same
reason Docker pages sit inside `<EngineScope>`: a value computed above the tag is captured once and
never recomputed.

### 9.2 Client library

`KubernetesClient` 21.0.8 — the official C# client, net10.0, built against 1.37.

- **Watch through the generated `WatchList…Async` methods.** The older `WatchAsync` extension over
  an `HttpOperationResponse` — the shape most examples still use — is marked obsolete in 21.0.8,
  and the build's warnings-as-errors refuses it. The replacements take the same `onError`
  callback, and §9.3's 410 behaviour was re-measured through them.
- **No informer.** The client does not reconnect a watch or track `resourceVersion`. Dray writes
  its own reflector (§9.3), which it would want anyway to feed `ClusterStore`.
- **Not trim- or AOT-safe**, and the separate `KubernetesClient.Aot` package dropped Watch when it
  was introduced (*read, not run*). Fine while no head ships trimmed; a constraint the day one does.
- **Exec and port-forward are WebSocket-only, and work** (§5.3, §5.4).
- **The port-forward trap.** `StreamDemuxer.GetStream(0, 0)` — one stream for both directions, the
  obvious call and the shape most examples use — **deadlocks**. Measured: the first request echoed;
  the second write never returned, because a read was pending on the same stream. A one-shot HTTP
  request survives it, which is why it looks fine in a quick test and fails the first time a
  database client or a websocket uses the forward. Separate streams for each direction —
  `GetStream(0, null)` to read, `GetStream(null, 0)` to write — work: §5.4's held connection is that
  shape. Exec is unaffected because stdin and stdout are already separate streams. A test asserts
  the duplex case so a refactor cannot quietly reintroduce the shared stream.
- **The wire format underneath is simple** and was verified raw: one channel byte per frame, and on
  each channel's first frame a two-byte little-endian port number. Should the demuxer ever need
  replacing, the replacement is small.

### 9.3 The event stream is still the source of truth

Kubernetes watches are a better event stream than Docker's, and the rule in `PRODUCT.md` holds
with no exception for lists. One reflector per resource kind:

1. **Stream the initial list** with `sendInitialEvents=true` where the server supports it.
   Measured on 1.37: every pod arrived as `Added`, then a `Bookmark` annotated
   `k8s.io/initial-events-end: "true"`, all within 164 ms, then live changes on the same
   connection. The client exposes the parameter; nothing extra is needed. Where unsupported, list
   then watch from the list's `resourceVersion`.
2. **Track the cursor** through every event, bookmarks included. A bookmark carries a
   `resourceVersion` and nothing else — no name, no namespace — and must not reach the store as an
   object. `resourceVersion` is opaque — never compared numerically.
3. **Re-watch quietly** when the server ends a watch on its timeout. Measured: the stream simply
   ends — no exception, no event — with a bookmark arriving a second or two before.
4. **On 410, relist and diff.** Measured: a watch from a compacted cursor gets `Status` code 410,
   reason `Expired`, "too old resource version" — delivered **only through the watch's `onError`
   callback**, after which the enumeration ends *normally*. That is indistinguishable from step 3's
   timeout unless the callback is observed, and a reflector that does not observe it re-watches the
   same stale cursor, gets 410, and ends again — a tight loop that looks like a quiet watch. On 410
   the fresh list is compared against the store and only differences are written, with removals for
   what vanished — the lesson of `docs/ARCHITECTURE.md` §3.1. Comparison is by rendered fields, as
   `ContainerSummary.SameAs` does, never by record equality.
5. **Coalesce before rendering.** Measured: deleting a two-replica Deployment's pods produced
   fourteen events in half a second. The store applies every one; the UI repaints once per frame,
   not once per event.

**Metrics are the one place nothing can be watched.** `metrics.k8s.io` has no watch verb. It is
fetched for visible rows and the open detail only, never for a whole list, at the metrics
pipeline's own resolution — the same terms as Docker stats (`docs/ARCHITECTURE.md` §3). Its CPU
figure is already a rate in Kubernetes quantity units (`250m`, `1200000n`), so §2.9's division by
wall time does not apply, and quantities are parsed, never string-matched. **Measured: kind ships
no metrics server**, so on kind CPU and memory are absent, and the page says why rather than
showing zeroes.

### 9.4 Capabilities are measured

`ClusterCapabilities`, set at connect and never guessed:

- **Server version**, from `/version` — measured `v1.37.0`.
- **What this user may do**, from `SelfSubjectAccessReview`: list across namespaces, exec,
  port-forward, delete, scale. Measured: all five answer, and all are allowed for kind's admin
  user. A control the user cannot use is hidden, and a user who can see one namespace gets a working
  one-namespace view, not a screen of 403s.
- **Metrics available**, from whether the `v1beta1.metrics.k8s.io` APIService exists — measured
  absent on kind.
- **Streaming lists supported**, from trying one — measured present on 1.37.

### 9.5 Credentials

`docs/CREDENTIALS.md` applies unchanged: Dray stores no secret.

- The kubeconfig is read at connect and held in memory. Dray persists the **context name** and
  nothing from the `users` section.
- Local clusters authenticate with client certificates embedded in the kubeconfig — measured for
  kind. Those bytes are never rendered, logged, or copied into Dray's config — the same treatment
  as a Docker context's TLS material.
- `Authorization` headers and `*-data` fields are scrubbed from any diagnostic output, including
  the client's own HTTP tracing.
- **Exec credential plugins** (`aws eks get-token`, `gke-gcloud-auth-plugin`, `kubelogin`) mint
  short-lived tokens held in memory, which fits the rule. They are a Remote concern, and two
  problems come with them: the GUI `PATH` (§7.1 meets it first), and browser-based logins that
  print their instructions to stderr, which must reach the user rather than a log.
- The legacy `oidc` auth-provider is declined: kubectl's implementation writes refreshed tokens
  back into the kubeconfig, and Dray does not write secrets. `kubelogin` is the documented
  replacement. Whether the C# provider writes back is unverified.

---

## 10. K0 — the spike

The spike is `.scratch/k8s-k0`, one mode per row below, against the workloads in
`.fixtures/kubernetes`; its header has the commands to stand the cluster up. Every result here was
re-run from that committed copy.

| Question | Result |
|---|---|
| Port-forward through `KubernetesClient` | **Works**, with separate read and write streams. The shared stream deadlocks (§9.2) |
| Exec over `v5.channel.k8s.io`, stdin close, exit code | **Works**. `v4` hangs on anything reading to EOF (§5.3) |
| Full-duplex exec | **Works** (§5.3) |
| Watch timeout, bookmarks, 410 | Measured; 410 arrives only through `onError` (§9.3) |
| Streaming initial list | **Works** on 1.37 (§9.3) |
| kubectl STATUS parity | 6 of 6, plus one deliberate departure for crash loops (§5.1) |
| Previous-instance logs | Same output as the plain log during back-off (§5.2) |
| Local classification, pairing | Loopback, published port, `providerID` and labels all measured for kind (§3, §4) |
| Image visibility | Pull event plus engine image list; `status.images` lags, digests differ (§8) |
| kind create, load, current-context | 14 s, 0.35 s, and it switches the context (§7) |
| OrbStack, Docker Desktop's Kubernetes, k3d, minikube, Rancher | **Not run** — not installed here |
| Exec credential plugins, GUI `PATH` | **Not run** — spike was a console app on a local cluster |
| Anything inside the macOS head | **Not run** |

### 10.1 What running it changed

Two claims in the first draft were wrong, caught only by executing them — the same lesson
`docs/ARCHITECTURE.md` §2.11 records for Apple's engine:

- "Open the previous instance's log first" would have shown the same lines under a different label
  (§5.2).
- "`status.images` measures whether a store is shared" would have said "not loaded" for up to a
  minute after the user fixed it (§8).

And one risk turned out real but small: port-forward was the piece most likely to force a fallback
to `kubectl port-forward`. It does not — but only because the deadlock was found in a spike rather
than in a user's database client.

---

## 11. Open questions

- **minikube with a VM driver** — Remote by §3's rule. Should a host-only VM network count as
  local, and how is that measured rather than guessed?
- **kind on podman.** kind's podman provider is experimental and opt-in. Offer creation on a podman
  engine, or only on Docker-flavoured ones, until it has been run?
- **Helm releases as a grouping.** A release is the closest thing to a stack, and Helm labels what
  it installs. Grouping by release is cheap; managing releases is not v1.
- **Where forwards live** when the Services page is not open — the menu-bar item is the obvious
  candidate.
