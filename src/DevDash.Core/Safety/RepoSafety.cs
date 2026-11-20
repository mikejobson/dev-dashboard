using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DevDash.Contracts;

namespace DevDash.Core.Safety;

/// <summary>The answer to "what happens if this folder is deleted?", worst first.</summary>
public enum SafetyVerdict
{
    /// <summary>Something in it exists nowhere else: deleting the folder loses work.</summary>
    AtRisk,

    /// <summary>
    /// Nothing is known to be lost, but it cannot be called safe either: a fact is missing (an old
    /// agent, tags never compared with the remote) or something still depends on it (linked worktrees).
    /// </summary>
    Unclear,

    /// <summary>Everything git tracks is on a remote, but there are ignored files that are not build output. Removing it needs those acknowledged.</summary>
    SafeWithCaveats,

    /// <summary>Completely clean: everything in it is on a remote or can be rebuilt.</summary>
    Safe,
}

/// <summary>What a reason does to the verdict.</summary>
public enum SafetyEffect
{
    /// <summary>Only-copy work. Makes the verdict <see cref="SafetyVerdict.AtRisk"/>.</summary>
    Risk,

    /// <summary>Nothing lost, but removing the folder would break something else. Makes it <see cref="SafetyVerdict.Unclear"/>.</summary>
    Blocker,

    /// <summary>A fact the agent did not send. Makes it <see cref="SafetyVerdict.Unclear"/>: never safe without the facts.</summary>
    Unknown,

    /// <summary>Lost on removal, but not work git knows about. Makes it <see cref="SafetyVerdict.SafeWithCaveats"/>.</summary>
    Caveat,
}

/// <summary>How much a risk matters. Used to sort, and for the navigation count (High only).</summary>
public enum RiskLevel
{
    None,

    /// <summary>Untracked files, or a repository with no remote and no commits.</summary>
    Low,

    /// <summary>Uncommitted changes, stashes, local-only tags.</summary>
    Medium,

    /// <summary>Commits that are on no remote.</summary>
    High,
}

public enum SafetyReasonKind
{
    NoRemote,
    UnpushedCommits,
    LocalOnlyBranch,
    DetachedHead,
    UncommittedChanges,
    UntrackedFiles,
    Stashes,
    LocalOnlyTags,
    SubmoduleUnpushed,
    SubmoduleDirty,
    LinkedWorktrees,
    NotARepo,
    OldAgent,
    BranchesUnknown,
    TagsUnknown,
    SubmoduleUnknown,
    IgnoredFiles,
}

/// <summary>One thing that stands between a working tree and "safe to remove".</summary>
public sealed record SafetyReason
{
    public required SafetyReasonKind Kind { get; init; }
    public required SafetyEffect Effect { get; init; }
    public RiskLevel Level { get; init; }

    /// <summary>A short sentence for people: "2 commits on feature/x not on any remote, oldest 4 days".</summary>
    public required string Text { get; init; }

    /// <summary>The branch or submodule the reason is about, when it is about one.</summary>
    public string? Subject { get; init; }

    /// <summary>How many commits, files, stashes or tags, when counted.</summary>
    public int? Count { get; init; }

    /// <summary>How long the work has existed only here: the oldest unpushed commit, the oldest stash, the last edit. Null when unknown.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>File, tag or branch names behind the reason, as far as the agent sent them (capped at <see cref="SafetyFacts.MaxNames"/>).</summary>
    public IReadOnlyList<string> Names { get; init; } = [];
}

/// <summary>The safety of one working tree on one machine.</summary>
public sealed record SafetyAssessment
{
    public required SafetyVerdict Verdict { get; init; }

    /// <summary>Every reason, risks first. Empty only when <see cref="Verdict"/> is <see cref="SafetyVerdict.Safe"/>.</summary>
    public IReadOnlyList<SafetyReason> Reasons { get; init; } = [];

    /// <summary>The worst <see cref="SafetyReason.Level"/> among the risks.</summary>
    public RiskLevel Level { get; init; }

    /// <summary>The oldest moment any at-risk work dates from. Null when nothing is at risk or no date is known.</summary>
    public DateTimeOffset? OldestRiskAt { get; init; }

    /// <summary>The newest commit or file edit.</summary>
    public DateTimeOffset? LastTouchedAt { get; init; }

    /// <summary>The folder's size, when the agent has measured it.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>The part of it that is build output.</summary>
    public long? ReclaimableBytes { get; init; }

