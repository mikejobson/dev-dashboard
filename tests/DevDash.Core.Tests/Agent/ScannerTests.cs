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
    private static string Git(string workingDirectory, params string[] args)
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
