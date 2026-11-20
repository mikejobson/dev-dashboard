using System.Text.Json;
using DevDash.Contracts;
using DevDash.Core.Safety;
using static DevDash.Core.Tests.Cleanup.Build;

namespace DevDash.Core.Tests.Safety;

public class RepoSafetyTests
{
    /// <summary>A clone as a current agent reports it when nothing is out of place: every fact present and clean.</summary>
    private static GitState Clean(Func<GitState, GitState>? with = null, Func<SafetyFacts, SafetyFacts>? facts = null)
    {
        var safety = new SafetyFacts { BranchesChecked = true, HeadOnRemote = true };
        var git = GitOf() with
        {
            Branches = [Branch("main", upstream: "origin/main", current: true) with { OnRemote = true }],
            Safety = facts is null ? safety : facts(safety),
        };
        return with is null ? git : with(git);
    }

    private static SafetyAssessment Assess(GitState git, SizeInfo? size = null, bool includeRepository = true) =>
        RepoSafety.Assess(Repo("widget", git, size: size), Now, includeRepository);

    private static BranchInfo Local(string name, int unpushed, double oldestDays, string? upstream = null, bool gone = false) =>
        Branch(name, upstream: upstream, gone: gone, ahead: upstream is null || gone ? 0 : unpushed) with
        {
            OnRemote = false,
            UnpushedCount = unpushed,
            OldestUnpushedAt = DaysAgo(oldestDays),
        };

    [Fact]
    public void A_completely_clean_clone_is_safe_to_remove_with_its_size_and_last_touched()
    {
        var size = new SizeInfo { TotalBytes = 5000, ReclaimableBytes = 3000, MeasuredAt = Now };

        var safety = Assess(Clean(), size);

        Assert.Equal(SafetyVerdict.Safe, safety.Verdict);
        Assert.Empty(safety.Reasons);
        Assert.True(safety.IsRemovable);
        Assert.Equal(RiskLevel.None, safety.Level);
        Assert.Null(safety.OldestRiskAt);
        Assert.Equal(5000, safety.FreedBytes);
        Assert.Equal(3000, safety.ReclaimableBytes);
        Assert.Equal(DaysAgo(1), safety.LastTouchedAt);
    }

    [Fact]
    public void Unpushed_commits_are_at_risk_with_their_count_and_the_age_of_the_oldest()
    {
        var safety = Assess(Clean(g => g with
        {
            Branches = [.. g.Branches, Local("feature/x", unpushed: 2, oldestDays: 4.5, upstream: "origin/feature/x")],
        }));

        Assert.Equal(SafetyVerdict.AtRisk, safety.Verdict);
        var reason = Assert.Single(safety.Reasons);
        Assert.Equal(SafetyReasonKind.UnpushedCommits, reason.Kind);
        Assert.Equal("2 commits on feature/x not on any remote, oldest 4 days", reason.Text);
        Assert.Equal("feature/x", reason.Subject);
        Assert.Equal(2, reason.Count);
        Assert.Equal(RiskLevel.High, safety.Level);
        Assert.Equal(DaysAgo(4.5), safety.OldestRiskAt);
        Assert.False(safety.IsRemovable);
    }

    [Fact]
    public void A_branch_with_no_upstream_is_at_risk_only_when_no_remote_ref_contains_it()
    {
        var safety = Assess(Clean(g => g with
        {
            Branches =
            [
                .. g.Branches,
                // Merged, then deleted on the remote: its commits are in origin/main.
                Branch("merged-and-deleted", upstream: "origin/merged-and-deleted", gone: true) with { OnRemote = true },
                Branch("never-pushed-but-merged") with { OnRemote = true },
                Local("spike", unpushed: 1, oldestDays: 90),
                Local("orphaned", unpushed: 3, oldestDays: 10, upstream: "origin/orphaned", gone: true),
            ],
        }));

        Assert.Equal(SafetyVerdict.AtRisk, safety.Verdict);
        Assert.Equal(
            [
                "1 commit on spike not on any remote, oldest 2 months (never pushed)",
                "3 commits on orphaned not on any remote, oldest 10 days (its upstream was deleted)",
            ],
            safety.Reasons.Select(r => r.Text));
        Assert.All(safety.Reasons, r => Assert.Equal(SafetyReasonKind.LocalOnlyBranch, r.Kind));
        Assert.Equal(DaysAgo(90), safety.OldestRiskAt);
    }

