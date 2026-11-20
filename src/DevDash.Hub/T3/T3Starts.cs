using DevDash.Contracts;
using DevDash.Core;
using DevDash.Hub.Data;
using DevDash.Hub.Services;
using Microsoft.EntityFrameworkCore;

namespace DevDash.Hub.T3;

/// <summary>A clone a thread could be started in, and whether its machine can take one now.</summary>
public sealed record T3Target(CloneView Target, ActionAvailability Availability)
{
    public MachineView Machine => Target.Machine;
    public string Label => $"{Target.Machine.Machine.Hostname} · {Target.Entry.Path}";
}

/// <summary>An issue a thread is for.</summary>
public sealed record IssueRef(string RepoKey, int Number);

/// <summary>Everything one "start a thread" needs. <see cref="Again"/> is the Issues page's "Start again".</summary>
public sealed record T3StartRequest
{
    public required string MachineId { get; init; }
    public required string RepoPath { get; init; }
    public required string RepoKey { get; init; }
    public required string Prompt { get; init; }
    public required string Title { get; init; }
    public required string WorktreeBase { get; init; }
    public required string NewBranch { get; init; }

    /// <summary><c>repo</c>, <c>issue</c>, <c>ci</c>, <c>deps</c> or <c>toolchain</c>.</summary>
    public string Origin { get; init; } = "repo";
    public IssueRef? Issue { get; init; }
    public bool Automatic { get; init; }
    public bool Again { get; init; }
}

public sealed record T3StartOutcome(T3ThreadEntity? Thread, string? Error)
{
    public bool Ok => Thread is not null;
}

/// <summary>
/// Starting T3 threads from the dashboard, and the record of the ones it started. A start is a
/// <see cref="AgentActionKind.StartT3Thread"/> action through <see cref="ActionQueue"/>, with every check
/// the queue makes (hub switch, the machine's own switch, online, agent version, the repository reported),
/// plus: the clone is a main working tree there, the new branch is not one that clone has, and an issue
/// gets a second thread only when asked for in so many words.
/// </summary>
public sealed class T3Starts(IDbContextFactory<DevDashDbContext> dbFactory, ActionQueue queue, SnapshotStore snapshots, DashboardNotifier notifier, TimeProvider time)
{
    /// <summary>The clones of a repo a thread could start in: main working trees, one per machine and folder, the ones that can take it now first.</summary>
    public IReadOnlyList<T3Target> Targets(RepoRow row) =>
        row.Clones
            .Where(c => c.Entry.Kind == EntryKind.GitRepo && c.Git is { IsLinkedWorktree: false })
            .Select(c => new T3Target(c, queue.Availability(c.Machine, AgentActionKind.StartT3Thread)))
            .OrderByDescending(t => t.Availability.Available)
            .ThenByDescending(t => t.Machine.LastSeenAt)
            .ThenBy(t => t.Target.Entry.Path, StringComparer.Ordinal)
            .ToList();

    /// <summary>Clones that could be opened in the T3 Code app.</summary>
    public IReadOnlyList<T3Target> OpenTargets(RepoRow row) =>
        row.Clones
            .Where(c => c.Entry.Kind == EntryKind.GitRepo)
            .Select(c => new T3Target(c, queue.Availability(c.Machine, AgentActionKind.OpenInT3)))
            .ToList();

