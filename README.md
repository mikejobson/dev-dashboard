<img src="assets/icon.svg" width="64" height="64" alt="">

# Dev Dashboard

Created in [T3 Code](https://t3.codes).

A self-hosted dashboard for the state of your code folders on every machine, your GitHub pull
requests, issues and recent org activity, and the things worth cleaning up. The full design is in
[PLAN.md](PLAN.md). Phases 1–3 are built (agent, hub, GitHub poller and cleanup rules), plus phase 4: the Docker + Tailscale hub, agent service installers and releases. Agents [update themselves](#updating-the-agent) from signed releases.

## Run it locally

You need the .NET SDK (10 or newer), `git`, and the `gh` CLI logged in (or a `GITHUB_TOKEN`).

```sh
dotnet build
dotnet test

# 1. Start the hub: http://localhost:5210
dotnet run --project src/DevDash.Hub

# 2. In another terminal, run the agent against ~/Developer
KEY="$PWD/src/DevDash.Hub/devdash-data/dev-agent-key.txt"
dotnet run --project src/DevDash.Agent -- scan --api-key-file "$KEY"   # scan once and push
dotnet run --project src/DevDash.Agent -- run  --api-key-file "$KEY"   # keep scanning every 15 min
```

On first run the hub creates its SQLite database in `src/DevDash.Hub/devdash-data/`, issues one
agent API key, prints it once, and (in Development only) saves it to `dev-agent-key.txt` so the
agent can pick it up. More keys can be issued and revoked on the Machines page.

Useful agent commands (`dotnet run --project src/DevDash.Agent -- <command>`):

| Command | What it does |
|---|---|
| `scan --dry-run` | Prints the snapshot JSON. Never fetches, never pushes. |
| `scan` | Scans once and pushes to the hub. |
| `run` | Scans every 15 minutes, rescans an entry shortly after its files change, and pushes each time. |
| `status` | Shows the configuration, whether the hub is reachable, and the service state. |
| `install`, `uninstall`, `start`, `stop`, `restart`, `logs` | Run the agent as a background service: see [Install the agent](#install-the-agent). |
| `update`, `update --check`, `update --version 0.3.0` | Install the latest (or the named) release over this one, after verifying it: see [Updating the agent](#updating-the-agent). |
| `--version` | Prints the agent version. |

Options: `--root <path>` (repeatable), `--hub-url <url>`, `--api-key <key>`, `--api-key-file <file>`,
`--no-fetch`, `--sizes`, `--no-watch`, `--enable-actions`, `--updates off|notify|auto`. The same settings can live in `~/.config/devdash/agent.json`
under an `Agent` section, or in `DEVDASH_Agent__*` environment variables.

### What the agent does to your repos

It reads them with `git status`, `for-each-ref`, `worktree list`, `stash list`, `remote -v` and
`log`. Its only write is `git fetch --prune --quiet`, at most every 4 hours per repo, which updates
remote-tracking refs and nothing else. Turn that off with `--no-fetch` or
`"Agent": { "AutoFetch": { "Enabled": false } }`. Credential prompts are disabled and every git call
has a timeout.

That is all it does to your repos unless you turn on agent actions for that machine (see Cleanup
below). They are off by default, and nothing the hub sends can turn them on.

Separately, an installed agent asks the hub every 30 seconds whether you have clicked **Update** for
it on the Machines page. That is the default update mode (`notify`); `--updates off` stops it. An
update never touches a repository: see [Updating the agent](#updating-the-agent).

### GitHub

The hub polls GitHub every 5 minutes with read-only GraphQL queries. It takes the token from
`GitHub:Token`, then `GITHUB_TOKEN`, then `gh auth token`. All your orgs are discovered
automatically; list any to skip in `GitHub:ExcludeOrgs` in `src/DevDash.Hub/appsettings.json`.

### Cleanup

The Cleanup page lists findings from the rules in `src/DevDash.Core/Cleanup/Rules`. Each finding has
a short list of commands, each with a label and a Copy button: the ones that only look come first
(`git log`, `git stash show -p`, `git status` in a worktree), then the ones that fix. A branch that is
behind its upstream as well as ahead gets a rebase before its push, not a push that would be
rejected. Small `cd` and `code` chips copy a shortcut to the folder.

Each group also has a **Copy the fixes as a script** button per machine. It copies that machine's
fix commands for the group, with a comment line per finding. A script never mixes machines.

Snooze and dismiss decisions are stored against the finding's fingerprint, so they survive rescans.

#### Running a fix from the dashboard (agent actions)

By default DevDash only shows commands. A machine can opt in to running the safe ones:

```sh
devdash-agent run --enable-actions        # or "Agent": { "Actions": { "Enabled": true } } in ~/.config/devdash/agent.json
```

That machine then gets a **Run on \<machine\>** button beside the fixes that have an allowlisted
action. Clicking it shows the machine, the repo and the exact git command, and nothing is queued until
you confirm. The agent picks it up on its next poll (every 30 seconds), runs it, reports the result
and rescans, so the finding goes away. The Machines page lists every action: who queued it, when it
was claimed, and what git printed.

What keeps this safe:

- **Off by default, per machine.** Only the machine's own configuration turns it on. The hub shows
  Run buttons only for machines that report it, and `Hub:Actions:Enabled=false` turns it off everywhere.
- **Typed actions, not commands.** The hub sends a kind and its fields; the agent builds the git
  arguments itself. The whole list: `branch -d` (never `-D`), `worktree remove` (never `--force`),
  `worktree prune`, `fetch --prune`, `push` of one branch to the same name (never forced; the agent
  fetches first and refuses if the branch is behind), and `stash drop` (only if the stash is still the
  commit you were shown). There is no pull, rebase, reset or force-delete action.
- **Checked again on the machine.** The repo must be the top of a git working tree inside the agent's
  scan roots. Branch names go through `git check-ref-format` and must exist; a worktree must be a
  linked, unlocked worktree of that repo, on a branch. Git runs with an argument list, no shell.
- **Bound, once, and short-lived.** An agent only receives actions for the machine its key is bound
  to. Each action is handed out once and never retried, and expires if not picked up in 10 minutes.

A hub database created before this feature gains the new `AgentActions` table on start; nothing needs
resetting.

## Run the hub in Docker on your tailnet

The hub runs as two containers: Tailscale, which joins your tailnet as its own ephemeral node, and
the hub, which shares Tailscale's network and listens on `127.0.0.1:8080` only. `tailscale serve`
provides HTTPS with a tailnet certificate and tells the hub who you are, so there is no login page and
no port is published on the host.

1. **Tailnet setup, once:** in the Tailscale admin console, turn on MagicDNS and HTTPS certificates.
   If you use an OAuth client (recommended), add `"tag:devdash": ["autogroup:admin"]` to `tagOwners`
   in the ACL policy.
2. **Configure:** `cp .env.example .env`. Fill in `TS_AUTHKEY` and `GITHUB_TOKEN` (`gh auth token`
   prints one). `DEVDASH_ALLOWED_USERS` is optional. `.env.example` explains each setting.
3. **Start:** `docker compose up -d` runs the published image,
   `ghcr.io/mikejobson/dev-dashboard-hub` (`DEVDASH_VERSION` in `.env` picks the release; the default
   is `latest`). To build the hub from your checkout instead: `docker compose up -d --build`.
4. **Open** `https://devdash.<your-tailnet>.ts.net`. It's reachable from any of your devices on the
   tailnet.
5. **Agent key:** the hub prints its first agent API key once on first start:
   `docker compose logs hub | grep ddk_`. Copy it straight away: `docker compose down` deletes those
   logs, and the key isn't printed again. Any time, you can issue a key per machine on the Machines page.
   Then [install the agent](#install-the-agent) on each machine with that hub URL and key.

**Updating:** `docker compose pull && docker compose up -d`. After a local `--build`, the same
`pull` puts the published image back.

**Data:** state lives in two Docker volumes. `devdash-data` holds the SQLite database and
data-protection keys; `tailscale-state` holds the node key and HTTPS certificate. Keeping
`tailscale-state` means a restart reuses the same node, so the hostname doesn't drift to
`devdash-1` and no new certificate is requested. The node is still ephemeral: once it has been
offline for a while, Tailscale removes it, and it rejoins with `TS_AUTHKEY` on the next start.

**Who can sign in:** identity headers are trusted only when they arrive from the local proxy.
Tagged devices are refused, and agents authenticate with their API keys instead.

## Install the agent

The agent is one self-contained program, with no .NET install needed. It runs in the background as
you, not as root or SYSTEM, because it needs your code folders, your git config and your SSH keys
or credential helper. `git` must be on your `PATH`.

**macOS and Linux**

```sh
curl -fsSL https://raw.githubusercontent.com/mikejobson/dev-dashboard/main/scripts/install.sh \
  | sh -s -- --hub-url https://devdash.<your-tailnet>.ts.net --api-key <key>
```

**Windows** (PowerShell, no administrator rights)

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/mikejobson/dev-dashboard/main/scripts/install.ps1))) --hub-url https://devdash.<your-tailnet>.ts.net --api-key <key>
```

Add `--root <path>` (repeatable) to scan somewhere other than `~/Developer`, `--no-fetch` to stop
the agent running `git fetch`, `--updates off|notify|auto` to choose how the agent
[updates itself](#updating-the-agent), or `--set Key=Value` for any other `Agent` setting.
`devdash-agent install --help` lists them all, and `--dry-run` prints what would be written without
changing anything.

What the script does:

1. Downloads the build for your OS and CPU from the latest
   [release](https://github.com/mikejobson/dev-dashboard/releases) and checks its SHA-256 against
   the release's `checksums.txt`, and `checksums.txt` against the release's signature
   (`install.sh` needs `openssl` for that and says so if it is missing; `install.ps1` uses Windows'
   own crypto). Set `DEVDASH_VERSION=v0.2.0` to pin a version. v0.1.0 was published before
   releases were signed; installing that one needs `DEVDASH_ALLOW_UNSIGNED=1`.
2. Puts `devdash-agent` in `~/.local/bin` (Windows: `%LOCALAPPDATA%\DevDash\bin`, added to your
   user `PATH`).
3. Runs `devdash-agent install` with your arguments, which saves the configuration and registers
   and starts the service.

| | macOS | Linux | Windows |
|---|---|---|---|
| Service | LaunchAgent `com.github.mikejobson.devdash-agent` in `~/Library/LaunchAgents` | `systemd --user` unit `devdash-agent.service` | Scheduled task "DevDash Agent (*you*)", at logon, restarts on failure |
| Logs | `~/Library/Logs/DevDash` | `~/.local/state/devdash/logs` | `%LOCALAPPDATA%\DevDash\logs` |
| Config | `~/.config/devdash/agent.json` | same | `%USERPROFILE%\.config\devdash\agent.json` |

The API key is stored beside the config in `agent.key`, readable only by you (mode `0600`; on
Windows, an ACL with a single entry for your account). Logs roll daily and the last 14 files are
kept. Edit `agent.json` and run `devdash-agent restart` to change a setting by hand.

```sh
devdash-agent status             # configuration, hub reachability, service state
devdash-agent logs               # where the logs are, and the latest lines
devdash-agent restart            # also: start, stop
devdash-agent uninstall          # remove the service; keeps config and logs
devdash-agent uninstall --purge  # also delete the config, API key and logs
```

**Upgrade:** `devdash-agent update`, or the Update button on the Machines page: see
[Updating the agent](#updating-the-agent). Running the install one-liner again with no arguments
after `--` also works (it replaces the program, keeps your configuration and restarts the service),
and is the way to move a v0.1.0 agent, which cannot update itself, to a version that can.

**Remove completely:** `devdash-agent uninstall --purge`, then delete the program from the folder
in step 2.

Notes:

- **macOS Gatekeeper:** the binaries are not signed with an Apple Developer ID or notarised. A
  download made with `curl`, as the script does, is not quarantined and runs normally. If you
  download the archive in a browser instead, macOS will refuse to open it until you clear the flag:
  `xattr -d com.apple.quarantine ~/.local/bin/devdash-agent`.
- **Windows:** the task runs `devdash-agentw.exe`, the same program built without a console
  window. SmartScreen may warn about the unsigned download if you fetch the zip in a browser.
- **Linux:** the agent runs while you are logged in. To keep it running after you log out:
  `sudo loginctl enable-linger $USER`. A systemd user service does not inherit `SSH_AUTH_SOCK`, so
  SSH remotes that need an agent-held key will not fetch; HTTPS remotes with a credential helper
  are unaffected.

## Updating the agent

An installed agent can replace itself with a newer release. There are three ways to start that, and
one code path behind all of them.

| | |
|---|---|
| **By hand** | `devdash-agent update` installs the latest release and restarts the service. `--check` only says whether there is one; `--version 0.3.0` installs that release (never an older one than is installed); `--no-restart` leaves the running service alone. |
| **From the dashboard** | The Machines page shows each agent's version against the latest release, with an **Update available** chip, an **Update** button per machine and **Update all**. You confirm in a dialog; the request then shows in the audit trail as queued, claimed, succeeded or failed, and the machine's version changes when the new agent reports. The Overview page says when any agent is behind. |
| **Automatically** | With `--updates auto`, the agent also looks for a new release itself about every 6 hours (plus a random part) and installs it, never while an action is running and only when the hub is reachable. |

**The update mode** is a setting on each machine, `Agent:Updates:Mode`, written by
`devdash-agent install --updates <mode>` (or `--set Updates:Mode=<mode>`, or by editing
`agent.json`). Nothing the hub sends can change it.

| Mode | The agent |
|---|---|
| `notify` (default) | updates when you ask from the dashboard or run `devdash-agent update`; never on its own |
| `auto` | as `notify`, and installs new releases itself |
| `off` | refuses updates from the dashboard and never updates on its own (`devdash-agent update` at the keyboard still works) |

This is independent of agent actions: `--enable-actions` is about git fixes in your repositories and
stays off by default; a machine with actions off still accepts an update in `notify` or `auto` mode,
and a machine with updates `off` refuses one even with actions on. The agent reports its mode, so the
dashboard only offers Update for machines that will accept it.

**What an update trusts.** The dashboard sends a version number and nothing else: no file, no URL.
The agent downloads that release from GitHub itself
(`https://github.com/mikejobson/dev-dashboard/releases/download/v<version>/`) and, before using any
of it:

1. checks that `checksums.txt` carries a valid signature (`checksums.txt.sig`, ECDSA P-256 over
   SHA-256) from the release key. The public key is compiled into the agent; there is no setting
   that changes it. An unsigned release, or one signed by anyone else, is refused;
2. checks `version.txt` and the archive for its platform against the SHA-256 values in that signed
   `checksums.txt`, and that the version is the one asked for and newer than the one installed;
3. unpacks the new program beside the current one and runs it once (`--version` must print the
   expected version) before anything is replaced.

`Agent:Updates:BaseUrl` points the agent at a mirror of the releases instead of GitHub (HTTPS only,
other than `localhost` for tests); the signature is checked whatever it points at.

**The swap.** The previous program is kept as `devdash-agent.old` beside the new one.

- **macOS and Linux:** the current file is copied to `.old`, then the new file is renamed over it.
  The rename is atomic, so the path always holds a complete program. The agent then exits and
  launchd (`KeepAlive`) or systemd (`Restart=always`) starts the new one, within about 30 seconds.
- **Windows:** a running `.exe` cannot be overwritten but can be renamed, so `devdash-agent.exe` and
  `devdash-agentw.exe` are both renamed to `.old` and the new pair moved in; the two always change
  together. The agent starts a small helper that waits for it to exit and runs the scheduled task
  again.

**Roll back.** After the swap the update is *pending*. The first snapshot the hub accepts from the
new version confirms it, and only then is `.old` deleted. If the new version starts 3 times without
getting that far (it keeps crashing), or runs for 5 minutes without it (it cannot reach or satisfy
the hub), it puts `.old` back, restarts into it, and the restored agent reports
"rolled back from X to Y" on the Machines page. Auto mode then leaves that version alone for a day.
`devdash-agent status` shows a pending update and how the last one ended. The record is
`devdash-agent.update.json`, beside the program. If an update ever leaves an agent that will not
start at all, put the previous one back by hand: rename `devdash-agent.old` to `devdash-agent` and
run `devdash-agent restart`.

**The hub's side.** The hub asks GitHub for the latest release once an hour (the Machines page has
*Check now*). The repository is public, so this works with no token; `GITHUB_TOKEN` only raises the
rate limit. Drafts and pre-releases are ignored. Settings, under `Hub:Updates` (or
`Hub__Updates__*` in the environment): `Enabled` (default `true`; `false` turns off both the check
and the Update buttons), `Repository` (default `mikejobson/dev-dashboard`), `CheckIntervalMinutes`
(60), `IncludePrereleases` (`false`).

**v0.1.0 agents cannot update themselves.** They do not know the update action and carry no release
key. The Machines page shows them as "updates: not supported by this version" with no Update button.
Run the install one-liner on each of those machines once; from then on they update as above. The
hub keeps accepting their snapshots in the meantime.

## Releases

Pushing a version tag publishes everything:

```sh
git tag v0.2.0
git push origin v0.2.0
```

The [Release workflow](.github/workflows/release.yml) then runs the tests and:

- publishes the agent for macOS (arm64, x64), Windows (x64, arm64) and Linux (x64, arm64) as
  `devdash-agent-<os>-<cpu>.tar.gz` or `.zip`, plus `version.txt`, `checksums.txt` and its signature
  `checksums.txt.sig`, on a GitHub Release, after running the macOS and Windows builds on real
  runners and checking the signature with the binaries just built;
- pushes the hub image for amd64 and arm64 to `ghcr.io/mikejobson/dev-dashboard-hub`, tagged
  `0.2.0`, `0.2` and `latest`.

The tag is the version: `devdash-agent --version` and the Machines page report it. A tag with a
suffix, such as `v0.3.0-rc.1`, becomes a pre-release, which the install scripts and `latest` skip.
The first time, make the `dev-dashboard-hub` package public in the repository's Packages settings
so `docker compose up -d` can pull it without logging in.

`scripts/publish-agent.sh <version> [rid...]` builds the same archives locally into `dist/`.

### Signing key

Every release is signed, and the workflow refuses to publish one that is not: it fails at its first
step if the `DEVDASH_RELEASE_SIGNING_KEY` secret is missing, or is not the private half of the
public key in the source.

Set it up once, before the first signed release:

```sh
scripts/release-signing-key.sh generate           # new ECDSA P-256 key pair
gh secret set DEVDASH_RELEASE_SIGNING_KEY < ~/devdash-release-signing-key.pem
git commit -am "Add the release public key" && git push
```

`generate` writes the private key to `~/devdash-release-signing-key.pem` (never into the repository)
and the public key into the three places that need it:
`src/DevDash.Agent/Updates/ReleaseSigning.cs`, `scripts/install.sh` and `scripts/install.ps1`. CI
checks that the three agree. Keep a copy of the private key somewhere safe and offline and delete it
from your machine. If it is lost, installed agents can never verify another release and each needs
one reinstall; if it leaks, whoever has it can sign a release your agents will accept, so generate a
new one, release, and reinstall the agents.

Tag only after the commit with the public key is on `main`: the install one-liner reads
`install.sh` from `main`, and from that commit on it refuses a release with no signature.

**Forks:** a fork's agents must trust the fork's key, not this repository's. Run the three commands
above in the fork, and set the fork as the source of releases: the default of
`Agent:Updates:Repository` in `src/DevDash.Agent/appsettings.json` and `UpdateOptions.cs`,
`DEVDASH_REPO` (or the default) in the install scripts, and `Hub:Updates:Repository` for the hub.

`scripts/test-ota.sh` runs the whole update for real on your machine, in a temporary folder with a
throwaway key: two builds, a signed release folder on localhost, install, update, roll back,
releases that must be refused, and a confirmation against a hub. CI runs it on Linux, macOS and
Windows. Test builds get their key from `-p:DevDashUpdatePublicKeyFile=<pem>` at compile time; there
is no way to change the trusted key at run time, and the release workflow rejects a binary built
that way.

## Layout

```
src/DevDash.Contracts   snapshot DTOs shared by agent and hub
src/DevDash.Core        git output parsers, remote URL normalisation, cleanup rules (pure)
src/DevDash.Agent       devdash-agent: scanner, worker service, CLI, service installers, self-update (Updates/)
src/DevDash.Hub         ingest API, GitHub poller, Blazor UI, SQLite
tests/                  parser fixtures, rule tests, scanner tests on throwaway repos, hub integration tests
scripts/                install.sh and install.ps1 (agent installers), publish-agent.sh (release builds),
                        release-signing-key.sh (the release key), test-ota.sh (self-update, end to end)
.github/workflows/      ci.yml (build, test and self-update on Linux, macOS, Windows), release.yml (tag to signed release)
```

## Licence

MIT, see [LICENSE](LICENSE).
