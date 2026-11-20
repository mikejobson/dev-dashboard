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

Machine: hostname, OS, Tailscale name/IP (`tailscale status --json` when available), agent version, scan roots, scan duration,
and the installed toolchains (§17): .NET SDKs, Node, Python, Go, Rust, Java, with the versions the version managers hold.
Collected at start and every 6 hours, not per scan.

For each top-level entry under each root (default `~/Developer`, plus `~/.t3/projects` when T3 Code has made it (§18), configurable,
depth configurable for `*.worktrees` folders):

- **Kind**: git repo / worktree container / non-git folder / loose files
- **Git**: current branch, detached flag, upstream, ahead/behind (vs last fetch), staged/unstaged/untracked/conflicted counts,
  stash count, last commit (sha, date, subject, author), all remotes (normalised to `host/owner/repo`), local branches with upstream state
  (`[gone]`, merged into default branch, last commit date), worktrees (path, branch, prunable flag)
- **Description inputs**: README first paragraph, plus manifest names/descriptions (`*.csproj`, `package.json`, `pyproject.toml`, `Cargo.toml`, `*.sln`)
- **Size** (daily, not every scan): total size, plus reclaimable build dirs (`bin/obj/node_modules/.venv/target/dist/.next`, and the
  framework caches `.angular`, `.nuxt`, `.svelte-kit`, `.astro`, `.dart_tool`)
- **Last modified** (newest mtime, ignoring `.git` and build dirs)
- **Safety facts** (§14): which branches are on a remote and how many commits are not, local-only tags, ignored files that are
  not build output, untracked secret-looking files, submodule state. Names and counts only
- **Pinned tool versions** (§17): `global.json` (`sdk.version`, `rollForward`, `allowPrerelease`), `.nvmrc`, `.node-version`,
  `package.json` (`engines.node`, `volta.node`), `.python-version`, `.tool-versions`. The version text only
- **Commit activity** (§17): commits per day for the last year, and how many of them carry the repo's own `user.email`
  (a count; the email stays on the machine). One `git log`, repeated only when a ref moves
- **Dependency health** (§17, daily, in the background, never during a scan): from `dotnet list package` and `npm audit` /
  `npm outdated`, counts by severity, how many are outdated, and at most 10 package names with advisory ids

Cadence: full scan every 15 min, a debounced rescan of a single repo on file changes, size scan daily, toolchains every 6 h,
dependency checks once a day per repo.
Auto-fetch (on by default, see §10): `git fetch --prune` every 4h so ahead/behind and `[gone]` stay accurate.

## 4. GitHub data (hub)

Polled every 5 min with GraphQL and ETag-aware REST where it helps:

- **My PRs**: `is:open is:pr author:@me`, `review-requested:@me`, `assignee:@me`, with CI status (statusCheckRollup), review decision, draft, mergeable, updatedAt,
  and the head commit (`headRefOid`) and `isCrossRepository`, which is what puts a PR on the right branch of the branch map (below)
- **Issues**: `is:open is:issue assignee:@me`, plus `mentions:@me` (last 14 days)
- **Recent repos in orgs**: for each org (configurable exclude list), repos ordered by `PUSHED_AT` within the last N days, with who pushed, default-branch head, open PR count, archived flag
- **Join to local**: remote URLs from agents are matched to GitHub repos, which gives:
  - "cloned on: laptop (dirty, 2 ahead), desktop-pc (clean, 5 behind)"
  - "active in org but not cloned anywhere"
  - "cloned locally but archived/deleted on GitHub"

**On the branch map (built 2026-11-20).** `BranchPullRequests` (hub, pure) decides which open PR belongs to which branch, within one repo
(`RepoCatalog` has already joined PRs to the repo by `github.com/owner/repo`): by commit when the branch is at the PR's head commit, whatever
either is called, and otherwise by name against the branch's name on its remote (what it tracks, else its own). A PR from a fork matches by
commit only, since its branch name belongs to the fork. `BranchGraph.PlacePullRequests` uses the same rule: the commit the PR is at when
the map has it with a name on it, else the remote-tracking ref of that name, else the local branch. `PrChip` draws it (CI dot, number,
title, draft/review state, link) on the map and on RepoDetail's branch rows, with **≠** when the commit it sits on is not the PR's head.
No extra requests: two more fields on the PR search that already asks for `statusCheckRollup`. Only PRs the poll knows (mine, assigned,
review requested) are shown; listing every open PR of every cloned repo would be one more paged query per repo and was left out.

### The commit graph behind the branch map (merged branches fixed 2026-11-20)

`CommitGraphCollector` (agent) sends, per repository: the commits off the trunk (at most 250), the trunk's newest 20 first-parent
commits with their position counted from the root (`TrunkPos`), and the single commits the rest hangs off (fork points, merged tips).
The trunk is the default branch, or `develop` when that is where the newest work is (`RepoScanner.GraphTrunkAsync`).

- **Merged tips are connected.** A merged tip is not on the first-parent line, and in a repository of stacked pull requests it is not
  a parent of any trunk commit either: it is reachable only through second parents and merges no branch points at. For those (and for
  fork points inside merged history) the collector reads the trunk's recent history once (`git log --date-order --format=%H %P`, bounded
  to what the newest 500 first-parent commits brought in and 6,000 commits) and works out in memory the oldest first-parent commit
  that contains each one and the shortest run of commits up to it. It sends that run (at most 40 commits per tip, 200 per repository)
  and marks the tip `JoinsTrunkAt = <merge sha>`; the merge itself is always sent. Past the caps only `JoinsTrunkAt` goes out. Both
  fields are optional: schema version unchanged, older hubs ignore them. Still one cached result per ref state.
- **Hub ordering (`BranchGraph.Order`).** Newest first, as `git log --graph --date-order` reads: a row goes in once every row that
  descends from it is above it, and of the rows free to go next the newest wins (an uncommitted-changes row or a gap sits right on top
  of its commit; "older commits" closes the map). Branches worked on alongside the trunk interleave with it by date, at the cost of a
  few more lanes. (0.6.0 first grouped each merged branch under its merge; users read dates down the page, so that was dropped.)
- **Join links.** A merged commit with `JoinsTrunkAt` and no line of children up to the trunk sits at its date and is joined to that
  trunk commit with a dashed line ("merged there, through commits not shown").
- **Merged means merged into the trunk.** `BranchInfo.MergedIntoDefault` keeps its meaning for cleanup. When the trunk is not the
  default branch the agent also sends `MergedIntoTrunk` (optional), and the map's dimming and "hide merged" use it; the default branch
  and the trunk branch never count as merged. With merged branches hidden, a trunk merge no longer pulls in what it merged: merged
  history is only kept where something still shown needs it (a checked-out branch, the fork point of an open branch) together with
  its way up to the trunk, and the upstream ref of a hidden branch is hidden with it unless it has moved on to unmerged commits.
- **"N commits ahead" between devices.** Counted on the map this undercounts, because the map holds the first-parent line and not what
  it merged. `BranchGraph.Describe` uses git's own ahead/behind when some device has a branch at one of the two commits whose upstream
  ref is at the other; otherwise the map's count, as "at least N" unless every commit between the two is in the map (no gap, no
  parent that was not sent).

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
| Untracked secret-looking files not in .gitignore | `.env.local`, `server.pem` (names only, §14) |
| Large reclaimable build dirs | `node_modules`, `bin/obj` > 500 MB |
| Pinned tool version not installed on that machine | `global.json` asks for 10.0.1xx, the machine has 9.0.305 (§17) |
| Dependencies with known vulnerabilities (high or critical) | `Some.Json 12.0.1`, GHSA id (§17) |
| Repo untouched for more than 6 months | candidate to archive or delete locally |
| Same repo diverged across machines | different HEADs, both ahead |
| Remote archived or deleted on GitHub | |
| Stale PRs (draft or no activity > 30d), PRs with failing CI | |
| Duplicate clones on one machine | `widget` vs `Widget-old`? (same remote) |

Findings can be snoozed or dismissed (stored per finding fingerprint).

## 6. UI (Blazor)

- **Overview**: machines (online / last seen), attention list (failing CI, review requests, dirty-and-old repos), counts, daily brief,
  and a year of commits as a calendar heatmap (§17)
