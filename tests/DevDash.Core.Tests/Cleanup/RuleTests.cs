using DevDash.Contracts;
using DevDash.Core.Cleanup;
using DevDash.Core.Cleanup.Rules;
using DevDash.Core.GitHub;
using static DevDash.Core.Tests.Cleanup.Build;

namespace DevDash.Core.Tests.Cleanup;

public class NonGitFolderRuleTests
{
    private readonly NonGitFolderRule _rule = new();

    [Fact]
    public void Flags_folders_and_loose_files_but_not_repos()
    {
        var context = Context(Machine("mac",
            Repo("widget"),
            Folder("Vendor_SDK_v3"),
            Folder("notes.txt", EntryKind.LooseFile)));

        var findings = _rule.Evaluate(context).ToList();

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.Title == "Not a git repo: Vendor_SDK_v3" && Fix(f).Command == "mv '/dev/Vendor_SDK_v3' ~/Documents/");
        Assert.Contains(findings, f => f.Title == "Loose file: notes.txt");
        Assert.All(findings, f => Assert.Equal("mac", f.MachineId));
    }

    [Fact]
    public void Suggests_git_init_when_the_folder_looks_like_code()
    {
        var folder = Folder("prototype") with
        {
            Description = new DescriptionInputs { Manifests = [new ManifestInfo { File = "package.json", Name = "prototype" }] },
        };

        var finding = Assert.Single(_rule.Evaluate(Context(Machine("mac", folder))));

        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Equal("git -C '/dev/prototype' init", Fix(finding).Command);
    }

    [Fact]
    public void A_plain_folder_holding_clones_is_reported_once_and_worktree_folders_are_left_to_the_worktree_rule()
    {
        var context = Context(Machine("mac",
            Folder("Client Workshop", EntryKind.WorktreeContainer, Repo("workshop-brief"), Folder("_to_delete")),
            Folder("core.worktrees", EntryKind.WorktreeContainer, Repo("feature-x"))));

        var finding = Assert.Single(_rule.Evaluate(context));

        Assert.Equal("Not a git repo: Client Workshop", finding.Title);
        Assert.Contains("workshop-brief", finding.Explanation);
        Assert.Contains("_to_delete", finding.Explanation);
    }

    [Fact]
    public void Quotes_paths_with_apostrophes()
    {
        var folder = Folder("dev's stuff");

        var finding = Assert.Single(_rule.Evaluate(Context(Machine("mac", folder))));

        Assert.Equal(@"mv '/dev/dev'\''s stuff' ~/Documents/", Fix(finding).Command);
    }
}

public class StaleWorktreeRuleTests
{
    private readonly StaleWorktreeRule _rule = new();

    private static GitState WithWorktrees(params WorktreeInfo[] extra) => GitOf(with: g => g with
    {
        Branches =
        [
            Branch("main", "origin/main", current: true, merged: true),
            Branch("done", "origin/done", merged: true),
            Branch("deleted", "origin/deleted", gone: true),
            Branch("old", "origin/old", ageDays: 90),
            Branch("fresh", "origin/fresh", ageDays: 2),
        ],
        Worktrees = [new WorktreeInfo { Path = "/dev/widget", Branch = "main" }, .. extra],
    });

    [Fact]
    public void Prunable_worktrees_suggest_prune()
    {
        var git = WithWorktrees(new WorktreeInfo { Path = "/tmp/gone", Detached = true, Prunable = true, PrunableReason = "gitdir file points to non-existent location" });

        var finding = Assert.Single(_rule.Evaluate(Context(Machine("mac", Repo("widget", git)))));

        Assert.Equal("Prunable worktree: /tmp/gone", finding.Title);
        Assert.Equal("git -C '/dev/widget' worktree prune", Fix(finding).Command);
        Assert.Contains("non-existent location", finding.Explanation);
    }

