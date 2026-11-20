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
`--no-fetch`, `--sizes`, `--no-watch`, `--no-deps`, `--no-t3`, `--enable-actions`, `--updates off|notify|auto`. The same settings can live in `~/.config/devdash/agent.json`
under an `Agent` section, or in `DEVDASH_Agent__*` environment variables.

### What the agent does to your repos

It reads them with `git status`, `for-each-ref`, `worktree list`, `stash list`, `remote -v`, `log`,
`rev-list --count`, `config --get user.email` and, in a repo that has submodules, `submodule status`. Its only write is `git fetch --prune --quiet`, at most every 4 hours per repo (and when
you click **Refresh** for that repo), which updates remote-tracking refs and nothing else. When it fetches it
also runs `git ls-remote --tags`, which lists the remote's tags and writes nothing. Turn both off with
`--no-fetch` or `"Agent": { "AutoFetch": { "Enabled": false } }`; a machine with it off never talks to a remote, Refresh included. Credential prompts are disabled and every git call
has a timeout.

It never opens a file in your working trees apart from the README and manifests it describes a repo
from, and the small files that pin a tool version (`global.json`, `.nvmrc`, `.node-version`,
`package.json`, `.python-version`, `.tool-versions`: see [Toolchains](#toolchains)), of which only the
version is sent. Ignored files and secret-looking files (see [Safety](#safety)) are reported by name only.

About once a day per repo it also runs the package tools' own checks, `dotnet list package` and
`npm audit` / `npm outdated`, and keeps the counts: see [Dependency health](#dependency-health). They
read; the agent never restores, installs or fixes anything. `--no-deps` turns that off.

That is all it does to your repos unless you turn on agent actions for that machine (see
[Cleanup](#running-a-fix-from-the-dashboard-agent-actions) and [Removing a project](#removing-a-project-and-cloning-it-back)).
They are off by default, and nothing the hub sends can turn them on.

Separately, an installed agent keeps one request open to the hub, waiting to hear that you have clicked
**Update** for it on the Machines page or **Refresh** for one of its folders. That is the default update
mode (`notify`); `--updates off` stops it (unless agent actions are on, which also need it). An update
never touches a repository: see [Updating the agent](#updating-the-agent).

### Refresh one project

The agent rescans everything every 15 minutes, and a folder about 20 seconds after files in it change.
That misses what happens elsewhere: a push from another machine, a branch deleted on GitHub. For that,
the repo page has a **Refresh** button (every machine that has the repo, or one machine's row) and the
Local page has a small ⟳ on every entry. A click asks that machine's agent to rescan that one top-level
folder now, without waiting for the next full scan:

- it fetches, whatever the 4 hour interval says, but only if that machine has auto-fetch on;
- it measures the folder's size again, which is otherwise done once a day;
- it re-reads the git state, and the branch map follows if the fetch moved anything;
- then it pushes the snapshot and reports. The button shows "Refreshing on work-mac…", then
  "Refreshed 3 s ago" or what went wrong (a fetch that failed counts as wrong).

It is read-only apart from that fetch, so there is no confirmation dialog and it does **not** need
`--enable-actions`. What it does need is an agent on 0.5.0 or newer that listens for requests, which
is every agent unless both updates and actions are off. Where it is not available the button is greyed
out and its tooltip says why. The agent checks that the path is inside its scan roots before doing
anything, runs one scan at a time (a refresh waits for a full scan in progress), and treats several
requests for the same folder as one. Refreshes are not listed among the agent actions on the Machines
page; the last 100 are folded away under that list, so they cannot crowd out the record of a fix.

**How a click reaches the agent.** The hub never connects to an agent. The agent asks the hub for
work with `POST /api/v1/actions/claim?wait=25`, and when there is none the hub holds that request for
up to 25 seconds and answers the moment something is queued for that machine; the agent then asks
again straight away. A click therefore arrives in well under a second instead of on the next 30 second
poll, and that goes for Run and Update too. 25 seconds is under the idle timeout of `tailscale serve`
and most proxies; change it with `Agent:Actions:LongPollSeconds` (0 turns it off). If the hub answers
at once with nothing (a hub older than 0.5.0) or cannot be reached, the agent goes back to asking every
`Agent:Actions:PollSeconds` (30), backing off from 2 seconds after an error. An agent older than 0.5.0
sends no `wait` and is answered at once, as before.

### Branch map

A repo's page opens with a branch map: its commits drawn like `git log --graph`, for every device
that has the repo at once. It shows where each branch, checked-out HEAD (ringed), linked worktree,
stash and remote-tracking ref sits, with a colour per device. A commit that only one device has is
drawn in that device's colour, and a dot per device on every row says whether the commit is on a
local branch there, only fetched, or absent. Unpushed commits are hollow, uncommitted changes are a
dashed "ghost" commit on top of the HEAD they sit on, merged branches are dimmed (or hidden), and
open pull requests sit on their branch as a chip that links to GitHub (see
[GitHub on the branch map](#github-on-the-branch-map)). A branch that is at different commits on
two devices is called out above the map: "3 commits ahead" when one only has to catch up, "diverged"
when each has commits the other lacks. The number is git's own count when a device has one (its
branch against an upstream that is where the other device is); otherwise it is counted on the map
and reads "at least 3" when the map does not hold every commit in between. `origin/main` appears twice when one device fetched more
recently than another. Click a commit for its full sha, author and which devices have it; click a
device in the legend to take it out of the picture.

For this the agent sends a compact commit graph per repo (a handful of `git log` calls, cached
until a ref moves): up to 250 commits that are not on the default branch yet, the default branch's
newest 20, and the commits branches fork from. Long stretches of the default branch in between are
drawn as "… N commits". A branch that was merged is drawn under the merge that brought it in, even
when it got there through a chain of other pull requests: the agent sends the few commits in
between, or, when they are many, just where it joined, which the map draws as a dashed line.

In a git-flow repository (default branch `main`, work merged into a newer `develop`) the map is
drawn along `develop`, and "merged" on the map means merged into `develop`. **Hide merged branches**
takes those branches off the map together with the commits only they needed. The Cleanup page still
means merged into the default branch. Agents older than this feature send no graph: the map says which device
needs [updating](#updating-the-agent), and still places that device's branches where another device
knows the commit.

Above the map, and on each branch's row further down the page, a device that is plainly ahead of its
remote branch gets **Push \<branch\> from \<machine\>** and one that is plainly behind gets
**Pull \<branch\> on \<machine\>**. Both are [agent actions](#running-a-fix-from-the-dashboard-agent-actions):
confirmed first, and only on a machine that has them on. The pull is a fast-forward and nothing else.
The agent fetches; a checked-out branch is moved with `git merge --ff-only @{u}`, and only when its
working tree has no uncommitted changes (the button is disabled, and says so, when the last scan saw
some); any other branch is moved with `git fetch <remote> <branch>:<branch>`, which git refuses unless
it is a fast-forward. A branch that is both ahead and behind gets neither button.

The repo page also lists each clone's **build output** folder by folder with a **Clean** button, and
**Clean all build output** per clone. Clean deletes the folder for good, not to the trash, since a
build or an install recreates it. The agent only deletes a folder that has a build-output name by
the size scan's own rule, that is really inside the repository with no link anywhere on the way, and
that `git ls-files` says holds nothing tracked. The Cleanup page's "build output" finding has the
same action as its **Run** button.

### Safety

The Safety page answers two questions for every working tree on every machine: is anything here the
only copy, and could this folder be deleted without losing anything?

**At risk** lists work that exists only on that machine, with what it is and how long it has been
there: "2 commits on feature/x not on any remote, oldest 4 days". That covers commits on no remote,
branches that were never pushed (unless a remote branch already contains them, as after a merge and a
deleted remote branch), uncommitted changes, untracked files, stashes, tags that are not on the
remote, a detached HEAD on no branch, a submodule with unpushed or uncommitted work, and a repo with
no remote at all. Group it by machine or by repo; sort it oldest first or most severe first. The
number beside Safety in the navigation counts repos with commits on no remote.

**Safe to remove** lists working trees that are completely clean: nothing uncommitted or untracked,
no stashes, every branch contained in a remote branch, no local-only tags, no linked worktrees,
submodules clean and pushed, a remote, and no ignored files other than build output. It shows what
deleting each folder frees and the total, longest untouched first. A clone that is clean apart from
ignored files that are not build output (a `.env`, a local database) is **safe with caveats**: the
names are listed, because deleting the folder deletes them too. Each row has a **Remove** button, and
**Remove N safe projects** takes every plainly safe one at once: see
[Removing a project](#removing-a-project-and-cloning-it-back).

Everything else is folded away under **not sure**: nothing is known to be lost, but something is
missing or in the way. A machine whose agent is older than 0.6.0 does not report tags, ignored files
or submodules, so nothing on it is ever called safe; a machine that does not fetch never compares its
tags with the remote; and a repo that linked worktrees hang off cannot go before they do. The repo
page shows the same verdict as a badge beside each device.

What counts as build output is the list the size scan already uses (`node_modules`, `bin`/`obj` next
to a project file, `.venv`, `target`, `dist`, `.next`, and the framework caches `.angular`, `.nuxt`,
`.svelte-kit`, `.astro`, `.dart_tool`) plus tool caches and OS noise (`__pycache__`, `.pytest_cache`,
`.nyc_output`, `.vs`, `.DS_Store` and the like). Three more are recognised next to what makes them,
so they are not listed as files worth keeping, but they are never counted as reclaimable or offered
for cleaning because their names are too ordinary: NuGet's `packages` (with a `.sln` or
`packages.config` beside it or in the repo root), `test-results` and `playwright-report` (beside a
`package.json`), and `coverage`. Editor and assistant settings (`.idea`, `.vscode`, `.claude`) and
`.env` are not on any list.

**Linked worktrees** are listed under their main repo on every page, not as projects of their own,
and one that looks finished with (branch merged or deleted upstream, or idle for 30 days) is flagged.
Removing a worktree keeps its branches in the main repo, so a worktree is judged on its own folder
and the main working tree answers for the branches, stashes and tags.

#### Removing a project, and cloning it back

On a machine with [agent actions](#running-a-fix-from-the-dashboard-agent-actions) on (and an agent on
0.6.0 or newer), **Remove** moves a project that is safe to remove **to that machine's trash**. It is
never deleted: the macOS Trash, the Windows Recycle Bin, or the freedesktop trash on Linux
(`~/.local/share/Trash`, with a `.trashinfo` so your desktop can restore it). If the folder cannot be
moved to the trash, the removal fails and the folder stays; there is no fallback to deleting.

On macOS the agent asks the system's own `/usr/bin/trash` first (it ships with recent macOS), so the
folder shows up in the Trash with Finder's **Put Back**. On a Mac without that tool, or if it fails
and the folder is still where it was, the agent renames the folder into `~/.Trash` itself: it is in
the Trash and can be dragged out, but Finder offers no Put Back for it. Either way the agent checks
that the folder has really left its path before it reports the removal as done.

The dialog shows why the project is safe, what it frees, and, for a project that is safe with
caveats, a tick box per ignored file: nothing is queued until each is ticked. **Remove N safe
projects** lists every project and the total first, then queues one removal per project; projects
with caveats are left out of it and removed one at a time.

The page can be minutes old by the time you click, so the agent does not take its word:

1. the folder must be a repository inside the agent's scan roots, and actions must be on;
2. it runs `git fetch --all --prune`, and refuses if that fails;
3. it scans the folder again and judges it with the same rules as the page;
4. it refuses unless that verdict is safe, or safe with caveats and every caveat it finds was ticked;
5. it refuses if anything differs from what you were shown (the page sends a fingerprint of the
   facts: head commit, branches, reasons), with the reason.

Only then does the folder move. A **linked worktree** is not moved to the trash: it is removed with
`git worktree remove` (no `--force`), which deletes the folder and keeps its branch in the main repo.
The dialog says so.

Each removal is recorded: machine, path, remotes, default branch, size, who and when. **Removed**
on the Safety page and **Removed projects** on the Machines page list them, each with **Re-clone**,
which runs `git clone` into the same folder. A clone is only accepted into a folder that does not
exist, inside a scan root, from a URL that project had as a remote. The hub checks that against its
record, and the agent against its own: it only clones a URL it recorded when it removed that folder,
or one a repository it scans already uses. So the hub cannot make an agent clone an arbitrary URL.
A project that comes back (re-cloned, or dragged out of the trash) leaves the list; **Forget** drops
an entry without touching anything.

To send removed projects somewhere other than the system trash, set
`"Agent": { "Actions": { "TrashDirectory": "/path/on/the/same/volume" } }`. A removal is a rename,
never a copy, so it has to be on the same volume as the projects.

### Archiving a project

For a project you are done with but that holds work nowhere else (or that you just want to keep), set
an archive folder on that machine, a local folder or a mounted NAS share:
`"Agent": { "Archive": { "Folder": "/Volumes/nas/archive" } }`. The agent never creates the folder,
so a share that is not mounted is reported, not filled in on the local disk. The Safety page and
the repo page then show **Archive** beside each project on that machine, at risk or not.

Archiving does the same re-check as a removal (fetch, scan, fingerprint), checks there is room, and
writes `<name>-<yyyyMMdd-HHmmss>.tar.gz` (`.zip` on Windows) with everything in the folder: `.git`,
uncommitted, untracked and ignored files. Regenerable build output (`node_modules`, `bin`/`obj`,
`.venv`…) is left out unless you tick the box. Symbolic links are stored as links and never
followed. The agent reads the archive back and checks every entry, checks nothing in the folder
changed meanwhile, writes `<name>-<stamp>.json` beside it (SHA-256, file count, remotes, branches,
HEAD), and only then moves the folder to the trash. If any step fails, the archive is deleted and
the folder is left alone. The Removed list then has **Restore**, which unpacks it, after checking
the checksum, into the old folder or any free folder in a scan root.

`Agent:Archive:TimeoutMinutes` (180) bounds one archive or restore; `Hub:Actions:ArchiveLeaseMinutes`
(240) is how long the hub waits for its result. See PLAN.md §23 for the details and the reasoning.

### Toolchains

The Machines page has a **Toolchains** section: what each machine has installed, and which repos on
it pin a version it does not have. The same thing is a Cleanup finding ("Pinned tool version not
installed"), and a repo's page lists each pin with **installed** or **missing** per machine.

**What is installed.** When it starts, and every 6 hours after, the agent runs `dotnet --list-sdks`,
`node --version`, `python3 --version`, `go version`, `rustc --version` and `java -version`. Each is
started directly (no shell) with a 10 second limit. A background service starts with a bare `PATH`,
so the agent also looks where installers put things: Homebrew, `~/.dotnet`,
`/usr/local/share/dotnet`, `~/.cargo/bin`, `%ProgramFiles%` and so on. Versions held by nvm, fnm,
volta, pyenv, asdf and mise are read off their folder names without starting anything. On macOS the
`/usr/bin/python3` and `/usr/bin/java` stand-ins are only run when the real thing is installed, since
otherwise they open an install dialog.

**What a repo pins.** From the top of each working tree:

| File | Read |
|---|---|
| `global.json` | `sdk.version`, `sdk.rollForward`, `sdk.allowPrerelease` |
| `.nvmrc`, `.node-version` | the first line: `20`, `v20.11.0`, `lts/iron`, `lts/*`, `node` |
| `package.json` | `engines.node` (an npm range such as `>=18 <21` or `^20.1.0`) and `volta.node` |
| `.python-version` | the first line: `3.12` matches any 3.12.x, `3.12.4` only that one |
| `.tool-versions` | `nodejs`, `python`, `dotnet-core`, `golang`, `rust`, `java`; any of the versions listed will do |

**`global.json` is judged the way `dotnet` judges it.** The SDK that `rollForward` would select is
worked out from the installed SDKs with the .NET host's own rules: `disable` needs the exact version;
`patch` and `latestPatch` stay in the feature band (`10.0.1xx`); `feature`, `minor` and `major` take
the lowest band that is new enough, inside the same major.minor, the same major, or anywhere;
`latestFeature`, `latestMinor` and `latestMajor` take the highest. No `rollForward` means
`latestPatch`. Previews count unless `allowPrerelease` is `false`. The tests include a table recorded
from the real `dotnet` command, policy by policy.

A pin DevDash cannot read (a virtualenv name, a range it does not understand) is never reported: a
finding always means the version was looked for and is not there. A machine whose agent is older than
0.6.0 sends no list of what is installed, and nothing is said about its repos.

Settings, on each machine, under `Agent:Toolchains`: `Enabled` (`true`), `IntervalHours` (6),
`TimeoutSeconds` (10).

### Dependency health

Each repo's page has a **Dependencies** table (known vulnerabilities by severity, how many packages
are outdated, the top few names with their advisory links), the repo lists show an **N vulnerable**
chip, and a high or critical vulnerability is a Cleanup finding.

The numbers come from the package tools themselves. In the background, one repo at a time and at low
priority, the agent runs:

- `dotnet list <solution or project> package --vulnerable --include-transitive --format json`, then
  `--outdated --format json`, on the solution at the top of the repo (else a project there, else one
  in `src/`). It passes `--no-restore` to SDKs that would otherwise restore first. A repo that has not
  been restored is shown as "not restored" and left alone.
- `npm audit --json` and `npm outdated --json`, only where `package-lock.json` (or
  `npm-shrinkwrap.json`) and `node_modules` both exist.

What it never does: restore, install, `audit fix`, or anything else that changes a repo or downloads
packages. Each tool is started directly with fixed arguments (on Windows npm is started as
`node npm-cli.js`, not through `npm.cmd`) and stopped after 2 minutes. Only counts and at most 10
package names with advisory IDs are sent to the hub: no package sources, no paths, none of the tools'
own output. Results are kept in the agent's state folder (`agent-deps.json`, beside the size cache),
so a restart does not start the day's checks again.

Each repo is checked once a day. When the package source cannot be reached, the last good answer
stays on the page and the agent tries again in 2 hours. A result shows up with the next scan.

This is **on by default**. Both tools talk to your package sources, and `dotnet list package` reads
the project files the way a build or an IDE does, so if you scan clones of code you do not trust,
turn it off on that machine:

```sh
devdash-agent install --no-deps        # or "Agent": { "Dependencies": { "Enabled": false } } in agent.json
```

Other settings under `Agent:Dependencies`: `IntervalHours` (24), `RetryHours` (2), `TimeoutSeconds`
(120), `StartDelaySeconds` (120), `PauseSeconds` (5). Other ecosystems (pip, cargo, go) are not checked.

### Activity

The Overview page has a calendar heatmap of your commits over the last year, and each repo's page a
small one. Hover a square for "12 commits on 3 Nov"; **By month** under it is the same data as a table.

The agent sends a count per day and nothing else: one
`git log --since=1.year --exclude=refs/stash --all --format=%ad%x1f%ae --date=short` per repo, run
again only when a ref moves or the day changes.

- **The day is the author date**, in the author's time zone: the day the work was done, which a rebase
  or a cherry-pick does not change.
- **Every branch counts**, remote-tracking branches and tags included. Stashes do not.
- **Mine or everyone's.** The agent compares each commit's author email with that repo's
  `git config user.email` and sends a second count for the ones that match. The email never leaves the
  machine. The Overview heatmap shows yours by default, with a switch to everyone's; a clone with no
  `user.email` cannot tell and is left out of "mine".
- **Counted once across machines.** The hub has counts, not commit IDs, so for each repo and day it
  takes the highest count any machine reports, and then adds the repos up. Clones share their history,
  so a commit that three machines have is one commit. The cost: if two machines each have commits of
  the same repo, on the same day, that the other has not fetched, that day shows the larger number,
  not the sum.

`"Agent": { "Activity": false }` turns it off on a machine.

### Command palette

**⌘K** (Ctrl+K on Windows and Linux) opens a command palette from any page; the search button at the
top of the navigation does the same. Type to find repos (by name, owner or branch), pull requests,
machines and pages, then **↑ ↓** to move, **Enter** to open, **Esc** to close. A match at the start
of a name comes before one at the start of a later word, which comes before one in the middle, and
things you opened recently are nudged up. With nothing typed it lists what you used last (remembered
in your browser), the pages, and these actions:

| Action | What choosing it does |
|---|---|
| **Refresh \<repo\> on \<machine\>** | asks that agent to rescan now, like the page's own [Refresh](#refresh-one-project) button, and opens the repo |
| **Fetch all** | the same for every repo that has a remote, on every machine that takes refreshes (the 60 most recently active, if there are more) |
| **Remove N safe projects…** | opens the Safety page with its confirmation dialog showing; nothing is removed unless you confirm there |
| **Update all agents…** | opens the Machines page with its Update dialog showing; nothing is queued unless you confirm there |

Nothing destructive ever runs from the palette itself. The only things it does directly are
refreshes, which the pages do not ask you to confirm either.

### Alerts

The hub can tell you about things instead of waiting for you to look. The **Alerts** page lists what
is alerting now, the channels, the triggers, and a log of every delivery with what went wrong.

| Alert | When | Setting (under `Hub:Alerts:Triggers`) |
|---|---|---|
| Work that exists on one machine only | a working tree has had commits on no remote for 3 days or more (the [Safety](#safety) page's "at risk") | `AtRisk`: `Enabled`, `Days` (3), `Level` (`High`: commits on no remote; `Medium`: also uncommitted changes, stashes, local-only tags; `Low`: also untracked files) |
| A machine goes offline | its agent has not reported for 60 minutes | `MachineOffline`: `Enabled`, `Minutes` (60), `Ignore` (host names or machine ids to leave out, separated by commas: a laptop that sleeps every night) |
| A branch diverges across machines | the Cleanup page's "same repo diverged across machines" finding appears | `Diverged`: `Enabled` |
| CI turns red | checks fail on a pull request of yours | `CiFailed`: `Enabled`, `IncludeDrafts` (`false`) |
| An agent action fails | a fix, removal, clone or update queued from the dashboard fails (a failed Refresh does not alert) | `ActionFailed`: `Enabled` |
| Daily digest | once a day: "3 repos at risk, 12 safe to remove, 4.2 GB" | `DailyDigest`: `Enabled` (`false`), `Time` (`08:00`) |
| A T3 thread needs input | a [T3 Code](#t3-code-and-github-issues) thread the dashboard started is waiting for an answer or an approval | `T3NeedsInput`: `Enabled` (`false`) |
| A T3 thread is done | a thread the dashboard started opened a pull request, or settled | `T3Done`: `Enabled` (`false`) |

**Once, not every five minutes.** An alert goes out when its condition first becomes true and not
again while it lasts. When the condition clears (you push, the machine reports, CI goes green) the
hub forgets it, so the same thing happening again is a new alert. Set `RenotifyHours` to be reminded
every so many hours while it lasts; `0`, the default, means once. Snoozing or dismissing the Cleanup
finding for a diverged branch or a failing pull request silences its alert too. More than five new
alerts in one go (the first run, usually) arrive as a single summary. With **quiet hours** on, nothing
is sent between `Start` and `End`; what came up meanwhile is sent when they end, if it is still true.
The digest is sent at its own time regardless. An alert that reaches no channel is tried again, a
minute later and then at growing intervals, and every attempt is in the delivery log.

After a restart the hub gives every machine the full `Minutes` again before calling it offline: the
agents could not report while it was down.

The hub looks a few seconds after every snapshot, GitHub poll or action result, and every 5 minutes
besides (`EvaluateIntervalMinutes`).

#### Channels

Set them up on the Alerts page, or in configuration. Each has a **Send test** button.

**Home Assistant.** DevDash posts to a
[webhook trigger](https://www.home-assistant.io/docs/automation/trigger/#webhook-trigger), and your
automation decides what happens. Make up a long random webhook ID, enter Home Assistant's address and
the ID on the Alerts page, and add an automation like this:

```yaml
automation:
  - alias: DevDash alert
    triggers:
      - trigger: webhook
        webhook_id: !secret devdash_webhook_id
        allowed_methods: [POST]
        local_only: true          # false if the hub reaches Home Assistant over Tailscale or the internet
    actions:
      - action: notify.mobile_app_your_phone
        data:
          title: "{{ trigger.json.title }}"
          message: "{{ trigger.json.message }}"
          data:
            url: "{{ trigger.json.url }}"       # tapping the notification opens the hub page
            tag: "{{ trigger.json.key }}"       # a reminder replaces the earlier notification
      # Only for the serious ones, e.g. commits that exist on one machine only:
      - if:
          - "{{ trigger.json.severity == 'high' }}"
        then:
          - action: light.turn_on
            target: { entity_id: light.desk }
            data: { color_name: red }
```

`trigger.json` is the body below. Home Assistant answers 200 to any webhook ID, known or not, so
**Send test** proves the address; watch the automation's trace to see it fire.

**ntfy.** Enter the server (`https://ntfy.sh` or your own), a topic, and an access token if the topic
needs one. The alert's title, text and priority are sent, tapping it opens the hub page, and a
failing pull request gets an **Open on GitHub** button. On a server without access control anyone who
knows the topic can read it, so pick one nobody could guess; DevDash treats it as a secret.

**Webhook.** A JSON `POST` to any address, with `Authorization: Bearer <token>` if you set a token:

```json
{
  "source": "devdash",
  "trigger": "at-risk",
  "severity": "high",
  "title": "widget on work-mac: 2 commits on feature/x not on any remote, oldest 4 days",
  "message": "Only on work-mac, in /Users/you/Developer/widget.",
  "url": "https://devdash.example.ts.net/safety",
  "path": "safety",
  "key": "at-risk:5d9e9560a396b73ebc0d8a9c",
  "count": 1,
  "time": "2026-11-20T08:00:00+00:00",
  "test": false
}
```

`trigger` is `at-risk`, `machine-offline`, `diverged`, `ci-failed`, `action-failed`, `digest`,
`summary` (several at once; `count` says how many) or `test`. `severity` is `info`, `warning` or
`high`. `key` is the same for the same condition every time. `link` is added when there is a page
outside the hub, such as the pull request. `url` is left out until the hub knows its own address:

```sh
Hub__PublicUrl=https://devdash.<your-tailnet>.ts.net     # DEVDASH_PUBLIC_URL in .env for docker compose
```

#### Settings

Everything is under `Hub:Alerts` in `appsettings.json`, or `Hub__Alerts__…` in the environment:

```sh
Hub__Alerts__Channels__HomeAssistant__Enabled=true
Hub__Alerts__Channels__HomeAssistant__Url=http://homeassistant.local:8123
Hub__Alerts__Channels__HomeAssistant__WebhookId=<the webhook id>
Hub__Alerts__Channels__Ntfy__Enabled=true
Hub__Alerts__Channels__Ntfy__Server=https://ntfy.sh
Hub__Alerts__Channels__Ntfy__Topic=<topic>
Hub__Alerts__Channels__Ntfy__Token=<optional access token>
Hub__Alerts__Channels__Webhook__Enabled=true
Hub__Alerts__Channels__Webhook__Url=https://example.com/hooks/<key>
Hub__Alerts__Channels__Webhook__Token=<optional bearer token>
Hub__Alerts__QuietHours__Enabled=true      # Start 22:00, End 07:00
Hub__Alerts__TimeZone=Europe/London        # for quiet hours and the digest; default: the hub's own (TZ)
Hub__Alerts__RenotifyHours=24
Hub__Alerts__Enabled=false                 # switches all of it off
```

With docker compose, add the lines you want to the hub's `environment:` in `docker-compose.yml`
(without `Hub__PublicUrl`, which is already there), or simply use the page.

Anything you change on the Alerts page is saved in the hub's database and wins over configuration;
a setting you have not changed there keeps following configuration, and **Reset to configuration**
forgets everything saved on the page. The webhook address and token, the ntfy topic and token, and
the Home Assistant webhook ID are secrets: the page only ever says "set", "set in configuration" or
"not set", they are stored encrypted with the hub's data-protection key, and they are never logged,
nor are the addresses alerts are posted to. That key is kept in `keys/` inside the hub's data
directory (`/data` in the container, which is the `devdash-data` volume), so the secrets stay
readable across `docker compose pull && docker compose up -d`. If you ever move the database without
that folder, the saved secrets cannot be read any more: the hub says so in its log and you enter them
again. A full Home
Assistant webhook address (`…/api/webhook/<id>`) pasted into the address box is split, so the ID is
kept with the secrets.

A hub database from before alerts gains the three alert tables on start, like the others.

### GitHub

The hub polls GitHub every 5 minutes with read-only GraphQL queries. It takes the token from
`GitHub:Token`, then `GITHUB_TOKEN`, then `gh auth token`. All your orgs are discovered
automatically; list any to skip in `GitHub:ExcludeOrgs` in `src/DevDash.Hub/appsettings.json`.

#### GitHub on the branch map

Open pull requests are drawn on a repo's [branch map](#branch-map), at the tip of their branch: a
chip with the CI state as a dot (green passing, amber running, red failing), the number and title, and
"draft", "in review", "approved" or "changes" for where the review stands. It links to the pull
request. The same chip sits beside the branch's name in the **Branches** table further down the page.

A pull request is matched to the repo by its GitHub identity, and to a branch in two ways: by commit,
when a branch is at the very commit the pull request is at on GitHub (so a local branch with another
name is found too, and it is the only way a pull request from a fork matches), and otherwise by name,
against the branch a local branch tracks. When the two disagree (you have commits you have not
pushed, or GitHub has some you have not fetched) the chip is marked **≠** and its tooltip says which
commit GitHub has. Pull requests whose branch is on none of your machines are listed under the map.

This uses what the poll already fetches (pull requests you opened, are assigned, or are asked to
review, with the `statusCheckRollup` of the head commit) plus the head commit's id, so it costs no
extra requests. Pull requests you are not involved in are not shown.

### T3 Code and GitHub issues

When a machine has [T3 Code](https://t3.codes), its agent (0.7.0 or newer) finds T3's command-line tool
(`~/.t3/bin/t3`; on Windows `t3.exe` or `t3.cmd` there, else on `PATH`; or `Agent:T3:Path`), scans
`~/.t3/projects` as well as `~/Developer` when no roots are configured, and reports each repository's
T3 threads: what they are called, whether they are running, waiting for you or settled, their branch and
any pull request they opened. Only a repository's **own** T3 project counts: a project that several
scanned repositories fall inside (one rooted at `~/Developer`, say) is shown for none of them. Worktrees
T3 makes for its threads (`~/.t3/worktrees/…`) are listed on the repository they belong to, marked
"T3 Code", and never offered for removal: archive the thread in T3 instead. `--no-t3` turns all of it off.
If T3 Code is not installed or not running, nothing is reported and the scan carries on.

With actions on (`--enable-actions`), the repo page also has **Open in T3** (opens the project in the
T3 Code app on that machine) and **Ask Claude in T3**: a prompt, a machine, and a new branch
(`t3/<slug>`) to start a T3 thread on, in a new worktree based on the repo's default branch, so your
checked-out work is not touched. **Fix in T3** buttons start one with the prompt filled in, for a pull
request of yours with failing CI (with the failing run's address), vulnerable packages, and a pinned
toolchain a machine lacks. ⌘K offers "Open … in T3" and "Ask Claude in T3 about …".

The **Issues** page lists the open issues of the GitHub repositories you have cloned (conditional
requests, so an unchanged list costs nothing), each with **Start in T3**: the issue, its labels and
its latest comments go into a prompt from a template you can edit, framed as untrusted text written by
the issue's author, asking for a fix on `issue/<n>-<slug>`, the tests run, and a pull request that says
"Fixes #<n>". An issue gets one thread; **Start again** starts another.

**Starting threads from a label** is off until you turn it on in the Issues page's settings, and then
only for the repos you tick. When an open issue gets the label (`t3` by default), a thread starts on the
repo's preferred machine, or the most recently seen one that can. Because a thread usually runs with
full access to its machine, the label is only trusted when it was added by an allowed GitHub login (by
default the user the hub's token belongs to, checked against the issue's events), and on a public
repository only when the issue was written by one too. Anything else waits on the page as "needs a
manual start", with the reason. Optionally, and off by default, the hub comments "Started a Claude
thread for this issue" (no links, no machine names) and/or swaps the label for `t3:started`; a token
that may not do that is reported on the page.

### Cleanup

The Cleanup page lists findings from the rules in `src/DevDash.Core/Cleanup/Rules`. Each finding has
a short list of commands, each with a label and a Copy button: the ones that only look come first
(`git log`, `git stash show -p`, `git status` in a worktree), then the ones that fix. A branch that is
behind its upstream as well as ahead gets a rebase before its push, not a push that would be
rejected. Small `cd` and `code` chips copy a shortcut to the folder.

Each group also has a **Copy the fixes as a script** button per machine. It copies that machine's
fix commands for the group, with a comment line per finding. A script never mixes machines.

Snooze and dismiss decisions are stored against the finding's fingerprint, so they survive rescans.

One rule is about secrets: untracked files named like one (`.env*`, `*.pem`, `*.key`, `*.p12`, `*.pfx`,
`id_rsa*` and the other SSH key names, `credentials*.json`) that `.gitignore` does not cover, so a
`git add .` would commit them. It goes by the file name alone and suggests the lines to add to
`.gitignore`. Templates such as `.env.example` and public keys (`*.pub`) are left alone.

#### Running a fix from the dashboard (agent actions)

By default DevDash only shows commands. A machine can opt in to running the safe ones:

```sh
devdash-agent run --enable-actions        # or "Agent": { "Actions": { "Enabled": true } } in ~/.config/devdash/agent.json
```

That machine then gets a **Run on \<machine\>** button beside the fixes that have an allowlisted
action. Clicking it shows the machine, the repo and the exact git command, and nothing is queued until
you confirm. The agent picks it up at once (see [how a click reaches the agent](#refresh-one-project)), runs it, reports the result
and rescans, so the finding goes away. The Machines page lists every action: who queued it, when it
was claimed, and what git printed.

What keeps this safe:

- **Off by default, per machine.** Only the machine's own configuration turns it on. The hub shows
  Run buttons only for machines that report it, and `Hub:Actions:Enabled=false` turns it off everywhere.
- **Typed actions, not commands.** The hub sends a kind and its fields; the agent builds the git
  arguments itself. The whole list: `branch -d` (never `-D`), `worktree remove` (never `--force`),
  `worktree prune`, `fetch --prune`, `push` of one branch to the same name (never forced; the agent
  fetches first and refuses if the branch is behind), `stash drop` (only if the stash is still the
  commit you were shown), and, from 0.6.0, a fast-forward-only pull, deleting build output,
  [moving a safe project to the trash and cloning it back](#removing-a-project-and-cloning-it-back).
  There is no rebase, reset, merge commit, force-delete or permanent delete of anything but build output.
- **Only what the agent says it knows.** An agent reports which kinds it takes. An older agent is
  never sent a newer kind; its buttons are disabled with "update the agent" as the reason.
- **Checked again on the machine.** The repo must be the top of a git working tree inside the agent's
  scan roots. Branch names go through `git check-ref-format` and must exist; a worktree must be a
  linked, unlocked worktree of that repo, on a branch. Git runs with an argument list, no shell.
- **Bound, once, and short-lived.** An agent only receives actions for the machine its key is bound
  to. Each action is handed out once and never retried, and expires if not picked up in 10 minutes.

A hub database created before this feature gains the new `AgentActions` table on start, and one from
before 0.6.0 gains `RemovedProjects` the same way; nothing needs resetting.

### Claude (MCP)

The hub is also an MCP server, so Claude Code (or Claude inside T3 Code) can ask it things: "what
haven't I pushed?", "which clones are safe to delete?", "compare my clones of this repo", "what have
I been working on this week?". Open the **Claude** page, create a token, and run the command it shows:

```sh
claude mcp add --transport http devdash https://devdash.<your-tailnet>.ts.net/mcp --header "Authorization: Bearer ddm_…"
```

The address is `Hub:PublicUrl` when set, otherwise whatever address you opened the page at. To share
it with a project instead, put this in its `.mcp.json` and keep the token in your environment
(`export DEVDASH_MCP_TOKEN=ddm_…`), never in the file:

```json
{
  "mcpServers": {
    "devdash": {
      "type": "http",
      "url": "https://devdash.<your-tailnet>.ts.net/mcp",
      "headers": { "Authorization": "Bearer ${DEVDASH_MCP_TOKEN}" }
    }
  }
}
```

A token is shown once and stored only as a hash; the page lists when each was last used and revokes
them. Every request to `/mcp` needs one, even from a browser the dashboard already trusts.

| Scope | Tools |
|---|---|
| **read** (default) | `list_machines`, `list_projects`, `get_project`, `at_risk_work`, `safe_to_remove`, `findings`, `pull_requests`, `compare_clones`, `recent_activity`, `action_status`, `t3_threads`, `list_issues` |
| **read + actions** | also `refresh_project`, `fetch`, `push_branch` (never forced), `pull_fast_forward`, `clean_build_dirs` |

Actions go through the same queue and checks as the dashboard's buttons (the machine must have
[actions enabled](#running-a-fix-from-the-dashboard-agent-actions)), are recorded as queued by
`mcp:<token name>`, and are re-validated by the agent. Removing a project, dropping a stash, deleting
branches or worktrees and updating agents are not available over MCP at all. Each token gets 120
requests a minute (`Hub:Mcp:RequestsPerMinute`); `Hub:Mcp:Enabled=false` turns the server off.

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

**Data:** state lives in two Docker volumes. `devdash-data` holds the SQLite database and the
data-protection keys (`/data/keys`: they decrypt the saved alert secrets, so back the two up together); `tailscale-state` holds the node key and HTTPS certificate. Keeping
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
src/DevDash.Core        git output parsers, remote URL normalisation, cleanup rules, safety analysis,
                        toolchain pins and global.json roll-forward (Toolchains/) (pure)
src/DevDash.Agent       devdash-agent: scanner, worker service, CLI, service installers, self-update (Updates/),
                        toolchains (Toolchains/), dependency checks (Dependencies/)
src/DevDash.Hub         ingest API, GitHub poller, alerts (Alerts/), Blazor UI, SQLite; wwwroot/palette.js is the
                        command palette's keyboard handling
tests/                  parser fixtures, rule tests, scanner tests on throwaway repos, hub integration tests
scripts/                install.sh and install.ps1 (agent installers), publish-agent.sh (release builds),
                        release-signing-key.sh (the release key), test-ota.sh (self-update, end to end)
.github/workflows/      ci.yml (build, test and self-update on Linux, macOS, Windows), release.yml (tag to signed release)
```

## Licence

MIT, see [LICENSE](LICENSE).