    /// <summary>True for a linked worktree: removing it is <c>git worktree remove</c>, and its branches stay in the main repository.</summary>
    public bool IsLinkedWorktree { get; init; }

    /// <summary>False when the repository-wide checks (branches, stashes, tags, remote) were left to the main working tree's own assessment.</summary>
    public bool IncludesRepository { get; init; }

    /// <summary>
    /// Changes whenever the facts behind the verdict change. A later step that acts on an assessment
    /// (acknowledging caveats, removing the folder) can quote it, and whoever carries the step out can
    /// assess again and refuse if it no longer matches.
    /// </summary>
    public required string Fingerprint { get; init; }

    public bool IsAtRisk => Verdict == SafetyVerdict.AtRisk;

    /// <summary>True for <see cref="SafetyVerdict.Safe"/> and <see cref="SafetyVerdict.SafeWithCaveats"/>.</summary>
    public bool IsRemovable => Verdict is SafetyVerdict.Safe or SafetyVerdict.SafeWithCaveats;

    /// <summary>What deleting the whole folder frees. Null when the size is not known yet.</summary>
    public long? FreedBytes => TotalBytes;

    public IEnumerable<SafetyReason> Risks => Reasons.Where(r => r.Effect == SafetyEffect.Risk);

    /// <summary>The reasons that must be acknowledged before a <see cref="SafetyVerdict.SafeWithCaveats"/> removal.</summary>
    public IEnumerable<SafetyReason> Caveats => Reasons.Where(r => r.Effect == SafetyEffect.Caveat);
}

/// <summary>An assessment together with where it is.</summary>
public sealed record CloneSafety(MachineInfo Machine, DevEntry Entry, string RepoKey, SafetyAssessment Safety)
{
    /// <summary>The main working tree's entry when this is a linked worktree and both were scanned. Null otherwise.</summary>
    public DevEntry? Main { get; init; }
}

/// <summary>
/// Decides, from a snapshot alone, whether a working tree holds the only copy of anything. Pure: no
/// git, no disk, so the hub and an agent reach the same answer from the same scan.
/// <para>
/// The rule that matters: a repository is only ever <see cref="SafetyVerdict.Safe"/> when every fact
/// was reported and every one is clean. A missing fact is <see cref="SafetyVerdict.Unclear"/>, never safe.
/// </para>
/// </summary>
public static class RepoSafety
{
    /// <summary>Assesses every git working tree in a snapshot, linked worktrees and worktree-folder children included.</summary>
    public static IReadOnlyList<CloneSafety> AssessAll(MachineSnapshot snapshot, DateTimeOffset now)
    {
        var mains = RepoIdentity.MainWorkingTrees(snapshot.Entries);
        var result = new List<CloneSafety>();
        foreach (var entry in RepoIdentity.Flatten(snapshot.Entries))
        {
            if (entry.Kind != EntryKind.GitRepo || entry.Git is null)
            {
                continue;
            }

            // Branches, stashes and tags belong to the repository: they are reported once, on the main
            // working tree. A linked worktree whose main working tree was not scanned reports them itself.
            var main = RepoIdentity.MainOf(entry, mains);
            var safety = Assess(entry, now, includeRepository: main is null);
            result.Add(new CloneSafety(snapshot.Machine, entry, RepoIdentity.KeyFor(snapshot.Machine.MachineId, main ?? entry), safety) { Main = main });
        }

        return result;
    }

    public static IReadOnlyList<CloneSafety> AssessAll(IEnumerable<MachineSnapshot> snapshots, DateTimeOffset now) =>
        snapshots.SelectMany(s => AssessAll(s, now)).ToList();

    /// <summary>Assesses one entry.</summary>
    /// <param name="includeRepository">
    /// False for a linked worktree whose main working tree is assessed too: only what lives in this
    /// folder counts (uncommitted and untracked files, ignored files, submodules, a detached HEAD).
    /// Its branches survive <c>git worktree remove</c>, and the main working tree reports them.
    /// </param>
    public static SafetyAssessment Assess(DevEntry entry, DateTimeOffset now, bool includeRepository = true)
    {
        var reasons = new List<SafetyReason>();
        if (entry.Kind != EntryKind.GitRepo || entry.Git is not { } git)
        {
            reasons.Add(Reason(SafetyReasonKind.NotARepo, SafetyEffect.Unknown, "Not a git repository, so nothing says whether it is a copy of anything."));
            return Finish(entry, reasons, includeRepository: true);
        }

        var facts = git.Safety;
        WorkingTree(git, facts, now, reasons);
        if (includeRepository)
        {
            Repository(git, facts, now, reasons);
        }

        if (facts is null)
        {
            reasons.Add(Reason(SafetyReasonKind.OldAgent, SafetyEffect.Unknown,
                "The agent on this machine is too old to check tags, ignored files and submodules. Update it."));
        }

        return Finish(entry, reasons, includeRepository);
    }