    [Fact]
    public void Merged_gone_and_idle_worktrees_suggest_remove_and_fresh_ones_are_left_alone()
    {
        var git = WithWorktrees(
            new WorktreeInfo { Path = "/wt/done", Branch = "done" },
            new WorktreeInfo { Path = "/wt/deleted", Branch = "deleted" },
            new WorktreeInfo { Path = "/wt/old", Branch = "old" },
            new WorktreeInfo { Path = "/wt/fresh", Branch = "fresh" },
            new WorktreeInfo { Path = "/wt/locked", Branch = "done", Locked = true });

        var findings = _rule.Evaluate(Context(Machine("mac", Repo("widget", git)))).ToList();

        Assert.Equal(3, findings.Count);
        Assert.Contains(findings, f => f.Title == "Worktree for merged branch done" && Fix(f).Command == "git -C '/dev/widget' worktree remove '/wt/done'");
        Assert.Contains(findings, f => f.Title == "Worktree for deleted branch deleted");
        Assert.Contains(findings, f => f.Title == "Idle worktree: /wt/old" && f.Explanation.Contains("90 days"));
    }

    [Fact]
    public void A_single_working_tree_has_nothing_to_report() =>
        Assert.Empty(_rule.Evaluate(Context(Machine("mac", Repo("widget")))));

    [Fact]
    public void Linked_worktrees_do_not_report_the_same_worktree_twice()
    {
        var git = WithWorktrees(new WorktreeInfo { Path = "/dev/widget-done", Branch = "done" });
        var main = Repo("widget", git);
        var linked = Repo("widget-done", git with { Branch = "done", IsLinkedWorktree = true, CommonDir = "/dev/widget/.git" });

        var finding = Assert.Single(_rule.Evaluate(Context(Machine("mac", main, linked))));

        Assert.Equal("/dev/widget", finding.Path);
    }

    [Fact]
    public void Uncommitted_work_in_a_merged_worktree_lowers_the_severity()
    {
        var git = WithWorktrees(new WorktreeInfo { Path = "/dev/widget-done", Branch = "done" });
        var linked = Repo("widget-done", git with { Branch = "done", IsLinkedWorktree = true, CommonDir = "/dev/widget/.git", Unstaged = 2 });

        var finding = Assert.Single(_rule.Evaluate(Context(Machine("mac", Repo("widget", git), linked))));

        Assert.Equal(FindingSeverity.Low, finding.Severity);
        Assert.Contains("uncommitted changes", finding.Explanation);
    }

    [Fact]
    public void Empty_worktree_folders_and_orphaned_children_are_reported()
    {
        var context = Context(Machine("mac",
            Folder("copilot-worktrees", EntryKind.WorktreeContainer),
            Folder("core.worktrees", EntryKind.WorktreeContainer, Folder("copilot-worktree-2026-04-10"), Repo("panel-app-polish"))));

        var findings = _rule.Evaluate(context).ToList();

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.Title == "Empty worktree folder: copilot-worktrees" && Fix(f).Command == "rmdir '/dev/copilot-worktrees'");
        Assert.Contains(findings, f => f.Title == "Orphaned worktree folder: core.worktrees/copilot-worktree-2026-04-10");
    }
}

public class MergedBranchRuleTests
{
    private readonly MergedBranchRule _rule = new();

    [Fact]
    public void Lists_merged_branches_in_one_delete_command()
    {
        var git = GitOf(with: g => g with
        {
            Branches =
            [
                Branch("main", "origin/main", merged: true, current: true),
                Branch("feature/b", "origin/feature/b", merged: true),
                Branch("feature/a", merged: true),
                Branch("wip", merged: false),
                Branch("gone-and-merged", "origin/x", gone: true, merged: true),
            ],
        });

        var finding = Assert.Single(_rule.Evaluate(Context(Machine("mac", Repo("widget", git)))));

        Assert.Equal("widget: 2 branches merged into main", finding.Title);
        Assert.Equal("git -C '/dev/widget' branch -d 'feature/a' 'feature/b'", Fix(finding).Command);
    }

