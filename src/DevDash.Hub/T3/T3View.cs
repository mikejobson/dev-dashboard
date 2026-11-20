using DevDash.Contracts;
using DevDash.Core;
using DevDash.Hub.Data;
using DevDash.Hub.GitHub;
using DevDash.Hub.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DevDash.Hub.T3;

/// <summary>A thread as a page shows it: where it was reported, and whether the dashboard started it.</summary>
public sealed record T3ThreadRow(T3Thread Thread, MachineView Machine, DevEntry Entry, T3ThreadEntity? Started)
{
    public T3ThreadState State => Thread.State;
}

/// <summary>An open issue with what the dashboard knows of its threads.</summary>
public sealed record IssueRow(TrackedIssue Issue, RepoIssues Repo, IReadOnlyList<IssueThreadEntity> Threads, T3Thread? Live)
{
    /// <summary>The newest start, or the newest decision when nothing was started.</summary>
    public IssueThreadEntity? Latest => Threads.Where(t => t.ActionId is not null).OrderByDescending(t => t.Id).FirstOrDefault() ?? Threads.OrderByDescending(t => t.Id).FirstOrDefault();

    public bool Started => Threads.Any(t => t.ActionId is not null);
}

/// <summary>Joins the threads agents report with the starts the dashboard recorded. Pure.</summary>
public static class T3View
{
    /// <summary>Every thread reported in the snapshots, each once (the newest report wins), newest first.</summary>
    public static IReadOnlyList<T3ThreadRow> All(IEnumerable<MachineView> machines, IReadOnlyList<T3ThreadEntity> started) =>
        Rows(machines.SelectMany(m => RepoIdentity.Flatten(m.Snapshot.Entries).Select(e => (m, e))), started);

    /// <summary>The threads of one repo's clones.</summary>
    public static IReadOnlyList<T3ThreadRow> ForRepo(RepoRow row, IReadOnlyList<T3ThreadEntity> started) =>
        Rows(row.Clones.Select(c => (c.Machine, c.Entry)), started);

    private static IReadOnlyList<T3ThreadRow> Rows(IEnumerable<(MachineView Machine, DevEntry Entry)> entries, IReadOnlyList<T3ThreadEntity> started)
    {
        var byThread = started.Where(s => s.ThreadId is not null).GroupBy(s => s.ThreadId!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return entries
            .Where(x => x.Entry.T3 is not null)
            .SelectMany(x => x.Entry.T3!.Threads.Select(t => new T3ThreadRow(t, x.Machine, x.Entry, byThread.GetValueOrDefault(t.Id))))
            .GroupBy(r => r.Thread.Id, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(r => r.Machine.LastSeenAt).First())
            .OrderBy(r => r.State switch { T3ThreadState.NeedsInput => 0, T3ThreadState.Running => 1, _ => 2 })
            .ThenByDescending(r => r.Thread.UpdatedAt ?? r.Thread.CreatedAt)
            .ToList();
    }

    /// <summary>A reported thread by id, wherever it is.</summary>
    public static T3Thread? Find(IEnumerable<MachineView> machines, string? threadId) =>
        threadId is null
            ? null
            : machines.SelectMany(m => RepoIdentity.Flatten(m.Snapshot.Entries))
                .SelectMany(e => e.T3?.Threads ?? [])
                .FirstOrDefault(t => t.Id == threadId);

    /// <summary>Open issues, newest activity first, with their thread rows and the live thread of the newest start.</summary>
    public static IReadOnlyList<IssueRow> Issues(IssueSnapshot issues, IReadOnlyList<IssueThreadEntity> threads, IEnumerable<MachineView> machines)
    {
        var list = machines.ToList();
        var byIssue = threads.GroupBy(t => (t.Repo, t.Number)).ToDictionary(g => g.Key, g => (IReadOnlyList<IssueThreadEntity>)g.ToList());
        return issues.Repos.Values
            .SelectMany(r => r.Issues.Select(i =>
            {
                var rows = byIssue.GetValueOrDefault((i.RepoKey, i.Number)) ?? [];
                var newest = rows.Where(t => t.ThreadId is not null).OrderByDescending(t => t.Id).FirstOrDefault();
                return new IssueRow(i, r, rows, Find(list, newest?.ThreadId));
            }))
            .OrderByDescending(r => r.Issue.UpdatedAt)
            .ToList();
    }

    /// <summary>What an issue's thread state reads as: the live thread when reported, else the start's own status.</summary>
    public static (string Text, string Class, string? Title) Status(IssueRow row)
    {
        if (row.Live is { } live)
        {
            return (T3Threads.Label(live.State), T3Threads.ChipClass(live.State), live.Title);
        }

        return row.Latest switch
        {
            null => ("", "", null),
            { Status: IssueThreadStatus.Queued } => ("starting", "info", "Queued for the agent"),
            { Status: IssueThreadStatus.Started } l => ("started", "ok", l.ThreadId is null ? "T3 Code did not say the thread's id" : "Not reported by the agent yet"),
            { Status: IssueThreadStatus.Failed } l => ("failed", "danger", l.Reason),
            { Status: IssueThreadStatus.Pending } l => ("pending", "warn", l.Reason),
            { Status: IssueThreadStatus.NeedsManualStart } l => ("needs a manual start", "warn", l.Reason),
            _ => ("", "", null),
        };
    }
}

public static class T3HubServices
{
    /// <summary>T3 Code threads and GitHub issues: starts, issue polling, the label trigger and its settings.</summary>
    public static IServiceCollection AddT3Integration(this IServiceCollection services)
    {
        services.AddHttpClient<GitHubIssuesClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IssueStore>();
        services.AddSingleton<IssueSettingsStore>();
        services.AddSingleton<T3Starts>();
        services.AddSingleton<IssueWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<IssueWorker>());
        return services;
    }
}