    /// <summary>What lives in the folder itself.</summary>
    private static void WorkingTree(GitState git, SafetyFacts? facts, DateTimeOffset now, List<SafetyReason> reasons)
    {
        if (git.IsDirty)
        {
            reasons.Add(Reason(SafetyReasonKind.UncommittedChanges, SafetyEffect.Risk,
                Plural(git.DirtyCount, "uncommitted change")
                + (git.Conflicted > 0 ? $" ({git.Conflicted} conflicted)" : "")
                + (git.NewestTrackedChangeAt is { } edited ? $", last edited {Ago(now, edited)}" : ""),
                RiskLevel.Medium, count: git.DirtyCount, since: git.NewestTrackedChangeAt));
        }

        if (git.Untracked > 0)
        {
            var secrets = facts?.SecretFiles ?? [];
            reasons.Add(Reason(SafetyReasonKind.UntrackedFiles, SafetyEffect.Risk,
                Plural(git.Untracked, "untracked file or folder", "untracked files or folders")
                + (secrets.Count > 0 ? $", including {NameList(secrets, facts!.SecretFileCount, 3)}" : ""),
                RiskLevel.Low, count: git.Untracked, since: git.NewestUntrackedAt, names: secrets));
        }

        if (git is { Detached: true, HeadSha: { } head } && git.Branches.All(b => b.Sha != head))
        {
            switch (facts?.HeadOnRemote)
            {
                case false:
                    reasons.Add(Reason(SafetyReasonKind.DetachedHead, SafetyEffect.Risk,
                        $"Detached HEAD at {Short(head)} is on no branch and no remote", RiskLevel.High, since: git.LastCommit?.Date));
                    break;
                case null when facts is not null:
                    reasons.Add(Reason(SafetyReasonKind.BranchesUnknown, SafetyEffect.Unknown,
                        $"Could not tell whether the detached HEAD at {Short(head)} is on a remote"));
                    break;
            }
        }

        if (facts is null)
        {
            return;
        }

        foreach (var sub in facts.Submodules.Where(s => s.Initialized))
        {
            if (!sub.Checked)
            {
                reasons.Add(Reason(SafetyReasonKind.SubmoduleUnknown, SafetyEffect.Unknown, $"Submodule {sub.Path} was not checked", subject: sub.Path));
                continue;
            }

            if (sub.Unpushed)
            {
                reasons.Add(Reason(SafetyReasonKind.SubmoduleUnpushed, SafetyEffect.Risk,
                    $"Submodule {sub.Path} has commits not on any remote", RiskLevel.High, subject: sub.Path));
            }

            if (sub.Dirty || sub.Stashes > 0)
            {
                string[] what = [.. sub.Dirty ? ["uncommitted changes"] : Array.Empty<string>(), .. sub.Stashes > 0 ? [Plural(sub.Stashes, "stash", "stashes")] : Array.Empty<string>()];
                reasons.Add(Reason(SafetyReasonKind.SubmoduleDirty, SafetyEffect.Risk,
                    $"Submodule {sub.Path} has {string.Join(" and ", what)}", RiskLevel.Medium, subject: sub.Path));
            }
        }

        if (facts.IgnoredFileCount > 0)
        {
            reasons.Add(Reason(SafetyReasonKind.IgnoredFiles, SafetyEffect.Caveat,
                Plural(facts.IgnoredFileCount, "ignored file or folder", "ignored files or folders")
                + $" that {(facts.IgnoredFileCount == 1 ? "is" : "are")} not build output: {NameList(facts.IgnoredFiles, facts.IgnoredFileCount, 4)}",
                count: facts.IgnoredFileCount, names: facts.IgnoredFiles));
        }
    }