- **Local**: repo × machine matrix with status chips; filters (dirty, ahead, behind, stale); search
- **Repo detail**: description, per-machine state, branches/worktrees, linked GitHub PRs/issues, findings, its own heatmap,
  pinned toolchains per machine, dependency health
- **GitHub**: recent org activity, my PRs (grouped: needs my review / mine / CI failing), my issues
- **Safety**: what exists only on one machine, and what is safe to remove with the space it frees (§14)
- **Issues**: open issues of cloned GitHub repos, T3 threads started for them, and the label trigger's settings (§18)
- **Cleanup**: findings grouped by rule, with snooze, dismiss and copy-command buttons
- **Alerts**: what is alerting now, channels with a test button, triggers, quiet hours, delivery log (§16)
- **Machines**: agent versions, scan health, toolchains per machine and the pins they do not meet, issue and revoke API keys
- **Command palette** (⌘K / Ctrl+K, any page): repos, pull requests, machines, pages and a few actions (§17)
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
`ActionWorker` polls `POST /api/v1/actions/claim` (every 30s; a long poll since 0.5.0, see §13) → row becomes Claimed → `ActionValidator` + `ActionExecutor` run it →
`POST /api/v1/actions/{id}/result` (Succeeded / Failed) → the agent rescans that entry at once and pushes a snapshot.
Polling keeps the "hub never dials agents" rule from §1; SignalR was not needed.

**Security model.**

1. Off by default per machine: `Agent:Actions:Enabled` / `--enable-actions`. The agent reports it in `MachineInfo.ActionsEnabled`; the hub
   shows Run buttons only for machines that report it and are online. `Hub:Actions:Enabled` is a hub-wide off switch. The executor checks
   the local setting itself, so a hub that believes otherwise changes nothing.
2. Typed actions only (`DevDash.Contracts/Actions.cs`): DeleteBranches (`branch -d`), RemoveWorktree (no `--force`), PruneWorktrees, Fetch,
   Push (same-name refspec, never forced, fetch first and refuse if behind), DropStash (expected commit must match). No command string ever
   crosses the wire, and there is no rebase, reset, `-D` or force; the only pull is the fast-forward added in 0.6.0 (below). For these
   kinds `AgentActions.GitArgs` is the single source of both what the agent runs and what the dialog shows.
3. The agent re-validates locally: repo is the top of a git working tree inside its scan roots (symlinks resolved, scanner depth), branch
   names pass `git check-ref-format` and exist, never the default or a checked-out branch, worktree is a linked, unlocked, on-a-branch
   worktree of that repo. Git runs through `GitRunner` (argument list, no shell, no prompts, timeouts, `core.fsmonitor=false`).
4. Bound to a machine: the claim and result endpoints use the machine the agent key is bound to. Claiming is one conditional update, so an
   action is handed out at most once and never retried. Queued actions expire after 10 minutes; a claim with no result is marked failed after 5.
5. Confirmed and audited: queueing only happens from the Blazor dialog (there is no browser HTTP endpoint for it) and records the
   `BrowserIdentity` login. The `AgentActions` table keeps queued/claimed/completed times, status, exit code and up to 4,000 characters of
   output for the last 500 actions; the Machines page and `GET /api/v1/actions` show it.

**More kinds (0.6.0).** Four kinds that are more than one fixed git command, run by `ProjectActions` after the same gate, switch and
`ActionValidator` as the rest. `AgentActions.GitArgs` throws for them; `CommandLine` gives the equivalent a person would type, for the dialog
and the audit trail.

| Kind | Fields | The agent refuses unless |
|---|---|---|
| `RemoveProject` | path, `ExpectedFingerprint`, `AcknowledgedCaveats` | see §15 |
| `Clone` | path, `Url`, optional remote name | see §15 |
| `CleanBuildDirs` | repo, `BuildDirs` (1 to 25 absolute paths) | each is strictly inside the repo with no `.git` segment; every step from the repo down is a real folder, not a link, and the resolved path is still inside the repo and a scan root; its name is build output by `BuildOutput.IsReclaimable` against its real siblings (`bin` only beside a project file, and so on); `git ls-files -- <name>` run in its parent prints nothing (so a folder inside a submodule is asked of the submodule). One bad folder refuses the whole action. |
| `PullFastForward` | repo, remote, branch | the remote and branch exist and the branch's upstream is `<remote>/<branch>`; after `git fetch --prune <remote>` the branch is behind and not ahead; where it is checked out, that working tree is one the agent scans and has no staged, unstaged or conflicted changes |

`CleanBuildDirs` is a real delete (`ProjectActions.DeleteTree`, which removes a link as a link and never follows one): build output is
regenerable, and the UI says "for good" wherever the button is. `PullFastForward` runs `git merge --ff-only @{u}` in the working tree that
has the branch checked out, or `git fetch <remote> refs/heads/b:refs/heads/b` when none has; both are refused by git itself for anything
but a fast-forward, the agent's own ahead/behind check only makes the message clearer. `SyncOffers` (hub, pure) decides where **Push from**
and **Pull on** are offered: upstream of the same name, ahead xor behind; a pull into a working tree the last scan saw dirty is shown
disabled with the reason. After any of these the agent rescans the entries it touched, as for the older kinds.

**Capabilities.** `MachineInfo` gained `AcceptsRemoveProject`, `AcceptsClone`, `AcceptsCleanBuildDirs`, `AcceptsPull` (optional, schema
still 1; today all four are `Actions:Enabled`). `AgentActions.Accepts(machine, kind)` is the one rule: the hub offers a button, queues, and
hands out a row only when it holds (`ActionQueue.Allows`, also checked at claim time, because an agent that predates a kind cannot even
deserialize a claim that names it). `WhyNotAccepted` tells "actions are off there" from "update the agent to 0.6.0", and that sentence is
the tooltip of the disabled button. New kinds are appended to the enum, which is stored and sent by name.

**Buttons outside findings.** `ActionBoard` (scoped, like `RefreshBoard`) and `ActionButton` give any page a confirm-then-queue button
with the same dialog and status chips as a finding's Run button; the Safety page has its own dialog for removal (§15).

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

## 13. Refresh and long-poll delivery (0.5.0, built 2026-11-19)

**Refresh.** A new typed action, `AgentActionKind.Refresh { RepoPath }`: rescan the top-level entry that holds that path, now. It is not a
git action (`AgentActions.IsRepoAction` is false), so neither side gates it on the actions switch. Agent: `ActionValidator.RefreshRefusal`
(plain absolute path, inside a scan root after resolving symlinks, not a dot entry) → `RescanRequests.Refresh` hands the batch to the scan
loop → `AgentWorker` rescans each entry with `SizeMode.Force` and, only when `Agent:AutoFetch:Enabled`, a `ForcedFetch` (skips the 4 h
interval; once per common dir per refresh, so a repository and its worktrees fetch once) → one push → the action's result. The scan loop
stays the only scanner and the only writer of the last snapshot; refreshes wait in their own table (a full scan clears the file-change
set, not that one), requests for one entry share one rescan, and a refresh stuck behind a long full scan reports "busy" after 4 minutes
and still happens afterwards. A failed fetch or a failed push is a failed refresh.

**Who can be asked.** `MachineInfo.AcceptsRefresh` (optional field, schema still 1) is true when the agent polls at all
(`ActionWorker.ShouldPoll`: actions on, or updates notify/auto). `RefreshGate` turns that and the agent version into "offer it", "update
the agent on X" (before 0.5.0) or "this agent doesn't check for requests". The hub never hands a Refresh to an agent that did not say it
takes them: an older agent could not even parse the kind.

**Long poll.** `POST /api/v1/actions/claim?wait=N` (N ≤ 60): `ActionQueue.ClaimAsync(machine, wait, ...)` takes the machine's
`ActionSignals` task, looks, and if there is nothing waits on that task, the deadline, the request and `ApplicationStopping`. Queueing
pulses the machine's signal. No `wait`: answered at once, as before. The agent (`ActionWorker.NextDelay`) asks again immediately after work
or after a held poll, keeps the 30 s cadence when an empty answer came straight back (an older hub), and backs off 2 s → 30 s on errors.
`ActionsClient` has no client-wide timeout; a claim gets wait + 20 s. Still agent-initiated only, so §1's "hub never dials agents" holds.

**Audit.** Refresh rows live in `AgentActions` like any action (who, when, result) but are trimmed separately (last 100) and shown on the
Machines page as one folded summary under the list of fixes and updates, never in it. `GET /api/v1/actions` returns everything.

