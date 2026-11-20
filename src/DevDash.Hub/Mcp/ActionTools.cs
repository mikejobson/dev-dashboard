using System.ComponentModel;
using DevDash.Contracts;
using DevDash.Core;
using DevDash.Hub.Services;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DevDash.Hub.Mcp;

/// <summary>
/// The tools that ask an agent to do something. Only for tokens with the <c>read+actions</c> scope
/// (each tool carries the policy, so other tokens neither see nor call them). Every one goes through
/// <see cref="ActionQueue.QueueAsync"/>, the same call the dashboard's buttons make, with the same checks:
/// hub switch, the machine's own opt-in and version, online, the path still reported, the typed action's
/// shape. The agent then validates again before running anything.
/// <para>
/// Deliberately absent: removing a project, dropping a stash, deleting branches, removing a worktree,
/// updating an agent. Those stay behind the dashboard's confirmation dialogs (§19).
/// </para>
/// </summary>
[McpServerToolType]
public sealed class ActionTools(SnapshotStore snapshots, RepoCatalog catalog, ActionQueue queue, IHttpContextAccessor http)
{
    private const string Queued = "Queued for the machine's agent, which picks it up within about 30 s if it is online, re-validates it and runs it. " +
                                  "Check the outcome with action_status.";

    [McpServerTool(Name = "refresh_project", Title = "Refresh project", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Authorize(Policy = McpSetup.ActionsPolicy)]
    [Description("Asks the agents to rescan a project now instead of at their next scan, so the other tools show fresh data. Read-only on the " +
                 "machines (the scan only fetches where that machine has auto-fetch on). Without 'machine', refreshes every clone. " +
                 "Returns one action per machine; wait for them with action_status, then call get_project again.")]
    public async Task<string> RefreshProject(
        [Description("Project name, owner/name, key, or the path of a clone.")] string project,
        [Description("Only the clone on this machine (hostname or machine id).")] string? machine = null,
        CancellationToken ct = default)
    {
        var row = McpLookup.Project(await catalog.GetAsync(ct), project);
        var clones = row.Clones.Where(c => McpLookup.MachineMatches(c.Machine, machine)).ToList();
        if (clones.Count == 0)
        {
            throw new McpException($"{row.Name} has no clone on '{machine}'.");
        }

        var queued = new List<object>();
        var refused = new List<string>();
        foreach (var target in RefreshTargets.For(clones))
        {
            var outcome = await queue.QueueAsync(target.Machine.MachineId, AgentAction.Refresh(target.Path), QueuedBy, ct: ct);
            if (outcome.Ok)
            {
                queued.Add(DashboardTools.Describe(outcome.Action!));
            }
            else
            {
                refused.Add($"{target.Machine.Machine.Hostname}: {outcome.Error}");
            }
        }

        return queued.Count == 0
            ? throw new McpException("Nothing was queued. " + string.Join(" ", refused))
            : McpOutput.Json(new { queued, refused = refused.Count == 0 ? null : refused, note = Queued });
    }

    [McpServerTool(Name = "fetch", Title = "Fetch", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Authorize(Policy = McpSetup.ActionsPolicy)]
    [Description("Runs 'git fetch --prune' in one clone on one machine: updates remote-tracking refs only, never the working tree or local branches. " +
                 "Use it before judging ahead/behind, which is only as fresh as the last fetch. Needs a machine whose agent has actions enabled.")]
    public async Task<string> Fetch(
        [Description("Project name, owner/name, key, or the path of the clone.")] string project,
        [Description("The machine (hostname or machine id). Needed when the project is on more than one.")] string? machine = null,
        CancellationToken ct = default)
    {
        var clone = await CloneAsync(project, machine, ct);
        return await QueueAsync(clone.Machine, AgentAction.Fetch(clone.Entry.Path), ct);
    }

    [McpServerTool(Name = "push_branch", Title = "Push branch", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Authorize(Policy = McpSetup.ActionsPolicy)]
    [Description("Pushes one local branch to the remote branch of the same name, never forced. Only offered when the branch tracks a remote branch " +
                 "of the same name and is ahead of it and not behind (see the 'sync' list of get_project or compare_clones). The agent fetches " +
                 "first and refuses if the branch turns out to be behind.")]
    public async Task<string> PushBranch(
        [Description("Project name, owner/name, key, or the path of the clone.")] string project,
        [Description("The local branch to push.")] string branch,
        [Description("The machine (hostname or machine id). Needed when the project is on more than one.")] string? machine = null,
        CancellationToken ct = default)
    {
        var offer = await OfferAsync(project, machine, branch, SyncKind.Push, ct);
        return await QueueAsync(offer.Machine, offer.Action, ct);
    }

    [McpServerTool(Name = "pull_fast_forward", Title = "Fast-forward branch", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Authorize(Policy = McpSetup.ActionsPolicy)]
    [Description("Brings one local branch up to its remote branch of the same name, as a fast-forward only (no merge commit, no rebase). Only " +
                 "offered when the branch is behind and not ahead, and refused while the working tree that has it checked out has uncommitted " +
                 "changes (see the 'sync' list of get_project or compare_clones).")]
    public async Task<string> PullFastForward(
        [Description("Project name, owner/name, key, or the path of the clone.")] string project,
        [Description("The local branch to fast-forward.")] string branch,
        [Description("The machine (hostname or machine id). Needed when the project is on more than one.")] string? machine = null,
        CancellationToken ct = default)
    {
        var offer = await OfferAsync(project, machine, branch, SyncKind.Pull, ct);
        return offer.Blocked is { } blocked
            ? throw new McpException(blocked)
            : await QueueAsync(offer.Machine, offer.Action, ct);
    }

    [McpServerTool(Name = "clean_build_dirs", Title = "Delete build output", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Authorize(Policy = McpSetup.ActionsPolicy)]
    [Description("Deletes build output (node_modules, bin/obj, target, .venv, …) inside one clone to free space. A real delete, not the trash: a build " +
                 "or restore recreates it. Only folders the agent reported as build output that git tracks nothing in (listed under size.buildDirs " +
                 "of get_project); the agent checks each one again. Without 'dirs', deletes all of them.")]
    public async Task<string> CleanBuildDirs(
        [Description("Project name, owner/name, key, or the path of the clone.")] string project,
        [Description("The machine (hostname or machine id). Needed when the project is on more than one.")] string? machine = null,
        [Description("Some of the folders, by absolute path or by path inside the repo (e.g. 'web/node_modules'). Omit for all.")] string[]? dirs = null,
        CancellationToken ct = default)
    {
        var clone = await CloneAsync(project, machine, ct);
        var known = clone.Entry.Size?.ReclaimableDirs ?? [];
        if (known.Count == 0)
        {
            throw new McpException($"{clone.Machine.Machine.Hostname} reports no build output in {clone.Entry.Path}.");
        }

        var chosen = known.ToList();
        if (dirs is { Length: > 0 })
        {
            chosen = [];
            foreach (var wanted in dirs.Select(d => d.Trim().TrimEnd('/', '\\')).Where(d => d.Length > 0))
            {
                var match = known.FirstOrDefault(k => RepoIdentity.SamePath(k.Path, wanted))
                    ?? known.FirstOrDefault(k => RepoIdentity.SamePath(k.Path, clone.Entry.Path.TrimEnd('/', '\\') + "/" + wanted.Replace('\\', '/')))
                    ?? known.FirstOrDefault(k => RepoIdentity.SamePath(k.Path, clone.Entry.Path.TrimEnd('/', '\\') + "\\" + wanted.Replace('/', '\\')))
                    ?? throw new McpException($"'{wanted}' is not build output that {clone.Machine.Machine.Hostname} reported. It reported: {string.Join(", ", known.Select(k => k.Path))}.");
                if (!chosen.Contains(match))
                {
                    chosen.Add(match);
                }
            }
        }

        return await QueueAsync(clone.Machine, AgentAction.CleanBuildDirs(clone.Entry.Path, chosen.Take(AgentActions.MaxBuildDirs).Select(d => d.Path)), ct);
    }

    private string QueuedBy => McpCaller.QueuedBy(http.HttpContext?.User);

    private async Task<CloneView> CloneAsync(string project, string? machine, CancellationToken ct) =>
        McpLookup.Clone(snapshots, McpLookup.Project(await catalog.GetAsync(ct), project), project, machine);

    /// <summary>The push or fast-forward the dashboard itself would offer for that branch, or an error saying why there is none.</summary>
    private async Task<SyncOffer> OfferAsync(string project, string? machine, string branch, SyncKind kind, CancellationToken ct)
    {
        var row = McpLookup.Project(await catalog.GetAsync(ct), project);
        var clone = McpLookup.Clone(snapshots, row, project, machine);
        var offer = SyncOffers.For(row.Clones).FirstOrDefault(o => o.Machine.MachineId == clone.Machine.MachineId && o.Kind == kind && o.Branch == branch.Trim());
        if (offer is not null)
        {
            return offer;
        }

        var host = clone.Machine.Machine.Hostname;
        var info = clone.Git!.Branches.FirstOrDefault(b => b.Name == branch.Trim());
        var why = info switch
        {
            null => $"{host} has no local branch '{branch}' in {clone.Entry.Path}.",
            { Upstream: null } => $"'{branch}' on {host} tracks no remote branch, so there is nothing to compare it with.",
            { UpstreamGone: true } => $"The remote branch '{branch}' on {host} tracks is gone.",
            { Ahead: > 0, Behind: > 0 } => $"'{branch}' on {host} has diverged ({info.Ahead} ahead, {info.Behind} behind {info.Upstream}). That needs a person.",
            _ when kind == SyncKind.Push => $"'{branch}' on {host} has nothing to push (ahead {info.Ahead}, behind {info.Behind} of {info.Upstream}, as of its last fetch).",
            _ => $"'{branch}' on {host} is not behind {info.Upstream} (as of its last fetch; fetch first to be sure).",
        };
        throw new McpException(why + " Only a branch that tracks the same-named remote branch and is strictly ahead (push) or behind (pull) is offered.");
    }

    private async Task<string> QueueAsync(MachineView machine, AgentAction action, CancellationToken ct)
    {
        var outcome = await queue.QueueAsync(machine.MachineId, action, QueuedBy, ct: ct);
        return outcome.Ok
            ? McpOutput.Json(new { queued = DashboardTools.Describe(outcome.Action!), note = Queued })
            : throw new McpException(outcome.Error!);
    }
}
