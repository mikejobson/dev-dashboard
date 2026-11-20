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
  that contains each one and the shortest run of commits up to it. It sends that run and marks the tip `JoinsTrunkAt = <merge sha>`;
  the merge itself is always sent. Runs are taken shortest first, at most 150 commits per tip and 300 per repository (≈90 KB at
  ~300 bytes a commit; a whole graph stays under ~300 KB: 250 side + 20 trunk + ≤400 hangers + ≤300 joins). Past the caps the tip
  gets `JoinDistance` (commits in between) instead. All optional: schema version unchanged, older hubs ignore them. Still one cached
  result per ref state.
- **Why dashed links appeared (0.7.0).** On a real repo the gap was a 63-commit feature branch merged with a merge commit and a
  branch forked from inside it: over the old 40-commit cap, so only `JoinsTrunkAt` went out. It is now sent whole and folds as a
  quiet run.
- **Hub ordering (`BranchGraph.Order`).** Newest first, as `git log --graph --date-order` reads: a row goes in once every row that
  descends from it is above it, and of the rows free to go next the newest wins (an uncommitted-changes row or a gap sits right on top
  of its commit; "older commits" closes the map). Branches worked on alongside the trunk interleave with it by date, at the cost of a
  few more lanes. (0.6.0 first grouped each merged branch under its merge; users read dates down the page, so that was dropped.)
- **Join links.** A merged commit with `JoinsTrunkAt` and no line of children up to the trunk sits at its date. With `JoinDistance`
  it hangs from that trunk commit through a solid collapsed row ("63 more commits of a merged branch", `MapRowKind.Collapsed`,
  dimmed); from older agents, by a dashed line ("merged there, through commits not shown").
- **Squash-merged branches.** A squash merge leaves the branch's commits on no remote and off the trunk, so they read as unpushed.
  The agent (`SquashDetector`, cached per branch/trunk sha pair, ≤40 unmerged branches with unpushed commits, only when the trunk
  is a remote-tracking ref and has moved ≤1,000 commits since the fork) proves it two ways: `log --cherry-mark` finds every commit
  of the branch with an equal patch on the trunk (rebase merge, cherry-pick; the named commit is the newest non-empty match), or one
  first-parent trunk commit since the merge-base touches exactly the branch's files and its `diff --unified=0` has the same
  removed/added lines per file as `diff <merge-base> <tip>` (no context, no line numbers; binary files by resulting blob id). Proof
  sets `BranchInfo.SquashMergedInto` and `GraphCommit.SquashMerged` on its unpushed commits (which keep `Unpushed` for older hubs).
  The hub also counts a branch at the head commit of one of my pull requests merged in the last 30 days (`GitHubSnapshot.MergedPullRequests`,
  same-repo only) as merged on GitHub: its unpushed commits up to that commit only. Squash-merged commits are muted ("squash-merged" /
  "merged in #N"), not counted as unpushed on the map, and the branch counts as merged (hide merged hides it). `RepoSafety` and the
  unpushed/gone-upstream cleanup rules trust only the agent's proof: a GitHub-only match leaves safety at risk. Limits: a squash that
  was edited or conflict-resolved on merge, a PR that merged the trunk in after branching (merge-base moves; usually still matches),
  or a branch with commits after the merge are not proven and stay unpushed.
- **Paging.** The page draws 100 rows, then "show 100 more" (or "all N"); a last page under 20 is folded into the previous one
  (`BranchMapPaging`). Lanes are assigned over every row before any is drawn, so more rows never move the ones above.
- **Remote branches.** `GitState.RemoteBranches` (optional): every `refs/remotes` branch except `*/HEAD`, newest commit first, at
  most 200, with sha, date and remote; one `for-each-ref`. Dropped from a linked worktree whose main working tree is sent.
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
- **History**: disk, at-risk work and unpushed commits per machine over time, the fastest-growing repos; "since yesterday" on the Overview (§22)
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
| `history` (metric, machine, project, days) | read | `HistoryStore` per-day rollups (§22) |
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

## 20. Where I left off, tags and notes (built 2026-11-20)

Two things for getting back into work: a list of what was touched last, on any machine, and the user's own words about each repo.
The agent's part is one optional field, so `SnapshotSchema.Version` stays 1; tags, notes and pins never leave the hub.

**New snapshot field.** `GitState.RecentChanges` (`RecentChange { Path, ModifiedAt, Untracked? }[]`): the changed paths of `git status`
(tracked and untracked) that `RepoScanner.NewestChange` already stats for `NewestTrackedChangeAt`, newest first, at most
`RecentChange.MaxPerRepo` (5). Paths as git prints them, relative to the working tree; a deleted file has no mtime and is left out.
Names only, never contents. Null when the tree is clean and from older agents.

### Where I left off

`LeftOff` (Services, pure) builds the list from `RepoCatalog` rows:

- **Last touched** (`LeftOff.LastTouched`) for one working tree is the newest of: the last commit's date, `NewestTrackedChangeAt`,
  `NewestUntrackedAt`, the `RecentChanges` times, and the `UpdatedAt` (else `CreatedAt`) of the T3 threads on it. It says which of the
  three it was (committed / edited / T3). `DevEntry.LastModified` is not used: a checkout or pull rewrites file times without anyone
  working there.