    [Fact]
    public void Uncommitted_changes_untracked_files_and_stashes_are_at_risk_worst_first()
    {
        var safety = Assess(Clean(
            g => g with
            {
                Unstaged = 2,
                Staged = 1,
                Untracked = 3,
                NewestTrackedChangeAt = DaysAgo(3),
                NewestUntrackedAt = DaysAgo(20),
                Stashes =
                [
                    new StashInfo { Index = 0, Date = DaysAgo(2), Message = "wip" },
                    new StashInfo { Index = 1, Date = DaysAgo(40), Message = "older" },
                ],
            },
            f => f with { SecretFiles = [".env"], SecretFileCount = 1 }));

        Assert.Equal(SafetyVerdict.AtRisk, safety.Verdict);
        Assert.Equal(
            [
                "2 stashes, oldest 40 days",
                "3 uncommitted changes, last edited 3 days ago",
                "3 untracked files or folders, including .env",
            ],
            safety.Reasons.Select(r => r.Text));
        Assert.Equal(RiskLevel.Medium, safety.Level);
        Assert.Equal(DaysAgo(40), safety.OldestRiskAt);
    }

    [Fact]
    public void Local_only_tags_are_at_risk_and_tags_never_compared_are_unknown()
    {
        var localOnly = Assess(Clean(facts: f => f with { TagCount = 5, LocalOnlyTags = ["v1.0", "v1.1"], LocalOnlyTagCount = 2, RemoteTagsCheckedAt = Now }));
        Assert.Equal(SafetyVerdict.AtRisk, localOnly.Verdict);
        Assert.Equal("2 tags not on any remote: v1.0, v1.1", Assert.Single(localOnly.Reasons).Text);

        var allPushed = Assess(Clean(facts: f => f with { TagCount = 5, RemoteTagsCheckedAt = Now }));
        Assert.Equal(SafetyVerdict.Safe, allPushed.Verdict);

        // A machine that does not fetch never lists the remote's tags: not safe, but not known to be lost either.
        var unchecked_ = Assess(Clean(facts: f => f with { TagCount = 5 }));
        Assert.Equal(SafetyVerdict.Unclear, unchecked_.Verdict);
        Assert.Equal(SafetyReasonKind.TagsUnknown, Assert.Single(unchecked_.Reasons).Kind);
    }

    [Fact]
    public void No_remote_at_all_is_at_risk_whatever_else_is_clean()
    {
        var safety = Assess(GitOf(remote: null) with
        {
            Branches = [Branch("main", current: true, ageDays: 3) with { OnRemote = false }, Branch("old", ageDays: 200) with { OnRemote = false }],
            Safety = new SafetyFacts { BranchesChecked = true, HeadOnRemote = false, TagCount = 1, LocalOnlyTags = ["v1"], LocalOnlyTagCount = 1 },
        });

        Assert.Equal(SafetyVerdict.AtRisk, safety.Verdict);
        var reason = Assert.Single(safety.Reasons);
        Assert.Equal(SafetyReasonKind.NoRemote, reason.Kind);
        Assert.Equal("No remote: 2 branches and 1 tag exist only here", reason.Text);
        Assert.Equal(RiskLevel.High, safety.Level);
        Assert.Equal(DaysAgo(200), safety.OldestRiskAt);
    }