    [Fact]
    public void Skips_branches_checked_out_in_any_worktree_and_repos_without_a_default_branch()
    {
        var git = GitOf(branch: "feature/a", with: g => g with
        {
            Branches = [Branch("main", merged: true), Branch("feature/a", merged: true, current: true), Branch("feature/b", merged: true)],
            Worktrees = [new WorktreeInfo { Path = "/dev/widget", Branch = "feature/a" }, new WorktreeInfo { Path = "/wt/b", Branch = "feature/b" }],
        });

        Assert.Empty(_rule.Evaluate(Context(Machine("mac", Repo("widget", git)))));
        Assert.Empty(_rule.Evaluate(Context(Machine("mac", Repo("other", GitOf(with: g => g with { DefaultBranch = null, Branches = [Branch("x", merged: null)] }))))));
    }

    [Fact]
    public void Fingerprint_changes_when_the_set_of_branches_changes()
    {
        GitState With(params string[] names) => GitOf(with: g => g with { Branches = [Branch("main", merged: true, current: true), .. names.Select(n => Branch(n, merged: true))] });

        var one = Assert.Single(_rule.Evaluate(Context(Machine("mac", Repo("widget", With("a"))))));
        var same = Assert.Single(_rule.Evaluate(Context(Machine("mac", Repo("widget", With("a"))))));
        var two = Assert.Single(_rule.Evaluate(Context(Machine("mac", Repo("widget", With("a", "b"))))));

        Assert.Equal(one.Fingerprint, same.Fingerprint);
        Assert.NotEqual(one.Fingerprint, two.Fingerprint);
    }
}

public class GoneUpstreamRuleTests
{
    private readonly GoneUpstreamRule _rule = new();

    [Fact]
    public void Merged_branches_get_a_safe_delete_and_unmerged_ones_a_forced_delete_with_a_warning()
    {
        var git = GitOf(with: g => g with
        {
            Branches =
            [
                Branch("main", "origin/main", merged: true, current: true),
                Branch("merged", "origin/merged", gone: true, merged: true),
                Branch("squashed", "origin/squashed", gone: true, merged: false),
                Branch("alive", "origin/alive"),
            ],
        });

        var findings = _rule.Evaluate(Context(Machine("mac", Repo("widget", git)))).ToList();

        Assert.Equal(2, findings.Count);
        var merged = findings.Single(f => f.Title == "widget: upstream gone for merged");
        Assert.Equal("git -C '/dev/widget' branch -d 'merged'", Fix(merged).Command);
        Assert.Equal(FindingSeverity.Low, merged.Severity);

        var squashed = findings.Single(f => f.Title == "widget: upstream gone for squashed");
        Assert.Equal("git -C '/dev/widget' branch -D 'squashed'", Fix(squashed).Command);
        Assert.Equal(FindingSeverity.Medium, squashed.Severity);
        Assert.Contains("check before", squashed.Explanation);
    }

    [Fact]
    public void Says_when_the_branch_is_checked_out()
    {
        var git = GitOf(branch: "feature", with: g => g with { Branches = [Branch("feature", "origin/feature", gone: true, merged: true, current: true)] });

        var finding = Assert.Single(_rule.Evaluate(Context(Machine("mac", Repo("widget", git)))));

        Assert.Contains("switch away first", finding.Explanation);
    }
}

public class OldUncommittedChangesRuleTests
{
    private readonly OldUncommittedChangesRule _rule = new();

    [Theory]
    [InlineData(13, 0)]
    [InlineData(14, 1)]
    [InlineData(200, 1)]
    public void Flags_tracked_changes_once_they_are_old_enough(int days, int expected)
    {
        var git = GitOf(with: g => g with { Unstaged = 3, Staged = 1, NewestTrackedChangeAt = DaysAgo(days) });

        var findings = _rule.Evaluate(Context(Machine("mac", Repo("widget", git)))).ToList();

        Assert.Equal(expected, findings.Count);
        if (expected == 1)
        {
            Assert.Equal($"widget: uncommitted changes for {days} days", findings[0].Title);
            Assert.Equal(days >= 56 ? FindingSeverity.High : FindingSeverity.Medium, findings[0].Severity);
            Assert.Contains("4 changed files", findings[0].Explanation);
        }
    }