    /// <summary>What the repository holds whichever working tree is looked at: branches, stashes, tags.</summary>
    private static void Repository(GitState git, SafetyFacts? facts, DateTimeOffset now, List<SafetyReason> reasons)
    {
        if (git.Remotes.Count == 0)
        {
            var hasCommits = git.LastCommit is not null || git.Branches.Count > 0;
            DateTimeOffset?[] dates = [.. git.Branches.Select(b => b.LastCommitDate), git.LastCommit?.Date];
            reasons.Add(Reason(SafetyReasonKind.NoRemote, SafetyEffect.Risk,
                hasCommits
                    ? $"No remote: {Plural(git.Branches.Count, "branch", "branches")}{(facts is { TagCount: > 0 } ? $" and {Plural(facts.TagCount, "tag")}" : "")} exist only here"
                    : "No remote, and no commits yet",
                hasCommits ? RiskLevel.High : RiskLevel.Low, count: git.Branches.Count, since: dates.Min()));
        }
        else
        {
            Branches(git, now, reasons);

            if (facts is { LocalOnlyTagCount: > 0 })
            {
                reasons.Add(Reason(SafetyReasonKind.LocalOnlyTags, SafetyEffect.Risk,
                    Plural(facts.LocalOnlyTagCount, "tag") + $" not on any remote: {NameList(facts.LocalOnlyTags, facts.LocalOnlyTagCount, 4)}",
                    RiskLevel.Medium, count: facts.LocalOnlyTagCount, names: facts.LocalOnlyTags));
            }
            else if (facts is { TagCount: > 0, RemoteTagsCheckedAt: null })
            {
                reasons.Add(Reason(SafetyReasonKind.TagsUnknown, SafetyEffect.Unknown,
                    Plural(facts.TagCount, "tag") + " not compared with the remote yet (that needs a fetch from this machine)", count: facts.TagCount));
            }
        }

        if (git.Stashes.Count > 0)
        {
            var oldest = git.Stashes.Min(s => s.Date);
            reasons.Add(Reason(SafetyReasonKind.Stashes, SafetyEffect.Risk,
                Plural(git.Stashes.Count, "stash", "stashes") + $", oldest {Age(now, oldest)}",
                RiskLevel.Medium, count: git.Stashes.Count, since: oldest));
        }

        // A main working tree that others hang off: deleting it orphans them. Records of worktrees whose
        // folder is already gone do not count.
        var linked = git.IsLinkedWorktree ? 0 : git.Worktrees.Skip(1).Count(w => !w.Prunable);
        if (linked > 0)
        {
            reasons.Add(Reason(SafetyReasonKind.LinkedWorktrees, SafetyEffect.Blocker,
                Plural(linked, "linked worktree") + (linked == 1 ? " uses" : " use") + " this repository",
                count: linked, names: git.Worktrees.Skip(1).Where(w => !w.Prunable).Select(w => w.Path).Take(SafetyFacts.MaxNames).ToList()));
        }
    }

    private static void Branches(GitState git, DateTimeOffset now, List<SafetyReason> reasons)
    {
        var unknown = new List<string>();
        foreach (var branch in git.Branches)
        {
            // Not ahead of an upstream that still exists: the tip is on that remote-tracking ref.
            var atUpstream = branch is { Upstream: not null, UpstreamGone: false, Ahead: 0 };
            if (branch.OnRemote == true || (branch.OnRemote is null && atUpstream))
            {
                continue;
            }

            if (branch.OnRemote is null && branch is not { Upstream: not null, UpstreamGone: false })
            {
                // An agent that did not say whether the tip is on a remote: it may be a merged branch
                // (nothing lost) or work that was never pushed. Unknown, not safe.
                unknown.Add(branch.Name);
                continue;
            }

            var count = branch.UnpushedCount ?? (branch.OnRemote is null ? branch.Ahead : (int?)null);
            var since = branch.OldestUnpushedAt ?? branch.LastCommitDate;
            var commits = count is { } n ? Plural(n, "commit") : "Commits";
            var oldest = branch.OldestUnpushedAt is { } o ? $", oldest {Age(now, o)}" : "";
            if (branch is { Upstream: not null, UpstreamGone: false })
            {
                reasons.Add(Reason(SafetyReasonKind.UnpushedCommits, SafetyEffect.Risk,
                    $"{commits} on {branch.Name} not on any remote{oldest}", RiskLevel.High, branch.Name, count, since));
            }
            else
            {
                reasons.Add(Reason(SafetyReasonKind.LocalOnlyBranch, SafetyEffect.Risk,
                    $"{commits} on {branch.Name} not on any remote{oldest} ({(branch.UpstreamGone ? "its upstream was deleted" : "never pushed")})",
                    RiskLevel.High, branch.Name, count, since));
            }
        }

        if (unknown.Count > 0)
        {
            reasons.Add(Reason(SafetyReasonKind.BranchesUnknown, SafetyEffect.Unknown,
                $"Could not tell whether {NameList(unknown, unknown.Count, 3)} {(unknown.Count == 1 ? "is" : "are")} on a remote",
                count: unknown.Count, names: unknown.Take(SafetyFacts.MaxNames).ToList()));
        }
    }