    [Fact]
    public void Ignored_files_that_are_not_build_output_make_it_safe_with_caveats_and_name_them()
    {
        var safety = Assess(Clean(facts: f => f with { IgnoredFiles = [".env", "local.db", "notes/"], IgnoredFileCount = 3 }));

        Assert.Equal(SafetyVerdict.SafeWithCaveats, safety.Verdict);
        Assert.True(safety.IsRemovable);
        var caveat = Assert.Single(safety.Caveats);
        Assert.Equal(SafetyReasonKind.IgnoredFiles, caveat.Kind);
        Assert.Equal([".env", "local.db", "notes/"], caveat.Names);
        Assert.Equal("3 ignored files or folders that are not build output: .env, local.db, notes/", caveat.Text);
        Assert.Equal(RiskLevel.None, safety.Level);
    }

    [Fact]
    public void Data_from_an_agent_without_the_new_facts_is_never_safe()
    {
        // What a 0.5.0 agent sends for a clean, pushed clone.
        var old = GitOf();

        var safety = Assess(old);

        Assert.Equal(SafetyVerdict.Unclear, safety.Verdict);
        Assert.False(safety.IsRemovable);
        Assert.Equal(SafetyReasonKind.OldAgent, Assert.Single(safety.Reasons).Kind);
    }

    [Fact]
    public void An_old_agent_still_shows_what_it_does_know_and_calls_the_rest_unknown()
    {
        var safety = Assess(GitOf(with: g => g with
        {
            Branches =
            [
                Branch("main", upstream: "origin/main", current: true),
                Branch("ahead", upstream: "origin/ahead", ahead: 2),
                Branch("merged-locally", merged: true),
            ],
        }));

        Assert.Equal(SafetyVerdict.AtRisk, safety.Verdict);
        Assert.Equal(
            [SafetyReasonKind.UnpushedCommits, SafetyReasonKind.BranchesUnknown, SafetyReasonKind.OldAgent],
            safety.Reasons.Select(r => r.Kind));
        Assert.Equal("2 commits on ahead not on any remote", safety.Reasons[0].Text);
        Assert.Equal("Could not tell whether merged-locally is on a remote", safety.Reasons[1].Text);
    }

    [Fact]
    public void A_snapshot_written_before_the_new_fields_still_reads_and_is_not_called_safe()
    {
        const string json = """
            {"schemaVersion":1,"machine":{"machineId":"old","hostname":"old"},"roots":["/dev"],"entries":[
              {"root":"/dev","name":"widget","path":"/dev/widget","kind":"GitRepo","git":{"branch":"main","upstream":"origin/main",
               "remotes":[{"name":"origin","url":"https://example.invalid/acme/widget.git"}],
               "branches":[{"name":"main","sha":"1111111","upstream":"origin/main","isCurrent":true}]}}]}
            """;

        var snapshot = JsonSerializer.Deserialize<MachineSnapshot>(json, SnapshotSchema.JsonOptions)!;
        var clone = Assert.Single(RepoSafety.AssessAll(snapshot, Now));

        Assert.Null(clone.Entry.Git!.Safety);
        Assert.Null(clone.Entry.Git.Branches[0].OnRemote);
        Assert.Equal(SafetyVerdict.Unclear, clone.Safety.Verdict);
    }

    [Fact]
    public void The_new_fields_are_optional_on_the_wire()
    {
        var old = JsonSerializer.Serialize(GitOf(), SnapshotSchema.JsonOptions);
        Assert.DoesNotContain("safety", old);
        Assert.DoesNotContain("onRemote", old);
        Assert.DoesNotContain("mainWorktreePath", old);

        var round = JsonSerializer.Deserialize<GitState>(JsonSerializer.Serialize(Clean(facts: f => f with { IgnoredFiles = [".env"], IgnoredFileCount = 1 }), SnapshotSchema.JsonOptions), SnapshotSchema.JsonOptions)!;
        Assert.Equal([".env"], round.Safety!.IgnoredFiles);
        Assert.True(round.Branches[0].OnRemote);
        Assert.Equal(1, SnapshotSchema.Version);
    }