    [Fact]
    public void Untracked_only_is_informational_and_clean_repos_are_ignored()
    {
        var untracked = GitOf(with: g => g with { Untracked = 2, NewestUntrackedAt = DaysAgo(40) });

        var finding = Assert.Single(_rule.Evaluate(Context(Machine("mac", Repo("widget", untracked), Repo("clean")))));

        Assert.Equal(FindingSeverity.Info, finding.Severity);
        Assert.Equal("widget: untracked files for 40 days", finding.Title);
    }

    [Fact]
    public void Honours_the_configured_threshold()
    {
        var git = GitOf(with: g => g with { Unstaged = 1, NewestTrackedChangeAt = DaysAgo(5) });
        var context = Context(Machine("mac", Repo("widget", git))) with { Options = new CleanupOptions { UncommittedDays = 3 } };

        Assert.Single(_rule.Evaluate(context));
    }
}

public class UnpushedCommitsRuleTests
{
    private readonly UnpushedCommitsRule _rule = new();

    [Fact]
    public void Reports_branches_ahead_of_upstream_and_branches_never_pushed()
    {
        var git = GitOf(with: g => g with
        {
            Branches =
            [
                Branch("main", "origin/main", merged: true, current: true),
                Branch("ahead", "origin/ahead", ahead: 3),
                Branch("diverged", "origin/diverged", ahead: 1, behind: 4),
                Branch("local-only"),
                Branch("local-but-merged", merged: true),
                Branch("gone", "origin/gone", gone: true, ahead: 2),
            ],
        });

        var findings = _rule.Evaluate(Context(Machine("mac", Repo("widget", git)))).ToList();

        Assert.Equal(3, findings.Count);
        Assert.Contains(findings, f => f.Title == "widget: 3 unpushed commits on ahead" && Fix(f).Command == "git -C '/dev/widget' push 'origin' 'ahead'");
        Assert.Contains(findings, f => f.Title == "widget: 1 unpushed commit on diverged" && f.Explanation.Contains("4 behind"));
        Assert.Contains(findings, f => f.Title == "widget: local-only has no upstream" && Fix(f).Command == "git -C '/dev/widget' push -u 'origin' 'local-only'");
    }

    [Fact]
    public void A_repo_without_a_remote_is_one_finding()
    {
        var git = GitOf(remote: null, with: g => g with { Branches = [Branch("main", current: true), Branch("feature")] });

        var finding = Assert.Single(_rule.Evaluate(Context(Machine("mac", Repo("widget", git)))));

        Assert.Equal("widget: no remote", finding.Title);
        Assert.Contains("2 local branches", finding.Explanation);
        Assert.StartsWith("gh repo create", Fix(finding).Command);
    }

    [Fact]
    public void Fingerprint_survives_a_change_in_the_commit_count()
    {
        Finding For(int ahead) => Assert.Single(_rule.Evaluate(Context(Machine("mac",
            Repo("widget", GitOf(with: g => g with { Branches = [Branch("main", "origin/main", ahead: ahead, merged: true)] }))))));

        Assert.Equal(For(1).Fingerprint, For(5).Fingerprint);
    }
}

public class OldStashRuleTests
{
    private readonly OldStashRule _rule = new();

    [Fact]
    public void Counts_only_stashes_past_the_threshold()
    {
        var git = GitOf(with: g => g with
        {
            Stashes =
            [
                new StashInfo { Index = 0, Date = DaysAgo(2), Message = "On main: fresh" },
                new StashInfo { Index = 1, Date = DaysAgo(45), Message = "WIP on main: abc older" },
                new StashInfo { Index = 2, Date = DaysAgo(70), Message = "WIP on main: abc oldest" },
            ],
        });

        var finding = Assert.Single(_rule.Evaluate(Context(Machine("mac", Repo("widget", git), Repo("clean")))));

        Assert.Equal("widget: 2 old stashes", finding.Title);
        Assert.Contains("70 days old", finding.Explanation);
        Assert.Contains("abc oldest", finding.Explanation);
    }
}

