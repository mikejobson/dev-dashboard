# Dev Dashboard — Plan

A self-hosted dashboard, reachable only over Tailscale, showing the state of every machine's
`~/Developer` folder, recent GitHub activity across your orgs, open PRs/issues, and things worth
cleaning up.

## 1. Architecture

```
 ┌──────────── each machine (mac / windows / linux) ────────────┐
 │  devdash-agent  (.NET 10 worker, native user service)        │
 │   • scans ~/Developer (git CLI)                               │
 │   • FileSystemWatcher + timer → push snapshot                 │
 └───────────────┬───────────────────────────────────────────────┘
                 │ HTTPS over tailnet, per-machine API key
                 ▼
 ┌──────────── hub host (docker compose) ───────────────────────┐
 │  tailscale sidecar  ── `tailscale serve` → https://devdash.<tailnet>.ts.net
 │  devdash-hub (ASP.NET Core 10 + Blazor Server + SignalR)      │
 │   • ingest API for agents                                     │
 │   • GitHub poller (GraphQL)                                   │
 │   • cleanup rules engine                                      │
 │   • Claude API: descriptions, daily brief                     │
 │   • SQLite (EF Core) on a volume                              │
 └───────────────────────────────────────────────────────────────┘
                 ▲
                 │ browser (any device on tailnet)
```

### Key decisions

| Decision | Choice | Why |
|---|---|---|
| Agent runs natively, not in a container | user-level service (launchd / Task Scheduler at logon / systemd --user) | Needs the real `$HOME`, git config and credential helpers. Bind-mounting into Docker Desktop on mac/Windows is slow and misses things like worktree absolute paths. |
| Hub runs in a container with a Tailscale sidecar | `tailscale/tailscale` + `network_mode: service:tailscale` | Gets its own tailnet node and HTTPS cert, with no ports open on the host. |
| Push model (agent → hub) | Agents POST snapshots; hub never dials agents in v1 | Works through sleep/offline laptops; "last seen" comes free. |
| Git via CLI, not LibGit2Sharp | `git status --porcelain=v2 --branch`, `for-each-ref`, `worktree list --porcelain`, `stash list` | Matches what you see in the terminal and handles worktrees, sparse checkouts, and so on. |
| UI | Blazor Web App (Interactive Server) + SignalR push | One .NET stack, live updates without a JS build. |
| DB | SQLite + EF Core (path configurable) | One user and a handful of machines. Can swap to Postgres later through the EF provider. |
| Auth (browser) | Tailscale identity headers from `tailscale serve` (`Tailscale-User-Login`) + allowlist | No passwords. Only tailnet users can reach the hub at all. |
| Auth (agents) | Per-machine API key (hashed in the DB), issued from the UI or CLI | Revocable per machine. |
| GitHub auth | Reuse the `gh` token (`GITHUB_TOKEN` env, else `gh auth token`) | Already authenticated across all your orgs. |
| LLM | Official `Anthropic` C# SDK, `claude-opus-5-5`, effort `low` for one-liners | Configurable model and effort. Results cached by content hash, so a repo is only summarised again after its README or manifest changes. |

Target framework is `net10.0` (LTS); any SDK from 10.0 up builds it.
Container base images: `mcr.microsoft.com/dotnet/aspnet:10.0`.

## 2. Solution layout

```
DevDash.slnx
src/
  DevDash.Contracts/   DTOs shared by hub + agent (snapshot schema, versioned)
  DevDash.Core/        git output parsers, cleanup rules, remote URL normalisation (pure, unit-tested)
  DevDash.Agent/       worker service + `devdash-agent install|uninstall|scan --dry-run|status`
  DevDash.Hub/         ASP.NET Core: ingest API, GitHub poller, rules, LLM, Blazor UI
tests/
  DevDash.Core.Tests/  parser fixtures captured from real repos, rule tests
  DevDash.Hub.Tests/   ingest + API integration tests (WebApplicationFactory, SQLite in-memory)
docker-compose.yml     hub + tailscale sidecar, volumes, serve config (repo root)
Dockerfile             hub image
deploy/
  tailscale/serve.json
scripts/
  install.sh           agent installer for macOS and Linux
  install.ps1          agent installer for Windows
  publish-agent.sh     self-contained agent release builds
```

## 3. What the agent collects (per scan)

Machine: hostname, OS, Tailscale name/IP (`tailscale status --json` when available), agent version, scan roots, scan duration.

For each top-level entry under each root (default `~/Developer`, configurable, depth configurable for `*.worktrees` folders):

- **Kind**: git repo / worktree container / non-git folder / loose files
- **Git**: current branch, detached flag, upstream, ahead/behind (vs last fetch), staged/unstaged/untracked/conflicted counts,
  stash count, last commit (sha, date, subject, author), all remotes (normalised to `host/owner/repo`), local branches with upstream state
  (`[gone]`, merged into default branch, last commit date), worktrees (path, branch, prunable flag)