    [Fact]
    public void Submodules_must_be_clean_pushed_and_checked()
    {
        SafetyAssessment With(params SubmoduleInfo[] subs) => Assess(Clean(facts: f => f with { Submodules = subs }));

        Assert.Equal(SafetyVerdict.Safe, With(
            new SubmoduleInfo { Path = "libs/a", Initialized = true, Checked = true },
            new SubmoduleInfo { Path = "libs/not-cloned", Initialized = false, Checked = true }).Verdict);

        var risky = With(
            new SubmoduleInfo { Path = "libs/a", Initialized = true, Checked = true, Dirty = true, Stashes = 1 },
            new SubmoduleInfo { Path = "libs/b", Initialized = true, Checked = true, Unpushed = true });
        Assert.Equal(SafetyVerdict.AtRisk, risky.Verdict);
        Assert.Equal(
            ["Submodule libs/b has commits not on any remote", "Submodule libs/a has uncommitted changes and 1 stash"],
            risky.Reasons.Select(r => r.Text));

        var unknown = With(new SubmoduleInfo { Path = "libs/c", Initialized = true, Checked = false });
        Assert.Equal(SafetyVerdict.Unclear, unknown.Verdict);
        Assert.Equal(SafetyReasonKind.SubmoduleUnknown, Assert.Single(unknown.Reasons).Kind);
    }

    [Fact]
    public void A_detached_head_on_no_branch_and_no_remote_is_at_risk()
    {
        GitState Detached(bool? onRemote) => Clean(
            g => g with { Branch = null, Detached = true, Upstream = null, HeadSha = "9999999999999999999999999999999999999999" },
            f => f with { HeadOnRemote = onRemote });

        Assert.Equal(SafetyVerdict.Safe, Assess(Detached(true)).Verdict);
        var lost = Assess(Detached(false));
        Assert.Equal(SafetyVerdict.AtRisk, lost.Verdict);
        Assert.Equal("Detached HEAD at 9999999 is on no branch and no remote", Assert.Single(lost.Reasons).Text);
        Assert.Equal(SafetyVerdict.Unclear, Assess(Detached(null)).Verdict);
    }

    [Fact]
    public void A_repo_with_linked_worktrees_is_not_safe_to_remove_but_prunable_records_do_not_count()
    {
        var withWorktree = Assess(Clean(g => g with
        {
            Worktrees =
            [
                new WorktreeInfo { Path = "/dev/widget", Branch = "main" },
                new WorktreeInfo { Path = "/dev/widget.worktrees/a", Branch = "a" },
                new WorktreeInfo { Path = "/dev/gone", Branch = "b", Prunable = true },
            ],
        }));

        Assert.Equal(SafetyVerdict.Unclear, withWorktree.Verdict);
        var blocker = Assert.Single(withWorktree.Reasons);
        Assert.Equal(SafetyEffect.Blocker, blocker.Effect);
        Assert.Equal("1 linked worktree uses this repository", blocker.Text);
        Assert.Equal(["/dev/widget.worktrees/a"], blocker.Names);

        // A T3 Code thread's worktree blocks removal just the same, and says whose it is.
        var withT3 = Assess(Clean(g => g with
        {
            Worktrees = [new WorktreeInfo { Path = "/dev/widget", Branch = "main" }, new WorktreeInfo { Path = "/home/me/.t3/worktrees/widget/t3-1a2b", Branch = "t3/fix", T3Owned = true }],
        }));
        Assert.Equal(SafetyVerdict.Unclear, withT3.Verdict);
        Assert.Equal("1 linked worktree uses this repository (made by T3 Code)", Assert.Single(withT3.Reasons).Text);

        var onlyPrunable = Assess(Clean(g => g with
        {
            Worktrees = [new WorktreeInfo { Path = "/dev/widget", Branch = "main" }, new WorktreeInfo { Path = "/dev/gone", Branch = "b", Prunable = true }],
        }));
        Assert.Equal(SafetyVerdict.Safe, onlyPrunable.Verdict);
    }

