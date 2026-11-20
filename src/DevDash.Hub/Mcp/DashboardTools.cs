using System.ComponentModel;
using System.Globalization;
using DevDash.Contracts;
using DevDash.Core;
using DevDash.Core.Cleanup;
using DevDash.Core.GitHub;
using DevDash.Core.Safety;
using DevDash.Core.Toolchains;
using DevDash.Hub.Data;
using DevDash.Hub.GitHub;
using DevDash.Hub.Services;
using DevDash.Hub.Updates;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DevDash.Hub.Mcp;

/// <summary>
/// The read tools. Each one reuses the service a dashboard page uses, so Claude sees the same verdicts
/// and lists the pages show. Nothing here changes anything.
/// </summary>
[McpServerToolType]
public sealed class DashboardTools(
    SnapshotStore snapshots,
    RepoCatalog catalog,
    SafetyService safety,
    FindingsService findings,
    GitHubStore github,
    AgentUpdates updates,
    IDbContextFactory<DevDashDbContext> dbFactory,
    TimeProvider time)
{
    private DateTimeOffset Now => time.GetUtcNow();

    // ---- machines -------------------------------------------------------------------------------

    [McpServerTool(Name = "list_machines", Title = "List machines", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists every machine whose DevDash agent reports to the hub: name, OS, online or last seen, agent version (and whether an update is out), " +
                 "scan roots, and capabilities, i.e. which actions its agent accepts (actions, refresh, pull, clean build dirs). " +
                 "Use it to learn machine names for the 'machine' filter of other tools, or to see why an action would be refused.")]
    public string ListMachines()
    {
        var machines = snapshots.Machines.Select(m =>
        {
            var info = m.Machine;
            var update = updates.For(m);
            return new
            {
                name = info.Hostname,
                machineId = m.MachineId,
                os = info.OsDescription ?? info.Os,
                online = snapshots.IsOnline(m),
                lastSeen = m.LastSeenAt,
                lastScan = m.Snapshot.ScannedAt,
                agentVersion = info.AgentVersion,
                latestAgentVersion = update.Latest,
                updateAvailable = update.Behind,
                roots = m.Snapshot.Roots,
                entries = m.Snapshot.Entries.Count,
                capabilities = new
                {
                    actions = info.ActionsEnabled,
                    fetchAndPush = AgentActions.Accepts(info, AgentActionKind.Fetch),
                    pullFastForward = AgentActions.Accepts(info, AgentActionKind.PullFastForward),
                    cleanBuildDirs = AgentActions.Accepts(info, AgentActionKind.CleanBuildDirs),
                    refresh = AgentActions.Accepts(info, AgentActionKind.Refresh),
                    updateMode = info.UpdateMode,
                },
                toolchainsReported = info.Toolchains is not null,
            };
        });

        return McpOutput.Json(new { now = Now, machines });
    }

    // ---- projects -------------------------------------------------------------------------------

    [McpServerTool(Name = "list_projects", Title = "List projects", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists working trees across all machines, one row per clone (a repo cloned on two machines is two rows; linked worktrees are rows " +
                 "marked worktree=true). Each row: project name, repo key, machine, path, branch, uncommitted (staged+unstaged+conflicted), untracked, " +
                 "ahead/behind its upstream, safety verdict (at risk / safe to remove / safe with caveats / not sure) and last activity. " +
                 "Use the filters to narrow it; use get_project for one project in depth.")]
    public async Task<string> ListProjects(
        [Description("Only this machine (hostname or machine id).")] string? machine = null,
        [Description("Only projects whose name, key, path or branch contains this text.")] string? text = null,
        [Description("Only clones whose safety verdict is 'at risk' (holding work that exists nowhere else).")] bool only_at_risk = false,
        [Description("Only clones with uncommitted or untracked changes.")] bool only_dirty = false,
        [Description("'name' (default) or 'recent' (most recent activity first).")] string? sort = null,
        [Description("Most rows to return (default 100, max 200).")] int? limit = null,
        CancellationToken ct = default)
    {
        var verdicts = safety.ByClone();
        var rows = (await catalog.GetAsync(ct))
            .SelectMany(r => r.Clones.Select(c => (Row: r, Clone: c)))
            .Where(x => McpLookup.MachineMatches(x.Clone.Machine, machine))
            .Where(x => string.IsNullOrWhiteSpace(text)
                || x.Row.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || x.Row.Key.Contains(text, StringComparison.OrdinalIgnoreCase)
                || x.Clone.Entry.Path.Contains(text, StringComparison.OrdinalIgnoreCase)
                || (x.Clone.Git?.Branch?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(x => (x.Row, x.Clone, Safety: verdicts.GetValueOrDefault((x.Clone.Machine.MachineId, x.Clone.Entry.Path))))
            .Where(x => !only_at_risk || x.Safety is { IsAtRisk: true })
            .Where(x => !only_dirty || x.Clone.Git is { IsDirty: true } or { Untracked: > 0 })
            .ToList();

        if (string.Equals(sort, "recent", StringComparison.OrdinalIgnoreCase))
        {
            rows = rows.OrderByDescending(x => LastActivity(x.Clone)).ToList();
        }

        var take = McpOutput.Limit(limit, 100);
        return McpOutput.Json(new
        {
            now = Now,
            total = rows.Count,
            shown = Math.Min(take, rows.Count),
            projects = rows.Take(take).Select(x => new
            {
                project = x.Row.Name,
                key = x.Row.Key,
                machine = x.Clone.Machine.Machine.Hostname,
                path = x.Clone.Entry.Path,
                kind = x.Clone.Entry.Kind,
                worktree = x.Clone.IsLinkedWorktree ? true : (bool?)null,
                branch = x.Clone.Git?.Detached == true ? "(detached)" : x.Clone.Git?.Branch,
                uncommitted = x.Clone.Git?.DirtyCount,
                untracked = x.Clone.Git?.Untracked,
                ahead = x.Clone.Git?.Ahead,
                behind = x.Clone.Git?.Behind,
                safety = x.Safety is null ? null : SafetyView.VerdictLabel(x.Safety.Verdict),
                lastActivity = LastActivity(x.Clone),
            }),
        });
    }

    [McpServerTool(Name = "get_project", Title = "Get project", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Everything DevDash knows about one project, on every machine (or one): per clone the branch, uncommitted/untracked counts, " +
                 "every local branch with upstream and ahead/behind and whether its commits are on a remote, stashes, worktrees, the safety verdict " +
                 "with its reasons, build output size, vulnerable dependencies and toolchain pins against what the machine has installed. " +
                 "Also the project's GitHub repo, open pull requests with CI, possible pushes/fast-forwards, and active cleanup findings. " +
                 "Identify the project by name, owner/name, key (github.com/owner/repo) or the path of a clone.")]
    public async Task<string> GetProject(
        [Description("Project name, owner/name, key, or the absolute path of a clone as the machine reported it.")] string project,
        [Description("Only clones on this machine (hostname or machine id).")] string? machine = null,
        CancellationToken ct = default)
    {
        var row = McpLookup.Project(await catalog.GetAsync(ct), project);
        var verdicts = safety.ByClone();
        var now = Now;
        var clones = row.Clones.Where(c => McpLookup.MachineMatches(c.Machine, machine)).ToList();
        var paths = row.Clones.Select(c => c.Entry.Path).ToHashSet(StringComparer.Ordinal);
        var active = (await findings.GetAsync(ct))
            .Where(v => v.IsActive && (string.Equals(v.Finding.RepoKey, row.Key, StringComparison.OrdinalIgnoreCase) || (v.Finding.Path is { } p && paths.Contains(p))))
            .ToList();

        return McpOutput.Json(new
        {
            now,
            key = row.Key,
            name = row.Name,
            owner = row.Owner,
            kind = row.Kind,
            description = row.Description?.Text,
            github = row.GitHub is not { } gh ? null : new
            {
                gh.NameWithOwner,
                gh.Url,
                gh.DefaultBranch,
                gh.PushedAt,
                archived = gh.IsArchived ? true : (bool?)null,
                @private = gh.IsPrivate,
            },
            remoteMissing = row.RemoteMissing ? true : (bool?)null,
            lastActivity = row.LastActivity,
            clones = clones.Select(c => Clone(c, verdicts.GetValueOrDefault((c.Machine.MachineId, c.Entry.Path)), now)),
            sync = SyncOffers.For(row.Clones).Where(o => McpLookup.MachineMatches(o.Machine, machine)).Select(Offer),
            pullRequests = row.PullRequests.Select(PullRequest),
            openIssues = row.Issues.Count == 0 ? null : McpOutput.Cap(row.Issues.OrderByDescending(i => i.UpdatedAt), 20).Select(i => new { i.Number, i.Title, i.Url, i.UpdatedAt }),
            findings = McpOutput.Cap(active, 30).Select(Finding),
        });
    }

    private object Clone(CloneView c, SafetyAssessment? assessment, DateTimeOffset now)
    {
        var git = c.Git;
        var entry = c.Entry;
        return new
        {
            machine = c.Machine.Machine.Hostname,
            online = snapshots.IsOnline(c.Machine),
            lastScan = c.Machine.Snapshot.ScannedAt,
            path = entry.Path,
            kind = entry.Kind,
            worktree = c.IsLinkedWorktree ? new { main = git?.MainWorktreePath ?? c.Main?.Path, stale = c.Stale is { IsStale: true } s ? s.Kind.ToString() : null } : null,
            error = entry.Error,
            git = git is null ? null : new
            {
                branch = git.Detached ? null : git.Branch,
                detached = git.Detached ? true : (bool?)null,
                head = Fmt.Sha(git.HeadSha),
                git.Upstream,
                upstreamGone = git.UpstreamGone ? true : (bool?)null,
                git.Ahead,
                git.Behind,
                git.Staged,
                git.Unstaged,
                git.Untracked,
                git.Conflicted,
                git.DefaultBranch,
                git.LastFetchAt,
                lastCommit = git.LastCommit is not { } lc ? null : new { sha = Fmt.Sha(lc.Sha), lc.Date, lc.Subject, lc.Author },
                remotes = git.Remotes.Select(r => new { r.Name, r.Url }),
                branches = McpOutput.Cap(git.Branches.OrderByDescending(b => b.IsCurrent).ThenByDescending(b => b.LastCommitDate), 50).Select(b => new
                {
                    b.Name,
                    current = b.IsCurrent ? true : (bool?)null,
                    b.Upstream,
                    upstreamGone = b.UpstreamGone ? true : (bool?)null,
                    b.Ahead,
                    b.Behind,
                    merged = b.MergedIntoDefault,
                    onRemote = b.OnRemote,
                    unpushed = b.UnpushedCount,
                    lastCommit = b.LastCommitDate,
                    b.Subject,
                }),
                moreBranches = git.Branches.Count > 50 ? git.Branches.Count - 50 : (int?)null,
                stashes = McpOutput.Cap(git.Stashes, 20).Select(s => new { s.Index, s.Message, s.Date, s.Branch }),
                worktrees = git.Worktrees.Count <= 1 ? null : git.Worktrees.Select(w => new { w.Path, w.Branch, locked = w.Locked ? true : (bool?)null, prunable = w.Prunable ? true : (bool?)null }),
            },
            safety = assessment is null ? null : Safety(assessment),
            size = entry.Size is not { } size ? null : new
            {
                total = Fmt.Size(size.TotalBytes),
                buildOutput = Fmt.Size(size.ReclaimableBytes),
                buildDirs = McpOutput.Cap(size.ReclaimableDirs, 25).Select(d => new { d.Path, size = Fmt.Size(d.Bytes) }),
            },
            dependencies = entry.Dependencies is not { } deps ? null : new
            {
                deps.CheckedAt,
                summary = DependencyView.Counts(deps),
                outdated = deps.OutdatedTotal,
                reports = deps.Reports.Select(r => new
                {
                    ecosystem = DependencyView.Ecosystem(r.Ecosystem),
                    r.Target,
                    vulnerable = r.TopVulnerable.Select(p => $"{p.Name} {p.Version} ({p.Severity})"),
                    outdated = r.TopOutdated.Select(p => $"{p.Name} {p.Current} -> {p.Latest}"),
                    r.Skipped,
                }),
            },
            toolchains = entry.Pins is not { Count: > 0 } ? null : ToolchainCheck.For(entry, c.Machine.Machine).Select(check => new
            {
                tool = ToolNames.Display(check.Pin.Tool),
                pinned = check.Pin.Version,
                source = check.Pin.Source,
                status = check.Status,
                check.Resolved,
                check.Detail,
            }),
        };
    }

    private static object Safety(SafetyAssessment s) => new
    {
        verdict = SafetyView.VerdictLabel(s.Verdict),
        level = s.IsAtRisk ? s.Level.ToString() : null,
        reasons = s.Reasons.Select(r => new { kind = r.Kind, effect = r.Effect, level = r.Effect == SafetyEffect.Risk ? r.Level.ToString() : null, text = r.Text, since = r.Since, names = r.Names.Count == 0 ? null : r.Names.Take(10) }),
        s.OldestRiskAt,
        s.LastTouchedAt,
        freed = s.FreedBytes is { } freed ? Fmt.Size(freed) : null,
    };

    private static object Offer(SyncOffer o) => new
    {
        kind = o.Kind == SyncKind.Push ? "push" : "pull_fast_forward",
        machine = o.Machine.Machine.Hostname,
        path = o.RepoPath,
        branch = o.Branch,
        remote = o.Remote,
        commits = o.Count,
        what = o.What,
        blocked = o.Blocked,
    };

    private static object PullRequest(PullRequestInfo p) => new
    {
        repo = p.Repo,
        number = p.Number,
        title = p.Title,
        url = p.Url,
        author = p.Author,
        draft = p.IsDraft ? true : (bool?)null,
        ci = p.Ci,
        review = p.ReviewDecision,
        mergeable = p.Mergeable,
        attention = PrTriage.Attention(p) is var a && a != PrAttention.None ? a.ToString() : null,
        branch = p.HeadRef,
        @base = p.BaseRef,
        updated = p.UpdatedAt,
        role = string.Join(",", new[] { p.Authored ? "author" : null, p.ReviewRequested ? "reviewer" : null, p.Assigned ? "assignee" : null }.Where(r => r is not null)),
    };

    private static object Finding(FindingView v) => new
    {
        fingerprint = v.Finding.Fingerprint,
        rule = v.Finding.RuleId,
        severity = v.Finding.Severity,
        title = v.Finding.Title,
        explanation = v.Finding.Explanation,
        machine = v.Finding.MachineName,
        path = v.Finding.Path,
        repo = v.Finding.RepoKey,
        url = v.Finding.Url,
        lastActivity = v.Finding.LastActivityAt,
        status = v.Status?.ToString(),
        commands = v.Finding.Commands.Select(c => new { c.Label, c.Command, kind = c.Kind, action = c.Action?.Kind.ToString(), machine = v.Finding.MachineNameFor(c) }),
    };

    private static DateTimeOffset? LastActivity(CloneView c) =>
        new[] { c.Entry.LastModified, c.Git?.LastCommit?.Date }.Max();

    // ---- safety ---------------------------------------------------------------------------------

    [McpServerTool(Name = "at_risk_work", Title = "Work at risk", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Everything that exists on only one machine and nowhere else: commits on no remote, local-only branches, uncommitted changes, " +
                 "untracked files, stashes, local-only tags, detached HEADs, repos with no remote. One row per working tree with each risk, " +
                 "its level (High = commits on no remote; Medium = uncommitted/stashes/tags; Low = untracked) and how long it has been there. " +
                 "Use it to answer 'what would I lose if this laptop died?' or 'what haven't I pushed?'.")]
    public string AtRiskWork(
        [Description("Only this machine (hostname or machine id).")] string? machine = null,
        [Description("'age' (default: oldest unpushed work first) or 'severity' (High risks first).")] string? sort = null,
        [Description("Most rows to return (default 100, max 200).")] int? limit = null)
    {
        var rows = SafetyView.AtRisk(safety.Get().Where(r => McpLookup.MachineMatches(r.Machine, machine)), SafetyView.ParseSort(sort));
        var take = McpOutput.Limit(limit, 100);
        return McpOutput.Json(new
        {
            now = Now,
            total = rows.Count,
            high = rows.Count(r => r.Safety.Level == RiskLevel.High),
            shown = Math.Min(take, rows.Count),
            atRisk = rows.Take(take).Select(r => new
            {
                project = r.RepoName,
                repo = r.RepoKey,
                machine = r.Machine.Machine.Hostname,
                path = r.Entry.Path,
                level = r.Safety.Level.ToString(),
                oldestRiskAt = r.Safety.OldestRiskAt,
                lastTouched = r.Safety.LastTouchedAt,
                risks = r.Safety.Risks.Select(x => new { kind = x.Kind, level = x.Level.ToString(), text = x.Text, since = x.Since, names = x.Names.Count == 0 ? null : x.Names.Take(10) }),
            }),
        });
    }

    [McpServerTool(Name = "safe_to_remove", Title = "Safe to remove", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Working trees whose deletion would lose nothing: everything is pushed, nothing uncommitted, no stashes ('safe to remove'), " +
                 "or only ignored files that are not build output ('safe with caveats', listed). Longest-untouched first, with the space each frees " +
                 "and the total. Removing is NOT available over MCP; the user does it from the dashboard's Safety page.")]
    public string SafeToRemove(
        [Description("Only this machine (hostname or machine id).")] string? machine = null,
        [Description("Most rows to return (default 100, max 200).")] int? limit = null)
    {
        var rows = SafetyView.Removable(safety.Get().Where(r => McpLookup.MachineMatches(r.Machine, machine)));
        var take = McpOutput.Limit(limit, 100);
        return McpOutput.Json(new
        {
            now = Now,
            total = rows.Count,
            totalFreed = Fmt.Size(rows.Sum(r => r.FreedBytes)),
            shown = Math.Min(take, rows.Count),
            candidates = rows.Take(take).Select(r => new
            {
                project = r.RepoName,
                machine = r.Machine.Machine.Hostname,
                path = r.Entry.Path,
                verdict = SafetyView.VerdictLabel(r.Safety.Verdict),
                freed = Fmt.Size(r.FreedBytes),
                freedBytes = r.FreedBytes,
                lastTouched = r.Safety.LastTouchedAt,
                worktree = r.Safety.IsLinkedWorktree ? true : (bool?)null,
                caveats = r.Safety.Caveats.Select(c => c.Text).ToList() is { Count: > 0 } list ? list : null,
            }),
        });
    }

    // ---- findings -------------------------------------------------------------------------------

    [McpServerTool(Name = "findings", Title = "Cleanup findings", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The dashboard's cleanup findings: merged branches, stale worktrees, gone upstreams, old stashes, unpushed commits, large build " +
                 "output, diverged clones, failing-CI or stale PRs, vulnerable dependencies, toolchain mismatches and so on. Each has a severity, " +
                 "an explanation and the suggested shell commands (to show the user; only some can be run as agent actions). Snoozed and dismissed " +
                 "findings are left out unless include_inactive is true.")]
    public async Task<string> Findings(
        [Description("Only this severity or worse: 'info', 'low', 'medium' or 'high'.")] string? min_severity = null,
        [Description("Only this rule id, e.g. 'merged-branch', 'stale-worktree', 'unpushed-commits'.")] string? rule = null,
        [Description("Only findings on this machine (hostname or machine id).")] string? machine = null,
        [Description("Only findings whose repo key, path or title contains this text.")] string? project = null,
        [Description("Also include snoozed and dismissed findings.")] bool include_inactive = false,
        [Description("Most findings to return (default 50, max 200).")] int? limit = null,
        CancellationToken ct = default)
    {
        FindingSeverity? floor = null;
        if (!string.IsNullOrWhiteSpace(min_severity))
        {
            floor = Enum.TryParse<FindingSeverity>(min_severity, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : throw new McpException($"Unknown severity '{min_severity}'. Use one of: {string.Join(", ", Enum.GetNames<FindingSeverity>()).ToLowerInvariant()}.");
        }

        var named = string.IsNullOrWhiteSpace(machine) ? null : McpLookup.Machine(snapshots, machine);
        var all = await findings.GetAsync(ct);
        var list = all
            .Where(v => include_inactive || v.IsActive)
            .Where(v => floor is null || v.Finding.Severity >= floor)
            .Where(v => string.IsNullOrWhiteSpace(rule) || string.Equals(v.Finding.RuleId, rule.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(v => named is null || string.Equals(v.Finding.MachineId, named.MachineId, StringComparison.OrdinalIgnoreCase)
                || v.Finding.Commands.Any(c => string.Equals(c.MachineId, named.MachineId, StringComparison.OrdinalIgnoreCase)))
            .Where(v => string.IsNullOrWhiteSpace(project)
                || (v.Finding.RepoKey?.Contains(project, StringComparison.OrdinalIgnoreCase) ?? false)
                || (v.Finding.Path?.Contains(project, StringComparison.OrdinalIgnoreCase) ?? false)
                || v.Finding.Title.Contains(project, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var take = McpOutput.Limit(limit, 50);
        return McpOutput.Json(new
        {
            now = Now,
            total = list.Count,
            shown = Math.Min(take, list.Count),
            bySeverity = list.GroupBy(v => v.Finding.Severity).OrderByDescending(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            rules = findings.Rules.Select(r => r.Id),
            findings = list.Take(take).Select(Finding),
        });
    }

    // ---- GitHub ---------------------------------------------------------------------------------

    [McpServerTool(Name = "pull_requests", Title = "Pull requests", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Open GitHub pull requests the user authored, was asked to review, or is assigned, from the hub's last GitHub poll: CI state " +
                 "(Success/Failure/Pending), review decision, mergeability, and 'attention' (CiFailing, ChangesRequested, Conflicts, ReadyToMerge). " +
                 "Also says when GitHub was last polled.")]
    public string PullRequests(
        [Description("'all' (default), 'mine' (authored), 'review' (review requested), 'assigned', or 'attention' (mine that need action).")] string? filter = null,
        [Description("Only PRs whose owner/repo contains this text.")] string? project = null,
        [Description("Most PRs to return (default 50, max 200).")] int? limit = null)
    {
        var data = github.Current;
        var mode = (filter ?? "all").Trim().ToLowerInvariant();
        Func<PullRequestInfo, bool> keep = mode switch
        {
            "all" or "" => _ => true,
            "mine" => p => p.Authored,
            "review" => p => p.ReviewRequested,
            "assigned" => p => p.Assigned,
            "attention" => p => p.Authored && PrTriage.Attention(p) != PrAttention.None,
            _ => throw new McpException($"Unknown filter '{filter}'. Use all, mine, review, assigned or attention."),
        };

        var list = data.PullRequests
            .Where(keep)
            .Where(p => string.IsNullOrWhiteSpace(project) || p.Repo.Contains(project.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.UpdatedAt)
            .ToList();
        var take = McpOutput.Limit(limit, 50);
        return McpOutput.Json(new
        {
            now = Now,
            github = new { polled = data.FetchedAt == default ? (DateTimeOffset?)null : data.FetchedAt, login = data.ViewerLogin, github.Status.LastError, enabled = github.Status.Enabled || github.HasData },
            total = list.Count,
            shown = Math.Min(take, list.Count),
            pullRequests = list.Take(take).Select(PullRequest),
        });
    }

    // ---- across machines ------------------------------------------------------------------------

    [McpServerTool(Name = "compare_clones", Title = "Compare clones", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Compares the same repository across machines: for each branch present on more than one machine, the commit each machine has and " +
                 "whether they agree, ahead/behind per clone, branches that have diverged from their upstream, and which pushes or fast-forward " +
                 "pulls would bring a machine level (these are what push_branch and pull_fast_forward accept). " +
                 "Without 'project', lists only repos cloned on 2+ machines that are out of step.")]
    public async Task<string> CompareClones(
        [Description("One project (name, owner/name, key or a clone's path). Omit to scan every multi-machine repo.")] string? project = null,
        [Description("Most repos to return when no project is given (default 30, max 200).")] int? limit = null,
        CancellationToken ct = default)
    {
        var rows = await catalog.GetAsync(ct);
        IEnumerable<RepoRow> candidates = string.IsNullOrWhiteSpace(project)
            ? rows.Where(r => r.Clones.Where(c => c.Git is not null).Select(c => c.Machine.MachineId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            : [McpLookup.Project(rows, project)];

        var compared = candidates.Select(Compare).Where(c => !string.IsNullOrWhiteSpace(project) || c.OutOfStep).ToList();
        var take = McpOutput.Limit(limit, 30);
        return McpOutput.Json(new
        {
            now = Now,
            total = compared.Count,
            shown = Math.Min(take, compared.Count),
            repos = compared.Take(take).Select(c => c.View),
        });
    }

    private (bool OutOfStep, object View) Compare(RepoRow row)
    {
        var mains = row.Clones.Where(c => c.Git is not null && !c.IsLinkedWorktree).ToList();
        var branches = mains
            .SelectMany(c => c.Git!.Branches.Select(b => (Clone: c, Branch: b)))
            .GroupBy(x => x.Branch.Name, StringComparer.Ordinal)
            .Where(g => g.Select(x => x.Clone.Machine.MachineId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(g => new
            {
                branch = g.Key,
                same = g.Select(x => x.Branch.Sha).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).Count() <= 1,
                machines = g.Select(x => new { machine = x.Clone.Machine.Machine.Hostname, sha = Fmt.Sha(x.Branch.Sha), x.Branch.Ahead, x.Branch.Behind, x.Branch.Upstream, lastCommit = x.Branch.LastCommitDate }),
            })
            .OrderBy(b => b.same)
            .ThenBy(b => b.branch, StringComparer.Ordinal)
            .ToList();
        var diverged = mains
            .SelectMany(c => c.Git!.Branches.Where(b => b.Ahead > 0 && b.Behind > 0).Select(b => new { machine = c.Machine.Machine.Hostname, branch = b.Name, b.Upstream, b.Ahead, b.Behind }))
            .ToList();
        var offers = SyncOffers.For(row.Clones);
        var outOfStep = branches.Any(b => !b.same) || diverged.Count > 0 || offers.Count > 0;

        return (outOfStep, new
        {
            project = row.Name,
            key = row.Key,
            inStep = !outOfStep,
            clones = row.Clones.Where(c => c.Git is not null).Select(c => new
            {
                machine = c.Machine.Machine.Hostname,
                online = snapshots.IsOnline(c.Machine),
                path = c.Entry.Path,
                worktree = c.IsLinkedWorktree ? true : (bool?)null,
                branch = c.Git!.Branch,
                head = Fmt.Sha(c.Git.HeadSha),
                c.Git.Ahead,
                c.Git.Behind,
                uncommitted = c.Git.DirtyCount,
                lastFetch = c.Git.LastFetchAt,
            }),
            sharedBranches = McpOutput.Cap(branches, 40),
            diverged = diverged.Count == 0 ? null : diverged,
            sync = offers.Select(Offer),
        });
    }

    // ---- activity -------------------------------------------------------------------------------

    [McpServerTool(Name = "recent_activity", Title = "Recent activity", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Commit activity over the last N days, from the agents' per-day commit counts (each commit counted once however many machines " +
                 "have it): commits per day, the busiest projects, and each clone's latest commit in the window. Use it for 'what have I been " +
                 "working on?' or a stand-up summary.")]
    public async Task<string> RecentActivity(
        [Description("How many days back (default 14, max 365).")] int? days = null,
        [Description("Only this project (name, owner/name, key or a clone's path).")] string? project = null,
        [Description("Only commits authored with the clone's own git user.email.")] bool mine = false,
        [Description("Most projects and commits to list (default 20, max 200).")] int? limit = null,
        CancellationToken ct = default)
    {
        var span = Math.Clamp(days ?? 14, 1, ActivityView.Days);
        var rows = await catalog.GetAsync(ct);
        if (!string.IsNullOrWhiteSpace(project))
        {
            rows = [McpLookup.Project(rows, project)];
        }

        var now = Now;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var from = today.AddDays(-(span - 1));
        var since = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var take = McpOutput.Limit(limit, 20);

        var perDay = ActivityView.Total(rows, mine).Where(d => d.Key >= from && d.Key <= today).OrderBy(d => d.Key).ToList();
        var busiest = rows
            .Select(r => (Row: r, Count: ActivityView.ForRepo(r, mine).Where(d => d.Key >= from && d.Key <= today).Sum(d => d.Value)))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .Take(take)
            .Select(x => new { project = x.Row.Name, key = x.Row.Key, commits = x.Count, machines = x.Row.Clones.Select(c => c.Machine.Machine.Hostname).Distinct(StringComparer.OrdinalIgnoreCase) });
        var latest = rows
            .SelectMany(r => r.Clones.Where(c => c.Git?.LastCommit is { } lc && lc.Date >= since).Select(c => (Row: r, Clone: c, Commit: c.Git!.LastCommit!)))
            .GroupBy(x => x.Commit.Sha, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(x => x.Commit.Date)
            .Take(take)
            .Select(x => new { project = x.Row.Name, machine = x.Clone.Machine.Machine.Hostname, branch = x.Clone.Git!.Branch, sha = Fmt.Sha(x.Commit.Sha), x.Commit.Date, x.Commit.Subject, x.Commit.Author });

        return McpOutput.Json(new
        {
            now,
            from,
            to = today,
            reported = ActivityView.Any(rows) ? (bool?)null : false,
            totalCommits = perDay.Sum(d => d.Value),
            activeDays = perDay.Count(d => d.Value > 0),
            perDay = perDay.Where(d => d.Value > 0).Select(d => new { date = d.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), commits = d.Value }),
            busiest,
            latestCommits = latest,
        });
    }

    // ---- actions --------------------------------------------------------------------------------

    [McpServerTool(Name = "action_status", Title = "Action status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The state of an agent action by its id (as returned by refresh_project, fetch, push_branch, pull_fast_forward or clean_build_dirs): " +
                 "Queued (waiting for the machine's agent, which checks in about every 30 s), Claimed (running), Succeeded, Failed or Expired " +
                 "(no agent picked it up in time), with git's output once finished.")]
    public async Task<string> ActionStatus(
        [Description("The action id.")] int id,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AgentActions.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new McpException($"No action {id}. The audit trail keeps the most recent few hundred.");
        return McpOutput.Json(Describe(row));
    }

    internal static object Describe(AgentActionEntity a) => new
    {
        id = a.Id,
        machine = a.MachineName,
        kind = a.Kind,
        command = a.CommandLine,
        path = a.RepoPath,
        status = a.Status,
        queuedBy = a.QueuedBy,
        queuedAt = a.QueuedAt,
        expiresAt = a.Status == AgentActionStatus.Queued ? a.ExpiresAt : (DateTimeOffset?)null,
        claimedAt = a.ClaimedAt,
        completedAt = a.CompletedAt,
        exitCode = a.ExitCode,
        output = a.Output,
        next = a.IsPending ? $"Call action_status with id {a.Id} again in a little while." : null,
    };
}