- **Description inputs**: README first paragraph, plus manifest names/descriptions (`*.csproj`, `package.json`, `pyproject.toml`, `Cargo.toml`, `*.sln`)
- **Size** (daily, not every scan): total size, plus reclaimable build dirs (`bin/obj/node_modules/.venv/target/dist/.next`)
- **Last modified** (newest mtime, ignoring `.git` and build dirs)

Cadence: full scan every 15 min, a debounced rescan of a single repo on file changes, and size scan daily.
Auto-fetch (on by default, see §10): `git fetch --prune` every 4h so ahead/behind and `[gone]` stay accurate.

## 4. GitHub data (hub)

Polled every 5 min with GraphQL and ETag-aware REST where it helps:

- **My PRs**: `is:open is:pr author:@me`, `review-requested:@me`, `assignee:@me`, with CI status (statusCheckRollup), review decision, draft, mergeable, updatedAt
- **Issues**: `is:open is:issue assignee:@me`, plus `mentions:@me` (last 14 days)
- **Recent repos in orgs**: for each org (configurable exclude list), repos ordered by `PUSHED_AT` within the last N days, with who pushed, default-branch head, open PR count, archived flag
- **Join to local**: remote URLs from agents are matched to GitHub repos, which gives:
  - "cloned on: laptop (dirty, 2 ahead), desktop-pc (clean, 5 behind)"
  - "active in org but not cloned anywhere"
  - "cloned locally but archived/deleted on GitHub"

## 5. Cleanup rules (hub, `DevDash.Core`)

Each rule returns findings with a severity, an explanation, and an ordered list of commands: each has a label and a kind
(Inspect, Fix or Open), inspect before fix. Commands are for copying. A Fix may also carry a typed agent action (phase 6, §11).

| Rule | Example |
|---|---|
| Non-git folders in Developer | `Client Workshop`, `Vendor_SDK_v3-…` (loose PDFs) |
| Stale or prunable worktrees | `copilot-worktrees/`, `core.worktrees/copilot-worktree-2026-04-10…` |
| Local branches merged into the default branch | `git branch -d …` |
| Branches whose upstream is `[gone]` | |
| Uncommitted changes older than N days | |
| Unpushed commits / no upstream | |
| Stashes older than N days | |
| Large reclaimable build dirs | `node_modules`, `bin/obj` > 500 MB |
| Repo untouched for more than 6 months | candidate to archive or delete locally |
| Same repo diverged across machines | different HEADs, both ahead |
| Remote archived or deleted on GitHub | |
| Stale PRs (draft or no activity > 30d), PRs with failing CI | |
| Duplicate clones on one machine | `widget` vs `Widget-old`? (same remote) |

Findings can be snoozed or dismissed (stored per finding fingerprint).

## 6. UI (Blazor)

- **Overview**: machines (online / last seen), attention list (failing CI, review requests, dirty-and-old repos), counts, daily brief
- **Local**: repo × machine matrix with status chips; filters (dirty, ahead, behind, stale); search
- **Repo detail**: description, per-machine state, branches/worktrees, linked GitHub PRs/issues, findings
- **GitHub**: recent org activity, my PRs (grouped: needs my review / mine / CI failing), my issues
- **Cleanup**: findings grouped by rule, with snooze, dismiss and copy-command buttons
- **Machines**: agent versions, scan health, issue and revoke API keys
- **Settings**: orgs exclude list, thresholds, LLM on/off and model

Dark mode by default. Live updates over SignalR when a snapshot or GitHub poll lands.

## 7. Generative features (Claude API)

1. **Repo one-liners** where there is no GitHub description: input is README excerpt + manifest names + top-level tree; cached by content hash.
2. **Daily brief** (on demand, plus a morning schedule): what changed across machines in the last 24h, what needs attention, suggested cleanup order.
3. **Finding explanations**: for complex cases (diverged clones), a short "what happened and what to do".

Uses structured outputs for (1) and (3). The API key comes from a hub secret (`ANTHROPIC_API_KEY`). The feature is fully optional; the hub works without the key.

## 8. Security

- The hub is only reachable on the tailnet (no Funnel). The browser user is checked against an allowlist from Tailscale identity headers.
  The hub trusts those headers only on requests arriving through the sidecar's serve proxy.
- Agent keys are random 32-byte tokens, stored as SHA-256 hashes, sent as `Authorization: Bearer`, and scoped to one machine id.
- Secrets (GitHub PAT, Anthropic key, TS auth key) live in Docker secrets or a `.env` that isn't committed.
- By default the agent never modifies working trees or branches. Its only write is `git fetch --prune`, which updates remote-tracking refs.
  Agent actions (phase 6) are a per-machine opt-in with their own rules; see §11.
