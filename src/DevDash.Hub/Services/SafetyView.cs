using DevDash.Contracts;
using DevDash.Core;
using DevDash.Core.Safety;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.Services;

/// <summary>One working tree on one machine with its safety assessment, ready for a table row.</summary>
public sealed record SafetyRow(MachineView Machine, CloneSafety Assessed)
{
    public DevEntry Entry => Assessed.Entry;
    public SafetyAssessment Safety => Assessed.Safety;
    public string RepoKey => Assessed.RepoKey;

    /// <summary>For a linked worktree: whether it looks finished with. Null for anything else.</summary>
    public WorktreeStaleness? Stale { get; init; }

    /// <summary>The repo's name as the row shows it: the main working tree's folder name.</summary>
    public string RepoName => (Assessed.Main ?? Entry).Name;

    /// <summary>What deleting the folder frees. Zero when the size has not been measured.</summary>
    public long FreedBytes => Safety.FreedBytes ?? 0;
}

public enum SafetySort
{
    /// <summary>The work that has been sitting unpushed longest comes first.</summary>
    Age,

    /// <summary>Commits on no remote before uncommitted changes before untracked files; oldest first within each.</summary>
    Severity,
}

public enum SafetyGrouping
{
    Machine,
    Repo,
}

/// <summary>Sorting and grouping for the Safety page. Pure, so the order is tested without a browser.</summary>
public static class SafetyView
{
    public static SafetySort ParseSort(string? value) =>
        Enum.TryParse<SafetySort>(value, ignoreCase: true, out var sort) && Enum.IsDefined(sort) ? sort : SafetySort.Age;

    public static SafetyGrouping ParseGrouping(string? value) =>
        Enum.TryParse<SafetyGrouping>(value, ignoreCase: true, out var group) && Enum.IsDefined(group) ? group : SafetyGrouping.Machine;

    /// <summary>The at-risk rows in the order asked for. Work with no known date sorts as the newest.</summary>
    public static List<SafetyRow> AtRisk(IEnumerable<SafetyRow> rows, SafetySort sort)
    {
        var risky = rows.Where(r => r.Safety.IsAtRisk);
        var ordered = sort == SafetySort.Severity
            ? risky.OrderByDescending(r => r.Safety.Level).ThenBy(r => r.Safety.OldestRiskAt ?? DateTimeOffset.MaxValue)
            : risky.OrderBy(r => r.Safety.OldestRiskAt ?? DateTimeOffset.MaxValue).ThenByDescending(r => r.Safety.Level);
        return ordered.ThenBy(r => r.RepoName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Entry.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>The removable rows, the longest untouched first: those are the least likely to be missed.</summary>
    public static List<SafetyRow> Removable(IEnumerable<SafetyRow> rows) => rows
        .Where(r => r.Safety.IsRemovable)
        .OrderBy(r => r.Safety.LastTouchedAt ?? DateTimeOffset.MinValue)
        .ThenBy(r => r.RepoName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(r => r.Entry.Path, StringComparer.Ordinal)
        .ToList();

    public static List<SafetyRow> Unclear(IEnumerable<SafetyRow> rows) => rows
        .Where(r => r.Safety.Verdict == SafetyVerdict.Unclear)
        .OrderBy(r => r.RepoName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(r => r.Machine.Machine.Hostname, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// Groups rows that are already sorted, keeping that order: a group sits where its first (most
    /// urgent) row is, and its rows stay in order.
    /// </summary>
    public static List<(string Key, string Title, List<SafetyRow> Rows)> Group(IReadOnlyList<SafetyRow> sorted, SafetyGrouping grouping)
    {
        var groups = new List<(string Key, string Title, List<SafetyRow> Rows)>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in sorted)
        {
            var (key, title) = grouping == SafetyGrouping.Repo ? (row.RepoKey, row.RepoName) : (row.Machine.MachineId, row.Machine.Machine.Hostname);
            if (!index.TryGetValue(key, out var at))
            {
                index[key] = at = groups.Count;
                groups.Add((key, title, []));
            }

            groups[at].Rows.Add(row);
        }

        return groups;
    }

    public static string VerdictLabel(SafetyVerdict verdict) => verdict switch
    {
        SafetyVerdict.AtRisk => "at risk",
        SafetyVerdict.Safe => "safe to remove",
        SafetyVerdict.SafeWithCaveats => "safe with caveats",
        _ => "not sure",
    };

    public static string VerdictClass(SafetyVerdict verdict) => verdict switch
    {
        SafetyVerdict.AtRisk => "danger",
        SafetyVerdict.Safe => "ok",
        SafetyVerdict.SafeWithCaveats => "warn",
        _ => "muted",
    };

    public static string LevelClass(RiskLevel level) => level switch
    {
        RiskLevel.High => "danger",
        RiskLevel.Medium => "warn",
        RiskLevel.Low => "info",
        _ => "",
    };
}

/// <summary>
/// The safety assessment of every working tree on every machine, from the latest snapshots. All of
/// the judgement is in <see cref="RepoSafety"/>; this only joins it to the machines.
/// </summary>
public sealed class SafetyService(SnapshotStore snapshots, IOptionsMonitor<HubOptions> options, TimeProvider time)
{
    public IReadOnlyList<SafetyRow> Get()
    {
        var now = time.GetUtcNow();
        var staleDays = options.CurrentValue.Cleanup.WorktreeStaleDays;
        return snapshots.Machines
            .SelectMany(machine => RepoSafety.AssessAll(machine.Snapshot, now)
                .Select(clone => new SafetyRow(machine, clone) { Stale = WorktreeState.Of(clone.Entry, now, staleDays) }))
            .ToList();
    }

    /// <summary>The assessment of each working tree, by machine and path: for a badge beside a clone.</summary>
    public IReadOnlyDictionary<(string MachineId, string Path), SafetyAssessment> ByClone() =>
        Get().GroupBy(r => (r.Machine.MachineId, r.Entry.Path)).ToDictionary(g => g.Key, g => g.First().Safety);
}
