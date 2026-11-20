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

        // Hiding merged branches takes the branch away, commits and all: the merge is all that is left of it.
        var hidden = BranchGraph.Build([Clone("mac", "t3", commits, [Branch("main", "t3", "origin/main"), Branch("topic", "s2", merged: true)], "origin/main=t3")], [], new BranchMapOptions { HideMerged = true });
        Assert.Equal(["t3", "t2", "t1"], Names(hidden));
        Assert.Empty(Row(hidden, "t3").Out);
        Assert.Equal(1, hidden.MergedBranches);
    }

    /// <summary>
    /// Stacked pull requests, as an agent sends them: main's merge t3 brings in m2, which merged m1, which
    /// merged the branch tips. Each tip is reachable from main only through second parents.
    /// </summary>
    private static GraphCommit[] StackedMerges() =>
    [
        C("t3", "t2 m2", pos: 3),
        C("m2", "a m1", inTrunk: true),
        C("m1", "b c", inTrunk: true),
        C("c", "c0", inTrunk: true) with { JoinsTrunkAt = Sha("t3") },
        C("b", "t1", inTrunk: true) with { JoinsTrunkAt = Sha("t3") },
        C("a", "t2", inTrunk: true) with { JoinsTrunkAt = Sha("t3") },
        C("t2", "t1", pos: 2),
        C("t1", pos: 1),
    ];

    [Fact]
    public void A_branch_merged_through_a_chain_of_merges_sits_under_the_merge_that_brought_it_in()
    {
        BranchInfo[] branches = [Branch("main", "t3", "origin/main"), Branch("first", "a", merged: true), Branch("second", "b", merged: true), Branch("third", "c", merged: true)];
        var map = BranchGraph.Build([Clone("mac", "t3", StackedMerges(), branches, "origin/main=t3")], []);

        // Everything t3 merged comes right under it, before the trunk goes on: no merged tip is left for the end.
        Assert.Equal(["t3", "m2", "m1", "c", "b", "a", "t2", "t1"], Names(map));
        AssertChildrenFirst(map);
        Assert.All((string[])["m2", "m1", "a", "b", "c"], n => Assert.True(Row(map, n).Dim));
        Assert.All((string[])["m2", "m1", "a", "b", "c"], n => Assert.True(Row(map, n).Up || Row(map, n).In.Count > 0 || map.Rows.Any(r => r.Out.Any(l => l.Lane == Row(map, n).Lane)), n + " hangs off something"));
        Assert.True(Row(map, "c").Stub); // its own history was not sent
        Assert.All(map.Rows.SelectMany(r => r.Out.Concat(r.In).Concat(r.Through)), l => Assert.Equal(MapLineStyle.Solid, l.Style));
        Assert.Equal(3, map.MergedBranches);
    }

    [Fact]
    public void A_merged_branch_that_forks_lower_down_does_not_pull_the_trunk_up_between_the_merged_commits()
    {
        // t3 merges a release branch r3 → r2 → r1 (forked at t2). r3 and r2 each merged a topic that forked at t1.
        var commits = new[]
        {
            C("t3", "t2 r3", pos: 3), C("r3", "r2 x", inTrunk: true), C("r2", "r1 y", inTrunk: true), C("r1", "t2", inTrunk: true),
            C("x", "t1", inTrunk: true), C("y", "t1", inTrunk: true), C("t2", "t1", pos: 2), C("t1", pos: 1),
        };
        var map = BranchGraph.Build([Clone("mac", "t3", commits, [Branch("main", "t3", "origin/main"), Branch("topic-x", "x", merged: true), Branch("topic-y", "y", merged: true)], "origin/main=t3")], []);

        Assert.Equal(["t3", "r3", "x", "r2", "y", "r1", "t2", "t1"], Names(map));
        AssertChildrenFirst(map);
    }

    [Fact]
    public void A_merged_tip_with_no_line_to_the_trunk_is_placed_by_its_date_not_after_the_trunk()
    {
        // An older agent sends a merged tip on its own: nothing leads to it. Dates run t4, t3, old, t2, t1.
        var commits = new[] { C("t4", "t3", pos: 4), C("t3", "t2", pos: 3), C("old", "o0", inTrunk: true), C("t2", "t1", pos: 2), C("t1", pos: 1) };
        var map = BranchGraph.Build([Clone("mac", "t4", commits, [Branch("main", "t4", "origin/main"), Branch("done", "old", merged: true)], "origin/main=t4")], []);

        Assert.Equal(["t4", "t3", "old", "t2", "t1"], Names(map));
        AssertChildrenFirst(map);
        var row = Row(map, "old");
        Assert.NotEqual(0, row.Lane);
        Assert.False(row.Up);
        Assert.True(row.Stub);
        Assert.Equal([0], row.Through.Select(l => l.Lane)); // the trunk passes it by
    }

    [Fact]
    public void A_loose_piece_goes_above_a_gap_it_is_newer_than_and_never_above_its_own_children()
    {
        // Dates: t30, t29, old, [t10]: "old" is newer than what lies under the gap.
        var commits = new[] { C("t30", "t29", pos: 30), C("t29", "t28", pos: 29), C("old", "o0", inTrunk: true), C("t10", "t9", pos: 10) };
        var map = BranchGraph.Build([Clone("mac", "t30", commits, [Branch("main", "t30", "origin/main"), Branch("done", "old", merged: true), Branch("base", "t10", merged: true)], "origin/main=t30")], []);
        Assert.Equal(["t30", "t29", "old", "Gap", "t10", "Gap"], Names(map));

        // A side branch s (newest of all) merged a commit q whose own history is unknown and old: q stays below s.
        var side = new[] { C("s", "t2 q"), C("t2", "t1", pos: 2), C("t1", pos: 1), C("q", "q0") };
        var merged = BranchGraph.Build([Clone("mac", "s", side, [Branch("main", "t2", "origin/main"), Branch("work", "s")], "origin/main=t2")], []);
        Assert.Equal(["s", "t2", "q", "t1"], Names(merged));
        AssertChildrenFirst(merged);
    }

    [Fact]
    public void A_merged_tip_that_only_knows_where_it_joined_is_hung_there_with_a_dashed_line()
    {
        // The agent found the merge (t4) but the commits between are too many to send. "old" is older than t2.
        var commits = new[] { C("t4", "t3 zz", pos: 4), C("t3", "t2", pos: 3), C("t2", "t1", pos: 2), C("t1", pos: 1), C("old", "o0", inTrunk: true) with { JoinsTrunkAt = Sha("t4") } };
        var map = BranchGraph.Build([Clone("mac", "t4", commits, [Branch("main", "t4", "origin/main"), Branch("done", "old", merged: true)], "origin/main=t4")], []);

        Assert.Equal(["t4", "old", "t3", "t2", "t1"], Names(map));
        var link = Assert.Single(Row(map, "t4").Out);
        Assert.Equal(MapLineStyle.Dashed, link.Style);
        Assert.Equal(Row(map, "old").Lane, link.Lane);
        Assert.True(Row(map, "old").Up);
        Assert.Equal(MapPresence.Local, Row(map, "old").Presence[0]);

        // A join that is not in the map (a hub that only has an older agent's trunk) falls back to the date.
        var lost = commits.Select(c => c.Sha == Sha("old") ? c with { JoinsTrunkAt = Sha("nowhere") } : c);
        var loose = BranchGraph.Build([Clone("mac", "t4", lost, [Branch("main", "t4", "origin/main"), Branch("done", "old", merged: true)], "origin/main=t4")], []);
        Assert.Equal(["t4", "t3", "t2", "t1", "old"], Names(loose));
        Assert.Empty(Row(loose, "t4").Out);
    }

    [Fact]
    public void A_join_deep_in_the_trunk_is_shown_so_the_merged_tip_has_somewhere_to_hang()
    {
        // t10 is only in the snapshot because "old" joined there; nothing else leads to it.
        var commits = Trunk(28, 30).Concat([C("t10", "t9 zz", pos: 10), C("old", "o0", inTrunk: true) with { JoinsTrunkAt = Sha("t10") }]);
        var map = BranchGraph.Build([Clone("mac", "t30", commits, [Branch("main", "t30", "origin/main"), Branch("done", "old", merged: true)], "origin/main=t30")], []);

        Assert.Equal(["t30", "t29", "t28", "Gap", "t10", "old", "Gap"], Names(map));
        Assert.Equal(17, map.Rows[3].Hidden);
        Assert.Equal(MapLineStyle.Dashed, Assert.Single(Row(map, "t10").Out).Style);
    }

    [Fact]
    public void With_merged_branches_hidden_only_the_merged_history_something_still_needs_is_kept()
    {
        // As above, plus an open branch "work" that forks from the merged tip b, and the upstreams of the merged branches.
        var commits = StackedMerges().Append(C("w", "b"));
        BranchInfo[] branches =
        [
            Branch("main", "t3", "origin/main"), Branch("first", "a", "origin/first", merged: true), Branch("second", "b", "origin/second", merged: true),
            Branch("third", "c", merged: true), Branch("work", "w"),
        ];
        var options = new BranchMapOptions { HideMerged = true };
        var map = BranchGraph.Build([Clone("mac", "t3", commits, branches, "origin/main=t3 origin/first=a origin/second=b")], [], options);

        // b stays because "work" forks from it, with its way up to main (m1, m2). a and c go, and so do the upstream names.
        Assert.Equal(["t3", "m2", "m1", "w", "b", "t2", "t1"], Names(map));
        AssertChildrenFirst(map);
        Assert.Empty(Row(map, "b").Refs);
        Assert.Equal("work", Row(map, "w").Refs.Single().Name);
        Assert.Equal(3, map.MergedBranches);

        // An upstream that has moved on to commits that are not merged is not hidden with its branch.
        var ahead = commits.Append(C("n", "a"));
        var moved = BranchGraph.Build([Clone("mac", "t3", ahead, branches, "origin/main=t3 origin/first=n origin/second=b")], [], options);
        Assert.Equal("origin/first", Row(moved, "n").Refs.Single().Name);
        Assert.Contains("a", Names(moved));
    }

    private static CloneView OnDevelop(CloneView clone) => clone with
    {
        Entry = clone.Entry with { Git = clone.Entry.Git! with { Graph = clone.Entry.Git.Graph! with { TrunkRef = "origin/develop" } } },
    };

    [Fact]
    public void In_a_git_flow_repository_merged_means_merged_into_develop()
    {
        // The map is drawn along develop. "done" is merged into develop but not into main, the default branch.
        var commits = new[] { C("t3", "t2 s1", pos: 3), C("s1", "t1", inTrunk: true), C("t2", "t1", pos: 2), C("t1", pos: 1) };
        BranchInfo[] branches =
        [
            Branch("develop", "t3", "origin/develop") with { MergedIntoDefault = false, MergedIntoTrunk = true },
            Branch("main", "t1", "origin/main") with { MergedIntoDefault = true, MergedIntoTrunk = true },
            Branch("done", "s1") with { MergedIntoDefault = false, MergedIntoTrunk = true },
            Branch("released", "t1") with { MergedIntoDefault = true, MergedIntoTrunk = true },
        ];
        var clone = OnDevelop(Clone("mac", "t3", commits, branches, "origin/develop=t3 origin/main=t1", onBranch: "develop"));

        var map = BranchGraph.Build([clone], []);
        Assert.Equal("develop", map.TrunkName);
        Assert.Equal(2, map.MergedBranches); // done and released: never develop or main themselves
        Assert.True(Row(map, "s1").Refs.Single().Merged);
        Assert.True(Row(map, "s1").Dim);
        Assert.False(Row(map, "t3").Refs.Single(r => r.Name == "develop").Merged);
        Assert.False(Row(map, "t1").Refs.Single(r => r.Name == "main").Merged);

        var hidden = BranchGraph.Build([clone], [], new BranchMapOptions { HideMerged = true });
        Assert.Equal(["t3", "t2", "t1"], Names(hidden));
        Assert.Equal(["main", "origin/main"], Row(hidden, "t1").Refs.Select(r => r.Name));
    }

    [Fact]
    public void A_branch_merged_into_main_but_not_develop_is_not_merged_on_a_develop_map_and_older_agents_fall_back()
    {
        var commits = new[] { C("t2", "t1", pos: 2), C("t1", pos: 1), C("h1", "t1") };

        // A hotfix that went to main only: cleanup calls it merged, the map along develop does not.
        var hotfix = OnDevelop(Clone("mac", "t2", commits, [Branch("develop", "t2", "origin/develop"), Branch("hotfix", "h1") with { MergedIntoDefault = true, MergedIntoTrunk = false }], "origin/develop=t2", onBranch: "develop"));
        Assert.Equal(0, BranchGraph.Build([hotfix], []).MergedBranches);
        Assert.False(Row(BranchGraph.Build([hotfix], []), "h1").Refs.Single().Merged);

        // An agent that does not report it: merged into the default branch is all there is to go by.
        var old = OnDevelop(Clone("mac", "t2", commits, [Branch("develop", "t2", "origin/develop"), Branch("hotfix", "h1", merged: true)], "origin/develop=t2", onBranch: "develop"));
        Assert.Equal(1, BranchGraph.Build([old], []).MergedBranches);
    }

    [Fact]
    public void How_far_ahead_a_device_is_comes_from_gits_own_count_when_a_device_has_one()
    {
        // The trunk between the two is mostly not sent, and what it merged is not on the first-parent line at all:
        // the map sees 4 commits between t2 and t6, git counted 36.
        var behind = Clone("laptop", "t2", Trunk(1, 2).Concat(Trunk(6, 6)), [Branch("main", "t2", "origin/main") with { Behind = 36 }], "origin/main=t6");
        var current = Clone("studio", "t6", Trunk(1, 2).Concat(Trunk(6, 6)), [Branch("main", "t6", "origin/main")], "origin/main=t6");

        var warning = Assert.Single(BranchGraph.Build([behind, current], []).Warnings);
        Assert.False(warning.Diverged);
        Assert.Equal("studio is 36 commits ahead of laptop.", warning.Text);

        // The same from the other side: a device that is ahead of an upstream which is where the other device is.
        var ahead = Clone("studio", "t6", Trunk(1, 2).Concat(Trunk(6, 6)), [Branch("main", "t6", "origin/main", ahead: 36)], "origin/main=t2");
        var stale = Clone("laptop", "t2", Trunk(1, 2).Concat(Trunk(6, 6)), [Branch("main", "t2")], "");
        Assert.Equal("studio is 36 commits ahead of laptop.", Assert.Single(BranchGraph.Build([stale, ahead], []).Warnings).Text);

        // Git's count also settles a divergence the map cannot see whole.
        var mine = Clone("laptop", "a1", Trunk(1, 2).Concat([C("a1", "a0"), C("b1", "b0")]), [Branch("main", "t2", "origin/main"), Branch("fix", "a1", "origin/fix", ahead: 3) with { Behind = 5 }], "origin/main=t2 origin/fix=b1");
        var theirs = Clone("studio", "b1", Trunk(1, 2).Concat([C("b1", "b0")]), [Branch("main", "t2", "origin/main"), Branch("fix", "b1", "origin/fix")], "origin/main=t2 origin/fix=b1");
        var diverged = Assert.Single(BranchGraph.Build([mine, theirs], []).Warnings);
        Assert.True(diverged.Diverged);
        Assert.Equal("has diverged: laptop has 3 commits that studio does not, and studio has 5 commits of its own.", diverged.Text);
    }

    [Fact]
    public void Without_gits_count_the_map_says_at_least_unless_it_holds_every_commit_between()
    {
        // No upstream to compare with, and a gap between the two tips.
        var behind = Clone("laptop", "t2", Trunk(1, 2).Concat(Trunk(6, 6)), [Branch("main", "t2")], "");
        var current = Clone("studio", "t6", Trunk(1, 2).Concat(Trunk(6, 6)), [Branch("main", "t6", "origin/main")], "origin/main=t6");
        Assert.Equal("studio is at least 4 commits ahead of laptop.", Assert.Single(BranchGraph.Build([behind, current], []).Warnings).Text);

        // Every commit between them is in the map: the count is exact.
        var near = Clone("laptop", "t2", Trunk(1, 4), [Branch("main", "t2")], "");
        var far = Clone("studio", "t4", Trunk(1, 4), [Branch("main", "t4", "origin/main")], "origin/main=t4");
        Assert.Equal("studio is 2 commits ahead of laptop.", Assert.Single(BranchGraph.Build([near, far], []).Warnings).Text);

        // A merge whose other parent was not sent hides commits: at least.
        var merge = new[] { C("t3", "t2 zz", pos: 3), C("t2", "t1", pos: 2), C("t1", pos: 1) };
        var low = Clone("laptop", "t2", merge, [Branch("main", "t2")], "");
        var high = Clone("studio", "t3", merge, [Branch("main", "t3", "origin/main")], "origin/main=t3");
        Assert.Equal("studio is at least 1 commit ahead of laptop.", Assert.Single(BranchGraph.Build([low, high], []).Warnings).Text);
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
    public void A_pull_request_with_a_known_head_commit_sits_on_that_commit()
    {
        static PullRequestInfo Pr(int number, string head, string? at, bool fork = false) =>
            new() { Repo = "acme/widget", Number = number, HeadRef = head, HeadSha = at is null ? null : Sha(at), IsCrossRepository = fork, Url = $"https://github.com/acme/widget/pull/{number}" };
        var commits = Trunk(1, 2).Concat([C("f2", "f1"), C("f1", "t2"), C("w1", "t2"), C("k1", "t2")]);
        var map = BranchGraph.Build(
            [Clone("mac", "f2", commits, [Branch("main", "t2", "origin/main"), Branch("feature", "f2", "origin/feature"), Branch("wip", "w1"), Branch("patch-1", "k1")], "origin/main=t2 origin/feature=f1")],
            [
                Pr(1, "feature", "f2"),          // GitHub is at f2; this device's origin/feature is a fetch behind
                Pr(2, "renamed-on-github", "w1"), // found by its commit although no branch here has that name
                Pr(3, "patch-1", "zz", fork: true), // a fork's patch-1 is not this repository's patch-1
                Pr(4, "main", "k1", fork: true),  // a fork's branch, placed because the commit is here
                Pr(5, "feature", "gone"),         // a commit no device has: falls back to the branch name
            ]);

        Assert.Equal([1], Row(map, "f2").PullRequests.Select(p => p.Number));
        Assert.Equal([5], Row(map, "f1").PullRequests.Select(p => p.Number));
        Assert.Equal([2], Row(map, "w1").PullRequests.Select(p => p.Number));
        Assert.Equal([4], Row(map, "k1").PullRequests.Select(p => p.Number));
        Assert.Empty(Row(map, "t2").PullRequests);
        Assert.Equal([3], map.UnplacedPullRequests.Select(p => p.Number));
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