public class LargeBuildDirsRuleTests
{
    private readonly LargeBuildDirsRule _rule = new();

    [Fact]
    public void Flags_repos_over_the_threshold_with_a_command_listing_each_folder()
    {
        const long mb = 1024 * 1024;
        var big = new SizeInfo
        {
            TotalBytes = 900 * mb,
            ReclaimableBytes = 700 * mb,
            ReclaimableDirs =
            [
                new ReclaimableDir { Path = "/dev/widget/ui/node_modules", Bytes = 600 * mb },
                new ReclaimableDir { Path = "/dev/widget/app/bin", Bytes = 100 * mb },
            ],
        };
        var small = new SizeInfo { TotalBytes = 400 * mb, ReclaimableBytes = 300 * mb, ReclaimableDirs = [new ReclaimableDir { Path = "/dev/small/node_modules", Bytes = 300 * mb }] };

        var finding = Assert.Single(_rule.Evaluate(Context(Machine("mac", Repo("widget", size: big), Repo("small", size: small), Repo("unmeasured")))));

        Assert.Equal("widget: 700 MB of build output", finding.Title);
        Assert.Equal("rm -rf '/dev/widget/ui/node_modules' '/dev/widget/app/bin'", Fix(finding).Command);
        Assert.Equal(["du -sh '/dev/widget/ui/node_modules' '/dev/widget/app/bin'"], Inspects(finding));
        Assert.Contains("ui/node_modules (600 MB)", finding.Explanation);
        Assert.Equal(FindingSeverity.Low, finding.Severity);
    }

    [Theory]
    [InlineData("/dev/widget", "/dev/widget/ui/node_modules", "ui/node_modules")]
    [InlineData("/dev/widget/", "/dev/widget/bin", "bin")]
    [InlineData(@"C:\Users\dev\widget", @"C:\Users\dev\widget\ui\node_modules", @"ui\node_modules")]
    [InlineData("/dev/widget", "/dev/widgetx/bin", "/dev/widgetx/bin")]
    public void Relative_paths_do_not_depend_on_the_hub_operating_system(string root, string path, string expected) =>
        Assert.Equal(expected, LargeBuildDirsRule.RelativeTo(root, path));
}

public class UntouchedRepoRuleTests
{
    private readonly UntouchedRepoRule _rule = new();

    private static DevEntry Old(string name, Func<GitState, GitState>? with = null) =>
        Repo(name, GitOf(with: g =>
        {
            g = g with
            {
                LastCommit = g.LastCommit! with { Date = DaysAgo(400) },
                Branches = [Branch("main", "origin/main", merged: true, current: true, ageDays: 400)],
            };
            return with is null ? g : with(g);
        }), modifiedDaysAgo: 300);

    [Fact]
    public void A_clean_fully_pushed_repo_can_be_deleted()
    {
        var finding = Assert.Single(_rule.Evaluate(Context(Machine("mac", Old("widget"), Repo("active")))));

        Assert.Equal("widget: untouched for 10 months", finding.Title);
        Assert.Equal("rm -rf '/dev/widget'", Fix(finding).Command);
    }

    [Fact]
    public void Local_only_work_gets_a_check_command_instead_of_a_delete()
    {
        var finding = Assert.Single(_rule.Evaluate(Context(Machine("mac", Old("widget", g => g with { Unstaged = 1 })))));

        Assert.Empty(Fixes(finding));
        Assert.Contains("git -C '/dev/widget' status --short --branch", Inspects(finding));
        Assert.Contains("local-only work", finding.Explanation);
    }

