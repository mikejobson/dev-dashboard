using System.Net;
using DevDash.Contracts;
using DevDash.Core.GitHub;
using DevDash.Hub.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DevDash.Hub.Tests;

/// <summary>The branch map's model: joining devices by commit, who has what, rows, lanes and folding.</summary>
public class BranchGraphTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A sha that reads as its name: "t3" becomes "0000…t3" in hex-safe form.</summary>
    private static string Sha(string name) => string.Concat(name.Select(c => ((int)c).ToString("x2"))).PadLeft(40, '0');

    private static string NameOf(MapRow row) => row.Commit is null ? row.Kind.ToString() : Name(row.Commit.Sha);

    private static string Name(string sha) => string.Concat(Enumerable.Range(0, 20).Select(i => Convert.ToInt32(sha.Substring(i * 2, 2), 16)).Where(v => v != 0).Select(v => (char)v));

    private static int _age;

    private static GraphCommit C(string name, string? parents = null, int? pos = null, bool unpushed = false, bool inTrunk = false) => new()
    {
        Sha = Sha(name),
        Parents = (parents ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Sha).ToList(),
        Date = Now.AddMinutes(-Interlocked.Increment(ref _age)),
        Author = "Sam",
        Subject = name,
        TrunkPos = pos,
        Unpushed = unpushed,
        InTrunk = inTrunk,
    };

    /// <summary>A default branch t1..tN, newest first, each the first parent of the one above.</summary>
    private static List<GraphCommit> Trunk(int from, int to) =>
        Enumerable.Range(from, to - from + 1).Reverse().Select(i => C($"t{i}", i > 1 ? $"t{i - 1}" : null, pos: i)).ToList();

    private static BranchInfo Branch(string name, string at, string? upstream = null, bool merged = false, int ahead = 0, bool gone = false) => new()
    {
        Name = name, Sha = Sha(at), Upstream = upstream, MergedIntoDefault = merged, Ahead = ahead, UpstreamGone = gone,
    };

    private static CloneView Clone(string machine, string head, IEnumerable<GraphCommit>? commits, IEnumerable<BranchInfo> branches,
        string? remotes = null, bool detached = false, int unstaged = 0, string path = "/dev/widget", IEnumerable<StashInfo>? stashes = null, bool linked = false, string? onBranch = null)
    {
        var list = branches.ToList();
        var current = detached ? null : onBranch ?? list.FirstOrDefault(b => b.Sha == Sha(head))?.Name;
        var git = new GitState
        {
            Branch = current,
            Detached = detached,
            HeadSha = Sha(head),
            DefaultBranch = "main",
            CommonDir = "/dev/widget/.git",
            IsLinkedWorktree = linked,
            Unstaged = unstaged,
            Branches = list,
            Stashes = (stashes ?? []).ToList(),
            LastFetchAt = Now.AddHours(-1),
            Graph = commits is null ? null : new CommitGraph
            {
                TrunkRef = "origin/main",
                Commits = commits.ToList(),
                RemoteRefs = (remotes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(r => r.Split('=')).Select(r => new GraphRef { Name = r[0], Sha = Sha(r[1]) }).ToList(),
            },
        };
        var snapshot = new MachineSnapshot { Machine = new MachineInfo { MachineId = machine, Hostname = machine + ".local", AgentVersion = commits is null ? "0.3.0" : "0.4.0" } };
        return new CloneView(new MachineView(snapshot, Now, Now), new DevEntry { Root = "/dev", Name = path.Split('/')[^1], Path = path, Kind = EntryKind.GitRepo, Git = git }, null);
    }

    private static MapRow Row(BranchMap map, string name) => map.Rows.Single(r => r.Commit?.Sha == Sha(name));

    private static List<string> Names(BranchMap map) => map.Rows.Select(NameOf).ToList();

    /// <summary>Every commit comes above all of its parents that are in the map.</summary>
    private static void AssertChildrenFirst(BranchMap map)
    {
        var at = map.Rows.Select((r, i) => (r, i)).Where(x => x.r.Commit is not null).ToDictionary(x => x.r.Commit!.Sha, x => x.i);
        foreach (var row in map.Rows.Where(r => r.Commit is not null))
        {
            foreach (var parent in row.Commit!.Parents.Where(at.ContainsKey))
            {
                Assert.True(at[row.Commit.Sha] < at[parent], $"{Name(row.Commit.Sha)} must be above {Name(parent)}");
            }
        }
    }

    [Fact]
    public void One_device_draws_the_trunk_in_lane_zero_and_a_branch_beside_it_right_above_its_fork()
    {
        var commits = Trunk(1, 4).Concat([C("f2", "f1"), C("f1", "t2")]);
        var map = BranchGraph.Build([Clone("mac", "f2", commits, [Branch("main", "t4", "origin/main"), Branch("feature", "f2")], "origin/main=t4")], []);

        Assert.Equal(["t4", "t3", "f2", "f1", "t2", "t1"], Names(map));
        AssertChildrenFirst(map);
        Assert.Equal(2, map.LaneCount);
        Assert.All(map.Rows.Where(r => r.OnTrunk), r => Assert.Equal(0, r.Lane));
        Assert.Equal(1, Row(map, "f2").Lane);
        Assert.Equal(1, Row(map, "f1").Lane);

        // The fork point takes the branch's lane in from above; rows between pass the trunk through.
        Assert.Equal([1], Row(map, "t2").In.Select(l => l.Lane));
        Assert.Equal([0], Row(map, "f1").Through.Select(l => l.Lane));
        Assert.False(Row(map, "f2").Up);
        Assert.NotNull(Row(map, "f2").Down);
        Assert.Null(Row(map, "t1").Down);
        Assert.False(Row(map, "t1").Stub);

        var head = Row(map, "f2");
        Assert.Equal("feature", head.Refs.Single().Name);
        Assert.Equal([0], head.Refs.Single().HeadOf);
        Assert.Single(head.Heads);
        Assert.Equal(["main", "origin/main"], Row(map, "t4").Refs.Select(r => r.Name));
        Assert.True(Row(map, "t4").Refs.All(r => r.IsDefault));
    }

    [Fact]
    public void Commits_take_the_colour_of_the_only_device_that_has_them()
    {
        // The studio has a branch the laptop has never seen; the laptop last fetched when main was at t2.
        var studio = Clone("studio", "f1", Trunk(1, 3).Concat([C("f1", "t3", unpushed: true)]), [Branch("main", "t3", "origin/main"), Branch("feature", "f1")], "origin/main=t3");
        var laptop = Clone("laptop", "t2", Trunk(1, 2), [Branch("main", "t2", "origin/main")], "origin/main=t2");

        var map = BranchGraph.Build([studio, laptop], []);

        Assert.Equal(["laptop", "studio"], map.Sources.Select(s => s.Label));
        var (lap, stu) = (0, 1);
        Assert.Equal(stu, Row(map, "f1").Color);
        Assert.Equal([MapPresence.None, MapPresence.Local], Row(map, "f1").Presence);
        Assert.True(Row(map, "f1").Unpushed);
        Assert.Equal(stu, Row(map, "t3").Color);
        Assert.Equal(MapColor.Shared, Row(map, "t2").Color);
        Assert.Equal([MapPresence.Local, MapPresence.Local], Row(map, "t1").Presence);
        Assert.Equal(1, map.Sources[stu].Unpushed);
        Assert.Equal(0, map.Sources[lap].Unpushed);

        // origin/main sits on two commits: each device's view of it, with when it fetched.
        var split = map.Rows.SelectMany(r => r.Refs).Where(r => r.Kind == MapRefKind.Remote).ToList();
        Assert.Equal(2, split.Count);
        Assert.All(split, r => Assert.True(r.Split));
        Assert.All(split, r => Assert.NotNull(r.FetchedAt));

        // main is simply behind on the laptop: said, but not as a divergence.
        var warning = Assert.Single(map.Warnings);
        Assert.Equal("main", warning.Branch);
        Assert.False(warning.Diverged);
        Assert.Equal("studio is 1 commit ahead of laptop.", warning.Text);
        Assert.All(map.Rows.SelectMany(r => r.Refs).Where(r => r.Name == "main"), r => Assert.True(r is { Split: true, Diverged: false }));
    }

    [Fact]
    public void A_commit_fetched_but_on_no_local_branch_is_fetched_only_and_one_on_no_device_is_remote()
    {
        // origin/main is one ahead of every local main, and origin/feature was fetched without being checked out.
        var commits = Trunk(1, 3).Concat([C("f1", "t2")]);
        var map = BranchGraph.Build([Clone("mac", "t2", commits, [Branch("main", "t2", "origin/main"), Branch("x", "t1", "origin/feature")], "origin/main=t3 origin/feature=f1")], []);

        Assert.Equal(MapColor.Remote, Row(map, "t3").Color);
        Assert.Equal([MapPresence.Fetched], Row(map, "t3").Presence);
        Assert.Equal([MapPresence.Fetched], Row(map, "f1").Presence);
        Assert.Equal([MapPresence.Local], Row(map, "t2").Presence);
        Assert.False(Row(map, "f1").Unpushed);
    }

    [Fact]
    public void A_commit_one_device_calls_unpushed_is_pushed_once_another_has_fetched_it()
    {
        var studio = Clone("studio", "f1", Trunk(1, 2).Concat([C("f1", "t2", unpushed: true)]), [Branch("main", "t2"), Branch("feature", "f1", "origin/feature")], "origin/main=t2");
        var laptop = Clone("laptop", "t2", Trunk(1, 2).Concat([C("f1", "t2")]), [Branch("main", "t2")], "origin/main=t2 origin/feature=f1");

        var both = BranchGraph.Build([studio, laptop], []);
        var alone = BranchGraph.Build([studio], []);

        Assert.False(Row(both, "f1").Unpushed);
        Assert.Equal([MapPresence.Fetched, MapPresence.Local], Row(both, "f1").Presence);
        Assert.True(Row(alone, "f1").Unpushed);
    }

    [Fact]
    public void The_same_branch_at_unrelated_commits_on_two_devices_is_a_divergence()
    {
        var studio = Clone("studio", "a1", Trunk(1, 2).Concat([C("a1", "t2", unpushed: true)]), [Branch("main", "t2"), Branch("fix", "a1")], "origin/main=t2");
        var laptop = Clone("laptop", "b2", Trunk(1, 2).Concat([C("b2", "b1", unpushed: true), C("b1", "t2", unpushed: true)]), [Branch("main", "t2"), Branch("fix", "b2")], "origin/main=t2");

        var map = BranchGraph.Build([studio, laptop], []);

        var warning = Assert.Single(map.Warnings);
        Assert.True(warning.Diverged);
        Assert.Equal("fix", warning.Branch);
        Assert.Contains("laptop has 2 commits", warning.Text);
        Assert.Contains("studio has 1 commit ", warning.Text);
        Assert.True(Row(map, "a1").Refs.Single().Diverged);
        Assert.True(Row(map, "b2").Refs.Single().Diverged);
        Assert.NotEqual(Row(map, "a1").Lane, Row(map, "b2").Lane);
        Assert.Equal(["main"], Row(map, "t2").Refs.Where(r => r.Kind == MapRefKind.Local).Select(r => r.Name));
        Assert.Equal([0, 1], Row(map, "t2").Refs.First(r => r.Kind == MapRefKind.Local).Sources);
        AssertChildrenFirst(map);
    }

    [Fact]
    public void Missing_stretches_of_the_default_branch_become_gap_rows_with_a_count()
    {
        // Context t28..t30, a fork point at t10, and nothing in between or below.
        var commits = Trunk(28, 30).Concat([C("t10", "t9", pos: 10), C("old", "t10")]);
        var map = BranchGraph.Build([Clone("mac", "t30", commits, [Branch("main", "t30", "origin/main"), Branch("old", "old")], "origin/main=t30")], []);

        Assert.Equal(["t30", "t29", "t28", "Gap", "old", "t10", "Gap"], Names(map));
        var gap = map.Rows[3];
        Assert.Equal(17, gap.Hidden);
        Assert.False(gap.Older);
        Assert.Equal(0, gap.Lane);
        Assert.True(gap.Up);
        Assert.NotNull(gap.Down);
        var older = map.Rows[^1];
        Assert.True(older.Older);
        Assert.Equal(9, older.Hidden);
        Assert.Null(older.Down);

        // The device has every commit below its branch tip, including the ones nobody sent.
        Assert.Equal([MapPresence.Local], gap.Presence);
        Assert.Equal([MapPresence.Local], Row(map, "t10").Presence);
        Assert.Equal([0], Row(map, "old").Through.Select(l => l.Lane));
    }

    [Fact]
    public void Long_quiet_runs_fold_and_unfold()
    {
        var commits = Trunk(1, 12);
        CloneView[] clones = [Clone("mac", "t12", commits, [Branch("main", "t12", "origin/main")], "origin/main=t12")];

        var map = BranchGraph.Build(clones, []);

        // t12 carries the names and t1 ends the line; the ten between are a run: two stay, one fold, one stays.
        Assert.Equal(["t12", "t11", "t10", "Fold", "t2", "t1"], Names(map));
        var fold = map.Rows[3];
        Assert.Equal(7, fold.Hidden);
        Assert.Equal(0, fold.Lane);
        Assert.True(map.HasFolds);

        var opened = BranchGraph.Build(clones, [], new BranchMapOptions { Expanded = new HashSet<string> { fold.Key } });
        Assert.Equal(12, opened.Rows.Count);
        var all = BranchGraph.Build(clones, [], new BranchMapOptions { ExpandAll = true });
        Assert.Equal(12, all.Rows.Count);
        Assert.True(all.HasFolds);

        // A run shorter than the threshold is left alone.
        var few = BranchGraph.Build([Clone("mac", "t6", Trunk(1, 6), [Branch("main", "t6")], "origin/main=t6")], []);
        Assert.DoesNotContain(few.Rows, r => r.Kind == MapRowKind.Fold);
        Assert.False(few.HasFolds);
    }

    [Fact]
    public void A_merge_sends_a_line_out_to_the_merged_branch_which_rejoins_at_its_fork()
    {
        var commits = new[] { C("t3", "t2 s2", pos: 3), C("s2", "s1", inTrunk: true), C("s1", "t1", inTrunk: true), C("t2", "t1", pos: 2), C("t1", pos: 1) };
        var map = BranchGraph.Build([Clone("mac", "t3", commits, [Branch("main", "t3", "origin/main"), Branch("topic", "s2", merged: true)], "origin/main=t3")], []);

        Assert.Equal(["t3", "s2", "s1", "t2", "t1"], Names(map));
        AssertChildrenFirst(map);
        var merge = Row(map, "t3");
        Assert.True(merge.IsMerge);
        Assert.Equal([1], merge.Out.Select(l => l.Lane));
        Assert.Equal(1, Row(map, "s2").Lane);
        Assert.True(Row(map, "s2").Up);
        Assert.Equal([1], Row(map, "t1").In.Select(l => l.Lane));
        Assert.True(Row(map, "s2").Dim);
        Assert.True(Row(map, "s2").Refs.Single().Merged);
        Assert.Equal(1, map.MergedBranches);

        // Hiding merged branches takes the name away; the commits stay because the merge still leads to them.
        var hidden = BranchGraph.Build([Clone("mac", "t3", commits, [Branch("main", "t3", "origin/main"), Branch("topic", "s2", merged: true)], "origin/main=t3")], [], new BranchMapOptions { HideMerged = true });
        Assert.Empty(Row(hidden, "s2").Refs);
        Assert.Equal(1, hidden.MergedBranches);
    }

    [Fact]
    public void Hiding_merged_branches_drops_the_old_commits_only_they_point_at()
    {
        var commits = Trunk(28, 30).Concat([C("t10", "t9", pos: 10)]);
        CloneView[] clones = [Clone("mac", "t30", commits, [Branch("main", "t30", "origin/main"), Branch("done", "t10", merged: true)], "origin/main=t30")];

        Assert.Contains("t10", Names(BranchGraph.Build(clones, [])));
        var hidden = BranchGraph.Build(clones, [], new BranchMapOptions { HideMerged = true });
        Assert.Equal(["t30", "t29", "t28", "Gap"], Names(hidden));
        Assert.Equal(27, hidden.Rows[^1].Hidden);
    }

    [Fact]
    public void Uncommitted_changes_are_a_ghost_row_on_top_of_the_checked_out_commit()
    {
        var commits = Trunk(1, 3).Concat([C("f1", "t3", unpushed: true)]);
        var onTrunk = BranchGraph.Build([Clone("mac", "t3", Trunk(1, 3), [Branch("main", "t3", "origin/main")], "origin/main=t3", unstaged: 2)], []);
        var onBranch = BranchGraph.Build([Clone("mac", "f1", commits, [Branch("main", "t3", "origin/main"), Branch("feature", "f1")], "origin/main=t3", unstaged: 2)], []);

        Assert.Equal(MapRowKind.Ghost, onTrunk.Rows[0].Kind);
        Assert.Equal(0, onTrunk.Rows[0].Lane); // nothing else is above the trunk's tip, so it continues the trunk's lane
        Assert.Equal(MapLineStyle.Dashed, onTrunk.Rows[0].Down!.Style);
        Assert.Equal(2, onTrunk.Rows[0].Tree!.Unstaged);
        Assert.True(Row(onTrunk, "t3").Up);
        Assert.Equal(1, onTrunk.Sources[0].Dirty);

        Assert.Equal(["Ghost", "f1", "t3", "t2", "t1"], Names(onBranch));
        Assert.Equal(Row(onBranch, "f1").Lane, onBranch.Rows[0].Lane);
        Assert.Equal(0, onBranch.Rows[0].Color);
    }

    [Fact]
    public void Unpushed_commits_on_the_default_branch_continue_the_trunk_lane()
    {
        var commits = Trunk(1, 3).Concat([C("u2", "u1", unpushed: true), C("u1", "t3", unpushed: true), C("f1", "t3")]);
        var map = BranchGraph.Build([Clone("mac", "u2", commits, [Branch("main", "u2", "origin/main", ahead: 2), Branch("feature", "f1")], "origin/main=t3")], []);

        Assert.Equal(0, Row(map, "u2").Lane);
        Assert.Equal(0, Row(map, "u1").Lane);
        Assert.False(Row(map, "u2").OnTrunk);
        Assert.Equal(1, Row(map, "f1").Lane);
        Assert.Equal(2, Row(map, "u2").Refs.Single().Ahead);
        AssertChildrenFirst(map);
    }

    [Fact]
    public void Linked_worktrees_detached_heads_and_stashes_are_pinned_to_their_commits()
    {
        var commits = Trunk(1, 3).Concat([C("h1", "t3")]).ToList();
        BranchInfo[] branches = [Branch("main", "t3", "origin/main"), Branch("hotfix", "h1")];
        var main = Clone("mac", "t3", commits, branches, "origin/main=t3", stashes: [new StashInfo { Index = 0, Sha = Sha("st"), BaseSha = Sha("t2"), Message = "On main: parked" }]);
        var hotfix = Clone("mac", "h1", null, branches, path: "/dev/widget.worktrees/hotfix", linked: true, unstaged: 1);
        var review = Clone("mac", "t1", null, branches, path: "/dev/widget.worktrees/review", linked: true, detached: true);

        var map = BranchGraph.Build([hotfix, main, review], []);

        var source = Assert.Single(map.Sources); // one repository, three working trees
        Assert.True(source.HasGraph);
        Assert.Equal("mac", source.Label);
        Assert.Equal([0], Row(map, "h1").Refs.Single(r => r.Name == "hotfix").HeadOf);
        Assert.Equal([0], Row(map, "t3").Refs.Single(r => r.Name == "main").HeadOf);
        var detached = Row(map, "t1").Refs.Single();
        Assert.Equal(MapRefKind.Detached, detached.Kind);
        Assert.Equal("review", detached.Tree);
        Assert.Equal("On main: parked", Row(map, "t2").Stashes.Single().Stash.Message);
        var ghost = map.Rows.Single(r => r.Kind == MapRowKind.Ghost);
        Assert.Equal("hotfix", ghost.Tree!.Label);
        Assert.True(ghost.Tree.Linked);
    }

    [Fact]
    public void A_device_with_an_old_agent_is_listed_and_its_branches_go_where_another_device_knows_the_commit()
    {
        var studio = Clone("studio", "t3", Trunk(1, 3), [Branch("main", "t3", "origin/main")], "origin/main=t3");
        var old = Clone("oldbox", "t2", null, [Branch("main", "t2", "origin/main"), Branch("mystery", "zz")]);

        var map = BranchGraph.Build([studio, old], []);

        var box = map.Sources.Single(s => s.MachineId == "oldbox");
        Assert.False(box.HasGraph);
        Assert.Equal("0.3.0", box.AgentVersion);
        Assert.Equal(1, box.Unplaced);
        Assert.True(map.HasGraph);
        Assert.Equal([0], Row(map, "t2").Refs.Single().Sources);
        Assert.Equal(MapPresence.Local, Row(map, "t2").Presence[0]);
        Assert.Equal(MapPresence.None, Row(map, "t3").Presence[0]);

        var none = BranchGraph.Build([old], []);
        Assert.False(none.HasGraph);
        Assert.Empty(none.Rows);
        Assert.Empty(BranchGraph.Build([], []).Sources);
    }

    [Fact]
    public void Switching_a_device_off_removes_its_names_and_the_commits_only_it_has()
    {
        var studio = Clone("studio", "f1", Trunk(1, 2).Concat([C("f1", "t2", unpushed: true)]), [Branch("main", "t2", "origin/main"), Branch("feature", "f1")], "origin/main=t2");
        var laptop = Clone("laptop", "t2", Trunk(1, 2), [Branch("main", "t2", "origin/main")], "origin/main=t2");
        var full = BranchGraph.Build([studio, laptop], []);
        var studioId = full.Sources.Single(s => s.MachineId == "studio").Id;

        var map = BranchGraph.Build([studio, laptop], [], new BranchMapOptions { HiddenSources = new HashSet<string> { studioId } });

        Assert.Equal(["t2", "t1"], Names(map));
        Assert.False(map.Sources.Single(s => s.MachineId == "studio").Visible);
        Assert.Equal(2, map.Sources.Count);
        Assert.Equal(full.Sources.Select(s => s.Color), map.Sources.Select(s => s.Color)); // colours do not shuffle
        Assert.Equal([0], Row(map, "t2").Refs.First().Sources);
    }

    [Fact]
    public void Device_colours_follow_the_hub_wide_machine_order_and_a_second_clone_gets_its_own()
    {
        var a = Clone("zeta", "t1", Trunk(1, 1), [Branch("main", "t1")]);
        var b = Clone("zeta", "t1", Trunk(1, 1), [Branch("main", "t1")], path: "/dev/widget-copy") with { };
        b = b with { Entry = b.Entry with { Git = b.Entry.Git! with { CommonDir = "/dev/widget-copy/.git" } } };

        var map = BranchGraph.Build([a, b], [], new BranchMapOptions { MachineOrder = ["alpha", "beta", "zeta"] });

        Assert.Equal([2, 3], map.Sources.Select(s => s.Color));
        Assert.Equal(["zeta · widget", "zeta · widget-copy"], map.Sources.Select(s => s.Label));
        Assert.Equal(MapColor.Shared, Row(map, "t1").Color);
    }

    [Fact]
    public void Pull_requests_sit_on_the_commit_their_branch_points_at()
    {
        static PullRequestInfo Pr(int number, string head) => new() { Repo = "acme/widget", Number = number, HeadRef = head, Url = $"https://github.com/acme/widget/pull/{number}" };
        var commits = Trunk(1, 2).Concat([C("f2", "f1", unpushed: true), C("f1", "t2"), C("g1", "t2", unpushed: true)]);
        var map = BranchGraph.Build(
            [Clone("mac", "f2", commits, [Branch("main", "t2", "origin/main"), Branch("feature", "f2", "origin/feature", ahead: 1), Branch("local-only", "g1")], "origin/main=t2 origin/feature=f1")],
            [Pr(1, "feature"), Pr(2, "local-only"), Pr(3, "someone-elses"), Pr(4, "main/feature")]);

        Assert.Equal(1, Row(map, "f1").PullRequests.Single().Number); // on what was pushed, not on the unpushed tip
        Assert.Empty(Row(map, "f2").PullRequests);
        Assert.Equal(2, Row(map, "g1").PullRequests.Single().Number);
        Assert.Equal([3, 4], map.UnplacedPullRequests.Select(p => p.Number));
    }

    [Fact]
    public void A_branch_whose_history_was_cut_off_ends_in_a_stub()
    {
        var commits = Trunk(1, 2).Concat([C("x2", "x1"), C("x1", "x0")]); // x0 was not sent
        var map = BranchGraph.Build([Clone("mac", "x2", commits, [Branch("main", "t2", "origin/main"), Branch("deep", "x2")], "origin/main=t2")], []);

        Assert.True(Row(map, "x1").Stub);
        Assert.Null(Row(map, "x1").Down);
        Assert.False(Row(map, "x2").Stub);
        AssertChildrenFirst(map);
    }

    [Fact]
    public async Task The_repo_page_draws_the_map_and_says_which_device_needs_a_newer_agent()
    {
        using var factory = new HubFactory();
        var store = factory.Services.GetRequiredService<SnapshotStore>();

        // Before any device sends a graph, the page asks for an agent update instead of drawing nothing.
        await store.IngestAsync(HubFactory.Snapshot("oldbox"));
        var before = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/repo?key=github.com%2Facme%2Fwidget"));
        Assert.Contains("Branch map", before);
        Assert.Contains("No device has sent this repository's history yet", before);

        var snapshot = HubFactory.Snapshot("studio", version: "0.4.0");
        var widget = snapshot.Entries[0];
        var commits = Trunk(1, 3).Concat([C("login", "t3", unpushed: true)]).ToList();
        var git = widget.Git! with
        {
            HeadSha = Sha("login"),
            Branches = [Branch("feature/login", "login", "origin/feature/login", ahead: 1), Branch("main", "t3", "origin/main")],
            Graph = new CommitGraph { TrunkRef = "origin/main", Commits = commits, RemoteRefs = [new GraphRef { Name = "origin/main", Sha = Sha("t3") }] },
        };
        await store.IngestAsync(snapshot with { Entries = [widget with { Git = git }] });

        var html = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/repo?key=github.com%2Facme%2Fwidget"));

        Assert.Contains("2 devices", html);
        Assert.Contains("studio-host", html);
        Assert.Contains("bm-ref local", html);
        Assert.Contains("feature/login", html);
        Assert.Contains("origin/main", html);
        Assert.Contains("uncommitted", html);
        Assert.Contains("unpushed", html);
        Assert.Contains("<svg", html);
        Assert.Contains("The agent on oldbox-host (0.1.0) is too old to send its commits.", html.Replace("<b>", "").Replace("</b>", ""));
        Assert.DoesNotContain("No device has sent", html);
    }

    [Fact]
    public void A_wide_history_stays_ordered_and_every_lane_line_is_accounted_for()
    {
        // Forty branches of three commits each, forking from different places on a trunk of sixty, from two devices.
        var trunk = Trunk(1, 60);
        var side = new List<GraphCommit>();
        var branches = new List<BranchInfo> { Branch("main", "t60", "origin/main") };
        for (var b = 0; b < 40; b++)
        {
            var fork = 1 + (b * 7 % 59);
            side.AddRange([C($"b{b}c", $"b{b}b", unpushed: b % 3 == 0), C($"b{b}b", $"b{b}a"), C($"b{b}a", $"t{fork}")]);
            branches.Add(Branch($"branch-{b}", $"b{b}c"));
        }

        var studio = Clone("studio", "t60", trunk.Concat(side), branches, "origin/main=t60");
        var laptop = Clone("laptop", "t40", trunk.Skip(20).Concat(side.Take(30)), branches.Take(11).Skip(1).Append(Branch("main", "t40", "origin/main")), "origin/main=t40");

        var map = BranchGraph.Build([studio, laptop], [], new BranchMapOptions { ExpandAll = true });

        Assert.Equal(60 + 120, map.Rows.Count);
        AssertChildrenFirst(map);
        Assert.InRange(map.LaneCount, 2, 4); // each branch is drawn right above its fork, so lanes are reused
        for (var i = 0; i < map.Rows.Count; i++)
        {
            var row = map.Rows[i];
            var lanes = row.Through.Select(l => l.Lane).Concat(row.In.Select(l => l.Lane)).Append(row.Lane).ToList();
            Assert.Equal(lanes.Count, lanes.Distinct().Count());
            Assert.All(lanes, lane => Assert.InRange(lane, 0, map.LaneCount - 1));

            // A lane that leaves a row arrives in the next one: as the node's own, as a line passing, or as a line turning in.
            if (i + 1 < map.Rows.Count)
            {
                var next = map.Rows[i + 1];
                var leaving = row.Through.Select(l => l.Lane).Concat(row.Out.Select(l => l.Lane)).Concat(row.Down is null ? [] : [row.Lane]).Distinct().Order().ToList();
                var arriving = next.Through.Where(l => !next.Out.Any(o => o.Lane == l.Lane) || leaving.Contains(l.Lane)).Select(l => l.Lane).Concat(next.In.Select(l => l.Lane)).Concat(next.Up ? [next.Lane] : []).Distinct().Order().ToList();
                Assert.Equal(leaving, arriving);
            }
        }
    }

    [Theory]
    [InlineData(new[] { "origin/develop", "origin/develop", "origin/main" }, "develop")]
    [InlineData(new[] { "main" }, "main")]
    [InlineData(new string[0], null)]
    public void The_trunk_name_is_what_most_devices_draw_along(string[] refs, string? expected) =>
        Assert.Equal(expected, BranchGraph.TrunkNameOf(refs));
}
