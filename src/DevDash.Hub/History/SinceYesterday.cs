using DevDash.Core.Safety;

namespace DevDash.Hub.History;

public enum ChangeKind
{
    /// <summary>Became at risk: holds work that exists nowhere else.</summary>
    BecameAtRisk,

    /// <summary>New commits on no remote.</summary>
    NewUnpushed,

    /// <summary>Was at risk, is not now.</summary>
    GotSafe,

    /// <summary>A branch that had commits on no remote has none now.</summary>
    BranchPushed,

    /// <summary>Grew or shrank by a lot.</summary>
    SizeChanged,
    Added,
    Removed,
}

/// <summary>One line of "what changed since yesterday".</summary>
public sealed record HistoryChange(ChangeKind Kind, CloneFacts Tree, string Text)
{
    /// <summary>The size change in bytes, for <see cref="ChangeKind.SizeChanged"/>.</summary>
    public long Bytes { get; init; }

    /// <summary>The branch, for <see cref="ChangeKind.BranchPushed"/>.</summary>
    public string? Branch { get; init; }
}

/// <summary>
/// Compares each machine's working trees now with the same machine's last day of history before today.
/// Pure. A machine with no earlier day is left out, so the first day of history is not "everything is new".
/// </summary>
public static class SinceYesterday
{
    /// <summary>A change in size this big, and at least <see cref="BigChangeShare"/> of what it was, is worth a line.</summary>
    public const long BigChangeBytes = 100L * 1024 * 1024;

    public const double BigChangeShare = 0.10;

    /// <param name="previous">The baseline: each machine's rows of its last day before today.</param>
    /// <param name="current">Every working tree now, from the latest snapshots.</param>
    /// <param name="machinesWithHistory">Machines that have a baseline day. Others are not compared.</param>
    public static IReadOnlyList<HistoryChange> Compare(IEnumerable<CloneFacts> previous, IEnumerable<CloneFacts> current, IReadOnlySet<string> machinesWithHistory)
    {
        var before = previous.Where(c => machinesWithHistory.Contains(c.MachineId)).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        var now = current.Where(c => machinesWithHistory.Contains(c.MachineId)).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        var changes = new List<HistoryChange>();

        foreach (var (id, clone) in now)
        {
            if (!before.TryGetValue(id, out var old))
            {
                changes.Add(new HistoryChange(ChangeKind.Added, clone, clone.IsAtRisk ? "new clone, at risk" : "new clone"));
                continue;
            }

            if (clone.IsAtRisk && !old.IsAtRisk)
            {
                changes.Add(new HistoryChange(ChangeKind.BecameAtRisk, clone, $"became at risk ({LevelText(clone.Level)})"));
            }
            else if (!clone.IsAtRisk && old.IsAtRisk)
            {
                changes.Add(new HistoryChange(ChangeKind.GotSafe, clone, clone.Verdict is SafetyVerdict.Safe or SafetyVerdict.SafeWithCaveats ? "no longer at risk: safe to remove" : "no longer at risk"));
            }

            if (clone.Unpushed > old.Unpushed)
            {
                var added = clone.Unpushed - old.Unpushed;
                changes.Add(new HistoryChange(ChangeKind.NewUnpushed, clone, $"{Fmt.Count(added, "new unpushed commit")} ({clone.Unpushed} in all)"));
            }

            var nowUnpushed = clone.UnpushedBranches.ToHashSet(StringComparer.Ordinal);
            foreach (var branch in old.UnpushedBranches.Where(b => !nowUnpushed.Contains(b)))
            {
                var deleted = clone.AllBranches is { } all && !all.Contains(branch, StringComparer.Ordinal);
                changes.Add(new HistoryChange(ChangeKind.BranchPushed, clone, deleted ? $"{branch} deleted (its unpushed commits are gone)" : $"{branch} pushed") { Branch = branch });
            }

            if (clone.SizeBytes is { } size && old.SizeBytes is { } was && IsBig(size - was, was))
            {
                var delta = size - was;
                changes.Add(new HistoryChange(ChangeKind.SizeChanged, clone, $"{(delta > 0 ? "grew" : "shrank")} by {Fmt.Size(Math.Abs(delta))} to {Fmt.Size(size)}") { Bytes = delta });
            }
        }

        foreach (var (id, old) in before)
        {
            if (!now.ContainsKey(id))
            {
                changes.Add(new HistoryChange(ChangeKind.Removed, old, old.IsAtRisk ? "gone, and it was at risk" : "gone"));
            }
        }

        return changes
            .OrderBy(c => c.Kind)
            .ThenByDescending(c => c.Kind == ChangeKind.SizeChanged ? Math.Abs(c.Bytes) : 0)
            .ThenBy(c => c.Tree.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Tree.Hostname, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Tree.Path, StringComparer.Ordinal)
            .ToList();
    }

    public static bool IsBig(long delta, long was) => Math.Abs(delta) >= BigChangeBytes && (was <= 0 || Math.Abs(delta) >= was * BigChangeShare);

    public static string LevelText(RiskLevel level) => level switch
    {
        RiskLevel.High => "commits on no remote",
        RiskLevel.Medium => "uncommitted changes, stashes or tags",
        _ => "untracked files",
    };

    public static string Label(ChangeKind kind) => kind switch
    {
        ChangeKind.BecameAtRisk => "Became at risk",
        ChangeKind.NewUnpushed => "New unpushed work",
        ChangeKind.GotSafe => "No longer at risk",
        ChangeKind.BranchPushed => "Branches pushed",
        ChangeKind.SizeChanged => "Big size changes",
        ChangeKind.Added => "New clones",
        _ => "Removed clones",
    };

    public static string Class(ChangeKind kind) => kind switch
    {
        ChangeKind.BecameAtRisk or ChangeKind.NewUnpushed => "danger",
        ChangeKind.GotSafe or ChangeKind.BranchPushed => "ok",
        ChangeKind.SizeChanged => "warn",
        _ => "info",
    };
}