## 14. Safety model: "only copy" and "safe to remove" (0.6.0, built 2026-11-19)

Part 1 of the removal work: the facts and the verdict. Nothing is deleted yet.

**The question.** For each working tree on each machine: does it hold the only copy of anything, and if not, would deleting the folder
lose anything at all? The answer is `RepoSafety.Assess` in `DevDash.Core/Safety`, pure over a `DevEntry`, so the hub and (later) an agent
about to remove a folder reach the same verdict from the same scan.

**Verdicts.** Each reason has an effect, and the worst effect present decides:

| Verdict | When | Reasons (`SafetyReasonKind`) |
|---|---|---|
| `AtRisk` | any *risk*: work that exists only here | `NoRemote`, `UnpushedCommits`, `LocalOnlyBranch`, `DetachedHead`, `UncommittedChanges`, `UntrackedFiles`, `Stashes`, `LocalOnlyTags`, `SubmoduleUnpushed`, `SubmoduleDirty` |
| `Unclear` | no risk, but an *unknown* or a *blocker* | `OldAgent`, `BranchesUnknown`, `TagsUnknown`, `SubmoduleUnknown`, `NotARepo`; `LinkedWorktrees` |
| `SafeWithCaveats` | only *caveats* | `IgnoredFiles` (ignored files that are not build output, by name) |
| `Safe` | no reasons at all | |

The rule that matters: **never safe without the facts.** A snapshot from an agent that predates the fields (`GitState.Safety` is null)
is `Unclear`, as is a repo with tags that were never compared with the remote. Risks carry a `RiskLevel` (High: commits on no remote;
Medium: uncommitted changes, stashes, local-only tags; Low: untracked files), a `Since` date, and names where there are any. The
assessment adds the oldest risk date, last touched, total and reclaimable size, and a `Fingerprint` of the facts (not of the wording, which
ages) for a later step to quote and re-check.

**New snapshot fields** (all optional; `SnapshotSchema.Version` stays 1, older hubs ignore them, older agents send none):

- `BranchInfo.OnRemote`, `UnpushedCount`, `OldestUnpushedAt`: from one `git log --branches HEAD --not --remotes` per scan. A commit on
  a remote has all its ancestors there, so a tip missing from that list is contained in some remote-tracking ref (this is what makes a
  merged-then-deleted branch safe), and walking a branch's parents inside the list counts exactly its only-here commits. Capped at 2,000
  commits; beyond that branches are flagged but not counted.
- `GitState.Safety` (`SafetyFacts`): `TagCount`, `LocalOnlyTags`, `RemoteTagsCheckedAt`, `BranchesChecked`, `HeadOnRemote`,
  `IgnoredFiles`, `SecretFiles`, `Submodules`, each list capped at 50 names with its real count beside it.
- `GitState.MainWorktreePath`: on a linked worktree, the main working tree it belongs to.

**How the agent gets them, cheaply.** `SafetyCollector`, called from `RepoScanner`:

- *Ignored files:* `--ignored` on the `git status` the scan already runs. A path is dropped when a segment is build output by the size
  scanner's own rules (`BuildOutput.IsReclaimable`, moved to Core and shared with `TreeWalker`) or a tool cache (`BuildOutput.IsCache`), or
  when it is a folder holding nothing but those (git reports `tools/` when all it has is `tools/__pycache__/`).
  A third list, `BuildOutput.IsRecreated`, is asked here and nowhere else: folders a restore or a test run writes again under names too
  ordinary to delete on sight. NuGet's `packages` (only with a `*.sln` or `packages.config` beside it or in the repository root),
  `test-results` and `playwright-report` (beside a `package.json`), `coverage` (beside a `package.json`, `pyproject.toml`, `setup.py`,
  `*.sln` or `*.csproj`). They stop being a caveat, but they are **not** `IsReclaimable`: the size scan does not count them and
  `CleanBuildDirs` refuses them, because a JavaScript monorepo keeps its source in `packages/`. `.idea`, `.vscode`, `.env` and `.claude`
  are on none of the lists and stay caveats.
- *Secrets:* untracked paths from the same status, matched by file name (`SecretFiles.LooksSecret`). Git reports an untracked folder as
  one line, so those are walked, names only, at most 5,000 entries, skipping ignored paths and nested repositories.
- *Tags:* local tags from `for-each-ref refs/tags`; the remote's from `git ls-remote --tags --refs`, the one new network call. It runs
  only in a scan that may fetch on a machine with auto-fetch on, and then only after a fetch, the first time, or once per fetch interval;
  the answer is kept in memory per repository, so watcher rescans reuse it and still notice a new local tag. Never listed → the tags are
  unknown, not "none". A repo with no tags never asks.
- *Submodules:* nothing unless `.gitmodules` exists; then `submodule status --recursive` and, inside each checked-out one (first 25),
  `status`, `log -1 --branches HEAD --not --remotes` and `stash list`.

`GitRunner`'s read-only allowlist gained `submodule status --recursive`; `ls-remote` has its own fixed-argument method, like fetch. An
ordinary repo costs two more git calls per scan, both local.

**Worktrees.** A linked worktree is a working tree, not a repository. `RepoSafety.AssessAll` assesses it on its own folder (uncommitted,
untracked, ignored, submodules, detached HEAD) and leaves branches, stashes and tags to the main working tree, because `git worktree
remove` keeps them; a worktree whose main working tree is outside the scan roots reports them itself (`IncludesRepository`). A main
working tree with live linked worktrees is blocked. `RepoIdentity.MainWorkingTrees`/`MainOf` give a linked worktree its main repo's key,
so `RepoCatalog` and the cleanup findings show one repo, with its worktrees nested under it (before, a repo with no hosted remote listed
each worktree as a separate project). "Stale" is defined once, in `WorktreeState.Classify`, used by `StaleWorktreeRule` and by the chips.

**Hub.** `SafetyService` joins the assessments to machines; `SafetyView` sorts and groups (pure, tested). `/safety` shows At risk
(grouped by machine or repo, oldest or most severe first), Safe to remove (longest untouched first, space freed, total) and a folded
"not sure" list. The navigation count is repos with a High risk. `SafetyBadge` puts the verdict beside each device on the repo page.
The Remove and Remove N buttons are part 2: their places are marked `data-safety-placeholder` in `Safety.razor`.

**Limits.** Containment is against remote-tracking refs as last fetched, so it is as fresh as the last fetch. A tag is "on the remote"
by name and object as of the last listing. The ignored-file list is only as good as the build-output list: anything not on it is a
caveat, which errs towards asking. The secrets rule goes by name and finds nothing inside a file.

## 15. Removing a project and cloning it back (0.6.0, built 2026-11-20)

Part 2 of the removal work: §14 says what is safe, this acts on it. Everything goes through the action queue of §11.

**Remove.** `AgentActionKind.RemoveProject { RepoPath, ExpectedFingerprint, AcknowledgedCaveats? }`. The Safety page's row button and its
"Remove N safe projects" button both queue one action per working tree; the bulk button only takes `Safe` rows, because a
`SafeWithCaveats` row needs each caveat ticked in its own dialog.

*Hub, before queueing* (`ActionQueue.RemoveRefusal`): assess the path again from the snapshot as it is now; refuse unless removable, the
fingerprint equals the one sent, and every caveat name is acknowledged. A page left open cannot remove something that has since changed.

*Agent, before touching anything* (`ActionExecutor` then `ProjectActions.RemoveAsync`):

1. `Actions:Enabled`, the shape check, and `RepoRefusalAsync`: a plain absolute path, the top of a git working tree, inside a scan root
   after resolving links, at a depth the scanner looks at. The folder itself must not be a link.
2. `git fetch --all --prune --quiet` when there is a remote. A failed fetch is a refusal: the verdict would be about stale refs.
3. `MachineScanner.ScanRepoAsync` on that one folder, with `ScanRequest.JustFetched` so the scan does not fetch again and does list the
   remote's tags afresh, whatever the auto-fetch setting.