- An agent only installs a release whose `checksums.txt` is signed by the release key compiled into it (§12). The hub can ask for a
  version; it can never supply a binary or a URL.

## 9. Build phases

| Phase | Deliverable | Done when |
|---|---|---|
| 1 | Solution skeleton, Contracts, Core parsers + tests, Agent scan (`scan --dry-run` prints JSON), Hub ingest + SQLite + Local page | `dotnet run` both on one machine and see ~/Developer in the browser |
| 2 | GitHub poller, PRs/Issues/Recent pages, local↔remote join | PRs and org activity visible and linked to local clones |
| 3 | Cleanup rules engine + Cleanup page + snooze/dismiss | Findings for one machine look right (worktrees, non-git folders) |
| 4 | Dockerfile, compose + Tailscale sidecar, identity-header auth, agent API keys, installers for macOS / Windows / Linux | Hub on the tailnet; agents from 2+ machines reporting |
| 5 | Claude descriptions, daily brief, finding explanations | One-liners filled for repos without descriptions |
| 6 (built) | Agent actions: allowlisted fixes queued from the dashboard, run by the machine's agent (polling, not SignalR; see §11) | Allowlisted, confirmed, audited |
| 7 (built) | Agent self-update from signed releases: by hand, from the dashboard, or automatic; rolls back by itself (see §12) | An agent moves to a new release and back without anyone touching the machine |

## 10. Decisions made (2026-11-17)

1. **Hub host**: decide later. Phases 1–3 run locally with `dotnet run`. Phase 4 adds the container, which must not assume a particular host.
2. **GitHub token**: reuse the `gh` token. The hub reads `GITHUB_TOKEN` from config or env; when that is unset in local dev, it falls back to running `gh auth token`.
3. **Orgs**: all orgs you belong to, discovered automatically, with an optional exclude list in settings.
4. **Auto-fetch**: on everywhere. The agent runs `git fetch --prune --quiet` per repo every 4h (configurable, can be turned off), with a timeout and no credential prompts (`GIT_TERMINAL_PROMPT=0`).
5. **Build order**: phases 1–3 first, then a review before phases 4–5.

## 11. Agent actions (phase 6, built 2026-11-18)

**Cleanup commands.** A finding has `Commands` (`FindingCommand`: label, command, kind, optional `AgentAction`, optional machine for
findings that span machines). `CleanupScript` builds the per-machine "copy as script" text from the Fix commands. Fingerprints did not change.

**Flow.** Dashboard Run button → confirmation dialog (machine, repo, exact git command) → `ActionQueue` row (Queued) → the agent's
`ActionWorker` polls `POST /api/v1/actions/claim` every 30s → row becomes Claimed → `ActionValidator` + `ActionExecutor` run it →
`POST /api/v1/actions/{id}/result` (Succeeded / Failed) → the agent rescans that entry at once and pushes a snapshot.
Polling keeps the "hub never dials agents" rule from §1; SignalR was not needed.

**Security model.**

1. Off by default per machine: `Agent:Actions:Enabled` / `--enable-actions`. The agent reports it in `MachineInfo.ActionsEnabled`; the hub
   shows Run buttons only for machines that report it and are online. `Hub:Actions:Enabled` is a hub-wide off switch. The executor checks
   the local setting itself, so a hub that believes otherwise changes nothing.
2. Typed actions only (`DevDash.Contracts/Actions.cs`): DeleteBranches (`branch -d`), RemoveWorktree (no `--force`), PruneWorktrees, Fetch,
   Push (same-name refspec, never forced, fetch first and refuse if behind), DropStash (expected commit must match). No command string ever
   crosses the wire, and there is no pull, rebase, reset, `-D` or force. `AgentActions.GitArgs` is the single source of both what the agent
   runs and what the dialog shows.
3. The agent re-validates locally: repo is the top of a git working tree inside its scan roots (symlinks resolved, scanner depth), branch
   names pass `git check-ref-format` and exist, never the default or a checked-out branch, worktree is a linked, unlocked, on-a-branch
   worktree of that repo. Git runs through `GitRunner` (argument list, no shell, no prompts, timeouts, `core.fsmonitor=false`).
4. Bound to a machine: the claim and result endpoints use the machine the agent key is bound to. Claiming is one conditional update, so an
   action is handed out at most once and never retried. Queued actions expire after 10 minutes; a claim with no result is marked failed after 5.
5. Confirmed and audited: queueing only happens from the Blazor dialog (there is no browser HTTP endpoint for it) and records the
   `BrowserIdentity` login. The `AgentActions` table keeps queued/claimed/completed times, status, exit code and up to 4,000 characters of
   output for the last 500 actions; the Machines page and `GET /api/v1/actions` show it.

