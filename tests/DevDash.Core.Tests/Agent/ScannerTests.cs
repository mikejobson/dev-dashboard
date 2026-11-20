using System.Diagnostics;
using DevDash.Agent;
using DevDash.Agent.Git;
using DevDash.Agent.Scanning;
using DevDash.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevDash.Core.Tests.Agent;

/// <summary>
/// End-to-end scans of throwaway repositories created under the system temp folder. This is the only
/// place the fetch code path runs in tests; it never touches a real working copy.
/// </summary>
public sealed class ScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("devdash-scan-").FullName;
    private readonly string _dev;
    private readonly string _origin;

    public ScannerTests()
    {
        _dev = Directory.CreateDirectory(Path.Combine(_root, "Developer")).FullName;
        _origin = Path.Combine(_root, "origin", "widget.git");

        // A bare "remote" with one commit on main, cloned into the scan root.
        var seed = Path.Combine(_root, "seed");
        Directory.CreateDirectory(seed);
        Git(seed, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(seed, "README.md"), "# Widget\n\n![badge](x.svg)\n\nA small widget for testing.\nSecond line.\n\nMore.\n");
        File.WriteAllText(Path.Combine(seed, "package.json"), """{ "name": "widget", "description": "Widget package" }""");
        Git(seed, "add", ".");
        Git(seed, "commit", "-q", "-m", "initial");
        Directory.CreateDirectory(Path.GetDirectoryName(_origin)!);
        Git(_root, "clone", "-q", "--bare", seed, _origin);
        Git(_dev, "clone", "-q", _origin, "widget");
    }

    private string Widget => Path.Combine(_dev, "widget");

    [Fact]
    public async Task Scans_repos_worktrees_folders_and_loose_files()
    {
        // Local state: a dirty file, an untracked file, a stash, an unpushed branch, a merged branch, a linked worktree.
        Git(Widget, "branch", "merged-branch");
        Git(Widget, "checkout", "-q", "-b", "feature/x");
        File.WriteAllText(Path.Combine(Widget, "work.txt"), "work");
        Git(Widget, "add", "work.txt");
        Git(Widget, "commit", "-q", "-m", "feature work");
        File.WriteAllText(Path.Combine(Widget, "README.md"), "# Widget\n\nstashed edit\n");
        Git(Widget, "stash", "push", "-q", "-m", "parked");
        File.AppendAllText(Path.Combine(Widget, "package.json"), "\n");
        File.WriteAllText(Path.Combine(Widget, "scratch.txt"), "?");
        Directory.CreateDirectory(Path.Combine(_dev, "widget.worktrees"));
        Git(Widget, "worktree", "add", "-q", Path.Combine(_dev, "widget.worktrees", "hotfix"), "-b", "hotfix", "main");
        Directory.CreateDirectory(Path.Combine(_dev, "widget.worktrees", "leftover"));

        Directory.CreateDirectory(Path.Combine(_dev, "Reference Docs"));
        File.WriteAllText(Path.Combine(_dev, "Reference Docs", "spec.pdf"), "pdf");
        File.WriteAllText(Path.Combine(_dev, "notes.txt"), "loose");
        File.WriteAllText(Path.Combine(_dev, ".DS_Store"), "noise");
        Directory.CreateDirectory(Path.Combine(_dev, "empty-worktrees"));

        // A repo that asks for git's fsmonitor daemon must not get one started by a scan.
        Git(Widget, "config", "core.fsmonitor", "true");
        var indexBefore = File.GetLastWriteTimeUtc(Path.Combine(Widget, ".git", "index"));

        var snapshot = await CreateScanner(autoFetch: false).ScanAsync(new ScanRequest(AllowFetch: true, SizeMode.Force), CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(Widget, ".git", "fsmonitor--daemon.ipc")));
        Assert.Equal(indexBefore, File.GetLastWriteTimeUtc(Path.Combine(Widget, ".git", "index")));
        Assert.Equal(SnapshotSchema.Version, snapshot.SchemaVersion);
        Assert.Equal(["empty-worktrees", "notes.txt", "Reference Docs", "widget", "widget.worktrees"], snapshot.Entries.Select(e => e.Name));

        var widget = snapshot.Entries.Single(e => e.Name == "widget");
        Assert.Equal(EntryKind.GitRepo, widget.Kind);
        var git = widget.Git!;
        Assert.Equal("feature/x", git.Branch);
        Assert.Null(git.Upstream);
        Assert.Equal("main", git.DefaultBranch);
        Assert.False(git.IsLinkedWorktree);
        Assert.Equal(1, git.Unstaged);
        Assert.Equal(1, git.Untracked);
        Assert.NotNull(git.NewestTrackedChangeAt);
        Assert.NotNull(git.NewestUntrackedAt);
        Assert.Equal("feature work", git.LastCommit!.Subject);
        Assert.Null(Assert.Single(git.Remotes).Normalized); // a local path remote has no hosted identity
        Assert.Equal("parked", Assert.Single(git.Stashes).Message.Split(": ")[^1]);
        Assert.Equal(2, git.Worktrees.Count);
        Assert.True(git.Branches.Single(b => b.Name == "merged-branch").MergedIntoDefault);
        Assert.False(git.Branches.Single(b => b.Name == "feature/x").MergedIntoDefault);
        Assert.True(git.Branches.Single(b => b.Name == "feature/x").IsCurrent);
        Assert.Equal("origin/main", git.Branches.Single(b => b.Name == "main").Upstream);
        Assert.Equal("A small widget for testing. Second line.", widget.Description!.ReadmeExcerpt);
        Assert.Equal("Widget package", widget.Description.Manifests.Single().Description);
        Assert.True(widget.Size!.TotalBytes > 0);

        var container = snapshot.Entries.Single(e => e.Name == "widget.worktrees");
        Assert.Equal(EntryKind.WorktreeContainer, container.Kind);
        var hotfix = container.Children.Single(c => c.Name == "hotfix");
        Assert.True(hotfix.Git!.IsLinkedWorktree);
        Assert.Equal(git.CommonDir, hotfix.Git.CommonDir);
        Assert.Equal("hotfix", hotfix.Git.Branch);
        Assert.Equal(EntryKind.NonGitFolder, container.Children.Single(c => c.Name == "leftover").Kind);

        Assert.Equal(EntryKind.WorktreeContainer, snapshot.Entries.Single(e => e.Name == "empty-worktrees").Kind);
        Assert.Equal(EntryKind.NonGitFolder, snapshot.Entries.Single(e => e.Name == "Reference Docs").Kind);
        Assert.Equal(1, snapshot.Entries.Single(e => e.Name == "Reference Docs").Folder!.FileCount);
        Assert.Equal(EntryKind.LooseFile, snapshot.Entries.Single(e => e.Name == "notes.txt").Kind);
    }

    [Fact]
    public async Task Auto_fetch_updates_remote_tracking_refs_and_respects_the_interval_and_the_off_switch()
    {
        // Someone else pushes a commit and deletes nothing; our clone does not know yet.
        var other = Path.Combine(_root, "other");
        Git(_root, "clone", "-q", _origin, other);
        File.WriteAllText(Path.Combine(other, "new.txt"), "new");
        Git(other, "add", ".");
        Git(other, "commit", "-q", "-m", "upstream change");
        Git(other, "push", "-q", "origin", "main");
        var headBefore = Git(Widget, "rev-parse", "HEAD");

        // Auto-fetch off: behind count stays stale and nothing in .git changes.
        var off = await CreateScanner(autoFetch: false).ScanAsync(new ScanRequest(AllowFetch: true, SizeMode.CachedOnly), CancellationToken.None);
        Assert.Equal(0, off.Entries.Single().Git!.Behind);
        Assert.False(File.Exists(Path.Combine(Widget, ".git", "FETCH_HEAD")));

        // A dry-run style request never fetches even when auto-fetch is on.
        var dry = await CreateScanner(autoFetch: true).ScanAsync(new ScanRequest(AllowFetch: false, SizeMode.CachedOnly), CancellationToken.None);
        Assert.Equal(0, dry.Entries.Single().Git!.Behind);
        Assert.False(File.Exists(Path.Combine(Widget, ".git", "FETCH_HEAD")));

        // Auto-fetch on: the scan fetches first, so the repo is now known to be one behind.
        var scanner = CreateScanner(autoFetch: true);
        var on = await scanner.ScanAsync(new ScanRequest(AllowFetch: true, SizeMode.CachedOnly), CancellationToken.None);
        var git = on.Entries.Single().Git!;
        Assert.Equal(1, git.Behind);
        Assert.NotNull(git.LastFetchAt);

        // Fetch only moved the remote-tracking ref: HEAD, the branch and the working tree are untouched.
        Assert.Equal(headBefore, Git(Widget, "rev-parse", "HEAD"));
        Assert.Equal("", Git(Widget, "status", "--porcelain"));
        Assert.False(File.Exists(Path.Combine(Widget, "new.txt")));

        // Within the interval a second scan does not fetch again.
        var fetchedAt = File.GetLastWriteTimeUtc(Path.Combine(Widget, ".git", "FETCH_HEAD"));
        await Task.Delay(1100);
        await scanner.ScanAsync(new ScanRequest(AllowFetch: true, SizeMode.CachedOnly), CancellationToken.None);
        Assert.Equal(fetchedAt, File.GetLastWriteTimeUtc(Path.Combine(Widget, ".git", "FETCH_HEAD")));
    }

    [Fact]
    public async Task Fetch_prunes_deleted_remote_branches_so_they_show_as_gone()
    {
        Git(Widget, "checkout", "-q", "-b", "feature/y");
        Git(Widget, "push", "-q", "-u", "origin", "feature/y");
        Git(Widget, "checkout", "-q", "main");
        Git(_origin, "branch", "-D", "feature/y"); // deleted on the "server"

        var snapshot = await CreateScanner(autoFetch: true).ScanAsync(new ScanRequest(AllowFetch: true, SizeMode.CachedOnly), CancellationToken.None);

        var branch = snapshot.Entries.Single().Git!.Branches.Single(b => b.Name == "feature/y");
        Assert.True(branch.UpstreamGone);
    }

    [Fact]
    public async Task Reports_a_compact_commit_graph_for_the_branch_map()
    {
        string Commit(string subject)
        {
            File.AppendAllText(Path.Combine(Widget, "log.txt"), subject + "\n");
            Git(Widget, "add", ".");
            Git(Widget, "commit", "-q", "-m", subject);
            return Git(Widget, "rev-parse", "HEAD");
        }

        var root = Git(Widget, "rev-parse", "HEAD");

        // An old branch that forks from the very first commit, far below what is sent of main.
        Git(Widget, "checkout", "-q", "-b", "old");
        var old = Commit("old work");
        Git(Widget, "push", "-q", "-u", "origin", "old");

        // main moves on by more than the context the agent sends, then merges a topic branch.
        Git(Widget, "checkout", "-q", "main");
        for (var i = 1; i <= CommitGraphCollector.TrunkContext + 4; i++)
        {
            Commit($"main {i}");
        }

        Git(Widget, "checkout", "-q", "-b", "topic");
        var topic = Commit("topic work");
        Git(Widget, "checkout", "-q", "main");
        Git(Widget, "merge", "-q", "--no-ff", "topic", "-m", "Merge topic");
        var merge = Git(Widget, "rev-parse", "HEAD");
        Git(Widget, "push", "-q", "origin", "main");

        // A feature branch with one pushed commit and one that is on no remote, and a stash on top of it.
        Git(Widget, "checkout", "-q", "-b", "feature");
        var pushed = Commit("feature, pushed");
        Git(Widget, "push", "-q", "-u", "origin", "feature");
        var unpushed = Commit("feature, not pushed");
        File.AppendAllText(Path.Combine(Widget, "log.txt"), "parked\n");
        Git(Widget, "stash", "push", "-q", "-m", "parked");
        Directory.CreateDirectory(Path.Combine(_dev, "widget.worktrees"));
        Git(Widget, "worktree", "add", "-q", "--detach", Path.Combine(_dev, "widget.worktrees", "look"), "main~3");
        var detached = Git(Widget, "rev-parse", "main~3");

        var scanner = CreateScanner(autoFetch: false);
        var snapshot = await scanner.ScanAsync(new ScanRequest(AllowFetch: false, SizeMode.CachedOnly), CancellationToken.None);
        var git = snapshot.Entries.Single(e => e.Name == "widget").Git!;
        var graph = git.Graph!;
        var commits = graph.Commits.ToDictionary(c => c.Sha);

        Assert.Equal("origin/main", graph.TrunkRef);
        Assert.False(graph.Truncated);
        Assert.Equal(["origin/feature", "origin/main", "origin/old"], graph.RemoteRefs.Select(r => r.Name).Order());
        Assert.Equal(merge, graph.RemoteRefs.Single(r => r.Name == "origin/main").Sha);

        // The default branch: its newest commits, numbered from the root, and nothing in between down to the fork point.
        var trunkLength = 1 + CommitGraphCollector.TrunkContext + 4 + 1;
        Assert.Equal(trunkLength, commits[merge].TrunkPos);
        Assert.Equal(2, commits[merge].Parents.Count);
        Assert.Equal(1, commits[root].TrunkPos);
        Assert.Equal(CommitGraphCollector.TrunkContext + 1, graph.Commits.Count(c => c.TrunkPos is not null));
        Assert.DoesNotContain(graph.Commits, c => c.TrunkPos is > 1 and < 7);
        Assert.Equal(trunkLength - 3, commits[detached].TrunkPos); // main~3: three first parents below the merge

        // Off the default branch: the old branch hangs off the root, the feature off the merge.
        Assert.Null(commits[old].TrunkPos);
        Assert.Equal([root], commits[old].Parents);
        Assert.False(commits[old].Unpushed);
        Assert.False(commits[old].InTrunk);
        Assert.Equal("old work", commits[old].Subject);
        Assert.Equal("DevDash Test", commits[old].Author);
        Assert.Equal([merge], commits[pushed].Parents);
        Assert.False(commits[pushed].Unpushed);
        Assert.True(commits[unpushed].Unpushed);

        // A merged branch's tip is part of main's history without being on its first-parent line.
        Assert.True(commits[topic].InTrunk);
        Assert.Null(commits[topic].TrunkPos);
        Assert.Contains(topic, commits[merge].Parents);

        // The stash knows the commit it was made on.
        Assert.Equal(unpushed, Assert.Single(git.Stashes).BaseSha);

        // The linked worktree shares the repository, so it does not repeat the graph.
        var look = snapshot.Entries.Single(e => e.Name == "widget.worktrees").Children.Single(c => c.Name == "look");
        Assert.True(look.Git!.IsLinkedWorktree);
        Assert.Null(look.Git.Graph);

        // Nothing moved: the same graph comes back. A new commit shows up on the next scan.
        var again = (await scanner.ScanAsync(new ScanRequest(AllowFetch: false, SizeMode.CachedOnly), CancellationToken.None)).Entries.Single(e => e.Name == "widget").Git!.Graph;
        Assert.Same(graph, again);
        var next = Commit("one more");
        var after = (await scanner.ScanAsync(new ScanRequest(AllowFetch: false, SizeMode.CachedOnly), CancellationToken.None)).Entries.Single(e => e.Name == "widget").Git!.Graph!;
        Assert.True(after.Commits.Single(c => c.Sha == next).Unpushed);

        // It survives the trip to the hub, and an old agent's snapshot simply has none.
        var json = System.Text.Json.JsonSerializer.Serialize(git, SnapshotSchema.JsonOptions);
        Assert.DoesNotContain("\"unpushed\":false", json);
        Assert.DoesNotContain("\"inTrunk\":false", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<GitState>(json, SnapshotSchema.JsonOptions)!;
        Assert.Equal(graph.Commits.Count, back.Graph!.Commits.Count);
        Assert.Equal(trunkLength, back.Graph.Commits.Single(c => c.Sha == merge).TrunkPos);
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize<GitState>("{\"branch\":\"main\"}", SnapshotSchema.JsonOptions)!.Graph);
    }

    [Fact]
    public void A_linked_worktree_keeps_the_graph_when_its_repository_is_not_in_the_snapshot()
    {
        static DevEntry Entry(string name, string commonDir, bool linked) => new()
        {
            Root = "/dev", Name = name, Path = "/dev/" + name, Kind = EntryKind.GitRepo,
            Git = new GitState { CommonDir = commonDir, IsLinkedWorktree = linked, Graph = new CommitGraph { TrunkRef = "origin/main" } },
        };

        var container = new DevEntry
        {
            Root = "/dev", Name = "widget.worktrees", Path = "/dev/widget.worktrees", Kind = EntryKind.WorktreeContainer,
            Children = [Entry("a", "/dev/widget/.git", linked: true), Entry("b", "/elsewhere/gadget/.git", linked: true), Entry("c", "/elsewhere/gadget/.git", linked: true)],
        };

        var result = MachineScanner.WithOneGraphPerRepository([container, Entry("widget", "/dev/widget/.git", linked: false)]);

        Assert.NotNull(result[1].Git!.Graph);
        Assert.Null(result[0].Children[0].Git!.Graph);    // widget is scanned itself
        Assert.NotNull(result[0].Children[1].Git!.Graph); // gadget's main working tree is not: the first worktree carries it
        Assert.Null(result[0].Children[2].Git!.Graph);
    }

    [Fact]
    public async Task Caps_the_commits_off_the_default_branch_and_says_so()
    {
        Git(Widget, "checkout", "-q", "-b", "long");
        for (var i = 0; i < CommitGraphCollector.MaxSideCommits + 5; i++)
        {
            Git(Widget, "commit", "-q", "--allow-empty", "-m", $"step {i}");
        }

        var graph = (await CreateScanner(autoFetch: false).ScanAsync(new ScanRequest(AllowFetch: false, SizeMode.CachedOnly), CancellationToken.None)).Entries.Single().Git!.Graph!;

        Assert.True(graph.Truncated);
        Assert.Equal(CommitGraphCollector.MaxSideCommits + 5, graph.SideCommitCount);
        // The newest 250, and the one commit the oldest of them hangs off.
        Assert.Equal(CommitGraphCollector.MaxSideCommits + 1, graph.Commits.Count(c => c.TrunkPos is null));
        Assert.DoesNotContain(graph.Commits, c => c.InTrunk);
        Assert.Contains(graph.Commits, c => c.Subject == $"step {CommitGraphCollector.MaxSideCommits + 4}"); // the newest are the ones kept
        Assert.DoesNotContain(graph.Commits, c => c.Subject == "step 0");
    }

    [Fact]
    public void Tree_walker_separates_build_output_and_ignores_it_for_last_modified()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root, "walk")).FullName;
        File.WriteAllText(Path.Combine(repo, "package.json"), "{}");
        File.WriteAllText(Path.Combine(repo, "App.csproj"), "<Project />");
        var source = Path.Combine(repo, "src", "main.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "code");
        File.SetLastWriteTimeUtc(source, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(repo, "package.json"), new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(repo, "App.csproj"), new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        foreach (var dir in new[] { "node_modules/pkg", "bin/Debug", "dist", "scripts/bin", ".git/objects" })
        {
            Directory.CreateDirectory(Path.Combine(repo, dir));
            File.WriteAllBytes(Path.Combine(repo, dir, "blob"), new byte[1000]);
        }

        var quick = TreeWalker.Walk(repo, measureSizes: false, DateTimeOffset.UtcNow);
        Assert.Null(quick.Size);

        var full = TreeWalker.Walk(repo, measureSizes: true, DateTimeOffset.UtcNow);
        Assert.Equal(5000 + "{}".Length + "<Project />".Length + "code".Length, full.Size!.TotalBytes);
        Assert.Equal(3000, full.Size.ReclaimableBytes);
        Assert.Equal(["bin", "dist", "node_modules"], full.Size.ReclaimableDirs.Select(d => Path.GetFileName(d.Path)).Order());
        // scripts/bin has no project file beside it, so it is source, and its newer mtime counts.
        Assert.True(full.LastModified > new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_deleted_build_folder_leaves_the_cached_size_without_waiting_for_a_new_measurement()
    {
        var modules = Directory.CreateDirectory(Path.Combine(Widget, "node_modules", "pkg")).FullName;
        File.WriteAllBytes(Path.Combine(modules, "big.bin"), new byte[64 * 1024]);
        var dist = Directory.CreateDirectory(Path.Combine(Widget, "dist")).FullName; // beside package.json
        File.WriteAllBytes(Path.Combine(dist, "app.js"), new byte[16 * 1024]);
        var scanner = CreateScanner(autoFetch: false);

        var measured = Find(await scanner.ScanAsync(new ScanRequest(AllowFetch: false, SizeMode.Force), CancellationToken.None)).Size!;
        Assert.Equal(2, measured.ReclaimableDirs.Count);

        Directory.Delete(Path.Combine(Widget, "node_modules"), recursive: true);
        var after = Find(await scanner.ScanAsync(new ScanRequest(AllowFetch: false, SizeMode.CachedOnly), CancellationToken.None)).Size!;

        var left = Assert.Single(after.ReclaimableDirs);
        Assert.EndsWith("dist", left.Path);
        Assert.Equal(left.Bytes, after.ReclaimableBytes);
        Assert.True(after.ReclaimableBytes < measured.ReclaimableBytes);
        Assert.Equal(measured.MeasuredAt, after.MeasuredAt);

        static DevEntry Find(MachineSnapshot snapshot) => snapshot.Entries.Single(e => e.Name == "widget");
    }

    [Theory]
    [InlineData(true, "origin/develop")]
    [InlineData(false, "origin/main")]
    public async Task The_branch_map_follows_develop_when_it_is_where_the_newest_work_is(bool developIsNewer, string expectedTrunk)
    {
        Git(Widget, "checkout", "-q", "-b", "develop");
        File.WriteAllText(Path.Combine(Widget, "dev.txt"), "dev");
        Git(Widget, "add", "dev.txt");
        GitAt(developIsNewer ? "2030-01-02T00:00:00Z" : "2020-01-01T00:00:00Z", Widget, "commit", "-q", "-m", "develop work");
        Git(Widget, "push", "-q", "-u", "origin", "develop");
        Git(Widget, "checkout", "-q", "main");
        File.WriteAllText(Path.Combine(Widget, "rel.txt"), "release");
        Git(Widget, "add", "rel.txt");
        GitAt("2025-06-01T00:00:00Z", Widget, "commit", "-q", "-m", "release");
        Git(Widget, "push", "-q", "origin", "main");

        var snapshot = await CreateScanner(autoFetch: false).ScanAsync(new ScanRequest(AllowFetch: false, SizeMode.CachedOnly), CancellationToken.None);
        var git = snapshot.Entries.Single(e => e.Name == "widget").Git!;

        Assert.Equal(expectedTrunk, git.Graph!.TrunkRef);
        Assert.Equal("main", git.DefaultBranch); // the default branch itself is unchanged
    }

    [Fact]
    public async Task A_branch_merged_into_develop_only_is_merged_for_the_map_and_not_for_cleanup()
    {
        // git-flow: main is the default branch, develop is where the work is merged.
        Git(Widget, "checkout", "-q", "-b", "develop");
        Git(Widget, "checkout", "-q", "-b", "feature/done");
        GitAt("2030-01-01T00:00:00Z", Widget, "commit", "-q", "--allow-empty", "-m", "feature work");
        Git(Widget, "checkout", "-q", "develop");
        GitAt("2030-01-02T00:00:00Z", Widget, "merge", "-q", "--no-ff", "feature/done", "-m", "Merge feature/done");
        Git(Widget, "push", "-q", "-u", "origin", "develop");
        Git(Widget, "checkout", "-q", "-b", "feature/open");
        GitAt("2030-01-03T00:00:00Z", Widget, "commit", "-q", "--allow-empty", "-m", "open work");

        var git = (await CreateScanner(autoFetch: false).ScanAsync(new ScanRequest(AllowFetch: false, SizeMode.CachedOnly), CancellationToken.None)).Entries.Single(e => e.Name == "widget").Git!;
        BranchInfo Branch(string name) => git.Branches.Single(b => b.Name == name);

        Assert.Equal("origin/develop", git.Graph!.TrunkRef);
        Assert.False(Branch("feature/done").MergedIntoDefault); // cleanup still asks about main
        Assert.True(Branch("feature/done").MergedIntoTrunk);
        Assert.False(Branch("feature/open").MergedIntoTrunk);
        Assert.True(Branch("main").MergedIntoTrunk);

        // An optional field: absent when the map is drawn along the default branch, and old snapshots read fine.
        var json = System.Text.Json.JsonSerializer.Serialize(git, SnapshotSchema.JsonOptions);
        Assert.Contains("\"mergedIntoTrunk\":true", json);
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize<BranchInfo>("{\"name\":\"x\",\"mergedIntoDefault\":true}", SnapshotSchema.JsonOptions)!.MergedIntoTrunk);
    }

    [Fact]
    public async Task Without_a_separate_trunk_merged_into_trunk_is_not_reported()
    {
        Git(Widget, "branch", "merged-branch");

        var git = (await CreateScanner(autoFetch: false).ScanAsync(new ScanRequest(AllowFetch: false, SizeMode.CachedOnly), CancellationToken.None)).Entries.Single(e => e.Name == "widget").Git!;

        Assert.All(git.Branches, b => Assert.Null(b.MergedIntoTrunk));
        Assert.DoesNotContain("mergedIntoTrunk", System.Text.Json.JsonSerializer.Serialize(git, SnapshotSchema.JsonOptions));
    }

    [Fact]
    public async Task A_branch_merged_through_a_chain_of_pull_requests_is_sent_with_its_way_up_to_the_trunk()
    {
        string Commit(string subject)
        {
            Git(Widget, "commit", "-q", "--allow-empty", "-m", subject);
            return Git(Widget, "rev-parse", "HEAD");
        }

        string Merge(string other, string subject)
        {
            Git(Widget, "merge", "-q", "--no-ff", other, "-m", subject);
            return Git(Widget, "rev-parse", "HEAD");
        }

        // Stacked pull requests: "first" has "second" merged into it after its own last commit, "second"
        // has "third" merged into it, and only then does "first" land on main. The local branches still
        // point at the commits made before those merges, so each tip is reachable from main only
        // through second parents and merges that no branch points at.
        Git(Widget, "checkout", "-q", "-b", "first");
        var first = Commit("first work");
        Git(Widget, "checkout", "-q", "-b", "second");
        Commit("second work 1");
        var second = Commit("second work 2");
        Git(Widget, "checkout", "-q", "-b", "third");
        var third = Commit("third work");
        Git(Widget, "checkout", "-q", "--detach", second);
        var secondMerged = Merge("third", "Merge pull request #3 from third");
        Git(Widget, "checkout", "-q", "--detach", first);
        var firstMerged = Merge(secondMerged, "Merge pull request #2 from second");
        Git(Widget, "checkout", "-q", "main");
        Commit("main moves on");
        var landed = Merge(firstMerged, "Merge pull request #1 from first");
        for (var i = 0; i < 3; i++)
        {
            Commit($"later {i}");
        }

        Git(Widget, "push", "-q", "origin", "main");

        var git = (await CreateScanner(autoFetch: false).ScanAsync(new ScanRequest(AllowFetch: false, SizeMode.CachedOnly), CancellationToken.None)).Entries.Single(e => e.Name == "widget").Git!;
        var commits = git.Graph!.Commits.ToDictionary(c => c.Sha);

        // The merges in between are sent, so every merged tip has an unbroken line of children up to main.
        Assert.Contains(firstMerged, commits.Keys);
        Assert.Contains(secondMerged, commits.Keys);
        Assert.Equal([first, secondMerged], commits[firstMerged].Parents);
        Assert.Equal([second, third], commits[secondMerged].Parents);
        Assert.Contains(firstMerged, commits[landed].Parents);
        Assert.All((string[])[firstMerged, secondMerged, first, second, third], sha =>
        {
            Assert.True(commits[sha].InTrunk);
            Assert.Null(commits[sha].TrunkPos);
        });

        // Each tip names the commit of main's first-parent line that brought it in.
        Assert.NotNull(commits[landed].TrunkPos);
        Assert.All((string[])[first, second, third], sha => Assert.Equal(landed, commits[sha].JoinsTrunkAt));
        Assert.Null(commits[landed].JoinsTrunkAt);
        Assert.All(git.Branches.Where(b => b.Name != "main"), b => Assert.True(b.MergedIntoDefault));

        // Optional on the wire: nothing for commits without one, and a snapshot from an older agent reads as before.
        var json = System.Text.Json.JsonSerializer.Serialize(git.Graph, SnapshotSchema.JsonOptions);
        Assert.DoesNotContain("\"joinsTrunkAt\":null", json);
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize<GraphCommit>($"{{\"sha\":\"{first}\"}}", SnapshotSchema.JsonOptions)!.JoinsTrunkAt);
    }

    [Fact]
    public async Task A_merged_tip_far_below_its_merge_is_sent_with_where_it_joins_instead_of_every_commit_between()
    {
        Git(Widget, "checkout", "-q", "-b", "long");
        Git(Widget, "commit", "-q", "--allow-empty", "-m", "early work");
        var early = Git(Widget, "rev-parse", "HEAD");
        Git(Widget, "branch", "early");
        for (var i = 0; i < CommitGraphCollector.MaxJoinPath + 5; i++)
        {
            Git(Widget, "commit", "-q", "--allow-empty", "-m", $"more {i}");
        }

        var tip = Git(Widget, "rev-parse", "HEAD");
        Git(Widget, "checkout", "-q", "main");
        Git(Widget, "merge", "-q", "--no-ff", "long", "-m", "Merge long");
        var landed = Git(Widget, "rev-parse", "HEAD");
        for (var i = 0; i < CommitGraphCollector.TrunkContext + 2; i++)
        {
            Git(Widget, "commit", "-q", "--allow-empty", "-m", $"later {i}");
        }

        Git(Widget, "push", "-q", "origin", "main");

        var graph = (await CreateScanner(autoFetch: false).ScanAsync(new ScanRequest(AllowFetch: false, SizeMode.CachedOnly), CancellationToken.None)).Entries.Single(e => e.Name == "widget").Git!.Graph!;
        var commits = graph.Commits.ToDictionary(c => c.Sha);

        // Both tips know the merge, and the merge is sent although it is below the context of main.
        Assert.Equal(landed, commits[early].JoinsTrunkAt);
        Assert.Equal(landed, commits[tip].JoinsTrunkAt);
        Assert.NotNull(commits[landed].TrunkPos);
        Assert.Contains(tip, commits[landed].Parents); // "long" itself is a parent of the merge: connected

        // The run between "early" and the tip is too long to send.
        Assert.DoesNotContain(graph.Commits, c => c.Subject.StartsWith("more ", StringComparison.Ordinal) && c.Sha != tip);
        Assert.False(graph.Truncated);
    }

    private MachineScanner CreateScanner(bool autoFetch)
    {
        var options = new StaticOptions(new AgentOptions
        {
            Roots = [_dev],
            MachineId = "test-machine",
            AutoFetch = new AutoFetchOptions { Enabled = autoFetch, IntervalHours = 4, TimeoutSeconds = 30 },
        });
        var git = new GitRunner();
        var fetch = new FetchService(git, options, NullLogger<FetchService>.Instance);
        var cache = new SizeCache(Path.Combine(_root, "sizes.json"));
        return new MachineScanner(new RepoScanner(git, fetch, options), cache, options, NullLogger<MachineScanner>.Instance);
    }

    /// <summary>Test-only git, used to build fixtures. The agent itself can only go through <see cref="GitRunner"/>.</summary>
    private static string Git(string workingDirectory, params string[] args) => GitAt(null, workingDirectory, args);

    /// <summary>Test-only git with a fixed committer date, for tests that compare commit times.</summary>
    private static string GitAt(string? committerDate, string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in (string[])["-c", "user.name=DevDash Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", "-c", "init.defaultBranch=main", .. args])
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (committerDate is not null)
        {
            psi.Environment["GIT_COMMITTER_DATE"] = committerDate;
            psi.Environment["GIT_AUTHOR_DATE"] = committerDate;
        }

        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        psi.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    public void Dispose()
    {
        try
        {
            // Git marks object files read-only on some platforms.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private sealed class StaticOptions(AgentOptions value) : IOptionsMonitor<AgentOptions>
    {
        public AgentOptions CurrentValue => value;

        public AgentOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<AgentOptions, string?> listener) => null;
    }
}
