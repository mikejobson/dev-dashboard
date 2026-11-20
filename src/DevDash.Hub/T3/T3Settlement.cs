using System.Text.RegularExpressions;
using DevDash.Contracts;
using DevDash.Hub.Data;
using Microsoft.EntityFrameworkCore;

namespace DevDash.Hub.T3;

/// <summary>
/// What a finished StartT3Thread action means for the thread records: the thread's id and link when it
/// started, the reason when it did not. Called by <see cref="Services.ActionQueue"/> with its own database
/// context, as the removal tombstones are.
/// </summary>
public static partial class T3Settlement
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_:.\-]{0,127}$")]
    private static partial Regex ThreadIdShape();

    /// <summary>The agent's word on the thread, checked before it is stored or shown: an id of a plain shape, and an https link.</summary>
    public static (string? Id, string? Link) Clean(T3StartedThread? thread)
    {
        if (thread is null || !ThreadIdShape().IsMatch(thread.ThreadId ?? ""))
        {
            return (null, null);
        }

        var link = Uri.TryCreate(thread.Link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && thread.Link!.Length <= 512 ? uri.ToString() : null;
        return (thread.ThreadId, link);
    }

    public static async Task SettleAsync(DevDashDbContext db, int actionId, bool succeeded, T3StartedThread? thread, string output, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.T3Threads.Where(t => t.ActionId == actionId).ToListAsync(ct);
        var issues = await db.IssueThreads.Where(t => t.ActionId == actionId).ToListAsync(ct);
        if (rows.Count == 0 && issues.Count == 0)
        {
            return;
        }

        var (id, link) = succeeded ? Clean(thread) : (null, null);
        var error = succeeded ? null : Short(output);
        foreach (var row in rows)
        {
            row.Status = succeeded ? T3StartStatus.Started : T3StartStatus.Failed;
            row.ThreadId = id;
            row.Link = link;
            row.Error = error;
            row.CompletedAt = now;
        }

        foreach (var issue in issues)
        {
            issue.Status = succeeded ? IssueThreadStatus.Started : IssueThreadStatus.Failed;
            issue.ThreadId = id;
            issue.Link = link;
            issue.Reason = error;
            issue.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Actions that expired or never reported: their threads did not start, as far as anyone knows.</summary>
    public static async Task FailAsync(DevDashDbContext db, IReadOnlyCollection<int> actionIds, string reason, DateTimeOffset now, CancellationToken ct)
    {
        await db.T3Threads.Where(t => actionIds.Contains(t.ActionId) && t.Status == T3StartStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, T3StartStatus.Failed).SetProperty(t => t.Error, reason).SetProperty(t => t.CompletedAt, now), ct);
        await db.IssueThreads.Where(t => t.ActionId != null && actionIds.Contains(t.ActionId.Value) && t.Status == IssueThreadStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, IssueThreadStatus.Failed).SetProperty(t => t.Reason, reason).SetProperty(t => t.UpdatedAt, now), ct);
    }

    private static string Short(string? text)
    {
        var line = (text ?? "").Trim();
        return line.Length <= 500 ? line : line[..499] + "…";
    }
}
