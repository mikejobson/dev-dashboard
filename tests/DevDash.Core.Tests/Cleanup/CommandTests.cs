using DevDash.Contracts;
using DevDash.Core.Cleanup;
using DevDash.Core.Cleanup.Rules;
using static DevDash.Core.Tests.Cleanup.Build;

namespace DevDash.Core.Tests.Cleanup;

/// <summary>The commands each rule suggests: inspect before fix, correct when a branch is behind, and which ones an agent may run.</summary>
public class UnpushedCommandTests
{
    private readonly UnpushedCommitsRule _rule = new();

    private Finding For(GitState git, string title) =>
        _rule.Evaluate(Context(Machine("mac", Repo("widget", git)))).Single(f => f.Title == title);

    [Fact]
    public void A_branch_that_is_only_ahead_shows_its_commits_then_pushes()
    {
        var git = GitOf(with: g => g with { Branches = [Branch("main", "origin/main", merged: true, current: true), Branch("ahead", "origin/ahead", ahead: 3)] });

        var finding = For(git, "widget: 3 unpushed commits on ahead");

        Assert.Equal(["git -C '/dev/widget' log --oneline 'origin/ahead'..'ahead'"], Inspects(finding));
        var push = Fix(finding);
        Assert.Equal("git -C '/dev/widget' push 'origin' 'ahead'", push.Command);
        Assert.Equal(AgentAction.Push("/dev/widget", "origin", "ahead"), push.Action);
    }

    [Fact]
    public void A_branch_that_is_also_behind_is_rebased_before_the_push_and_is_never_offered_as_a_plain_push()
    {
        // "26 ahead and 69 behind": a plain push would be rejected.
        var git = GitOf(with: g => g with { Branches = [Branch("main", "origin/main", ahead: 26, behind: 69, merged: true, current: true)] });

        var finding = For(git, "widget: 26 unpushed commits on main");

        Assert.Contains("69 behind", finding.Explanation);
        Assert.Equal(
            ["git -C '/dev/widget' fetch --prune", "git -C '/dev/widget' log --oneline --left-right 'origin/main'...'main'"],
            Inspects(finding));
        // main is checked out in /dev/widget, so it can be rebased in place.
        Assert.Equal(
            ["git -C '/dev/widget' pull --rebase", "git -C '/dev/widget' push 'origin' 'main'"],
            Fixes(finding));

        // Nothing that rewrites or pushes is handed to an agent while the branch is behind; only the fetch is.
        Assert.All(finding.Commands.Where(c => c.Kind == CommandKind.Fix), c => Assert.Null(c.Action));
        Assert.Equal(AgentActionKind.Fetch, finding.Commands.Single(c => c.Action is not null).Action!.Kind);
    }

    [Fact]
    public void A_behind_branch_that_is_not_checked_out_is_switched_to_first()
    {
        var git = GitOf(with: g => g with { Branches = [Branch("main", "origin/main", merged: true, current: true), Branch("diverged", "origin/diverged", ahead: 1, behind: 4)] });

        var finding = For(git, "widget: 1 unpushed commit on diverged");

        Assert.Equal(
            ["git -C '/dev/widget' switch 'diverged' && git -C '/dev/widget' pull --rebase", "git -C '/dev/widget' push 'origin' 'diverged'"],
            Fixes(finding));
        Assert.StartsWith("Switch to diverged", finding.Commands.First(c => c.Kind == CommandKind.Fix).Label);
    }

    [Fact]
    public void A_behind_branch_checked_out_in_a_linked_worktree_is_rebased_there()
    {
        var git = GitOf(with: g => g with
        {
            Branches = [Branch("main", "origin/main", merged: true, current: true), Branch("feature", "origin/feature", ahead: 2, behind: 1)],
            Worktrees = [new WorktreeInfo { Path = "/dev/widget", Branch = "main" }, new WorktreeInfo { Path = "/dev/widget.worktrees/feature", Branch = "feature" }],
        });

        var finding = For(git, "widget: 2 unpushed commits on feature");

        Assert.Equal(
            ["git -C '/dev/widget.worktrees/feature' pull --rebase", "git -C '/dev/widget.worktrees/feature' push 'origin' 'feature'"],
            Fixes(finding));
    }