- **Threads on a working tree** (`LeftOff.ThreadsOn`): the repo's reported threads whose `WorktreePath` is that folder, or, for a thread
  naming no worktree, whose branch is the one checked out.
- **One row per repo**, for its most recently touched working tree on any machine (a linked worktree counts); the others touched in the
  window are listed as "Also". Git repos only. Each row: machine, branch, last commit subject and age, uncommitted / untracked counts,
  the recent file names, stashes, unpushed (the agent's `UnpushedCount` for the branch, else ahead of its upstream, else nothing claimed),
  the open PR for the branch (`BranchPullRequests`, the branch map's rule) with CI, the T3 threads, tags and note.
- **Window and groups.** The last 14 days by default (`?days=` 1–90). Grouped by calendar day in the hub's time zone
  (`TimeProvider.LocalTimeZone`, i.e. `TZ` in the container): Today, Yesterday, This week (2–6 days), Earlier. Pinned repos form a group
  of their own at the top, whenever they were last touched.
- **Actions per row**: Open in T3 (the existing `ActionButton` with `AgentAction.OpenInT3`, so the existing gating and confirmation),
  copy `cd <path>`, GitHub (the PR, else the branch's tree when it has an upstream, else the repo), Details (the repo page).

Shown on a page (`/left-off`, nav "Left off", ⌘K "Where I left off") with a window, a tag filter and a "show #archived" switch, and as a
compact card on the Overview (six rows, `?tag=` filters it).

### Tags, notes and pins

*Storage.* One table, `RepoNotes` (key, tags, note, pinned, updated at, updated by), keyed by the repo's dashboard key lower-cased
(`github.com/owner/repo`, or `local:<machine>:<path>` for a repo with no hosted remote; lookups ignore case like the catalog's). A row
that ends up with no tags, no note and no pin is deleted. `RepoNotes` (singleton) keeps the table in memory, reads it again after a save,
and announces a save as `DashboardChange.Notes`, so open pages and the alert worker pick it up.

*Rules* (`RepoTags`, pure). A tag is lower case, a leading `#` dropped, letters, digits and `- _ . / :` (anything else becomes `-`), at
most 32 characters; at most 12 per repo, each once. A note is one line of at most 200 characters, control characters dropped. In a
search, a word `#tag` is a tag filter (`#12` is a PR number, not a tag).

*Where they show.* RepoDetail edits them in place (Pin / Unpin, "Edit tags and note"). Local shows the chips and note under the name, has
a tag select and `?tag=`, takes `#tag` in its search box, and puts pinned rows first whatever the sort. Where I left off and its Overview
card show them and filter by tag. ⌘K: repo items carry `#tag` and the note as keywords and the tags in their detail, so typing `#client`
lists the repos tagged client; a pinned repo gains 10 (ahead of an equal match, not of a better one).

*Quiet tags.* `Hub:Tags:QuietTags` (default `archived`; commas or spaces) silences a repo: `AlertCandidate.RepoKey` is set by the
repo-bound triggers (`at-risk`, `diverged`, `ci-failed`, `t3-needs-input`, `t3-done`) and `AlertEvaluator.Evaluate` drops candidates
whose repo is in `AlertInputs.QuietRepos`, in that one place. An alert already open clears on the next evaluation like any condition that
has gone. `machine-offline` and `action-failed` are not about a repo and are unaffected; so is the digest's count. Where I left off leaves
quiet repos out unless asked (`?quiet=1`, or filtering by the quiet tag itself).

*MCP.* `list_projects` and `get_project` carry `tags`, `note` and `pinned` when set; `list_projects` takes `tag`.

**Schema.** `RepoNotes`, added to an existing database by `SchemaUpgrade`.

**Tests.** Agent (`ScannerTests`): recent paths newest first, capped, deleted files left out, untracked marked; and in the end-to-end scan.
Hub (`LeftOffAndTagsTests`): last touched from each source, threads by worktree and by branch, day groups across a time zone, the list
(most recent clone, window, limit, also-touched, unpushed rules, PR and threads, pinned first, quiet hidden, tag filter), tag and note
rules, the note set, saving (case, normalising, delete when empty, notification), an old database gaining `RepoNotes`, alert silencing
(pure, and end to end clearing an open alert; the quiet list from configuration), ⌘K by `#tag`, note and pin, the page, Overview, Local
and repo page, and the MCP fields and filter.

**Limits.** "Committed" is the date of the checked-out branch's last commit, whoever made it, so a `git pull` of others' work counts as
touching the repo. Untracked files are statted only as far as `git status` lists them (a new folder is one entry, its time the folder's).
Time zones: one zone for the whole hub, not per viewer. Tags are per repo, not per clone. The page's look was checked through rendered
HTML in tests, not in a browser.
## 21. Disk and dev servers (0.8.0 agent actions)

Two machine-level views the per-repo scan could not give: where the disk goes outside the repositories, and which development
servers are listening right now. Both are optional `MachineInfo` fields and two new action kinds, so `SnapshotSchema.Version` stays 1.

**New snapshot fields.**

| Field | What | Null when |
|---|---|---|
| `MachineInfo.Disk` (`DiskReport { CollectedAt, Items[] }`; `DiskItem { Id, Path, Bytes, ReclaimableBytes, LastModified, MeasuredAt, Count, Cleanable, Problem, Parts[] }`) | the developer caches outside the repos | agent before 0.8.0, `Agent:Disk:Enabled` off, or the first measurement not done yet |
| `MachineInfo.DevServers` (`DevServerReport { CollectedAt, Servers[], Problem }`; `DevServer { Pid, Name, Command, StartedAt, Cwd, Ports[], RepoPath }`) | listening dev servers, as of this push | agent before 0.8.0 or `Agent:DevServers:Enabled` off |
| `MachineInfo.AcceptsCleanCache`, `AcceptsStopDevServer` | the capability flags | false from older agents |

`DiskItem.Id` is text, a `DiskCacheId` name: a hub that meets an id from a newer agent shows it plainly (no Clean button) instead of
failing to read the snapshot, which an unknown enum value would do.

### Disk (`src/DevDash.Agent/Disk`)

*What is measured.* `DiskCollector` finds each cache from the tool that owns it, or a fixed place, never from the hub:

| Id | Found by | Clean recipe (`DiskCaches`, Contracts) |
|---|---|---|
| `NuGetGlobalPackages`, `NuGetHttpCache` | `dotnet nuget locals all --list` (else `~/.nuget/packages`, measured, not cleanable) | `dotnet nuget locals global-packages\|http-cache --clear` |
| `NpmCache` | `npm config get cache` (Windows: `node npm-cli.js`, as §17) | `npm cache clean --force` |
| `PnpmStore` | `pnpm store path` | `pnpm store prune` |
| `YarnCache`, `GradleCache`, `MavenRepository` | fixed folders | report only |
| `XcodeDerivedData` (macOS) | `~/Library/Developer/Xcode/DerivedData` | each entry moved to the Trash with the §15 `ITrash`; the folder stays |
| `XcodeArchives`, `XcodeDeviceSupport` (macOS) | fixed folders (DeviceSupport: one part per iOS version) | report only: archives cannot be made again |
| `CoreSimulator` (macOS) | `~/Library/Developer/CoreSimulator` | `xcrun simctl delete unavailable`, only when Xcode.app exists |
| `DockerImages`, `DockerBuildCache`, `DockerVolumes` | `docker system df --format json`; dangling images summed from `docker image ls --filter dangling=true --format json` | `docker image prune -f` (dangling only, never `-a`), `docker builder prune -f`; volumes report only |
| `DotnetSdks` | `sdk/<version>` of every `dotnet` found (links followed), one part each | report only |
| `HomebrewCache` | `brew --cache` | `brew cleanup -s` |

*Off the hot path.* `DiskWorker` (hosted, `run` only) waits `StartDelaySeconds` (90), then measures whenever the kept report is older
than `IntervalHours` (6), checking every 10 minutes, one cache after another, each within `TimeoutSeconds` (120: a tool call or a walk
that runs out reports what it counted, with "at least this big"). `DiskService` keeps the report in `agent-disk.json` beside the size
cache, so a restart does not measure again; the scanner only reads it. After measuring, `RescanRequests.RequestMachine()` has the scan
loop push without waiting for the next full scan (`AgentWorker` re-reads the machine facts on every push now, not only on full scans).
`FolderMeasure` never follows a link and skips what it cannot read. An empty cache is not a row. `Agent:Disk:Skip` leaves ids out;
`--no-disk` turns it all off.

*Hub.* `DiskView` (pure) and the **Disk** page (`/disk`, nav and ⌘K): per machine, the caches by size with what cleaning does, whether it
is regenerable, last written, reclaimable (the tool's figure for Docker, all of it for a cache that is cleared, "unknown" for one that is
pruned) and a total; the .NET SDKs with the working trees each serves (each `global.json` resolved with §17's `DotnetSdkResolver`
against what is installed, the newest for everything unpinned; the rest marked "unused", report only); and the top 10 working trees by
build output from the existing size data.

### Dev servers (`src/DevDash.Agent/DevServers`)

*Listing, every push.* `IProcessProbe` per system:

- *macOS:* `lsof -nP -iTCP -sTCP:LISTEN -Fpcn`, then for those pids (at most 200) `ps -o pid=,lstart=,command=` and
  `lsof -a -p … -d cwd -Fpn`, all with `LC_ALL=C` so `lstart` is always `Mon Nov 16 07:32:33 2026` (read in the machine's zone).
- *Linux:* `/proc/net/tcp{,6}` (state `0A`) gives the inodes, `/proc/<pid>/fd/*` → `socket:[inode]` gives the process, then
  `comm`, `cmdline`, `cwd`, `exe`, and `stat` field 22 + `btime` for the start (100 ticks a second assumed).
- *Windows:* one PowerShell call (`Get-NetTCPConnection -State Listen` + `Win32_Process`: name, command line, path, creation date as
  ISO UTC). If PowerShell does not answer, `netstat -ano -p TCP` (rows whose remote is the any-address, so a localised state name does
  not matter) with names and start times from .NET and no command lines, so nothing is stoppable. A working folder cannot be read from
  outside a process on Windows, so repos are matched by command-line paths only.

`DevServerCollector` keeps a process when its working folder, program or an absolute path on its command line is inside a scanned
working tree (the deepest one, so a linked worktree wins over its container), or when its name is a well-known dev tool (node, deno,
bun, dotnet, python*, ruby, go, java, php, docker-proxy, vite, next-server, uvicorn, gunicorn, puma, rails, beam.smp…). Everything
else (desktop listeners, sync clients) is left out. Coding agents, editors and language servers are never reported, even inside a
repo (`IgnoredTools`: claude, codex, t3, code-*, cursor, rider…, npm installs of the agent CLIs, plus `Agent:DevServers:Ignore`): a Claude
session works in the repo and listens on localhost, and a Stop button for it would end the session. Found live on 2026-11-20. At most `MaxServers` (50), repos first. `DevServerText.Command` folds the command line,
masks secrets (`--token x`, `--password=x`, `API_KEY=x`, `https://user:pass@`) and cuts it to 300 characters, the same way on the side
that lists and the side that stops, because the stop compares them.

*Hub.* `DevServerView` (pure) joins servers to repos (a linked worktree's main repo key), marks a **port clash** when servers of
different repos (or a repo and a stray tool), on any machine, share a port, and **old** past 7 days. Overview: "Running: widget —
dotnet on :5001, 3 days" for servers in repos. RepoDetail: a Running table with Stop. Machines: every server, including tools outside
repos, with Stop where possible, and the listing's problem if any.

### The two actions

| Kind | Fields | The hub queues only when | The agent refuses unless |
|---|---|---|---|
| `CleanCache` | `CacheId` (a `DiskCacheId` name; no path) | the machine's last report has that id with `Cleanable` and this build knows a recipe | disk on, not skipped, a recipe exists, the id applies to this OS, and `LocateAsync` finds it again right now (folder exists, tool found) |
| `StopDevServer` | `RepoPath`, `Pid` (> 1), `ExpectedStartedAt`, `ExpectedCommand` | that repo is reported and a server with that pid, repo, command and start (±2 s) is in the last report | dev servers on; `RepoRefusalAsync` (a working tree at scanner depth in a root); not the agent's own pid; the process exists **now** with the same start (±2 s) and the same `DevServerText.Command`; and its working folder (Windows: a command-line path) resolves inside that repo |

**Safety rules.**

1. No command string crosses the wire. `CleanCache` carries an id, validated against the enum on both sides; the agent finds the cache
   and the tool itself and runs its own fixed argument list (no shell). `StopDevServer` carries a pid and what identifies the process.
2. Only regenerable caches have a recipe. Docker volumes, Xcode archives, SDKs, DeviceSupport, Gradle, Maven and Yarn are report only;
   Docker cleaning is dangling images and dangling build cache only, never `-a`, never `system prune`, never volumes. DerivedData goes
   to the Trash, not `rm`.
3. A recycled pid is refused: the start time and command line must match what the user was shown, checked by the hub against the
   snapshot and by the agent against the live process.
4. Only a process working inside a scanned repository can be stopped. A dev tool listed from outside the repos has no Stop button and
   the agent would refuse it.
5. SIGTERM (`kill(2)` via libc), never SIGKILL. The agent waits `StopWaitSeconds` (5) and reports "stopped" or "still shutting down;
   nothing more was sent". Windows has no SIGTERM: `CloseMainWindow`, then (no window, or still running after 5 s)
   `Process.Kill` of that one process, not its tree.
6. Gated like §11: actions on (`--enable-actions`), the feature on, agent ≥ **0.8.0** (`AgentActions.DiskMinAgentVersion`);
   `WhyNotAccepted` tells "update to 0.8.0" from "disk report / dev server list switched off". Both confirmed in `ActionButton`'s dialog
   (the dialog now omits the Repo line for an action with no path) and audited in `AgentActions`. Neither has an MCP tool.

**Tests.** Agent (`DiskAndDevServerTests`): parsers on captured output (`nuget locals` in both SDK formats, `docker system df` JSON
lines and sizes, dangling images, `lsof -F` sockets and cwd, `ps lstart` in two zones and a padded day, `/proc/net/tcp{,6}`, `/proc/<pid>/stat`
with a name holding parentheses, `cmdline`, `netstat` with a localised state, the Windows script's JSON as lists and single objects);
secret masking and truncation; repo mapping (cwd, deepest worktree, `--opt=path`, quoted program, case); the collector's filter; the
macOS probe against `FakeTools` and the Linux probe against a fake `/proc`; the disk collector on a made-up machine (every cache found
from its tool or place, only read-only commands run, Xcode only on macOS, skip, Docker not answering); the cleaner (recipe run, Docker
never `-a`/volumes, DerivedData into a recording trash, report-only/missing/unknown/foreign-OS/skipped/off refused, actions off refused by
the executor first); the stop (SIGTERM sent once, still-running reported, recycled pid, different command, gone, outside the repo,
repo outside the roots, the agent's own pid, switches off) against a fake process table and a real `git init` repo; the machine facts
attached on every push; contracts (shapes, gating, command lines, no recipe for anything that holds data, an unknown id still
deserialises). Hub (`DiskAndDevServerHubTests`): reclaimable rules, unused SDKs, the Disk page (three Clean buttons, never volumes or
SDKs; an 0.7.0 machine told to update), clashes and old servers, the three pages, the queue's checks for both kinds, an 0.7.0 agent
never sent them, and a queued stop claimed by the agent intact.

**Verified on a Mac (read-only) vs only by tests.** On macOS the collectors were run against the real machine without writing state:
every cache above was found and measured (sizes match `du`), Docker's df and dangling list parsed, and `lsof`/`ps` gave pids, start times,
working folders and command lines in about 0.1 s, matched to a repo by cwd. No clean was run and no process was signalled: the recipes,
the Trash step, SIGTERM and the Windows close/terminate are covered by fakes only. The Linux `/proc` probe and the Windows PowerShell and
netstat paths are tested on captured or made-up output and have not been run on those systems.

**Limits.** Sizes are apparent sizes: pnpm's store and hard-linked caches can report more than cleaning frees, and Docker's figures are
Docker's. Reclaimable for pnpm and simulators is "unknown". `brew cleanup -s` also removes old versions of installed formulae, as the
dialog says. Linux start times assume `CLK_TCK` = 100. The dev server list is as fresh as the last push (each full scan, rescan or
action); a server started a minute ago shows on the next one. On Windows, a console server with no window is terminated rather than
asked, there being no gentler signal.
## 22. History and trends (built 2026-11-20)

Until now the hub kept only each machine's latest snapshot, so it could say what is true but not what changed or for how long. It
now keeps compact per-day rollups, never whole snapshots. All of it is in `src/DevDash.Hub/History`, plus the History page, two small
components and one line each in `SnapshotStore`, `Program.cs`, the DbContext, `HubOptions`, the nav, the palette and `McpSetup`.

**Recording.** `SnapshotStore.IngestAsync` calls `HistoryStore.RecordAsync` after saving the snapshot. It assesses the snapshot with
`RepoSafety.AssessAll` (the Safety page's own verdicts) into `CloneFacts` (`HistoryRollup.Clones`, pure) and upserts today's rows: the
latest value of the day wins, and the counts where a peak matters also keep the day's highest (`MaxUnpushedCommits`, `MaxAtRisk`,
`MaxUnpushed`, `MaxRiskLevel`). A scan that did not measure sizes keeps the day's last known size. A day is the hub's local calendar
day (`TZ` in a container), as for the activity heatmap. Recording is serialised, and a database error is logged, never passed on: the
history must not cost an agent its push. Forgetting a machine deletes its history and episodes.

**What is kept.**

| Table | Key | Columns |
|---|---|---|
| `HistoryMachineDays` | machine, day | host name; repo count (main working trees), dirty count, unpushed commits (+ max), at risk by level High/Medium/Low (+ max of the total); bytes used by working trees, build output, safe to remove; samples, updated |
| `HistoryCloneDays` | machine, path, day | repo key (indexed, as the repo page uses it), name, branch, linked worktree; size, build output; dirty, unpushed (+ max), ahead, behind; risk level (+ max), verdict; unpushed branch names (≤ 20) |
| `RiskEpisodes` | machine, path | at risk since (any level), at Medium-or-worse since, at High since, current level, last seen |

Unpushed commits are the branches' `UnpushedCount`s (only on a tree that includes the repository, so a linked worktree does not count
its main repository's branches twice), falling back to `Ahead` for an agent that predates them.

**Risk episodes.** "Risk since" is the first snapshot in which a working tree was at risk, in its current unbroken run. Each level has
its own start, which resets when the tree drops below that level; the row is deleted when the tree is no longer at risk or no longer
reported, so the next time is a new episode (`HistoryRollup.Advance`, pure). Episodes are kept even with history switched off, because
the at-risk alert reads them, and being in the database they survive hub restarts.

**Retention and size.** `Hub:History:RetentionDays` (180) and `Hub:History:Enabled` (true). Rows older than the retention are deleted on
the first snapshot of each day. Measured: 50 working trees on 3 machines for 180 days (9,000 tree rows, 540 machine rows, with
indexes) is about 3.3 MB, roughly 370 bytes per tree per day, and flat after that.

**Since yesterday.** `SinceYesterday.Compare` (pure) compares each machine's working trees now (from the live snapshots) with that
machine's newest day of history before today (normally yesterday; after a gap, the last day there is). It lists trees that became at
risk, new unpushed commits, trees no longer at risk, branches whose unpushed commits are gone (pushed, or deleted when the branch is no
longer there), size changes of at least 100 MB and 10%, new clones and removed ones. A machine with no earlier day is not compared, so
the first day is not "everything is new". The baseline is read once per day and cached. The Overview shows it as a card
(`SinceYesterdayCard`), five lines per kind, and leaves it out until there is a day to compare with.

**Trends.** `/history` (nav and ⌘K): disk used by repos, at risk, unpushed commits and build output, one line per machine over 30, 90 or
180 days, and the working trees that grew most over 30 days with a sparkline each. The repo page has a Trends table: size and unpushed
commits per working tree over 30 days (`RepoTrends`). Charts are SVG drawn on the server by `HistoryCharts` (pure), like the heatmap:
one shared axis, a missing day breaks the line, a hover target with a `<title>` on every point, direct labels for up to four lines plus
a legend, and each chart has the same numbers as a table. A machine has the same colour on every chart, from the branch map's
`--dev-N` variables, so both themes and forced colours work.

**Alerts.** The `at-risk` trigger gets the episodes in `AlertInputs.RiskEpisodes`, in one place (`AlertEvaluator.AtRisk`). The body now
starts with how long: "Unpushed for 7 days", from the earlier of the hub's episode at that risk's level and, for work whose date says
when it started (oldest unpushed commit, oldest stash), that date; uncommitted changes carry the last edit rather than a start, so only
the episode dates them. New setting `Triggers:AtRisk:ForDays` (0, on the Alerts page): alert only once the hub has seen the tree at the
configured level or worse for that many days in a row. The key is unchanged, so open alerts stay open.

**MCP.** A read tool `history` (`HistoryTools`): a metric per day over N days, per machine (`disk`, `build_output`, `safe_to_remove`,
`repos`, `dirty`, `unpushed`, `at_risk`, `at_risk_high`) or, given a project, per working tree (`disk`, `build_output`, `dirty`,
`unpushed`, `ahead`, `behind`, `at_risk` as a level 0–3).

**Schema.** The three tables are added to an existing database by `SchemaUpgrade` like the earlier ones.

**Tests.** `HistoryTests`: the rollup of a snapshot and the machine totals; last value wins and the peaks stay; episodes (at risk,
level up and down, recovered, at risk again); since yesterday, every kind, with a machine that has no baseline; the size threshold; the
alert's duration from the episode or the commits and the `ForDays` threshold per level; the chart SVG (gaps, titles, direct labels) and
sparklines; top growing; then the real hub: upserts within a day and a new row the next, forget, pruning at the retention, history
off, episodes across pushes feeding a delivered alert, the Overview card, `/history` and the repo page's Trends, the MCP tool, and an
old database gaining the tables.

**Limits.** One point per day: a change made and undone within a day leaves only the peaks behind. Days follow the hub's time zone,
not the viewer's. A machine that did not report on a day has no point that day (the line breaks). An episode starts when the hub first
sees the risk, so on first install it starts then; the alert's duration still uses the commit dates where they say more. Removed
clones and pushed branches are seen only by comparing days, not as events. Not yet looked at in a real browser against real data.
## 23. Archiving a project (0.8.0 agents)

§15 removes what is safe to remove. This is for the rest: a project you are done with that still holds work
nowhere else (no remote, unpushed commits, a stash, ignored notes), or one you simply want kept. The agent packs
the whole folder into an archive in a folder its owner chose, proves the archive is good, and only then moves the
folder to the trash. Everything goes through the action queue of §11.

**Setting it up.** `Agent:Archive:Folder` (empty: off), e.g. `~/Archive` or a NAS mount. Also `Format` (empty:
tar.gz on macOS and Linux, zip on Windows), `TimeoutMinutes` (180) and `MarginMegabytes` (512). The agent never
creates the folder: a share that is not mounted must not turn into a folder on the local disk under its mount
point. Every full scan reports `MachineInfo.Archive` (`ArchiveInfo`: folder, exists, writable by a create-and-delete
probe, free space by `statvfs` / `GetDiskFreeSpaceEx` on the folder itself so a share reports its own, margin,
format, and a `Problem` sentence) and two capabilities, `AcceptsArchiveProject` (actions on, folder there and
writable) and `AcceptsRestoreArchive` (actions on, folder there). `AgentActions.WhyNotAccepted` tells "update to
0.8.0" (`ArchiveMinAgentVersion`) from "set Agent:Archive:Folder" from the agent's own problem ("does not exist").
The Machines page shows the folder and its free space under each machine's actions.

**Archive.** `AgentActionKind.ArchiveProject { RepoPath, ExpectedFingerprint, IncludeBuildOutput }`
(`IncludeBuildOutput` is left off the wire when false, so every older action's JSON and `AgentActions.Key` are
unchanged). Buttons (`ArchiveButton`): the Safety page (at-risk, safe and not-sure rows, beside Remove) and each
clone on the repo page, only on machines that report an archive folder. The dialog shows the destination pattern,
the estimated size (measured size, less reclaimable build output unless kept), the free space there, what at-risk
work the archive is keeping, and the build-output checkbox.

*Hub, before queueing* (`Archives.Refusal`): the path is reported, is not a linked worktree, has no linked
worktrees, the fingerprint equals the one shown, the machine's archive folder is available and writable, and the
estimate plus margin fits in its reported free space. Unlike `RemoveRefusal`, any verdict is accepted. A
tombstone is written as for a removal, with `Kind = Archived`.

*Agent* (`ArchiveActions`, after the gate, the switch and `ActionValidator`, which checks the path as for a
removal and that the folder is not a link):

1. The archive folder is configured, exists, is writable, is not inside the project, and the project is not
   inside it.
2. `git fetch --all --prune` when there are remotes. Unlike a removal, a failed fetch is **not** a refusal: a
   project whose remote is gone is exactly what gets archived, the archive keeps every local ref whatever the
   remote says, and the fingerprint (which covers the refs as the last scan saw them) still has to match. The
   output says the fetch failed.
3. `ScanRepoAsync`, `RepoSafety.Assess`; refuse a linked worktree (its commits are in the main repository) and a
   repository with linked worktrees (moving it would break them); refuse if the fingerprint differs.
4. Walk the folder (`ProjectArchive.PlanAsync`) without following any link. A folder whose name is build output
   by the scanner's own lists (`IsReclaimable`, `IsRecreated`, `IsCache`; never inside `.git`) is left out, unless
   kept, and only when `git ls-files -- <name>`, asked from its parent, says git tracks nothing in it.
5. Free space: refuse unless free ≥ the uncompressed size of what will be archived + margin. Unknown free space
   is a refusal too.
6. Write `<folder>/<name>-<yyyyMMdd-HHmmss>.<ext>.partial` (a counter is added when the name, its manifest or its
   partial is taken; nothing is ever replaced). Each file is read through `ExactFile`: exactly the size the walk
   saw, hashed as it goes; a file that turns out shorter or longer stops the archive. A content digest is built
   as it goes: SHA-256 over every entry's name, kind, size, link target and content hash.
7. Verify: SHA-256 of the archive file; re-open it and read every entry and all its data (`ProjectArchive.Read`);
   the counts of files, folders and links, the total bytes and the content digest must equal what was written.
8. Walk the folder again and compare with step 4 (names, kinds, sizes, link targets, file times). Anything
   written while it was being archived would otherwise go to the trash unarchived, so any difference fails it.
9. Rename `.partial` to the final name and write `<name>-<stamp>.json` (`ArchiveManifest`: source path, machine,
   archive file, format, SHA-256, archive bytes, file/folder/link counts, total bytes, content digest, excluded
   build output, skipped entries, remotes, branches with shas, HEAD branch and commit, verdict and fingerprint,
   created at, DevDash version), itself written to a temporary name and renamed.
10. `ITrash.Move` the folder (§15's trash; never deleted). If that fails, the archive and manifest are deleted:
    the action did everything or nothing. `RemovalLog` records the remotes, so Re-clone works as after a removal.

Any failure in 6–10, an exception, or the timeout deletes the partial archive, the archive and the manifest (in
a `finally`) and leaves the folder exactly as it was. The result carries `ActionResultRequest.Archive`
(`ArchiveResult`: archive path, manifest path, manifest), which settles the tombstone with `ArchivePath`,
`ArchiveSha256` and `ManifestJson` (`Archives.Recorded` checks it looks like one); a failed, expired or lost
result deletes it, as for a removal.

**Format: tar.gz on POSIX, zip on Windows.** A tar (PAX, written with `System.Formats.Tar`) stores a symbolic link
as a link with its target, and the full permission bits, the executable bit included; any `tar` reads it (the
tests list one with the system's `tar -tzvf`). Zip keeps neither natively, only through the Info-ZIP "Unix
attributes" extension, which many tools ignore. On Windows there is no executable bit to keep, creating symbolic
links needs a privilege, and Explorer opens a zip without extra software, so zip is the default there; a link in
a project on Windows is listed in the manifest's `Skipped` and not archived. Zip can still be chosen on POSIX
(`Format: zip`): it then stores modes and links the Info-ZIP way, and restores them. Not kept either way: owners,
extended attributes, ACLs, hard links (each name is archived as its own file) and the times of links. FIFOs and
sockets have no content and are archived as empty files.

**Links.** Never followed, in either direction. Archiving stores a link as a link (its target as written, even if
it points outside the project or nowhere); nothing it points at is read. Restoring creates every link *after*
every file and folder, so no file is ever written through a link, and creates files with `CreateNew`. Entry names
must be the project's folder name or start with it and a `/`, with no empty, `.` or `..` segment and no backslash
(`ProjectArchive.Relative`); anything else stops the restore.

**Restore.** `AgentActionKind.RestoreArchive { RepoPath (the target), ArchivePath, ArchiveSha256 }`, from the
tombstone's **Restore** button (`RestoreButton`), with a path box that defaults to where the project was, or
`<path>-restored` when something is there again.

*Hub* (`RestoreRefusalAsync`): a confirmed archived tombstone for that machine with that archive path and that
checksum; nothing reported at the target; the target is strictly inside one of the machine's reported roots.

*Agent*: the target passes the clone's free-folder rules (`ActionValidator.FreeFolderRefusal`: a plain absolute
path in a scan root at scanner depth, not a dot-folder, the parent exists and is not a repository, nothing at the
path, a dangling link included); the archive is inside the configured archive folder after resolving links,
exists and is not a link (a missing archive is a refusal); its manifest exists, names it, and records the
checksum the hub sent; the file's SHA-256 matches the manifest. Then it unpacks into
`<parent>/.devdash-restore-<guid>` (a dot name, which the scanner skips), checks the counts, bytes and content
digest against the manifest, refuses if the target appeared meanwhile, and renames the folder into place. Any
failure deletes the temporary folder; the archive is never touched. A successful restore deletes the tombstone
(the archive stays on disk).

An archived tombstone stays listed even when something is reported at its old path again, and a later removal or
clone at that path does not drop it: it is the dashboard's only record of an archive that still exists.
**Forget** drops the record and leaves the archive where it is.

**Long actions.** Archiving can take an hour. The agent bounds each archive or restore with
`Agent:Archive:TimeoutMinutes`; the hub's lease for these kinds is `Hub:Actions:ArchiveLeaseMinutes` (240), longer,
so the agent's own result (after its cleanup) arrives first. `AgentActions.IsLongRunning` (clone, archive,
restore) ends a claim: the hub hands out nothing after one in the same claim, because the agent runs a claim's
actions in turn and anything claimed behind an archive would outrun its 5-minute lease and be recorded as never
reported while it was still to run. Actions queued during a long action wait for the next claim, and expire after
`ExpiryMinutes` if it runs longer than that.

*Progress*: `POST /api/v1/actions/{id}/progress` (`ActionProgressRequest`), agent key only, accepted only for a
claimed action of the caller's machine; it replaces the row's `Output` until the result does. The agent sends
"Archiving <path>: n of m files, x of y" at most every 5 s, then "Checking the archive", from `ActionWorker`
without waiting on it; a hub before 0.8.0 answers 404, which is ignored. The Archive button shows it under its
chip.

**Schema.** `RemovedProjects` gains `Kind`, `ArchivePath`, `ArchiveSha256` and `ManifestJson`, all nullable.
`SchemaUpgrade.CreateMissingTablesAsync` now also adds a nullable column the model has and an existing table lacks
(`ALTER TABLE … ADD COLUMN … NULL`, names and types from EF's model), so an old database gains them on start;
existing rows read as plain removals.

**Not exposed over MCP**, like `RemoveProject`: it moves a folder to the trash.

**The safety argument.** Nothing leaves the project's folder until a complete, verified copy exists: the copy is
read back entry by entry against what was read from disk, the folder is shown not to have changed since, and the
archive's checksum is on disk beside it. Every failure before the move deletes only files the action itself
created (under names it chose because nothing was there), and a failed move deletes the archive too, so the
outcome is all or nothing. The move itself is §15's trash, so even an archive that was somehow wrong would leave
the folder recoverable. The hub cannot choose what is archived (only a path it was told about, with a fingerprint
the agent recomputes), where it goes (the agent's own folder), what is restored (only an archive inside that
folder whose manifest and checksum match) or where (only a free folder in a scan root). Links are never followed,
so nothing outside the project is read when archiving or written when restoring.

**Tests.** `ArchiveTests` (agent; the archive folder and the trash are in the test's temp folder): the shapes of
the new kinds; the snapshot fields (configured, a missing share reported and not created, not configured, too
old); an at-risk project archived, trashed and restored exactly (an unpushed commit, modified, untracked and
ignored files, build output left out, `.git`; on POSIX an executable script, a relative link and links leading out
of the project, kept as links, and the system `tar` reading the archive); a zip round trip with build output
kept; a corrupted archive failing verification with the folder untouched and nothing left in the archive folder;
a file written during archiving; the free-space refusal; a fingerprint mismatch; a failed trash deleting the
archive; a missing or unconfigured folder (not created) and a folder inside the project; linked worktrees; restore
refusing an existing target, a wrong checksum, an archive outside the folder, a target outside the roots, a
damaged archive and a missing one; entry names that would leave the folder; archive names never replacing one.
`ArchiveHubTests`: queueing an at-risk archive, progress, the settled tombstone and its restore action, the pages;
refusals (stale, unknown path, too big, no folder, too old, unmounted); a failed archive leaving no tombstone; the
long-action claim and lease; restore refusals and the tombstone's life cycle; a later removal not dropping the
archive record; an old database gaining the columns. `AgentLoopTests` archives a project with no remote and
restores it, with the real agent against the real hub.

**Limits and not verified.** Windows: the zip path, `GetDiskFreeSpaceExW` and the skipped-links rule run in CI's
Windows tests only against a temp folder; no real network share (an SMB or NFS mount on macOS or Linux, a UNC path
on Windows) has been tried, so free space, the write probe and renames on a NAS are as documented, not observed.
The re-walk compares sizes and times, so a file rewritten in place with the same size within the file system's
time resolution is not noticed (its new content would be in the trash, not the archive). The free-space check uses
the uncompressed size, which is conservative, but space can still run out if something else fills the volume
meanwhile; the write then fails and is cleaned up. Hard links come back as separate files. The write probe runs
on every full scan, so a NAS sees a create and a delete every 15 minutes.
