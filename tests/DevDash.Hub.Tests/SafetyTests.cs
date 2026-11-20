using System.Net;
using System.Text.RegularExpressions;
using DevDash.Contracts;
using DevDash.Core;
using DevDash.Core.Safety;
using DevDash.Hub.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DevDash.Hub.Tests;

/// <summary>The Safety page, the per-device badge, and linked worktrees grouped under their main repo.</summary>
public class SafetyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static GitState Git(string path, string? remote, Func<GitState, GitState>? with = null, SafetyFacts? facts = null)
    {
        var git = new GitState
        {
            Branch = "main",
            HeadSha = "1111111111111111111111111111111111111111",
            Upstream = remote is null ? null : "origin/main",
            DefaultBranch = "main",
            CommonDir = path + "/.git",
            LastCommit = new CommitInfo { Sha = "1111111111111111111111111111111111111111", Date = Now.AddDays(-3), Subject = "work", Author = "me" },
            Remotes = remote is null ? [] : [new RemoteInfo { Name = "origin", Url = $"https://github.com/{remote}.git", Normalized = $"github.com/{remote}" }],
            Branches = [new BranchInfo { Name = "main", Sha = "1111111111111111111111111111111111111111", Upstream = remote is null ? null : "origin/main", IsCurrent = true, OnRemote = remote is not null, LastCommitDate = Now.AddDays(-3) }],
            Worktrees = [new WorktreeInfo { Path = path, Branch = "main" }],
            Safety = facts ?? new SafetyFacts { BranchesChecked = true, HeadOnRemote = remote is not null },
        };
        return with is null ? git : with(git);
    }

    private static DevEntry Repo(string name, GitState git, double touchedDaysAgo, long? bytes = null, string? path = null) => new()
    {
        Root = "/dev",
        Name = name,
        Path = path ?? "/dev/" + name,
        Kind = EntryKind.GitRepo,
        Git = git with { LastCommit = git.LastCommit! with { Date = Now.AddDays(-touchedDaysAgo) } },
        LastModified = Now.AddDays(-touchedDaysAgo),
        Size = bytes is { } b ? new SizeInfo { TotalBytes = b, ReclaimableBytes = b / 2, MeasuredAt = Now } : null,
    };

    private static MachineSnapshot Snapshot(string machineId, params DevEntry[] entries) => new()
    {
        Machine = new MachineInfo { MachineId = machineId, Hostname = machineId + "-host", Os = "macOS", AgentVersion = "0.6.0" },
        ScannedAt = Now,
        Roots = ["/dev"],
        Entries = entries,
    };

    /// <summary>
    /// Two machines. laptop: a clean clone, one with ignored files, one with old unpushed commits, and a
    /// repo with no remote that has a merged linked worktree. desktop: one with fresh uncommitted changes.
    /// </summary>
    private static async Task<HubFactory> SeededAsync()
    {
        var factory = new HubFactory();
        var store = factory.Services.GetRequiredService<SnapshotStore>();

        var feature = new BranchInfo
        {
            Name = "feature/x", Sha = "2222222222222222222222222222222222222222", Upstream = "origin/feature/x", Ahead = 2,
            OnRemote = false, UnpushedCount = 2, OldestUnpushedAt = Now.AddDays(-45.5), LastCommitDate = Now.AddDays(-40),
        };
        WorktreeInfo[] notesTrees = [new() { Path = "/dev/notes", Branch = "main" }, new() { Path = "/dev/notes.worktrees/draft", Branch = "draft", Head = "3333333333333333333333333333333333333333" }];
        BranchInfo[] notesBranches =
        [
            new() { Name = "main", Sha = "1111111111111111111111111111111111111111", IsCurrent = true, OnRemote = false, LastCommitDate = Now.AddDays(-3) },
            new() { Name = "draft", Sha = "3333333333333333333333333333333333333333", OnRemote = false, MergedIntoDefault = true, LastCommitDate = Now.AddDays(-9) },
        ];
        var draft = Repo("draft", Git("/dev/notes", null, g => g with
        {
            Branch = "draft", HeadSha = "3333333333333333333333333333333333333333", IsLinkedWorktree = true, MainWorktreePath = "/dev/notes",
            Branches = notesBranches, Worktrees = notesTrees,
        }), touchedDaysAgo: 9, path: "/dev/notes.worktrees/draft");

        await store.IngestAsync(Snapshot("laptop",
            Repo("clean", Git("/dev/clean", "acme/clean"), touchedDaysAgo: 300, bytes: 3L * 1024 * 1024),
            Repo("caveats", Git("/dev/caveats", "acme/caveats", facts: new SafetyFacts { BranchesChecked = true, HeadOnRemote = true, IgnoredFiles = [".env", "local.db"], IgnoredFileCount = 2 }), touchedDaysAgo: 20, bytes: 1024 * 1024),
            Repo("stale-work", Git("/dev/stale-work", "acme/stale-work", g => g with { Branches = [.. g.Branches, feature] }), touchedDaysAgo: 40, bytes: 2048),
            Repo("notes", Git("/dev/notes", null, g => g with { Branches = notesBranches, Worktrees = notesTrees }), touchedDaysAgo: 3),
            new DevEntry { Root = "/dev", Name = "notes.worktrees", Path = "/dev/notes.worktrees", Kind = EntryKind.WorktreeContainer, Children = [draft] }));
        await store.IngestAsync(Snapshot("desktop",
            Repo("clean", Git("/dev/clean", "acme/clean", g => g with { Unstaged = 2, NewestTrackedChangeAt = Now.AddDays(-1) }), touchedDaysAgo: 1)));
        return factory;
    }

    private static async Task<string> GetAsync(HubFactory factory, string url)
    {
        var response = await factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_page_lists_what_is_at_risk_and_what_is_safe_to_remove_with_the_space_freed()
    {
        using var factory = await SeededAsync();

        var html = await GetAsync(factory, "/safety");

        Assert.Contains("<h1>Safety</h1>", html);
        Assert.Matches(new Regex(@"At risk <span class=""muted small"">3</span>"), html);
        Assert.Contains("2 commits on feature/x not on any remote, oldest 45 days", html);
        Assert.Contains("No remote: 2 branches exist only here", html);
        Assert.Contains("2 uncommitted changes, last edited 1 day ago", html);
        Assert.Contains("href=\"repo?key=github.com%2Facme%2Fstale-work\"", html);

        // Safe to remove: the clean clone, the one with caveats, and the merged worktree; sizes add up.
        Assert.Matches(new Regex(@"Safe to remove <span class=""muted small"">3</span>"), html);
        Assert.Contains("safe to remove", html);
        Assert.Contains("safe with caveats", html);
        Assert.Contains("2 ignored files or folders that are not build output: .env, local.db", html);
        Assert.Contains("Removing all 3 would free <strong>4 MB</strong>", html);
        Assert.Contains("3 MB", html);
        Assert.Contains("1.5 MB build output", html);
        Assert.Contains("not measured", html); // the worktree's size is not known

        // Each row has a Remove button. These machines have actions off, so it is disabled and says why,
        // and there is nothing for "Remove N safe projects" to remove.
        Assert.Equal(3, Regex.Matches(html, @"<button type=""button"" class=""danger is-disabled"" aria-disabled=""true""[^>]*>Remove</button>").Count);
        Assert.Contains("Agent actions are off on laptop-host. Start its agent with --enable-actions to allow this.", html);
        Assert.DoesNotContain("safe project</button>", html);
        Assert.Contains("Nothing can be removed from here right now.", html);

        // The worktree is listed under its main repo's name and flagged as finished with.
        Assert.Contains("A linked worktree of notes", html);
        Assert.Contains(">merged</span>", html);
        Assert.Contains("1 linked worktree uses this repository", html);
    }

    [Fact]
    public async Task At_risk_rows_group_by_machine_or_by_repo_and_sort_by_age_or_severity()
    {
        using var factory = await SeededAsync();

        var byMachine = await GetAsync(factory, "/safety");
        Assert.Contains("href=\"http://localhost/safety?group=repo\"", byMachine);
        Assert.Contains("href=\"http://localhost/safety?sort=severity\"", byMachine);
        // Oldest first: the 45 day old commits on the laptop come before yesterday's edits on the desktop.
        Assert.True(byMachine.IndexOf("laptop-host</span>", StringComparison.Ordinal) < byMachine.IndexOf("desktop-host</span>", StringComparison.Ordinal));

        var byRepo = await GetAsync(factory, "/safety?group=repo&sort=severity");
        Assert.Contains("href=\"http://localhost/safety?sort=severity\"", byRepo); // back to by machine keeps the sort
        Assert.Matches(new Regex(@"<a class=""title-link mono"" href=""repo\?key=github\.com%2Facme%2Fstale-work"">stale-work</a>"), byRepo);
        Assert.Contains("<th>Machine</th>", byRepo);
    }

    [Fact]
    public async Task Sorting_puts_the_oldest_or_the_most_severe_risk_first_and_the_longest_untouched_removable_first()
    {
        using var factory = await SeededAsync();
        var rows = factory.Services.GetRequiredService<SafetyService>().Get();

        Assert.Equal(["stale-work", "notes", "clean"], SafetyView.AtRisk(rows, SafetySort.Age).Select(r => r.RepoName));
        var severe = SafetyView.AtRisk(rows, SafetySort.Severity);
        Assert.Equal([RiskLevel.High, RiskLevel.High, RiskLevel.Medium], severe.Select(r => r.Safety.Level));
        Assert.Equal("clean", severe[^1].RepoName);

        var removable = SafetyView.Removable(rows);
        Assert.Equal(["/dev/clean", "/dev/caveats", "/dev/notes.worktrees/draft"], removable.Select(r => r.Entry.Path));
        Assert.Equal(4L * 1024 * 1024, removable.Sum(r => r.FreedBytes));
        Assert.Empty(SafetyView.Unclear(rows));

        var groups = SafetyView.Group(SafetyView.AtRisk(rows, SafetySort.Age), SafetyGrouping.Machine);
        Assert.Equal(["laptop-host", "desktop-host"], groups.Select(g => g.Title));
        Assert.Equal(2, groups[0].Rows.Count);

        Assert.Equal(SafetySort.Age, SafetyView.ParseSort("nonsense"));
        Assert.Equal(SafetyGrouping.Repo, SafetyView.ParseGrouping("REPO"));
    }

    [Fact]
    public async Task The_navigation_counts_repos_with_commits_on_no_remote()
    {
        using var factory = await SeededAsync();

        Assert.Equal(2, factory.Services.GetRequiredService<NavCounts>().Safety()); // stale-work and notes; uncommitted edits do not count

        var html = await GetAsync(factory, "/safety");
        Assert.Matches(new Regex(@"Safety\s*<span class=""nav-count danger""[^>]*>2</span>"), html);
        Assert.Contains("with commits on no remote", await GetAsync(factory, "/"));
    }

    [Fact]
    public async Task A_linked_worktree_is_listed_under_its_main_repo_not_as_a_project_of_its_own()
    {
        using var factory = await SeededAsync();

        var rows = await factory.Services.GetRequiredService<RepoCatalog>().GetAsync();
        var notes = Assert.Single(rows, r => r.Name == "notes");
        Assert.Equal("local:laptop:/dev/notes", notes.Key);
        Assert.Equal(["/dev/notes", "/dev/notes.worktrees/draft"], notes.Clones.Select(c => c.Entry.Path));
        Assert.Equal(WorktreeStaleKind.Merged, notes.Clones[1].Stale!.Kind);
        Assert.Same(notes.Clones[0].Entry, notes.Clones[1].Main);
        Assert.DoesNotContain(rows, r => r.Name.Contains("draft", StringComparison.Ordinal));
        Assert.Equal(4, rows.Count); // clean (both machines), caveats, stale-work, notes

        var local = await GetAsync(factory, "/local");
        Assert.Contains("worktree-child", local);
        Assert.Contains("↳ notes.worktrees/draft", local);
        Assert.Contains("A linked worktree of notes", local);

        // Its findings belong to the same repo in the Cleanup page's by-repo view.
        var findings = await factory.Services.GetRequiredService<FindingsService>().GetAsync();
        Assert.Contains(findings, f => f.Finding.RuleId == "stale-worktree" && f.Finding.RepoKey == notes.Key);
    }

    [Fact]
    public async Task The_repo_page_shows_a_safety_badge_per_device()
    {
        using var factory = await SeededAsync();

        var html = await GetAsync(factory, "/repo?key=github.com%2Facme%2Fclean");

        Assert.Matches(new Regex(@"<a class=""chip safety-badge ok"" href=""safety""[^>]*>safe to remove</a>"), html);
        Assert.Matches(new Regex(@"<a class=""chip safety-badge danger"" href=""safety"" title=""2 uncommitted changes, last edited 1 day ago"">at risk</a>"), html);
    }

    [Fact]
    public async Task Machines_with_an_older_agent_are_never_listed_as_safe_and_the_page_says_why()
    {
        using var factory = new HubFactory();
        var snapshot = HubFactory.Snapshot();
        // What an agent before the safety facts sends for a clean, pushed clone.
        var old = snapshot.Entries[0] with { Name = "tidy", Path = "/dev/tidy", Git = snapshot.Entries[0].Git! with { Ahead = 0, Unstaged = 0, Branches = [snapshot.Entries[0].Git!.Branches[1]], CommonDir = "/dev/tidy/.git" } };
        await factory.Services.GetRequiredService<SnapshotStore>().IngestAsync(snapshot with { Entries = [.. snapshot.Entries, old] });

        var html = await GetAsync(factory, "/safety");

        Assert.Contains("mac-host runs an agent that does not report tags, ignored files or submodules yet", html);
        Assert.Contains("Nothing is known to be safe to remove.", html);
        Assert.Contains("1 working tree not sure about", html);
        Assert.Contains("The agent on this machine is too old to check tags, ignored files and submodules. Update it.", html);
        // What the old agent does report still counts as at risk.
        Assert.Contains("2 commits on feature/login not on any remote", html);
        Assert.Contains("3 uncommitted changes", html);
    }
}