    [Fact]
    public void A_branch_tracking_a_differently_named_branch_pushes_to_that_name_and_is_copy_only()
    {
        var git = GitOf(with: g => g with { Branches = [Branch("main", "origin/main", merged: true, current: true), Branch("local", "origin/theirs", ahead: 1)] });

        var push = Fix(For(git, "widget: 1 unpushed commit on local"));

        Assert.Equal("git -C '/dev/widget' push 'origin' 'local:theirs'", push.Command);
        Assert.Null(push.Action); // the agent's push is same-name only
    }

    [Fact]
    public void A_never_pushed_branch_shows_what_is_new_then_pushes_with_upstream()
    {
        var git = GitOf(with: g => g with { Branches = [Branch("main", "origin/main", merged: true, current: true), Branch("local-only")] });

        var finding = For(git, "widget: local-only has no upstream");

        Assert.Equal(["git -C '/dev/widget' log --oneline 'main'..'local-only'"], Inspects(finding));
        Assert.Equal(AgentAction.Push("/dev/widget", "origin", "local-only", setUpstream: true), Fix(finding).Action);
    }

    [Fact]
    public void Repo_findings_carry_cd_and_code_shortcuts()
    {
        var git = GitOf(with: g => g with { Branches = [Branch("main", "origin/main", ahead: 1, merged: true, current: true)] });

        var finding = Assert.Single(_rule.Evaluate(Context(Machine("mac", Repo("it's mine", git)))));

        Assert.Equal([@"cd '/dev/it'\''s mine'", @"code '/dev/it'\''s mine'"], Of(finding, CommandKind.Open));
    }
}

public class DivergedCommandTests
{
    private readonly DivergedAcrossMachinesRule _rule = new();

    private static DevEntry Clone(string sha, int ahead, int behind = 0) =>
        Repo("widget", GitOf(with: g => g with { HeadSha = sha, Ahead = ahead, Behind = behind }));