    [Fact]
    public void A_linked_worktree_answers_for_its_own_folder_and_leaves_the_branches_to_the_main_repo()
    {
        WorktreeInfo[] worktrees = [new() { Path = "/dev/widget", Branch = "main" }, new() { Path = "/dev/widget.worktrees/spike", Branch = "spike" }];
        BranchInfo[] branches = [Branch("main", upstream: "origin/main") with { OnRemote = true }, Local("spike", unpushed: 2, oldestDays: 5)];
        var main = Repo("widget", Clean(g => g with { Branches = branches, Worktrees = worktrees }));
        var linked = Folder("widget.worktrees", EntryKind.WorktreeContainer, Repo("spike", Clean(g => g with { Branch = "spike", Upstream = null, Branches = branches })));
        linked = linked with
        {
            Children = [linked.Children[0] with { Git = linked.Children[0].Git! with { IsLinkedWorktree = true, CommonDir = "/dev/widget/.git", Worktrees = worktrees, MainWorktreePath = "/dev/widget" } }],
        };

        var all = RepoSafety.AssessAll(Machine("mac", main, linked), Now);

        var mainSafety = all.Single(c => c.Entry.Name == "widget");
        Assert.Equal(SafetyVerdict.AtRisk, mainSafety.Safety.Verdict);
        Assert.Equal([SafetyReasonKind.LocalOnlyBranch, SafetyReasonKind.LinkedWorktrees], mainSafety.Safety.Reasons.Select(r => r.Kind));
        Assert.Null(mainSafety.Main);

        // Removing the worktree keeps the spike branch in the main repo, so the worktree itself is clean.
        var spike = all.Single(c => c.Entry.Name == "spike");
        Assert.Equal(SafetyVerdict.Safe, spike.Safety.Verdict);
        Assert.True(spike.Safety.IsLinkedWorktree);
        Assert.False(spike.Safety.IncludesRepository);
        Assert.Same(main, spike.Main);
        Assert.Equal(mainSafety.RepoKey, spike.RepoKey);

        // With the main working tree outside the scan roots, the worktree is all there is to report the branch.
        var alone = Assert.Single(RepoSafety.AssessAll(Machine("mac", linked), Now));
        Assert.Equal(SafetyVerdict.AtRisk, alone.Safety.Verdict);
        Assert.True(alone.Safety.IncludesRepository);
        Assert.Equal(SafetyReasonKind.LocalOnlyBranch, Assert.Single(alone.Safety.Reasons).Kind);
    }

    [Fact]
    public void Folders_that_are_not_repos_are_skipped_by_AssessAll_and_never_safe_on_their_own()
    {
        var folder = Folder("Vendor_SDK");

        Assert.Empty(RepoSafety.AssessAll(Machine("mac", folder), Now));
        Assert.Equal(SafetyVerdict.Unclear, RepoSafety.Assess(folder, Now).Verdict);
    }

    [Fact]
    public void The_fingerprint_follows_the_facts_not_the_clock()
    {
        var git = Clean(g => g with { Branches = [.. g.Branches, Local("feature/x", unpushed: 2, oldestDays: 4)] });
        var today = RepoSafety.Assess(Repo("widget", git), Now);
        var nextWeek = RepoSafety.Assess(Repo("widget", git), Now.AddDays(7));
        var oneMore = RepoSafety.Assess(Repo("widget", git with { Branches = [git.Branches[0], Local("feature/x", unpushed: 3, oldestDays: 4)] }), Now);
        var moved = RepoSafety.Assess(Repo("widget", Clean(g => g with { Branches = [g.Branches[0] with { Sha = "3333333333333333333333333333333333333333" }] })), Now);

        Assert.NotEqual(today.Reasons[0].Text, nextWeek.Reasons[0].Text);
        Assert.Equal(today.Fingerprint, nextWeek.Fingerprint);
        Assert.NotEqual(today.Fingerprint, oneMore.Fingerprint);
        Assert.NotEqual(RepoSafety.Assess(Repo("widget", Clean()), Now).Fingerprint, moved.Fingerprint);
    }

    [Theory]
    [InlineData(0.2, "today")]
    [InlineData(1.5, "1 day")]
    [InlineData(45, "45 days")]
    [InlineData(100, "3 months")]
    [InlineData(800, "2 years")]
    public void Ages_read_as_a_span(double days, string expected) =>
        Assert.Equal(expected, RepoSafety.Age(Now, DaysAgo(days)));
}