    [Fact]
    public void A_recent_commit_on_any_branch_counts_as_activity()
    {
        var entry = Old("widget", g => g with { Branches = [.. g.Branches, Branch("feature", "origin/feature", ageDays: 3)] });

        Assert.Empty(_rule.Evaluate(Context(Machine("mac", entry))));
    }
}

public class DivergedAcrossMachinesRuleTests
{
    private readonly DivergedAcrossMachinesRule _rule = new();

    private static DevEntry Clone(string sha, int ahead, string branch = "main") =>
        Repo("widget", GitOf(branch: branch, with: g => g with { HeadSha = sha, Ahead = ahead }));

    [Fact]
    public void Same_branch_different_heads_both_ahead_is_a_divergence()
    {
        var context = Context(
            Machine("mac", Clone("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 2)),
            Machine("desktop", Clone("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 5)));

        var finding = Assert.Single(_rule.Evaluate(context));

        Assert.Equal(FindingSeverity.High, finding.Severity);
        Assert.Equal("widget: main diverged across 2 machines", finding.Title);
        Assert.Contains("desktop at bbbbbbb (5 ahead)", finding.Explanation);
        Assert.Equal("github.com/acme/widget", finding.RepoKey);
    }

    [Fact]
    public void No_divergence_when_only_one_is_ahead_or_heads_match_or_branches_differ()
    {
        const string a = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", b = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        Assert.Empty(_rule.Evaluate(Context(Machine("mac", Clone(a, 2)), Machine("desktop", Clone(b, 0)))));
        Assert.Empty(_rule.Evaluate(Context(Machine("mac", Clone(a, 2)), Machine("desktop", Clone(a, 2)))));
        Assert.Empty(_rule.Evaluate(Context(Machine("mac", Clone(a, 2)), Machine("desktop", Clone(b, 2, "feature")))));
        Assert.Empty(_rule.Evaluate(Context(Machine("mac", Clone(a, 2)))));
    }
}

public class RemoteArchivedOrDeletedRuleTests
{
    private readonly RemoteArchivedOrDeletedRule _rule = new();

    private static CleanupContext WithLookup(RemoteRepoLookup? lookup) =>
        Context(Machine("mac", Repo("widget"))) with
        {
            RemoteRepos = lookup is null ? new Dictionary<string, RemoteRepoLookup>() : new Dictionary<string, RemoteRepoLookup> { [lookup.Key] = lookup },
        };

    [Fact]
    public void Archived_and_missing_remotes_are_reported()
    {
        var archived = WithLookup(new RemoteRepoLookup { Key = "github.com/acme/widget", Found = true, Repo = new GitHubRepo { NameWithOwner = "acme/widget", IsArchived = true } });
        var missing = WithLookup(new RemoteRepoLookup { Key = "github.com/acme/widget", Found = false });

        Assert.Equal("widget: remote is archived", Assert.Single(_rule.Evaluate(archived)).Title);
        var gone = Assert.Single(_rule.Evaluate(missing));
        Assert.Equal("widget: remote not found on GitHub", gone.Title);
        Assert.Equal(FindingSeverity.Medium, gone.Severity);
    }

    [Fact]
    public void Healthy_or_unknown_remotes_are_not_reported()
    {
        var healthy = WithLookup(new RemoteRepoLookup { Key = "github.com/acme/widget", Found = true, Repo = new GitHubRepo { NameWithOwner = "acme/widget" } });

        Assert.Empty(_rule.Evaluate(healthy));
        Assert.Empty(_rule.Evaluate(WithLookup(null)));
    }
}

public class PullRequestRuleTests
{
    [Fact]
    public void Stale_rule_uses_a_shorter_limit_for_drafts_and_ignores_other_peoples_prs()
    {
        var context = Context() with
        {
            PullRequests =
            [
                Pr(1, idleDays: 31),
                Pr(2, idleDays: 29),
                Pr(3, idleDays: 15, draft: true),
                Pr(4, idleDays: 10, draft: true),
                Pr(5, idleDays: 90, authored: false),
            ],
        };

        var findings = new StalePullRequestRule().Evaluate(context).ToList();

        Assert.Equal(["acme/widget#1: idle for 31 days", "acme/widget#3: draft idle for 15 days"], findings.Select(f => f.Title));
        Assert.Contains("gh pr view 1 --repo 'acme/widget' --web", Inspects(findings[0]));
        Assert.Empty(Fixes(findings[0]));
        Assert.Equal("https://github.com/acme/widget/pull/1", findings[0].Url);
    }

    [Fact]
    public void Failing_ci_rule_reports_my_failing_prs()
    {
        var context = Context() with
        {
            PullRequests =
            [
                Pr(1, 1, ci: CiState.Failure),
                Pr(2, 1, ci: CiState.Failure, draft: true),
                Pr(3, 1, ci: CiState.Pending),
                Pr(4, 1, ci: CiState.Failure, authored: false),
            ],
        };

        var findings = new FailingCiPullRequestRule().Evaluate(context).ToList();

        Assert.Equal(2, findings.Count);
        Assert.Equal(FindingSeverity.High, findings.Single(f => f.Title == "acme/widget#1: CI failing").Severity);
        Assert.Equal(FindingSeverity.Low, findings.Single(f => f.Title == "acme/widget#2: CI failing").Severity);
    }
}

public class DuplicateClonesRuleTests
{
    private readonly DuplicateClonesRule _rule = new();

    [Fact]
    public void Two_separate_clones_of_one_remote_on_one_machine_are_duplicates()
    {
        var context = Context(
            Machine("mac", Repo("widget"), Repo("widget-copy"), Repo("other", GitOf(remote: "git@github.com:acme/other.git"))),
            Machine("desktop", Repo("widget")));

        var finding = Assert.Single(_rule.Evaluate(context));

        Assert.Equal("github.com/acme/widget is cloned 2 times on mac", finding.Title);
        Assert.Contains("/dev/widget-copy", finding.Explanation);
    }

    [Fact]
    public void Linked_worktrees_and_repos_without_remotes_are_not_duplicates()
    {
        var linked = Repo("widget-wt", GitOf(with: g => g with { IsLinkedWorktree = true, CommonDir = "/dev/widget/.git" }));
        var context = Context(Machine("mac",
            Repo("widget"), linked,
            Repo("scratch-a", GitOf(remote: null)), Repo("scratch-b", GitOf(remote: null))));

        Assert.Empty(_rule.Evaluate(context));
    }
}

public class CleanupEngineTests
{
    [Fact]
    public void Has_a_rule_for_every_row_of_the_plan_with_unique_ids()
    {
        var engine = new CleanupEngine();

        Assert.Equal(14, engine.Rules.Count);
        Assert.Equal(engine.Rules.Count, engine.Rules.Select(r => r.Id).Distinct().Count());
        Assert.All(engine.Rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Title)));
    }

    [Fact]
    public void Fingerprints_are_unique_and_stable_across_evaluations()
    {
        var context = Context(Machine("mac",
            Repo("widget", GitOf(with: g => g with
            {
                Unstaged = 1,
                NewestTrackedChangeAt = DaysAgo(30),
                Branches = [Branch("main", "origin/main", ahead: 2, merged: true, current: true), Branch("old", merged: true), Branch("gone", "origin/gone", gone: true)],
            })),
            Folder("docs"),
            Folder("x.worktrees", EntryKind.WorktreeContainer)));

        var engine = new CleanupEngine();
        var first = engine.Evaluate(context);
        var second = engine.Evaluate(context with { Now = Now.AddHours(3) });

        Assert.True(first.Count >= 6);
        Assert.Equal(first.Count, first.Select(f => f.Fingerprint).Distinct().Count());
        Assert.Equal(first.Select(f => f.Fingerprint), second.Select(f => f.Fingerprint));
    }

    [Fact]
    public void An_empty_context_produces_nothing() => Assert.Empty(new CleanupEngine().Evaluate(new CleanupContext()));
}