    [Fact]
    public void Each_machine_gets_its_own_steps_and_rebases_before_pushing()
    {
        var context = Context(
            Machine("mac", Clone("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 26, 69)),
            Machine("desktop", Clone("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 5)));

        var finding = Assert.Single(_rule.Evaluate(context));

        Assert.Null(finding.MachineId);
        Assert.Contains("26 ahead, 69 behind", finding.Explanation);
        Assert.All(finding.Commands, c => Assert.NotNull(c.MachineId));

        foreach (var machine in new[] { "mac", "desktop" })
        {
            var fixes = finding.Commands.Where(c => c.Kind == CommandKind.Fix && c.MachineId == machine).ToList();
            Assert.Equal(["git -C '/dev/widget' pull --rebase", "git -C '/dev/widget' push 'origin' 'main'"], fixes.Select(c => c.Command));
            Assert.Contains(finding.Commands, c => c.MachineId == machine && c.Kind == CommandKind.Inspect && c.Command.EndsWith("--left-right 'origin/main'...'main'"));
            Assert.Contains(finding.Commands, c => c.MachineId == machine && c.Kind == CommandKind.Open && c.Command == "cd '/dev/widget'");
        }

        // The push is only offered to an agent on the machine that is not known to be behind.
        Assert.Null(finding.Commands.Single(c => c.MachineId == "mac" && c.Command.Contains(" push ")).Action);
        Assert.Equal(AgentAction.Push("/dev/widget", "origin", "main"), finding.Commands.Single(c => c.MachineId == "desktop" && c.Command.Contains(" push ")).Action);
    }
}

public class DestructiveFixCommandTests
{
    [Fact]
    public void Merged_branches_show_their_tips_then_delete_with_the_safe_flag()
    {
        var git = GitOf(with: g => g with { Branches = [Branch("main", "origin/main", merged: true, current: true), Branch("b", merged: true), Branch("a", merged: true)] });

        var finding = Assert.Single(new MergedBranchRule().Evaluate(Context(Machine("mac", Repo("widget", git)))));

        Assert.Equal(["git -C '/dev/widget' log --no-walk --oneline --decorate 'a' 'b'"], Inspects(finding));
        var fix = Fix(finding);
        Assert.Equal("git -C '/dev/widget' branch -d 'a' 'b'", fix.Command);
        Assert.Equal(AgentActionKind.DeleteBranches, fix.Action!.Kind);
        Assert.Equal(["a", "b"], fix.Action.Branches);
        Assert.Equal("/dev/widget", fix.Action.RepoPath);
    }

    [Fact]
    public void Gone_upstream_only_offers_the_safe_delete_to_an_agent()
    {
        var git = GitOf(with: g => g with
        {
            Branches =
            [
                Branch("main", "origin/main", merged: true, current: true),
                Branch("merged", "origin/merged", gone: true, merged: true),
                Branch("squashed", "origin/squashed", gone: true, merged: false),
            ],
        });

        var findings = new GoneUpstreamRule().Evaluate(Context(Machine("mac", Repo("widget", git)))).ToList();

        var merged = findings.Single(f => f.Title.EndsWith("merged"));
        Assert.Equal(["git -C '/dev/widget' log --oneline -10 'merged'"], Inspects(merged));
        Assert.Equal(AgentAction.DeleteBranches("/dev/widget", ["merged"]).Branches, Fix(merged).Action!.Branches);

        var squashed = findings.Single(f => f.Title.EndsWith("squashed"));
        Assert.Equal(
            ["git -C '/dev/widget' log --oneline -10 'squashed'", "git -C '/dev/widget' log --oneline 'main'..'squashed'"],
            Inspects(squashed));
        Assert.Contains("-D", Fix(squashed).Command);
        Assert.Null(Fix(squashed).Action); // a forced delete is never an agent action
    }

    [Fact]
    public void A_checked_out_gone_branch_is_not_offered_to_an_agent()
    {
        var git = GitOf(branch: "feature", with: g => g with { Branches = [Branch("feature", "origin/feature", gone: true, merged: true, current: true)] });

        var finding = Assert.Single(new GoneUpstreamRule().Evaluate(Context(Machine("mac", Repo("widget", git)))));

        Assert.Null(Fix(finding).Action);
    }

    [Fact]
    public void Old_stashes_are_shown_then_dropped_highest_index_first_and_pinned_to_their_commit()
    {
        const string sha1 = "1111111111111111111111111111111111111111", sha2 = "2222222222222222222222222222222222222222";
        var git = GitOf(with: g => g with
        {
            Stashes =
            [
                new StashInfo { Index = 0, Date = DaysAgo(2), Message = "On main: fresh" },
                new StashInfo { Index = 1, Sha = sha1, Date = DaysAgo(45), Message = "WIP on main: older" },
                new StashInfo { Index = 2, Sha = sha2, Date = DaysAgo(70), Message = "WIP on main: oldest" },
                new StashInfo { Index = 3, Date = DaysAgo(90), Message = "from an agent that did not report the commit" },
            ],
        });

        var finding = Assert.Single(new OldStashRule().Evaluate(Context(Machine("mac", Repo("widget", git)))));

        Assert.Equal(
            [
                "git -C '/dev/widget' stash list --date=relative",
                "git -C '/dev/widget' stash show -p 'stash@{3}'",
                "git -C '/dev/widget' stash show -p 'stash@{2}'",
                "git -C '/dev/widget' stash show -p 'stash@{1}'",
            ],
            Inspects(finding));
        // Highest index first: dropping one renumbers only the stashes above it.
        Assert.Equal(
            ["git -C '/dev/widget' stash drop 'stash@{3}'", "git -C '/dev/widget' stash drop 'stash@{2}'", "git -C '/dev/widget' stash drop 'stash@{1}'"],
            Fixes(finding));

        var drops = finding.Commands.Where(c => c.Kind == CommandKind.Fix).ToList();
        Assert.Null(drops[0].Action); // no commit to verify against, so copy-only
        Assert.Equal(AgentAction.DropStash("/dev/widget", 2, sha2), drops[1].Action);
        Assert.Equal(AgentAction.DropStash("/dev/widget", 1, sha1), drops[2].Action);
    }

    [Fact]
    public void Worktrees_are_inspected_from_inside_before_removal_and_dirty_or_detached_ones_are_copy_only()
    {
        var git = GitOf(with: g => g with
        {
            Branches = [Branch("main", "origin/main", merged: true, current: true), Branch("done", merged: true), Branch("dirty", merged: true)],
            Worktrees =
            [
                new WorktreeInfo { Path = "/dev/widget", Branch = "main" },
                new WorktreeInfo { Path = "/dev/widget-done", Branch = "done" },
                new WorktreeInfo { Path = "/dev/widget-dirty", Branch = "dirty" },
                new WorktreeInfo { Path = "/tmp/gone", Detached = true, Prunable = true },
            ],
        });
        var dirty = Repo("widget-dirty", git with { Branch = "dirty", IsLinkedWorktree = true, CommonDir = "/dev/widget/.git", Untracked = 1 });

        var findings = new StaleWorktreeRule().Evaluate(Context(Machine("mac", Repo("widget", git), dirty))).ToList();

        var done = findings.Single(f => f.Title == "Worktree for merged branch done");
        Assert.Equal(
            ["git -C '/dev/widget-done' status --short --branch", "git -C '/dev/widget-done' log --oneline -5"],
            Inspects(done));
        Assert.Equal("git -C '/dev/widget' worktree remove '/dev/widget-done'", Fix(done).Command);
        Assert.Equal(AgentAction.RemoveWorktree("/dev/widget", "/dev/widget-done"), Fix(done).Action);

        Assert.Null(Fix(findings.Single(f => f.Title == "Worktree for merged branch dirty")).Action);

        var prunable = findings.Single(f => f.Title == "Prunable worktree: /tmp/gone");
        Assert.Equal(["git -C '/dev/widget' worktree prune --dry-run --verbose"], Inspects(prunable));
        Assert.Equal(AgentAction.PruneWorktrees("/dev/widget"), Fix(prunable).Action);
    }

    [Fact]
    public void Every_fix_that_deletes_something_has_an_inspect_command_before_it()
    {
        var git = GitOf(with: g => g with
        {
            Branches = [Branch("main", "origin/main", merged: true, current: true), Branch("old", merged: true), Branch("gone", "origin/gone", gone: true)],
            Stashes = [new StashInfo { Index = 0, Date = DaysAgo(99), Message = "old" }],
        });
        var size = new SizeInfo { TotalBytes = 3_000_000_000, ReclaimableBytes = 2_000_000_000, ReclaimableDirs = [new ReclaimableDir { Path = "/dev/widget/node_modules", Bytes = 2_000_000_000 }] };
        var context = Context(Machine("mac",
            Repo("widget", git, size: size),
            Folder("Docs"),
            Folder("notes.txt", EntryKind.LooseFile),
            Folder("old-worktrees", EntryKind.WorktreeContainer)));

        var findings = new CleanupEngine().Evaluate(context);

        Assert.NotEmpty(findings);
        foreach (var finding in findings.Where(f => f.Commands.Any(c => c.Kind == CommandKind.Fix)))
        {
            var firstFix = finding.Commands.ToList().FindIndex(c => c.Kind == CommandKind.Fix);
            Assert.True(finding.Commands.Take(firstFix).Any(c => c.Kind == CommandKind.Inspect), $"{finding.Title} has a fix with nothing to look at first");
        }
    }

    [Fact]
    public void Fingerprints_are_the_same_as_before_commands_became_a_list()
    {
        var git = GitOf(with: g => g with
        {
            Branches = [Branch("main", "origin/main", ahead: 2, merged: true, current: true), Branch("old", merged: true)],
            Stashes = [new StashInfo { Index = 0, Sha = "1111111111111111111111111111111111111111", Date = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), Message = "old" }],
        });
        var findings = new CleanupEngine().Evaluate(Context(Machine("mac", Repo("widget", git), Folder("Docs"))));

        string Of(string rule) => findings.Single(f => f.RuleId == rule).Fingerprint;
        Assert.Equal(Finding.MakeFingerprint("unpushed", "mac", "/dev/widget", "ahead", "main"), Of("unpushed"));
        Assert.Equal(Finding.MakeFingerprint("merged-branch", "mac", "/dev/widget", "old"), Of("merged-branch"));
        Assert.Equal(Finding.MakeFingerprint("old-stash", "mac", "/dev/widget", "1700000000"), Of("old-stash"));
        Assert.Equal(Finding.MakeFingerprint("non-git-folder", "mac", "/dev/Docs"), Of("non-git-folder"));
    }
}

public class CleanupScriptTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 18, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void One_script_per_machine_with_a_header_a_comment_per_finding_and_only_fix_commands()
    {
        var git = GitOf(with: g => g with { Branches = [Branch("main", "origin/main", merged: true, current: true), Branch("old", merged: true)] });
        var rule = new MergedBranchRule();
        var findings = rule.Evaluate(Context(
            Machine("mac", Repo("widget", git), Repo("gadget", git)),
            Machine("desktop", Repo("widget", git)))).ToList();

        var scripts = CleanupScript.Build(rule.Title, findings, At);

        Assert.Equal(["mac", "desktop"], scripts.Select(s => s.MachineName));
        var mac = scripts[0];
        Assert.Equal(2, mac.FindingCount);
        Assert.Equal(2, mac.CommandCount);
        Assert.Equal(
            """
            # DevDash cleanup: Local branches merged into the default branch on mac, generated 2026-11-18 09:30 UTC
            # Read it before you run it. Each command stands alone; delete the ones you do not want.

            # widget: 1 branch merged into main
            git -C '/dev/widget' branch -d 'old'

            # gadget: 1 branch merged into main
            git -C '/dev/gadget' branch -d 'old'

            """.ReplaceLineEndings("\n"),
            mac.Text);
        Assert.DoesNotContain("log --no-walk", mac.Text); // inspect commands stay out
        Assert.DoesNotContain("cd '", mac.Text);
    }

    [Fact]
    public void A_finding_that_spans_machines_is_split_so_no_script_mixes_them()
    {
        var rule = new DivergedAcrossMachinesRule();
        DevEntry Clone(string name, string sha) => Repo(name, GitOf(with: g => g with { HeadSha = sha, Ahead = 1 }));
        var findings = rule.Evaluate(Context(
            Machine("mac", Clone("widget-on-mac", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
            Machine("desktop", Clone("widget-on-desktop", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")))).ToList();

        var scripts = CleanupScript.Build(rule.Title, findings, At);

        Assert.Equal(2, scripts.Count);
        var mac = scripts.Single(s => s.MachineId == "mac");
        var desktop = scripts.Single(s => s.MachineId == "desktop");
        static List<string> Runnable(MachineScript script) => script.Text.Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        Assert.Equal(["git -C '/dev/widget-on-mac' pull --rebase", "git -C '/dev/widget-on-mac' push 'origin' 'main'"], Runnable(mac));
        Assert.Equal(["git -C '/dev/widget-on-desktop' pull --rebase", "git -C '/dev/widget-on-desktop' push 'origin' 'main'"], Runnable(desktop));
    }

    [Fact]
    public void Findings_without_fix_commands_or_without_a_machine_are_handled()
    {
        var prs = new StalePullRequestRule().Evaluate(new CleanupContext { PullRequests = [Pr(1, idleDays: 60)], Now = Now }).ToList();
        Assert.Empty(CleanupScript.Build("Stale PRs", prs, At)); // only inspect commands: nothing to script

        var github = new Finding
        {
            RuleId = "x", Severity = FindingSeverity.Low, Title = "acme/widget#1", Explanation = "", Fingerprint = "f",
            Commands = [new FindingCommand { Label = "Close", Command = "gh pr close 1 --repo 'acme/widget'", Kind = CommandKind.Fix }],
        };
        var script = Assert.Single(CleanupScript.Build("Stale PRs", [github], At));
        Assert.Null(script.MachineId);
        Assert.Equal(CleanupScript.AnyMachine, script.MachineName);
        Assert.StartsWith("# DevDash cleanup: Stale PRs on any machine, generated", script.Text);
    }

    [Fact]
    public void A_line_break_in_a_title_cannot_smuggle_a_command_into_the_script()
    {
        var finding = new Finding
        {
            RuleId = "x", Severity = FindingSeverity.Low, Title = "widget: branch\nrm -rf ~", Explanation = "", Fingerprint = "f", MachineId = "mac", MachineName = "mac\nhost",
            Commands = [new FindingCommand { Label = "Fix", Command = "git -C '/dev/widget' worktree prune", Kind = CommandKind.Fix }],
        };

        var script = Assert.Single(CleanupScript.Build("Rule\ntitle", [finding], At));

        var runnable = script.Text.Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        Assert.Equal(["git -C '/dev/widget' worktree prune"], runnable);
    }
}

public class AgentActionContractTests
{
    public static TheoryData<AgentAction> Valid => new()
    {
        AgentAction.DeleteBranches("/dev/widget", ["feature/a", "b"]),
        AgentAction.RemoveWorktree("/dev/widget", "/dev/widget.worktrees/x"),
        AgentAction.PruneWorktrees("C:\\Users\\me\\Developer\\widget"),
        AgentAction.Fetch("/dev/widget"),
        AgentAction.Push("/dev/widget", "origin", "main", setUpstream: true),
        AgentAction.DropStash("/dev/widget", 2, "1111111111111111111111111111111111111111"),
    };

    [Theory]
    [MemberData(nameof(Valid))]
    public void Well_formed_actions_pass_and_survive_the_wire(AgentAction action)
    {
        Assert.Null(AgentActions.Validate(action));

        var json = System.Text.Json.JsonSerializer.Serialize(action, SnapshotSchema.JsonOptions);
        var back = System.Text.Json.JsonSerializer.Deserialize<AgentAction>(json, SnapshotSchema.JsonOptions)!;
        Assert.Equal(AgentActions.Key(action), AgentActions.Key(back));
        Assert.Equal(AgentActions.GitArgs(action), AgentActions.GitArgs(back));
    }

    [Theory]
    [MemberData(nameof(Valid))]
    public void No_action_can_force_or_rewrite(AgentAction action)
    {
        var args = AgentActions.GitArgs(action);

        Assert.DoesNotContain(args, a => a is "-D" or "-f" or "--force" or "--force-with-lease" or "--hard" or "--mirror" or "--delete" or "--all");
        Assert.Contains(args[0], new[] { "branch", "worktree", "fetch", "push", "stash" });
        Assert.DoesNotContain(args, a => a.StartsWith('+')); // a "+refspec" is a forced push
    }

    [Fact]
    public void The_command_line_shown_to_the_user_is_the_arguments_the_agent_runs()
    {
        Assert.Equal("git -C '/dev/widget' branch -d -- 'feature/a' 'b'", AgentActions.CommandLine(AgentAction.DeleteBranches("/dev/widget", ["feature/a", "b"])));
        Assert.Equal(["branch", "-d", "--", "feature/a", "b"], AgentActions.GitArgs(AgentAction.DeleteBranches("/dev/widget", ["feature/a", "b"])));
        Assert.Equal("git -C '/dev/widget' push -- 'origin' 'refs/heads/main:refs/heads/main'", AgentActions.CommandLine(AgentAction.Push("/dev/widget", "origin", "main")));
        Assert.Equal("git -C '/dev/widget' stash drop 'stash@{2}'", AgentActions.CommandLine(AgentAction.DropStash("/dev/widget", 2, new string('a', 40))));
        Assert.Equal("git -C '/dev/widget' worktree remove -- '/dev/it'\\''s'", AgentActions.CommandLine(AgentAction.RemoveWorktree("/dev/widget", "/dev/it's")));
    }

    public static TheoryData<AgentAction> Malformed => new()
    {
        AgentAction.DeleteBranches("/dev/widget", []),
        AgentAction.DeleteBranches("/dev/widget", ["-D"]),
        AgentAction.DeleteBranches("/dev/widget", ["--force"]),
        AgentAction.DeleteBranches("/dev/widget", ["a b"]),
        AgentAction.DeleteBranches("/dev/widget", ["a..b"]),
        AgentAction.DeleteBranches("/dev/widget", ["a", "a"]),
        AgentAction.DeleteBranches("relative/path", ["a"]),
        AgentAction.DeleteBranches("/dev/wid\nget", ["a"]),
        AgentAction.RemoveWorktree("/dev/widget", "--force"),
        AgentAction.Push("/dev/widget", "--mirror", "main"),
        AgentAction.Push("/dev/widget", "origin", "-main"),
        AgentAction.Push("/dev/widget", "origin", "main:other"),
        AgentAction.DropStash("/dev/widget", -1, new string('a', 40)),
        AgentAction.DropStash("/dev/widget", 0, "abc"),
        AgentAction.Fetch("/dev/widget") with { Branches = ["x"] },
        AgentAction.PruneWorktrees("/dev/widget") with { SetUpstream = true },
        new AgentAction { Kind = (AgentActionKind)99, RepoPath = "/dev/widget" },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Malformed_actions_are_rejected_before_any_repository_is_touched(AgentAction action) =>
        Assert.NotNull(AgentActions.Validate(action));

    [Fact]
    public void Long_output_keeps_its_end()
    {
        var text = AgentActions.Truncate(new string('x', 10_000) + "fatal: the reason");

        Assert.Equal(AgentActions.MaxOutputChars, text.Length);
        Assert.EndsWith("fatal: the reason", text);
    }
}
