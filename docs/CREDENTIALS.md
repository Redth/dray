# Credentials

Dray handles three kinds of secret and stores none of them itself.

| Secret | Where it lives | Dray's role |
|---|---|---|
| Registry credentials | The Docker credential helper's store — macOS Keychain, Windows Credential Manager, libsecret, `pass` | Speak the helper protocol. Never see the secret at rest. |
| SSH keys and passphrases | `ssh-agent` and `~/.ssh` | Never touch them. Shell out to the system `ssh`. |
| TLS client certificates | The Docker context's own `tls/` directory | Read the paths. Never copy the key material. |

The rule underneath all three: **the correct number of secret stores for Dray to invent is zero.**

---

## 1. Registry credentials

### 1.1 Use the helper protocol, not a keychain API

Docker's credential helpers are small executables named `docker-credential-<store>` that speak
JSON on stdin and stdout:

```
echo "ghcr.io" | docker-credential-osxkeychain get
→ {"ServerURL":"ghcr.io","Username":"redth","Secret":"…"}
```

`get`, `store`, `erase` and `list` are the whole surface. Dray shells out to these rather than
calling Keychain or Credential Manager directly, and that choice buys three things:

- **One store, shared with the CLI.** Sign in once and both `docker` and Dray work. Reimplementing
  keychain access would create a second store that silently diverges from the one the user's
  terminal uses.
- **The secret never lands in Dray's storage**, so there is nothing for Dray to leak, migrate or
  get wrong.
- **Every platform is already solved.** `osxkeychain`, `wincred`, `secretservice` and `pass` all
  exist and are maintained by Docker.

### 1.2 Resolution order, which is not obvious

`~/.docker/config.json` supports two mechanisms and the per-registry one wins:

```jsonc
{
  "credHelpers": { "myregistry.azurecr.io": "acr-env" },  // 1. per registry
  "credsStore": "osxkeychain",                            // 2. the default for everything else
  "auths": { "ghcr.io": {} }                              // 3. which registries exist
}
```

1. `credHelpers[registry]` → run `docker-credential-<that>`.
2. Otherwise `credsStore` → run `docker-credential-<that>`.
3. Otherwise the credential is base64 **in the file**, in `auths[registry].auth`.

Case 3 is not encryption — base64 is an encoding. Dray reads such a credential so the user is not
locked out of a registry they configured, but **never writes one**, and says plainly in the
registries list that it is stored unencrypted with the one-line fix.

An empty `auths` entry like `"ghcr.io": {}` means "this registry is configured and its secret is in
the helper's store". It is a list of registries, not a list of secrets.

### 1.3 A missing helper is a supported state, not an exception

This is the failure mode that motivated writing any of this down. It happened on the development
machine, and it is common:

> `config.json` said `credsStore: osxkeychain`. The binary
> `/usr/local/bin/docker-credential-osxkeychain` was a symlink into `/Applications/OrbStack.app`,
> and OrbStack had been uninstalled. Every `docker pull` — including of a public image — failed
> with `error getting credentials - err: exec: "docker-credential-osxkeychain": executable file not
> found in $PATH`.

An uninstalled engine takes its bundled helper with it and leaves the config pointing at nothing.
So Dray must:

- **Probe the helper on startup** and treat "named but not on PATH" as a first-class state.
- **Say what is wrong and how to fix it**: which helper is named, that it is missing, and that
  `brew install docker-credential-helper` (or the platform equivalent) restores it. Not
  "Authentication failed".
- **Degrade, not fail.** Public registries still work without credentials. Losing the helper must
  not break pulling `nginx:alpine`, which is what the CLI does today.
- **Never silently fall back to writing plaintext.** If Dray cannot reach a helper, it does not
  store the credential at all.

The secrets usually survive the helper. On the machine above, `ghcr.io` was still in the Keychain as
an internet-password and came straight back once a real helper was installed — nothing had been
lost, only the program that could read it. Dray should say that too, rather than implying the
credentials are gone.

### 1.4 Signing in

Dray takes a username and a token, hands both to the helper's `store`, and forgets them. The token
lives in a local variable for the length of one call.

- Never render a secret, not even masked, and never log one.
- The registries list shows the registry, the username, and which store holds the secret.
- Prefer a token over a password wherever the registry offers one, and say so in the field label.

For cloud registries with their own helpers already installed — `docker-credential-gcloud`,
`docker-credential-ecr-login`, `acr-env` — Dray uses the helper and offers no sign-in of its own.
Those helpers mint short-lived tokens from an ambient identity, and a username and password field
would be the wrong question.

---

## 2. Remote hosts

### 2.1 SSH: hand it to `ssh`

Dray shells out to the system `ssh` binary to forward the remote socket. It never reads a private
key, never prompts for a passphrase, and never stores either.

That is not laziness — it is the more secure and more correct option. `~/.ssh/config` already holds
the user's `Host` aliases, `ProxyJump`, `IdentityFile`, and hardware-key setup. An in-process SSH
client would have to reimplement that resolution, would get it subtly wrong, and would need to
handle key material Dray currently never sees. Agent forwarding, FIDO keys and jump hosts all work
for free by not competing with the tool that owns them.

If a host needs a passphrase, `ssh` prompts through the agent exactly as it does in a terminal.

### 2.2 TLS: read paths, copy nothing

A `tcp://` context carries its material at
`~/.docker/contexts/tls/<digest>/docker/{ca,cert,key}.pem`. Dray reads those paths at connection
time and hands them to the TLS handler. It does not copy, cache, or move them.

`SkipTLSVerify` is honoured only when the context itself sets it. Dray never decides to skip
verification on the user's behalf: a silently unverified connection to a remote daemon is a far
worse outcome than a visible failure. A private CA is handled by trusting `ca.pem` explicitly
rather than by disabling verification.

---

## 3. Dray's own secrets

Ideally none. If a feature genuinely needs one — a saved host profile that cannot be expressed as a
Docker context — it goes to the platform store through `ISecretStore`: Keychain, Credential
Manager, or libsecret. Never a file in the app's data directory, and never a config file.

One trap inherited from MAUI.Sherpa: **ad-hoc Debug signing rotates the code signature on every
rebuild**, so macOS Keychain items written by the previous build become inaccessible to the next
one. Debug builds need a fallback path, and it must be obvious in the UI that it is not the real
store.

---

## 4. What Dray writes to `config.json`

As little as possible.

- **Reads**: `auths` (which registries exist), `credsStore`, `credHelpers`, `currentContext`.
- **Writes**: nothing, except through a credential helper's `store` and `erase`.

The file belongs to the Docker CLI. Dray is a guest in it, and a guest that rewrites the house
rules is how two tools end up disagreeing about where the user is signed in.