    private static SafetyAssessment Finish(DevEntry entry, List<SafetyReason> reasons, bool includeRepository)
    {
        var ordered = reasons
            .OrderBy(r => r.Effect)
            .ThenByDescending(r => r.Level)
            .ThenBy(r => r.Since ?? DateTimeOffset.MaxValue)
            .ToList();
        var verdict =
            ordered.Any(r => r.Effect == SafetyEffect.Risk) ? SafetyVerdict.AtRisk
            : ordered.Any(r => r.Effect is SafetyEffect.Unknown or SafetyEffect.Blocker) ? SafetyVerdict.Unclear
            : ordered.Count > 0 ? SafetyVerdict.SafeWithCaveats
            : SafetyVerdict.Safe;
        var risks = ordered.Where(r => r.Effect == SafetyEffect.Risk).ToList();

        return new SafetyAssessment
        {
            Verdict = verdict,
            Reasons = ordered,
            Level = risks.Count == 0 ? RiskLevel.None : risks.Max(r => r.Level),
            OldestRiskAt = risks.Select(r => r.Since).Where(s => s is not null).Min(),
            LastTouchedAt = Cleanup.RuleHelpers.ActivityOf(entry),
            TotalBytes = entry.Size?.TotalBytes,
            ReclaimableBytes = entry.Size?.ReclaimableBytes,
            IsLinkedWorktree = entry.Git?.IsLinkedWorktree ?? false,
            IncludesRepository = includeRepository,
            Fingerprint = FingerprintOf(entry, verdict, ordered, includeRepository),
        };
    }

    /// <summary>Built from facts, not from the sentences, so it does not change as "4 days" becomes "5 days".</summary>
    private static string FingerprintOf(DevEntry entry, SafetyVerdict verdict, List<SafetyReason> reasons, bool includeRepository)
    {
        var text = new StringBuilder()
            .Append(entry.Path).Append('\n')
            .Append(entry.Git?.HeadSha).Append('\n')
            .Append(verdict).Append('\n')
            .Append(includeRepository).Append('\n');
        foreach (var reason in reasons)
        {
            text.Append(reason.Kind).Append('|').Append(reason.Subject).Append('|')
                .Append(reason.Count?.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(string.Join('\u001f', reason.Names)).Append('\n');
        }

        if (includeRepository && entry.Git is { } git)
        {
            foreach (var branch in git.Branches.OrderBy(b => b.Name, StringComparer.Ordinal))
            {
                text.Append(branch.Name).Append('=').Append(branch.Sha).Append('\n');
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())).AsSpan(0, 12));
    }

    private static SafetyReason Reason(
        SafetyReasonKind kind, SafetyEffect effect, string text, RiskLevel level = RiskLevel.None,
        string? subject = null, int? count = null, DateTimeOffset? since = null, IReadOnlyList<string>? names = null) => new()
        {
            Kind = kind,
            Effect = effect,
            Level = effect == SafetyEffect.Risk ? level : RiskLevel.None,
            Text = text,
            Subject = subject,
            Count = count,
            Since = since,
            Names = names ?? [],
        };

    private static string Plural(int n, string singular, string? plural = null) => Cleanup.RuleHelpers.Plural(n, singular, plural);

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    /// <summary>"a, b, c and 4 more".</summary>
    private static string NameList(IReadOnlyList<string> names, int total, int show)
    {
        var shown = names.Take(show).ToList();
        var more = total - shown.Count;
        return string.Join(", ", shown) + (more > 0 ? $" and {more} more" : "");
    }

    /// <summary>An age as a bare span: "today", "4 days", "3 months", "2 years".</summary>
    public static string Age(DateTimeOffset now, DateTimeOffset then)
    {
        var days = Math.Max(0, (now - then).TotalDays);
        return days < 1 ? "today"
            : days < 60 ? Plural((int)days, "day")
            : days < 730 ? Plural((int)(days / 30.4), "month")
            : Plural((int)(days / 365.25), "year");
    }

    private static string Ago(DateTimeOffset now, DateTimeOffset then) => Age(now, then) is "today" ? "today" : Age(now, then) + " ago";
}