4. `RepoSafety.Assess`, with `includeRepository` false for a linked worktree, exactly as `AssessAll` does on the hub. Refuse unless
   `IsRemovable`; refuse if a caveat name is not in `AcknowledgedCaveats`; refuse if the fingerprint differs ("has changed since it was
   shown"). The fingerprint covers path, head, verdict, reasons with their names and counts, and every branch tip.

*Then:* a main working tree goes to the trash through `ITrash.Move`; a linked worktree goes through `git worktree remove -- <path>` (no
`--force`), run in its main working tree, which must itself be one the agent scans. Git deletes that folder; it never reaches the trash,
and the dialog says so. Afterwards the agent rescans the entry (and the main repo's), so the row disappears.

**Trash.** `ITrash` has one rule: move, or fail. Never copy-and-delete, never delete. `AtomicMove.Rename` calls `rename(2)` directly on
macOS and Linux (the framework's own move may copy across volumes) and `MoveFile` on Windows.

| OS | Where | Notes |
|---|---|---|
| macOS | `/usr/bin/trash <absolute path>` when the system has it; else, or when it fails with the folder still in place, `~/.Trash` (a folder on another volume: that volume's `.Trashes/<uid>`) | the tool gives Finder's "Put Back"; the rename does not, but the folder is in the Trash |
| Linux | `$XDG_DATA_HOME/Trash/files` + `info/<name>.trashinfo`; another file system: `<top>/.Trash-<uid>` with a relative `Path=` | the info file is created exclusively first and reserves the name |
| Windows | Recycle Bin via `SHFileOperationW` (`FOF_ALLOWUNDO`, no UI) | the shell deletes outright when the bin is off or too small, so `NukeOnDelete` and `MaxCapacity` are read first and removal is refused then; fixed local drives only |

A name already in the trash gets the time, then a counter, appended; nothing in the trash is ever replaced. `Agent:Actions:TrashDirectory`
swaps in `FolderTrash` (a plain folder on the same volume), which is also what every test uses.

*macOS, the system tool.* `MacTrash` runs `/usr/bin/trash` (fixed path, never `PATH`) through `ProcessStartInfo.ArgumentList`, no shell, 60 s
limit, with one argument: the folder's absolute path. The tool has no `--` (`trash -h` lists only `-s` and `-v`, and it takes `--` for a
file name), so the absolute path is what keeps a folder called `-rf` from being read as an option. Its exit code is not believed either
way: the move counts only if the folder is gone from its path afterwards. Still there after the tool exited (not installed, refused,
or it claimed success) means nothing moved, so the rename is tried; still there after a timeout is a failure with nothing else tried,
because what a killed tool was doing is not known. A `MacTrash` given a trash folder (tests) never runs the tool unless a stand-in is
passed with it, so no test can reach the real Trash.

**Tombstones.** `RemovedProjects` (new table; `SchemaUpgrade` adds it to an existing database): machine, path, name, repo key, remotes as
JSON, default branch, checked-out branch and head, size, action id, who, queued and removed times. The row is written when the removal is
queued, while the snapshot still knows the remotes, with `RemovedAt` null; `ActionQueue.SettleTombstonesAsync` confirms it when the agent
reports success and deletes it on failure, expiry or a lost result. A linked worktree gets none. `TombstoneStore.ListAsync` leaves out a
tombstone whose machine reports something at that path again. Shown as **Removed** on the Safety page and **Removed projects** on the
Machines page, each with Re-clone and Forget.

**Clone.** `AgentActionKind.Clone { RepoPath, Url, Remote? }`, run as `git clone [--origin <name>] -- <url> <path>` from the parent
folder, with its own timeout (`Agent:Actions:CloneTimeoutSeconds`, 15 min) and a longer lease on the hub (`Hub:Actions:CloneLeaseMinutes`).

The rule that matters: **the hub cannot make an agent clone an arbitrary URL.** Two independent checks:

- *Hub* (`ActionQueue.CloneRefusalAsync`): there is a confirmed tombstone for that machine and path, nothing is reported at the path, and
  the URL is one of the tombstone's remotes or a remote some machine reports now for the same hosted repo (`TombstoneStore.AllowedUrls`).
- *Agent* (`ActionValidator.CloneRefusal`): the hub's tombstone is not trusted. The agent keeps its own record of what it removed
  (`RemovalLog`, `agent-removed.json` beside the size cache) and accepts a URL only if it recorded it for that exact folder, or a
  repository in its last scan has it as a remote (`KnownRemotes`). Plus: a plain absolute path inside a scan root at scanner depth, no
  dot-folder, the parent exists and is not itself a repository, and nothing at the path (a dangling link counts as something).
  `AgentActions.Validate` rejects a URL that starts with `-`, has whitespace, or uses the `transport::address` form.

A clone that times out leaves a partial folder; it did not exist before, so it is all the clone's, and it still goes to the trash rather
than being deleted. A successful clone deletes the tombstone.

**Tests.** `ProjectActionTests` (agent, real temp repositories, trash in the temp folder): what is removed and everything that is
refused (stale fingerprint, each kind of only-copy work, a branch deleted on the remote since the last fetch, failed fetch, unacknowledged
caveats, paths outside the roots, links, a trash that fails), worktrees, the three trash layouts, clone allow-listing and path checks,
build-folder checks including links in and out, and each fast-forward case. `RemovalTests` (hub): queue-time refusals, capability gating at
queue and at claim, tombstone life cycle, the pages. `AgentLoopTests` runs remove then re-clone with the real agent against the real hub.

**Limits.** The Windows path is written to the documented shell API and is compiled and unit-tested only as far as its factory: it has not
been run against a real Recycle Bin. Between the agent's last check and the rename there is a window of milliseconds in which a file could
be written; it would go to the trash with the folder. A project removed on a machine whose agent then loses `agent-removed.json` cannot be
re-cloned from the dashboard unless another scanned repository uses the same URL; the Removed list still shows the URL to clone by hand.

## 16. Alerts (built 2026-11-20)

The hub pushes instead of waiting to be looked at. All of it is in `src/DevDash.Hub/Alerts`.

**Pipeline.** `AlertWorker` (BackgroundService) wakes on `DashboardNotifier` (snapshot, GitHub poll, action result, finding snoozed; not on
its own `Alerts` notifications), waits `DebounceSeconds`, and also runs every `EvaluateIntervalMinutes`, since "offline" and "older than 3
days" become true with nothing arriving. Each run is `AlertService.EvaluateAsync`, one at a time:

1. `AlertSettings.GetAsync`: the settings in force (below).
2. `AlertEvaluator.Evaluate` (pure): the hub's state → `AlertCandidate`s, each with a stable `Key`, title, body, hub path, severity.
3. `AlertPlanner.Plan` (pure): candidates against the open alerts in `AlertStates` → fresh, due, held (quiet hours), cleared.
4. `AlertSender.SendAsync` per active channel; each attempt is a row in `AlertDeliveries` (last 200).

Time comes from the injected `TimeProvider` throughout, so the tests move a clock instead of waiting.

**Triggers.**

| Trigger | Source | Key | Link |
|---|---|---|---|
| `at-risk` | `SafetyService` rows that are `AtRisk` with a risk of at least `Level` (default High: commits on no remote) whose `Since` is `Days` old | machine + path | `safety` |
| `machine-offline` | `MachineView.LastSeenAt` older than `Minutes`, not in `Ignore` | machine | `machines#machine-<id>` |
| `diverged` | active findings of `DivergedAcrossMachinesRule` | the finding's fingerprint | the repo page |
| `ci-failed` | `GitHubStore` PRs that are mine with `CiState.Failure` (drafts only if asked), unless the `FailingCiPullRequestRule` finding is snoozed or dismissed | repo + number | `github`, and the PR as `link` |
| `action-failed` | `AgentActions` rows that failed in the last 2 hours, Refresh excepted | action id | `machines` |
| `digest` | counts from the same inputs, once a day at `Time` | - | `safety` |

Keys hold nothing volatile (no counts, no ages), so the wording can change while the alert stays the same alert.

**Dedupe** is the `AlertStates` table: one row per open alert. A condition seen for the first time gets a row and is sent; while the row
is there it is not sent again, unless `RenotifyHours` has passed since `LastNotifiedAt`. When the condition is no longer among the
candidates the row is deleted, which is what makes its return a new alert. A failed action is an event, not a state: it never "clears",
so its row is kept for a week after it leaves the 2 hour window and is never sent twice. In quiet hours the row is still opened, with
`LastNotifiedAt` null, and the alert goes out at the first evaluation after they end if it is still a candidate. The same waiting state
covers "no channel is set up yet" and "every channel failed": the latter counts `Attempts` and backs off 1, 2, 4 … 60 minutes, giving up
after 8. More than `MaxPerEvaluation` (5) due at once are sent as one `summary` and all marked sent. The digest keeps its last-sent time
in a row of its own that the planner never clears, and ignores quiet hours.

Two things keep "offline" honest: the trigger has its own threshold (not `OnlineWindowMinutes`, which is about showing Run buttons), and
a machine is only called offline once the hub itself has been up for that long, unless its alert was already open before the restart.

**Channels.** All three are one JSON `POST` with a `Content-Length` (not chunked: small webhook receivers do not all read chunked bodies).

- *Webhook:* the payload in the README, optional `Authorization: Bearer`.
- *ntfy:* its JSON publishing form to the server root (`topic`, `title`, `message`, `priority`, `tags`, `click`, `actions`), so the topic
  is not in the address and the title is not a header that has to be ASCII.
- *Home Assistant:* the webhook payload to `<Url>/api/webhook/<WebhookId>`; an automation with a webhook trigger reads it as `trigger.json`.

Links are `Hub:PublicUrl` + the path. There is no fallback to the request's host or to any name in the code: with it unset, `url` is
left out (the payload still carries `path`) and the Alerts page says so.

**Settings.** `Hub:Alerts` binds to `AlertOptions`. The Alerts page edits a copy; `AlertSettings.SaveAsync` flattens it to configuration
keys (`Triggers:AtRisk:Days`) and stores only those that differ from what configuration says, as rows of `AlertSettings`. The settings in
force are configuration with those rows laid over it as one more configuration source, so an untouched setting keeps following
appsettings and the environment.

Secrets (`Channels:Webhook:Url`, `…:Webhook:Token`, `…:Ntfy:Topic`, `…:Ntfy:Token`, `…:HomeAssistant:WebhookId`) never go through the
flattening. They are saved only when typed, encrypted with ASP.NET Core data protection (the key ring is beside the database), and the
page gets a `SecretState` (set / set in configuration / not set), never a value. The key ring is persisted, not regenerated:
`Program.cs` calls `PersistKeysToFileSystem(<Hub:DataDirectory>/keys)` with a fixed application name, the image sets
`Hub__DataDirectory=/data`, and compose mounts the `devdash-data` volume there, so an image update keeps the keys and the secrets stay
readable (checked in part 4: `AlertSecretsRestartTests` starts a second hub on the same data directory and reads them back, and a hub
given the database without the keys loses the secrets quietly and can have them entered again). A Home Assistant address that contains
`/api/webhook/<id>` is split wherever it comes from, so the id is always in the secret. Delivery errors are built from the status code or
the exception text and scrubbed of every configured secret before they are stored or logged, and the alert `HttpClient` has request
logging removed, because the framework's own would print the address.

**Schema.** Three tables, added to an existing database by `SchemaUpgrade` like the earlier ones: `AlertSettings` (key, value, is-secret,
who, when), `AlertStates` (key, trigger, title, path, first/last seen, last notified, attempts), `AlertDeliveries` (when, channel,
trigger, title, ok, error, test).

**Tests.** `AlertEvaluationTests`: each trigger and its thresholds, key stability, the restart rule, the digest's wording, and the planner
(once, cleared and back, renotify, events, quiet hours, backoff, digest timing across a time zone). `AlertDeliveryTests`: each channel's
request against a fake `HttpMessageHandler`, failures with no secret in the message, then the real hub with an in-memory database and a
stubbed alert handler: once / clear / return, renotify, quiet hours, the summary, several channels with one failing, retry, no channel
then one saved on the page, each trigger end to end, settings overrides, secrets (not in the database in clear, not in the page), the
page, and the worker waking on a snapshot.

**Limits.** Quiet hours apply to every alert alike; there is no "wake me for this one". A channel that fails while another succeeds is
not retried for that alert: it reached you. `diverged` is the cleanup rule's definition (the same branch checked out on two machines, both
with unpushed commits, at different commits), which is narrower than the branch map's "diverged" warning. `ci-failed` only knows the pull
requests the poll fetches, which are yours. Saved secrets are only as safe as the data volume: the key that decrypts them is in it.

## 17. Toolchains, dependency health, activity and the command palette (0.6.0, built 2026-11-20)

Part 4 of the 0.6.0 work. Four things that are mostly independent; all of the agent's side is optional fields, so
`SnapshotSchema.Version` stays 1.

**New snapshot fields.**

| Field | What | Null when |
|---|---|---|
| `MachineInfo.Toolchains` (`ToolchainInventory`: `CollectedAt`, `Tools[]` of `InstalledToolchain { Tool, Active, Versions[], Managers[] }`) | what is installed | agent before 0.6.0, or `Agent:Toolchains:Enabled` off |
| `MachineInfo.DependencyChecks` (bool?) | whether that agent runs the dependency checks | agent before 0.6.0 |
| `DevEntry.Pins` (`ToolchainPin { Tool, Version, Source, RollForward?, AllowPrerelease? }[]`) | what the folder's files pin | nothing pinned |
| `DevEntry.Dependencies` (`DependencyHealth { CheckedAt, Reports[] }`; `DependencyReport { Ecosystem, Target, Vulnerable { Critical, High, Moderate, Low }?, TopVulnerable[], Outdated?, TopOutdated[], Skipped? }`) | the last check | not checked yet, nothing to check, or checks off |
| `GitState.Activity` (`CommitActivity { Days, Mine? }`, `yyyy-MM-dd` → count, non-zero days only) | commits per day, last 365 days | agent before 0.6.0, `Agent:Activity` off, or a linked worktree whose main working tree is in the snapshot |

### Toolchains

*Agent* (`src/DevDash.Agent/Toolchains`). `IToolRunner` / `ProcessToolRunner` start a program by full path with an argument list: no
shell, stdin closed, a timeout that kills the process tree. `ToolLocator` finds the program: `PATH` first (relative entries skipped), then
the usual install places per OS, because a LaunchAgent, a systemd user unit and a scheduled task all start with a bare `PATH`. Everything
it knows about the machine comes through `ToolEnvironment`, which the tests build over a temporary folder. `ToolchainCollector` runs
`dotnet --list-sdks` (every `dotnet` it finds), `node --version`, `python3 --version`, `go version`, `rustc --version`, `java -version`,
and lists the versions under nvm, fnm, volta, pyenv, asdf and mise by folder name, starting nothing. macOS's `/usr/bin/python3` and
`/usr/bin/java` are stand-ins that open an install dialog when the real thing is absent; they are only run when the command line tools /
a JDK are there. `ToolchainService` keeps the inventory for `IntervalHours` (6): the first scan after start collects it, the rest reuse it.

*Pins.* `PinReader` reads six files at the top of a working tree (not links, at most 256 KB); `PinFiles` (Core, pure) parses them.

*Analysis* (`DevDash.Core/Toolchains`, pure). `ToolchainCheck.Check(pin, inventory)` → `Satisfied` / `Missing` / `Unknown`, with the
rule that anything it cannot read is `Unknown` and never a finding. `DotnetSdkResolver` is a port of the .NET host's SDK resolver
(`matches_policy` / `is_better_match`): an SDK must be at least the version asked for and inside the policy's scope (feature band for
`patch`/`latestPatch`, major.minor for `feature`/`latestFeature`, major for `minor`/`latestMinor`, anything for `major`/`latestMajor`);
among those, a `latest*` policy takes the highest and the others take the lowest band, and the latest patch within it; `disable` and
`patch` take an exact match first; no policy is `latestPatch`; no version is "any SDK". Pre-releases count unless `allowPrerelease` is
false, and always when the version asked for is one. `NodeRange` reads npm ranges (`^`, `~`, comparators, `x`, `||`, hyphen ranges);
`.nvmrc` aliases (`lts/iron`, `lts/*`, `node`) are understood; plain and partial versions are prefixes.

*Tests.* `DotnetSdkResolverTests` goes through each policy by hand. `DotnetHostRecordedTests` is 120 cases recorded from the real
`dotnet --version` (SDKs 8.0.415 and 11.0.100, every policy against twelve versions), which the resolver reproduces exactly.

*Hub.* `ToolchainMismatchRule` (`toolchain-mismatch`; Medium when a .NET SDK is missing, else Low; one finding per working tree; the
fingerprint is the pins, not what is installed). `ToolchainView` feeds the Machines page's Toolchains table and the repo page's.

### Dependency health

*Agent* (`src/DevDash.Agent/Dependencies`). `DependencyWorker` is a hosted service of `run` only. It waits `StartDelaySeconds`, then
takes the most overdue repository from `DependencyTargets` (the git working trees of the last full scan), checks it, saves, pauses, and
repeats: one repository at a time, at below-normal priority, never on the scan loop. The scanner only reads `DependencyCache`
(`agent-deps.json` in the state folder), so a result reaches the hub with the next scan.

`DependencyChecker`:

- *.NET:* the first `*.sln`, `*.slnx` or project at the top of the repo, else in `src/`. `dotnet list package --help` is run in the repo
  first: its `global.json` decides which SDK answers, and that SDK's help says whether it knows `--format` (7.0.200+) and `--no-restore`
  (SDKs that would otherwise restore before listing). Then `--vulnerable --include-transitive --format json` and `--outdated --format
  json`, with `--no-restore` when it exists. "No assets file was found" becomes `Skipped = "not restored"`; nothing is restored.
- *npm:* only with a lockfile and `node_modules`. `npm audit --json`, `npm outdated --json`. On Unix `npm` is a script that asks `env`
  for `node`, so node's folder is put on the child's `PATH`; on Windows `npm.cmd` needs a shell, so `node.exe` is started with
  `node_modules/npm/bin/npm-cli.js` instead.

`DependencyParsers` (pure) reduces the JSON to counts by severity (a package in several projects counts once, at its worst), the top 10
names, and advisory ids taken from the end of the advisory address (only `GHSA-…` / `CVE-…` shapes). Package sources, project paths and
the tools' text never leave the machine. Offline and timeouts are *transient*: the previous answer is kept and the repo is tried again
after `RetryHours` (2) rather than a day.

*Hub.* `VulnerableDependenciesRule` (`vulnerable-deps`; High with a critical, Medium with only high; fingerprint is the advisory ids, so
a new advisory resurfaces a dismissed finding). `DependencyView.Chip` is the "N vulnerable" chip in `CloneChips` (Local, repo page).

*Why on by default, and the caveat.* It is what makes the page useful without setup. But it runs package tools inside every scanned
repository: `dotnet list package` evaluates the project files (as an IDE does on open), and both tools send package names to the
configured sources. The README says so and gives `--no-deps`.

### Activity heatmap

*Agent.* `ActivityCollector`: `git log --since=1.year --exclude=refs/stash --all --format=%ad%x1f%ae --date=short`. **Author date**, in
the author's zone: the day the work was done; rebases and cherry-picks do not move it (`--since` goes by commit date, so a commit
authored over a year ago and rebased since is read and dropped). `%ae` is compared with `git config --get user.email` (the one `config`
form on `GitRunner`'s read-only list) to make `Mine`; no email is sent. Cached per repository against a hash of its refs, the email and
the date, so an unchanged repo costs `for-each-ref` and `config --get`. `MachineScanner.WithOneGraphPerRepository` drops a linked
worktree's copy, as it does the graph's.

*Hub.* `ActivityView` (pure). **No double counting without shas:** per repo and day, the maximum over its clones; then the sum over
repos. Clones share history, so the same commits on three machines count once. The known cost: two machines with *different*
unfetched commits of one repo on one day show the larger count, not the sum. `Build` lays 365 days out in Monday-first weeks with five
levels, scaled to the 90th percentile of active days so one huge day does not flatten the rest. `Svg` draws it on the server: a `<rect>`
per day with a `<title>` ("12 commits on 3 Nov"), `role="img"` with a one-sentence summary, classes `h0`–`h4` coloured by
`--heat-0`…`--heat-4` (one hue; separate light and dark steps), a forced-colors fallback, and a "By month" table as the same data in
text. `ActivityHeatmap.razor` wraps it: Overview (mine by default, `?activity=all` for everyone's) and, compact, each repo's page.

### Command palette

`CommandPalette.razor` (in `MainLayout`, replacing the navigation's search field with a button that opens it) plus
`wwwroot/palette.js`, an ES module of about 150 lines: the Cmd/Ctrl+K listener, arrow keys / Home / End moving `aria-selected` and
`aria-activedescendant`, Enter clicking the selected row, Escape and a click on the backdrop closing, a focus trap (Tab stays on the
field; focus that lands outside returns), focus restored on close, and recents in `localStorage`. The rows are rendered by the server.

`Palette` (Services, pure): the items are the pages, plus repos / pull requests / machines through `GlobalSearch`'s shared field and
hit builders, plus actions. Ranking per word, best field wins, every word must match: whole title 120, title prefix 100, word start 70,
inside 40; other fields 30 / 20 / 10; plus up to 25 for one of the last 8 things used. Ties: pages, repos, machines, PRs, actions.

**Nothing destructive runs from the palette.** `PaletteCommand` has two values. `Navigate` covers "Remove N safe projects…"
(`safety?ask=remove-safe`) and "Update all agents…" (`machines?ask=update-all`): the page opens its existing confirmation dialog in
`OnAfterRenderAsync` (so only for a live circuit, never from a fetch of the URL), and removes `?ask` from the address. `Refresh` covers
"Refresh \<repo\> on \<machine\>" and "Fetch all", which queue the same unconfirmed, read-only Refresh action as the pages' buttons.
"Fetch all" is capped at 60 entries (most recently active first), under the 100 refresh rows the hub keeps.

**Limits.** Pins are read at the top of a working tree only (no nested `global.json`, no `mise.toml`, `go.mod` or
`rust-toolchain`). `lts/*` is judged as "an even major is installed". Java under asdf/mise is not listed (vendor-named folders).
Dependency checks cover NuGet and npm only (no yarn, pnpm, pip, cargo), one solution or project per repo, and a linked worktree is
checked as a repo of its own. The heatmap's merge is a per-day maximum, as above. The palette's look was checked by measuring the open
dialog in a browser, not by screenshot; the Windows code paths (`%ProgramFiles%` lookup, `node npm-cli.js`) are unit-tested with a
fake machine and have not been run on Windows.

## 18. T3 Code and issue threads (0.7.0, built 2026-11-20)

T3 Code runs Claude in threads, each usually in its own git worktree. 0.7.0 shows a repository's threads, and lets the
dashboard start one: from a repo, from a "Fix in T3" button, or from a GitHub issue, by hand or by a label. All of the
agent's side is optional fields and two new action kinds, so `SnapshotSchema.Version` stays 1.

### The agent (`src/DevDash.Agent/T3Code`)

*Finding T3.* `T3Locator.Find`: `Agent:T3:Path` when set (and nothing else); else `<T3 home>/bin` (`t3`; on Windows `t3.exe`,
`t3.cmd`, `t3.bat`, `t3`, in that order), else `PATH`. The T3 home is `Agent:T3:Home`, else `T3CODE_HOME`, else `~/.t3`.
`T3Service.InfoAsync` runs `t3 --version` and keeps the answer 30 minutes (5 while it is missing). `--no-t3`
(`Agent:T3:Enabled=false`) turns everything here off.

*Roots.* With no roots configured, `AgentOptions.ResolveRoots` is `~/Developer` plus `<T3 home>/projects` when that folder
exists (and T3 is on). Configured roots are used exactly as configured. T3's own worktrees (`<T3 home>/worktrees/<repo>/t3-…`)
are linked worktrees of repositories that are scanned anyway, so they are reported, as before, in the main repository's
`Worktrees`, with `WorktreeInfo.T3Owned = true` (`T3Locator.MarkOwned`, comparing both the configured and the
symlink-resolved spelling, since git reports real paths). They are never a root and never an entry of their own.

*Threads.* After a full scan, `T3Service.EnrichAsync` runs `t3 thread list --project <path> --json` for every main
working tree (four at a time, 15 s each, about 0.2 s in practice), then `t3 thread show <id> --json --messages 0` for a
thread only when its `updatedAt` moved since it was last shown (in-memory cache; at most 30 shows per scan, the rest
next scan). Up to 10 non-archived threads per repo, newest first, go into `DevEntry.T3`. "No T3 Code project matches"
is an answer (no project); any other failure is a failure, and after three with no answer at all the rest of the scan
does not ask and `MachineInfo.T3.Problem` says "T3 Code did not answer; is the app running?". A single-entry rescan
(file change, refresh, after an action) does the same for its entry.

*Whose project.* `--project <path>` resolves to the nearest enclosing T3 project, so a project rooted at `~/Developer`
answers for every repo in it. The rule: a project id counts only when exactly one scanned repository resolved to it in
the last full scan. One that several resolve to is nobody's (left out for all of them); a rescan of one entry keeps the
full scan's verdict, and a new path resolving to an owned project makes that project shared from then on.

*Snapshot fields.* `MachineInfo.T3` (`T3Info { Available, Version, Problem }`, never the CLI's path),
`MachineInfo.AcceptsT3` (actions on and the CLI found), `DevEntry.T3` (`T3Project { ProjectId, Threads[], ThreadCount }`;
`T3Thread { Id, Title, Status, Link, Branch, WorktreePath, PendingRequests, PullRequests[], Settled, CreatedAt, UpdatedAt }`),
`WorktreeInfo.T3Owned`. `T3Threads.StateOf` reads a thread as running / needs input (`pendingRequestCount > 0`) /
settled / failed / idle. Links are kept only when they are https. The parser (`T3Parser`) reads every field as optional.

### The two actions

| Kind | Fields | What runs | The agent refuses unless |
|---|---|---|---|
| `OpenInT3` | repo | `t3 app <repo>` | actions on, T3 on and found, `RepoRefusalAsync` (a working tree at scanner depth in a root) |
| `StartT3Thread` | repo, `Prompt`, `Title`, `WorktreeBase`, `NewBranch` | `t3 thread start <repo> - --title <t> --worktree <base> --branch <new> --json`, prompt on stdin | the same, plus `git check-ref-format --branch <new>` and no `refs/heads/<new>`, and `git rev-parse --verify <base>^{commit}` |

Shape (`AgentActions.Validate`, both sides): prompt non-empty, at most 32 KB of UTF-8, no control characters but line
breaks and tabs; title one line of at most 200; base `[A-Za-z0-9_][A-Za-z0-9._/-]*` with no `..`, `//`, trailing `/` or `.`
or `.lock` (so no revision syntax and nothing option-like); new branch by the existing branch rule. Gated like the 0.6.0
kinds: `Accepts` is `ActionsEnabled && AcceptsT3`, `WhyNotAccepted` tells "update to 0.7.0" from "T3 Code is not
available on X: <why>" from "actions are off", the hub only queues and hands out to an agent that says it takes them.

Processes go through `IToolRunner` (argument list, no shell, time limit that kills the tree); `ToolCommand.StandardInput`
is new and is written while the output is read. On Windows a `.cmd` CLI runs through cmd.exe, which reads `" % ! ^ & | < >`
even inside quotes, so with a `.cmd` the agent refuses a path, title or branch containing one (the prompt is on stdin and
unaffected). `ActionResultRequest.Thread` (`T3StartedThread { ThreadId, Link }`, optional) carries what `--json` printed;
`T3Parser.ParseStarted` takes `threadId`/`id` (top level or under `thread`), a bare JSON string, or the first UUID and
https address in plain text. The affected path is the repo, so it is rescanned at once and the new thread shows.
`RemoveWorktree` is refused for a T3-owned worktree ("archive the thread in T3 Code instead").

### The hub (`src/DevDash.Hub/T3`)

*Starting.* `T3Starts.StartAsync` → `ActionQueue.QueueAsync` (every existing check) plus: a main working tree on that
machine, a branch that clone does not have, and for an issue no earlier start unless `Again`. It writes a `T3Threads` row
(action, machine, repo, path, title, branch, base, origin `repo|issue|ci|deps|toolchain`, issue number, queued by), and for
an issue an `IssueThreads` row. `T3Settlement` (called from `CompleteAsync` and the expiry path, like the tombstones)
stores the thread id and link (id `[A-Za-z0-9][A-Za-z0-9_:.-]{0,127}`, https link) or the failure text.

*UI.* `T3StartButton` (dialog: machine picker of the clones that can take it, editable prompt, title, branch, base; the
exact command line; disabled with the reason as tooltip, as `ActionButton`), `T3ThreadList` (state chip, title linking to
app.t3.codes, PR chips, "DevDash" when the dashboard started it), a "T3 · 1 needs input" chip in `CloneChips` (Local and
repo pages), an Overview card of threads running or waiting. RepoDetail: threads, **Open in T3** per clone, **Ask Claude
in T3** (`t3/<slug>`), and **Fix in T3** for a PR of mine with failing CI (`t3/fix-ci-<n>` from `origin/<head>`; the
prompt has the first failing check's page, now fetched as `statusCheckRollup.contexts(first: 25)` on the PR search),
vulnerable packages (`t3/fix-vulnerable-deps`) and a missing pinned toolchain. ⌘K: "Open X in T3 on M" (queued at once,
with who asked: it only opens a window) and "Ask Claude in T3 about X…" (a link to the repo page's dialog, `?ask=t3`).

*Issues.* `IssueWorker` (hosted, on the GitHub poll interval, after the first GitHub poll): for the GitHub repos agents
have clones of that GitHub says exist and are not archived (at most 40, most recently pushed first),
`GET /repos/{o}/{r}/issues?state=open&sort=updated&per_page=50` with `If-None-Match` (a 304 keeps the last list and costs
no rate limit); pull requests are dropped, bodies cut to 16 KB. `IssueStore` keeps it in the cache table. The Issues
page (nav count: open issues) lists number, title, labels, author, age, comments and the thread state, with **Start in T3**
/ **Start again**. `T3Prompts.RenderIssue` fills the template (editable on the page, stored in the cache table as
`IssueSettings`): placeholders `{{repo}} {{number}} {{url}} {{title}} {{author}} {{labels}} {{body}} {{comments}}
{{branch}}`, one pass (a placeholder inside issue text stays as typed), the BEGIN/END UNTRUSTED marker lines removed from
issue text, control characters dropped, the latest 3 comments (2 KB each), the body halved until the whole prompt fits
32 KB. Branch `issue/<n>-<slug>` (made free against the clone's branches), title `#<n> <issue title>`.

### The label trigger and its security model

Issue text in a public repository is written by anyone, and a T3 thread usually runs with full access to its machine.
So the trigger (`IssueTrigger.Decide`, pure) is:

1. **Off by default**, and when on, only for the repos ticked in the settings, and only for an open issue carrying the
   label (`t3`, configurable).
2. **Never a second thread**: an issue with any `IssueThreads` row that has an action (whatever became of it) is skipped.
   Only "Start again" on the page starts another.
3. **Who added the label** must be an allowed login: the configured list, by default only the token's own user
   (`GitHubSnapshot.ViewerLogin`, from the GraphQL `viewer`, the same user `/user` returns). It is read from the issue's
   events (the latest `labeled` event for that label, its `actor`), never assumed; not found means not allowed.
4. **On a repository not known to be private**, the issue's author must be allowed too. Otherwise anyone could write the
   instructions and only need a trusted person to add a label.
5. **Machine**: the repo's preferred machine when set (only it: it waits rather than going elsewhere), else the most
   recently seen online machine with a main-working-tree clone that accepts `StartT3Thread`.

3 and 4 failing record "needs a manual start" with the reason; 5 failing records "pending" with the reason, tried again
on every poll. A person starting it by hand sees the reason, and a warning when the author is not on the allowed list.
The prompt frames the issue as untrusted in any case. The thread itself still runs with whatever permissions T3 Code
gives threads on that machine: that, not DevDash, is the real limit on what a hostile issue could do once a person
has chosen to start it.

*Write-back*, off by default: once a thread the trigger started has started, comment "Started a Claude thread for this
issue." (no links, no machine names) and/or add `t3:started` and remove the trigger label. A 401/403/404 is stored on the
row ("the GitHub token may not write to this repository") and shown; it is never retried. These are the hub's first
writes to GitHub, in `GitHubIssuesClient`, separate from the read-only `GitHubClient`.

*Schema.* `T3Threads` and `IssueThreads` (repo, number, machine, action, thread id, link, status
`Pending|NeedsManualStart|Queued|Started|Failed`, reason, started by `auto|manual`, by, write-back, created, updated),
added to an existing database by `SchemaUpgrade`.

### Alerts

`t3-needs-input` (a thread the dashboard started has `pendingRequestCount > 0`; clears when answered) and `t3-done` (it
has a pull request or is settled), both off by default (`Triggers:T3NeedsInput`, `Triggers:T3Done`), keyed on the thread
id, with the thread or the PR as the alert's link. Threads started elsewhere never alert.

**Tests.** Agent (`T3Tests`): default roots with a temporary home, the locator, `MarkOwned`, list/show parsing with the
real field names, start-output shapes, the project rule (shared, owned, rescans, a new path), the show cache, T3 missing
or not answering, the snapshot fields, action shape and gating, execution against `FakeTools` (arguments, stdin, result,
failure, every refusal) and, on macOS and Linux, against a shell-script stand-in through the real process runner. Hub
(`T3HubTests`): prompts (framing, injection of markers and placeholders, size), names, every trigger rule, the issue poll
with ETags, the trigger end to end (start, dedupe, manual start over a decision, Start again), pending until a machine
can, write-back refused by the token, queue gating and results, the pages and palette, the alerts, and the schema upgrade.

**Limits and not verified.** `t3 thread start --json`'s output was never seen (no real thread was started while building
this); the parser is deliberately loose and a start that prints no id still counts as started, without a link. The CLI
was not run from a launchd/systemd/scheduled-task context: the macOS wrapper is a shell script that runs the desktop
app's bundled Node, which should work from a LaunchAgent but has not been tried. The Windows location and file type of
the CLI are guesses (`%USERPROFILE%\.t3\bin\t3.exe|t3.cmd`), and the `.cmd` restrictions are untested on Windows. The
project rule is by count only, so a lone repo inside a project rooted above it still looks like its own (T3's
`project list` would allow an exact root match). Threads are shown per repo, so a thread in a T3 project that is not a
scanned repo is not shown anywhere.

## 19. MCP server (0.7.0)

Claude (Claude Code, or Claude inside T3 Code) reads the dashboard, and with the right token queues a few safe actions, through an MCP
server in the hub. All of it is in `src/DevDash.Hub/Mcp`, plus the **Claude** page (`Components/Pages/Claude.razor`) and three lines in
`Program.cs` (`AddDevDashMcp`, `UseRateLimiter`, `MapDevDashMcp`).

**Transport.** The official C# SDK, `ModelContextProtocol.AspNetCore` 2.4.0: Streamable HTTP at `/mcp`, stateless
(`HttpServerTransportOptions.Stateless = true`), so a hub restart loses no session and needs no affinity; no GET/SSE stream and no
server-to-client requests, which no tool needs. `Hub:Mcp:Enabled=false` leaves `/mcp` unmapped. The not-found page re-execution skips
`/mcp` as it does `/api`, so clients get plain status codes.

**Auth.** Tokens are `ddm_` + 32 random bytes, stored as SHA-256 in `McpTokens` (name, prefix, scope, created by, created, last used,
revoked), the same scheme as agent keys but a separate table, so an agent key is not an MCP token and an MCP token claims no actions as an
agent. `McpTokenAuthenticationHandler` (scheme `McpToken`) is the only scheme the endpoint's policy names, so the browser's Tailscale
identity never satisfies it: no token, a wrong one or a revoked one is `401` with `WWW-Authenticate: Bearer`. The network boundary is
unchanged (tailnet only, no Funnel). "Last used" is written at most once a minute per token. Tokens are shown once on the Claude page,
which also prints `claude mcp add --transport http devdash <Hub:PublicUrl or the page's origin>/mcp --header "Authorization: Bearer
<token>"` and an `.mcp.json` that reads the token from `${DEVDASH_MCP_TOKEN}`. Tokens and Authorization headers are never logged; each
tool call logs `MCP token <name> called <tool>` at Information.

**Scopes.** `read` (default) or `read+actions`, as a claim. Action tools carry `[Authorize(Policy = McpSetup.ActionsPolicy)]`; with the
SDK's `AddAuthorizationFilters()` a `read` token does not see them in `tools/list` and is refused if it calls one anyway.

**Tools.** Registered in one place, `McpSetup.ToolTypes`; a new class of tools is one entry there. Every tool reuses the service the
matching page uses rather than deriving anything again, takes filters, and returns compact JSON (nulls left out, a `now` to reason about
ages, `total`/`shown` where it lists).

| Tool | Scope | From |
|---|---|---|
| `list_machines` | read | `SnapshotStore`, `AgentUpdates`, `AgentActions.Accepts` (capabilities) |
| `list_projects` (machine, text, only_at_risk, only_dirty, sort) | read | `RepoCatalog` clones + `SafetyService.ByClone` |
| `get_project` (name, owner/name, key or clone path; machine) | read | `RepoCatalog`, `SafetyService`, `SyncOffers`, `FindingsService`, `ToolchainCheck`, `DependencyView` |
| `at_risk_work` / `safe_to_remove` | read | `SafetyView.AtRisk` / `SafetyView.Removable` over `SafetyService` |
| `findings` (min_severity, rule, machine, project, include_inactive) | read | `FindingsService` |
| `pull_requests` (all, mine, review, assigned, attention) | read | `GitHubStore` + `PrTriage` |
| `compare_clones` | read | `RepoCatalog` branches by name across machines + `SyncOffers` |
| `recent_activity` (days, project, mine) | read | `ActivityView.Total` / `ForRepo`, last commits |
| `action_status` (id) | read | the `AgentActions` row |
| `t3_threads` (repo, machine, state) | read | `T3View.All`: reported threads joined with the dashboard's starts |
| `list_issues` (repo, label, include_body) | read | `T3View.Issues`; bodies only on request, labelled untrusted |
| `refresh_project` | read+actions | `RefreshTargets` → Refresh |
| `fetch` | read+actions | `AgentAction.Fetch` on the main working tree |
| `push_branch` / `pull_fast_forward` | read+actions | only an offer `SyncOffers.For` makes (same-name upstream, strictly ahead / behind; a pull into a dirty tree is refused with its `Blocked` text) |
| `clean_build_dirs` (dirs optional) | read+actions | the clone's reported `ReclaimableDirs` only |

Every action goes through `ActionQueue.QueueAsync`, the call the buttons make, so the hub switch, the machine's opt-in and version,
online, "still reported" and `AgentActions.Validate` all apply, and the agent re-validates as in §11. `QueuedBy` is `mcp:<token name>`.
A tool returns the action id, its command line and "check with `action_status`". Refusals are tool errors carrying the queue's own
sentence.

**Not exposed, on purpose.** `RemoveProject`, `Clone`, `DropStash`, `DeleteBranches`, `RemoveWorktree`, `PruneWorktrees` and
`UpdateAgent` have no MCP tool. They either delete something that is not regenerable or change what runs on a machine, and they stay
behind the dashboard's confirmation dialogs. `safe_to_remove` says so in its description. `StartT3Thread` and `OpenInT3` have no tool either: a thread usually runs with full access, so starting one from a
model (possibly one reading an issue body) would let text reach a full-access agent without a person in between.

**Limits on size and rate.** Lists default to 20–100 rows and accept at most 200; one result is cut at 60,000 characters with a note to
narrow it. A fixed-window limiter allows `Hub:Mcp:RequestsPerMinute` (120) requests per token per minute, then `429`. The request body
size is Kestrel's default.

**Schema.** One table, `McpTokens` (unique index on the hash), added to an existing database by `SchemaUpgrade`.

**Tests.** `McpTests`, against the real hub with the SDK's own `McpClient` over the test server: no token, a wrong token, an agent key
and a revoked token are 401 (and an MCP token is 401 at the agent endpoints); the per-token 429; a read token lists exactly the read
tools, and each read tool answers from the seeded snapshot; a read token cannot call an action tool and nothing is queued; an actions
token sees exactly read + action tools (nothing destructive), queues a fetch and a push that land in `AgentActions` with `mcp:<name>`,
and follows one with `action_status`; refusals carry the queue's and `SyncOffers`' reasons; the Claude page prints the command for
`Hub:PublicUrl`; an old database gains `McpTokens`.

**Limits.** A token is all-or-nothing within its scope: no per-machine or per-repo tokens. Stateless means no server-initiated
notifications (e.g. "your action finished"); the client polls `action_status`. The data is as fresh as each agent's last scan; the tools
say so and `refresh_project` asks for a new one. Tested with the SDK's client, not yet with Claude Code itself against a deployed hub.