    public async Task<T3StartOutcome> StartAsync(T3StartRequest request, string queuedBy, CancellationToken ct = default)
    {
        if (snapshots.Get(request.MachineId) is not { } machine)
        {
            return new T3StartOutcome(null, "That machine is not known to this hub.");
        }

        var clone = RepoIdentity.Flatten(machine.Snapshot.Entries).FirstOrDefault(e => e.Path == request.RepoPath && e.Kind == EntryKind.GitRepo);
        if (clone?.Git is not { IsLinkedWorktree: false } git)
        {
            return new T3StartOutcome(null, $"{machine.Machine.Hostname} does not report a repository (a main working tree) at {request.RepoPath}.");
        }

        if (git.Branches.Any(b => string.Equals(b.Name, request.NewBranch, StringComparison.Ordinal)))
        {
            return new T3StartOutcome(null, $"{request.NewBranch} already exists in {request.RepoPath} on {machine.Machine.Hostname}. Choose another branch name.");
        }

        if (request.Issue is { } issue && !request.Again && await HasThreadAsync(issue, ct))
        {
            return new T3StartOutcome(null, $"A thread was already started for #{issue.Number}. Use Start again to start another one.");
        }

        var action = AgentAction.StartT3Thread(request.RepoPath, request.Prompt, request.Title, request.WorktreeBase, request.NewBranch);
        var queued = await queue.QueueAsync(machine.MachineId, action, queuedBy, ct: ct);
        if (queued.Action is not { } entity)
        {
            return new T3StartOutcome(null, queued.Error);
        }

        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.T3Threads.FirstOrDefaultAsync(t => t.ActionId == entity.Id, ct);
        if (row is null)
        {
            row = new T3ThreadEntity
            {
                ActionId = entity.Id,
                MachineId = machine.MachineId,
                MachineName = machine.Machine.Hostname,
                RepoKey = request.RepoKey,
                RepoPath = request.RepoPath,
                Title = request.Title,
                Branch = request.NewBranch,
                WorktreeBase = request.WorktreeBase,
                Origin = request.Origin,
                IssueNumber = request.Issue?.Number,
                Status = T3StartStatus.Queued,
                QueuedBy = entity.QueuedBy,
                QueuedAt = now,
            };
            db.T3Threads.Add(row);
        }

        if (request.Issue is { } forIssue)
        {
            var repo = forIssue.RepoKey.ToLowerInvariant();
            // A decision the label trigger recorded (pending, needs a manual start) becomes this start.
            var decided = await db.IssueThreads
                .Where(t => t.Repo == repo && t.Number == forIssue.Number && t.ActionId == null)
                .OrderByDescending(t => t.Id)
                .FirstOrDefaultAsync(ct);
            if (!await db.IssueThreads.AnyAsync(t => t.ActionId == entity.Id, ct))
            {
                var link = decided ?? new IssueThreadEntity { Repo = repo, Number = forIssue.Number, CreatedAt = now };
                link.MachineId = machine.MachineId;
                link.ActionId = entity.Id;
                link.Status = IssueThreadStatus.Queued;
                link.Reason = null;
                link.StartedBy = request.Automatic ? IssueThreadEntity.Auto : IssueThreadEntity.Manual;
                link.By = entity.QueuedBy;
                link.UpdatedAt = now;
                if (decided is null)
                {
                    db.IssueThreads.Add(link);
                }
            }
        }

        await db.SaveChangesAsync(ct);
        notifier.Notify(DashboardChange.Actions);
        return new T3StartOutcome(row, null);
    }

    /// <summary>Whether an issue already has a thread start (in any state): the dedupe rule for the label trigger and the Start button.</summary>
    public async Task<bool> HasThreadAsync(IssueRef issue, CancellationToken ct = default)
    {
        var repo = issue.RepoKey.ToLowerInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.IssueThreads.AnyAsync(t => t.Repo == repo && t.Number == issue.Number && t.ActionId != null, ct);
    }

    /// <summary>The threads the dashboard started, newest first.</summary>
    public async Task<IReadOnlyList<T3ThreadEntity>> RecentAsync(int take = 200, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.T3Threads.AsNoTracking().OrderByDescending(t => t.Id).Take(take).ToListAsync(ct);
    }

    /// <summary>Every issue's thread rows, newest first.</summary>
    public async Task<IReadOnlyList<IssueThreadEntity>> IssueThreadsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.IssueThreads.AsNoTracking().OrderByDescending(t => t.Id).ToListAsync(ct);
    }

    /// <summary>
    /// Records what the label trigger decided for an issue it did not start (pending, or needs a manual
    /// start): one row per issue, updated in place, never touching a row that is a thread start.
    /// </summary>
    public async Task RecordDecisionAsync(IssueRef issue, IssueThreadStatus status, string reason, string? machineId, CancellationToken ct = default)
    {
        var repo = issue.RepoKey.ToLowerInvariant();
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.IssueThreads.AnyAsync(t => t.Repo == repo && t.Number == issue.Number && t.ActionId != null, ct))
        {
            return;
        }

        var row = await db.IssueThreads.Where(t => t.Repo == repo && t.Number == issue.Number && t.ActionId == null).OrderByDescending(t => t.Id).FirstOrDefaultAsync(ct);
        if (row is not null && row.Status == status && row.Reason == reason && row.MachineId == machineId)
        {
            return;
        }

        if (row is null)
        {
            row = new IssueThreadEntity { Repo = repo, Number = issue.Number, CreatedAt = now, StartedBy = IssueThreadEntity.Auto, By = IssueThreadEntity.Auto };
            db.IssueThreads.Add(row);
        }

        row.Status = status;
        row.Reason = reason;
        row.MachineId = machineId;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        notifier.Notify(DashboardChange.GitHub);
    }

    /// <summary>Drops a decision row that no longer applies (the label was taken off, the trigger switched off).</summary>
    public async Task ClearDecisionAsync(IssueRef issue, CancellationToken ct = default)
    {
        var repo = issue.RepoKey.ToLowerInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.IssueThreads.Where(t => t.Repo == repo && t.Number == issue.Number && t.ActionId == null).ExecuteDeleteAsync(ct) > 0)
        {
            notifier.Notify(DashboardChange.GitHub);
        }
    }

    /// <summary>Notes what the write-back to GitHub did on the issue's newest thread start.</summary>
    public async Task RecordWriteBackAsync(int actionId, string note, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.IssueThreads.Where(t => t.ActionId == actionId).ExecuteUpdateAsync(s => s.SetProperty(t => t.WriteBack, note), ct);
    }
}