**Schema.** The hub still uses `EnsureCreated`. `SchemaUpgrade.CreateMissingTablesAsync` runs after it on every start and creates any table
or index the model has and the database lacks, from EF's own create script, so existing databases gain `AgentActions` without a reset.
It does not add columns to existing tables.

## 12. Agent self-update (phase 7, built 2026-11-18)

**One updater, three triggers.** `AgentUpdater` (`src/DevDash.Agent/Updates`) is used by `devdash-agent update`, by the dashboard's Update
button, and by auto mode. It resolves a version, downloads `checksums.txt`, `checksums.txt.sig`, `version.txt` and the platform archive from
`github.com/<repo>/releases/download/v<version>/` (or `latest/download/`), verifies, unpacks beside the installed program, test-runs the new
program (`--version` must print the expected version), swaps, and asks to be restarted.

**Trust.** The release workflow signs `checksums.txt` with ECDSA P-256 / SHA-256; the private key is the `DEVDASH_RELEASE_SIGNING_KEY`
secret and the public key is a constant in `ReleaseSigning.cs` (and in both install scripts; `scripts/release-signing-key.sh` keeps the
three in step). `version.txt` is listed in `checksums.txt`, so the version a set of files claims is signed too: a genuine older release
cannot be passed off under a newer version's address. The agent refuses a downgrade, an unsigned release, and anything that does not match
its checksum. The only way to change the trusted key is to compile a different one in (`-p:DevDashUpdatePublicKeyFile`, used by the CI
update test); there is no runtime setting, and `devdash-agent verify-release`, run by the release workflow with the binaries it just built,
rejects a binary built that way and a signature that does not match the source.

**Trigger from the hub.** A new typed action, `AgentActionKind.UpdateAgent { Version }` (strict semver, no repository path), travels through
the existing queue and claim endpoint, so it is confirmed in a dialog and audited like any other action. It is gated separately from git
actions, on both sides: the agent accepts it when its own `Agent:Updates:Mode` is `notify` (default) or `auto`, whatever `Actions:Enabled`
says, and runs git actions only when `Actions:Enabled`, whatever the update mode says. The agent polls the claim endpoint when either is
on; the hub hands out only the kinds the machine's last snapshot said it accepts (`MachineInfo.ActionsEnabled`, `MachineInfo.UpdateMode`),
and the executor checks the local switch for the action's own kind again. The hub only ever asks for the release it currently sees as
latest (`ReleasePoller`, hourly, unauthenticated if need be), and only for a machine that is behind it.

**Swap and restart.** macOS and Linux: copy the current file to `.old`, rename the new one over it (atomic). Windows: rename both
`devdash-agent.exe` and `devdash-agentw.exe` to `.old`, move the new pair in; a failure part way puts every file back. The agent then
exits with code 75 and the service manager starts the new program: launchd `KeepAlive`, systemd `Restart=always`, and on Windows a
detached `relaunch-service` helper that waits for the old process and runs the scheduled task (with the task's restart-on-failure as a
second line). Exiting was chosen over asking the service manager to restart the service from inside it because the agent then shuts down
in its own time, after the action's result is reported, and the manager does only what it already does after a crash.

**Roll back.** `UpdateGuard` keeps `devdash-agent.update.json` beside the program: the pending update (from, to, start count, the files and
their `.old`), the last outcome, and the last version that failed. At the very start of `run`, a pending version counts the start; the
first snapshot the hub accepts confirms it and deletes `.old`. More than 3 starts without that, or 5 minutes running without it, and the
new version restores `.old` (Windows: the running program is moved aside as `.bad`), exits, and the restored version reports the roll back
in `MachineInfo.LastUpdate`. While pending, the first scan skips fetching and sizes so the confirming snapshot lands quickly. Limits: the
decision is made by the new program, so one that cannot even start is caught only by the test-run before the swap; and a roll back because
the hub was down is harmless but real, which is why auto mode installs only when the hub answers and waits a day before retrying a version.

**Compatibility.** No snapshot format change: `UpdateMode` and `LastUpdate` are optional fields, so `SnapshotSchema.Version` stays 1 and a
0.1.0 hub accepts a newer agent. The hub accepts `MinSupportedVersion..Version`, the rule being that a breaking change raises `Version` and
the minimum follows one release later. A 0.1.0 agent sends no `UpdateMode`: the hub reads that as "cannot self-update", offers no button
and never hands it an `UpdateAgent` action. Those machines need one reinstall.

**Tests.** Unit tests cover signature and checksum verification (good, changed asset, changed checksums, other key, no signature, replay),
version ordering, swap planning for both platforms, the confirm/roll back state machine with a fake clock, and the two-switch policy on
both sides. `scripts/test-ota.sh` does it for real with two published builds, in CI on Linux, macOS and Windows.
