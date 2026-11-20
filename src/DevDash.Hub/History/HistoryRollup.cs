using DevDash.Contracts;
using DevDash.Core.Safety;

namespace DevDash.Hub.History;

/// <summary>What the history keeps about one working tree at one moment. Built from a snapshot, or read back from a day's row.</summary>
public sealed record CloneFacts
{
    public required string MachineId { get; init; }
    public string Hostname { get; init; } = "";
    public required string Path { get; init; }
    public string RepoKey { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Branch { get; init; }
    public bool IsLinkedWorktree { get; init; }
    public long? SizeBytes { get; init; }
    public long? BuildBytes { get; init; }
    public int Dirty { get; init; }
    public int Unpushed { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public RiskLevel Level { get; init; }
    public SafetyVerdict Verdict { get; init; } = SafetyVerdict.Unclear;

    /// <summary>What removing it would free, when it is safe to remove and measured. Zero otherwise.</summary>
    public long SafeBytes { get; init; }
    public IReadOnlyList<string> UnpushedBranches { get; init; } = [];

    /// <summary>Every local branch, from a live snapshot only (a day's row does not keep them): tells "pushed" from "deleted".</summary>
    public IReadOnlyList<string>? AllBranches { get; init; }

    public bool IsAtRisk => Verdict == SafetyVerdict.AtRisk;
    public (string MachineId, string Path) Id => (MachineId, Path);
}

/// <summary>A machine's totals at one moment.</summary>
public sealed record MachineTotals(int RepoCount, int DirtyCount, int UnpushedCommits, int AtRiskHigh, int AtRiskMedium, int AtRiskLow, long RepoBytes, long BuildBytes, long SafeToRemoveBytes)
{
    public int AtRisk => AtRiskHigh + AtRiskMedium + AtRiskLow;
}

/// <summary>
/// The pure half of the history: what a snapshot comes to, how it folds into the day's row, and how a
/// risk episode moves on. No database here, so all of it is tested directly.
/// </summary>
public static class HistoryRollup
{
    /// <summary>The most unpushed branch names a day's row keeps for one working tree.</summary>
    public const int MaxBranchNames = 20;

    /// <summary>The hub's calendar day for a moment. Days follow the hub's own time zone (<c>TZ</c> in a container), like the activity heatmap.</summary>
    public static DateOnly DayOf(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    /// <summary>Every git working tree in a snapshot, with the facts the history keeps.</summary>
    public static IReadOnlyList<CloneFacts> Clones(MachineSnapshot snapshot, DateTimeOffset now) =>
        RepoSafety.AssessAll(snapshot, now)
            .GroupBy(c => c.Entry.Path, StringComparer.Ordinal)
            .Select(g => Facts(snapshot.Machine, g.First()))
            .ToList();

    private static CloneFacts Facts(MachineInfo machine, CloneSafety clone)
    {
        var git = clone.Entry.Git!;
        var safety = clone.Safety;

        // Branches belong to the repository: a linked worktree whose main working tree is assessed too
        // would count them twice.
        var branches = safety.IncludesRepository ? git.Branches : [];
        var counted = branches.Any(b => b.UnpushedCount is not null || b.OnRemote is not null);
        var unpushed = counted
            ? branches.Sum(b => b.UnpushedCount ?? (b.OnRemote == false ? Math.Max(1, b.Ahead) : 0))
            : branches.Sum(b => b.Ahead);
        var unpushedBranches = branches
            .Where(b => counted ? b.UnpushedCount > 0 || b.OnRemote == false : b.Ahead > 0)
            .Select(b => b.Name)
            .Order(StringComparer.Ordinal)
            .Take(MaxBranchNames)
            .ToList();

        return new CloneFacts
        {
            MachineId = machine.MachineId,
            Hostname = machine.Hostname,
            Path = clone.Entry.Path,
            RepoKey = clone.RepoKey,
            Name = (clone.Main ?? clone.Entry).Name,
            Branch = git.Detached ? null : git.Branch,
            IsLinkedWorktree = git.IsLinkedWorktree,
            SizeBytes = clone.Entry.Size?.TotalBytes,
            BuildBytes = clone.Entry.Size?.ReclaimableBytes,
            Dirty = git.DirtyCount,
            Unpushed = unpushed,
            Ahead = git.Ahead,
            Behind = git.Behind,
            Level = safety.IsAtRisk ? safety.Level : RiskLevel.None,
            Verdict = safety.Verdict,
            SafeBytes = safety.IsRemovable ? safety.FreedBytes ?? 0 : 0,
            UnpushedBranches = unpushedBranches,
            AllBranches = safety.IncludesRepository ? git.Branches.Select(b => b.Name).ToList() : null,
        };
    }

    public static MachineTotals Totals(IReadOnlyCollection<CloneFacts> clones) => new(
        clones.Count(c => !c.IsLinkedWorktree),
        clones.Count(c => c.Dirty > 0),
        clones.Sum(c => c.Unpushed),
        clones.Count(c => c.IsAtRisk && c.Level == RiskLevel.High),
        clones.Count(c => c.IsAtRisk && c.Level == RiskLevel.Medium),
        // An at-risk tree always has a level; Low also covers one with no level recorded.
        clones.Count(c => c.IsAtRisk && c.Level is RiskLevel.Low or RiskLevel.None),
        clones.Sum(c => c.SizeBytes ?? 0),
        clones.Sum(c => c.BuildBytes ?? 0),
        clones.Sum(c => c.SafeBytes));

    /// <summary>Folds a snapshot's totals into the day's row: the latest value wins, the peaks keep the highest.</summary>
    public static void Apply(MachineDayEntity row, string hostname, MachineTotals totals, DateTimeOffset now)
    {
        var first = row.Samples == 0;
        row.Hostname = hostname;
        row.RepoCount = totals.RepoCount;
        row.DirtyCount = totals.DirtyCount;
        row.UnpushedCommits = totals.UnpushedCommits;
        row.MaxUnpushedCommits = first ? totals.UnpushedCommits : Math.Max(row.MaxUnpushedCommits, totals.UnpushedCommits);
        row.AtRiskHigh = totals.AtRiskHigh;
        row.AtRiskMedium = totals.AtRiskMedium;
        row.AtRiskLow = totals.AtRiskLow;
        row.MaxAtRisk = first ? totals.AtRisk : Math.Max(row.MaxAtRisk, totals.AtRisk);
        row.RepoBytes = totals.RepoBytes;
        row.BuildBytes = totals.BuildBytes;
        row.SafeToRemoveBytes = totals.SafeToRemoveBytes;
        row.Samples++;
        row.UpdatedAt = now;
    }

    /// <summary>Folds one working tree's facts into its row for the day.</summary>
    /// <param name="isNew">True for a row created for this snapshot: its peaks start from this value.</param>
    public static void Apply(CloneDayEntity row, CloneFacts facts, bool isNew)
    {
        row.RepoKey = facts.RepoKey;
        row.Name = facts.Name;
        row.Branch = facts.Branch;
        row.IsLinkedWorktree = facts.IsLinkedWorktree;
        // A scan that did not measure sizes this time keeps the day's last known size.
        row.SizeBytes = facts.SizeBytes ?? row.SizeBytes;
        row.BuildBytes = facts.BuildBytes ?? row.BuildBytes;
        row.Dirty = facts.Dirty;
        row.Unpushed = facts.Unpushed;
        row.MaxUnpushed = isNew ? facts.Unpushed : Math.Max(row.MaxUnpushed, facts.Unpushed);
        row.Ahead = facts.Ahead;
        row.Behind = facts.Behind;
        row.RiskLevel = (int)facts.Level;
        row.MaxRiskLevel = isNew ? (int)facts.Level : Math.Max(row.MaxRiskLevel, (int)facts.Level);
        row.Verdict = facts.Verdict.ToString();
        row.UnpushedBranches = string.Join('\n', facts.UnpushedBranches);
    }

    /// <summary>A day's row read back as facts, to compare with now.</summary>
    public static CloneFacts FromRow(CloneDayEntity row, string hostname = "") => new()
    {
        MachineId = row.MachineId,
        Hostname = hostname,
        Path = row.Path,
        RepoKey = row.RepoKey,
        Name = row.Name,
        Branch = row.Branch,
        IsLinkedWorktree = row.IsLinkedWorktree,
        SizeBytes = row.SizeBytes,
        BuildBytes = row.BuildBytes,
        Dirty = row.Dirty,
        Unpushed = row.Unpushed,
        Ahead = row.Ahead,
        Behind = row.Behind,
        Level = Enum.IsDefined((RiskLevel)row.RiskLevel) ? (RiskLevel)row.RiskLevel : RiskLevel.None,
        Verdict = Enum.TryParse<SafetyVerdict>(row.Verdict, out var verdict) ? verdict : SafetyVerdict.Unclear,
        UnpushedBranches = row.UnpushedBranches.Length == 0 ? [] : row.UnpushedBranches.Split('\n'),
    };

    /// <summary>
    /// Moves a working tree's risk episode on by one snapshot. Returns the episode to keep, or null when
    /// it is not at risk (the row goes, and the next time it is at risk starts a new episode). Each level's
    /// "since" starts the first time the tree is at that level or worse, and resets when it drops below.
    /// </summary>
    public static RiskEpisodeEntity? Advance(RiskEpisodeEntity? episode, string machineId, string path, bool atRisk, RiskLevel level, DateTimeOffset now)
    {
        if (!atRisk)
        {
            return null;
        }

        episode ??= new RiskEpisodeEntity { MachineId = machineId, Path = path, Since = now };
        episode.MediumSince = level >= RiskLevel.Medium ? episode.MediumSince ?? now : null;
        episode.HighSince = level >= RiskLevel.High ? episode.HighSince ?? now : null;
        episode.Level = (int)level;
        episode.LastSeenAt = now;
        return episode;
    }
}
