using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevDash.Contracts;
using DevDash.Core.GitHub;
using DevDash.Hub.Alerts;
using DevDash.Hub.Data;
using DevDash.Hub.GitHub;
using DevDash.Hub.Mcp;
using DevDash.Hub.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DevDash.Hub.Tests;

/// <summary>§20: "Where I left off" (the pure list and its page), and tags, notes and pins (rules, storage, filters, alerts, ⌘K, MCP).</summary>
public class LeftOffAndTagsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    // ---- fixtures -------------------------------------------------------------------------------

    private static MachineView Machine(string id, params DevEntry[] entries) =>
        new(new MachineSnapshot
        {
            Machine = new MachineInfo { MachineId = id, Hostname = id + "-host", Os = "macOS", AgentVersion = "0.7.0" },
            ScannedAt = Now,
            Roots = ["/dev"],
            Entries = entries,
        }, Now.AddDays(-30), Now);

    private static DevEntry Repo(string name, DateTimeOffset? commit = null, DateTimeOffset? edited = null, string branch = "main", string? path = null,
        IReadOnlyList<T3Thread>? threads = null, int ahead = 0, int? unpushed = null, string? upstream = "origin/main", IReadOnlyList<RecentChange>? recent = null, int stashes = 0)
    {
        path ??= "/dev/" + name;
        return new DevEntry
        {
            Root = "/dev",
            Name = name,
            Path = path,
            Kind = EntryKind.GitRepo,
            T3 = threads is null ? null : new T3Project { ProjectId = "p-" + name, Threads = threads },
            Git = new GitState
            {
                Branch = branch,
                HeadSha = "1111111111111111111111111111111111111111",
                Upstream = upstream,
                Ahead = ahead,
                Unstaged = edited is null ? 0 : 2,
                Untracked = recent?.Count(r => r.Untracked == true) ?? 0,
                NewestTrackedChangeAt = edited,
                RecentChanges = recent,
                CommonDir = path + "/.git",
                LastCommit = commit is { } c ? new CommitInfo { Sha = "1111111111111111111111111111111111111111", Date = c, Subject = "Wire up the form", Author = "me" } : null,
                Remotes = [new RemoteInfo { Name = "origin", Url = $"https://github.com/acme/{name}.git", Normalized = $"github.com/acme/{name}" }],
                Branches = [new BranchInfo { Name = branch, Sha = "1111111111111111111111111111111111111111", Upstream = upstream, Ahead = ahead, IsCurrent = true, UnpushedCount = unpushed }],
                Stashes = [.. Enumerable.Range(0, stashes).Select(i => new StashInfo { Index = i, Message = "wip", Date = Now })],
            },
        };
    }

    private static RepoRow Row(string name, params (MachineView Machine, DevEntry Entry)[] clones) => new()
    {
        Key = "github.com/acme/" + name,
        Name = name,
        Owner = "acme",
        Kind = EntryKind.GitRepo,
        Clones = clones.Select(c => new CloneView(c.Machine, c.Entry, null)).ToList(),
    };

    private static RepoRow Row(string name, DevEntry entry, string machine = "laptop") => Row(name, (Machine(machine, entry), entry));

    private static RepoNoteSet Notes(params RepoNote[] notes) => new(notes, ["archived"]);

    // ---- last touched ---------------------------------------------------------------------------

    [Fact]
    public void Last_touched_is_the_newest_of_commit_edit_and_T3_activity()
    {
        var commitOnly = Repo("a", commit: Now.AddHours(-5));
        Assert.Equal(new Touch(Now.AddHours(-5), TouchKind.Commit), LeftOff.LastTouched(commitOnly, []));

        var edited = Repo("a", commit: Now.AddHours(-5), edited: Now.AddHours(-1));
        Assert.Equal(new Touch(Now.AddHours(-1), TouchKind.Edit), LeftOff.LastTouched(edited, []));

        // An untracked file and the recent paths count as edits too.
        var untracked = Repo("a", commit: Now.AddHours(-5), recent: [new RecentChange { Path = "notes.md", ModifiedAt = Now.AddMinutes(-20), Untracked = true }]);
        Assert.Equal(new Touch(Now.AddMinutes(-20), TouchKind.Edit), LeftOff.LastTouched(untracked, []));

        T3Thread[] threads = [new T3Thread { Id = "t", UpdatedAt = Now.AddMinutes(-2) }, new T3Thread { Id = "u", CreatedAt = Now.AddMinutes(-9) }];
        Assert.Equal(new Touch(Now.AddMinutes(-2), TouchKind.T3), LeftOff.LastTouched(edited, threads));

        Assert.Null(LeftOff.LastTouched(new DevEntry { Root = "/dev", Name = "x", Path = "/dev/x", Kind = EntryKind.GitRepo, Git = new GitState() }, []));
    }

    [Fact]
    public void A_thread_is_on_the_working_tree_it_names_or_else_on_the_branch_checked_out()
    {
        var main = new CloneView(Machine("m"), Repo("w", branch: "main"), null);
        var wt = new CloneView(Machine("m"), Repo("w-fix", branch: "t3/fix", path: "/dev/w.worktrees/fix"), "w.worktrees");
        T3Thread[] threads =
        [
            new() { Id = "1", Branch = "t3/fix", WorktreePath = "/dev/w.worktrees/fix/", UpdatedAt = Now },
            new() { Id = "2", Branch = "main", UpdatedAt = Now.AddHours(-1) },
            new() { Id = "3", Branch = "main", WorktreePath = "/elsewhere", UpdatedAt = Now },
        ];

        Assert.Equal(["2"], LeftOff.ThreadsOn(main, threads).Select(t => t.Id));
        Assert.Equal(["1"], LeftOff.ThreadsOn(wt, threads).Select(t => t.Id));
    }

    [Fact]
    public void Days_group_by_the_calendar_of_the_hubs_time_zone()
    {
        // UTC+10: 15:00 UTC is already 01:00 tomorrow there.
        var plusTen = TimeZoneInfo.CreateCustomTimeZone("plus-ten", TimeSpan.FromHours(10), "plus-ten", "plus-ten");
        Assert.Equal(LeftOffGroup.Today, LeftOff.GroupOf(Now.AddHours(-1), Now, plusTen));
        Assert.Equal(LeftOffGroup.Yesterday, LeftOff.GroupOf(Now.AddHours(-2), Now, plusTen)); // 23:00 the day before, there
        Assert.Equal(LeftOffGroup.Today, LeftOff.GroupOf(Now.AddHours(-2), Now, Utc));

        Assert.Equal(LeftOffGroup.Yesterday, LeftOff.GroupOf(Now.AddDays(-1), Now, Utc));
        Assert.Equal(LeftOffGroup.ThisWeek, LeftOff.GroupOf(Now.AddDays(-2), Now, Utc));
        Assert.Equal(LeftOffGroup.ThisWeek, LeftOff.GroupOf(Now.AddDays(-6), Now, Utc));
        Assert.Equal(LeftOffGroup.Earlier, LeftOff.GroupOf(Now.AddDays(-7), Now, Utc));
        Assert.Equal(LeftOffGroup.Today, LeftOff.GroupOf(Now.AddMinutes(5), Now, Utc)); // a clock a little ahead
        Assert.Equal(["Pinned", "Today", "Yesterday", "This week", "Earlier"], Enum.GetValues<LeftOffGroup>().Select(LeftOff.Label));
    }

    // ---- the list -------------------------------------------------------------------------------

    [Fact]
    public void The_list_takes_each_repos_most_recently_touched_clone_newest_first_within_the_window()
    {
        var laptop = Repo("widget", commit: Now.AddDays(-3));
        var desktop = Repo("widget", commit: Now.AddDays(-3), edited: Now.AddHours(-2), branch: "feature/login", ahead: 2, upstream: "origin/feature/login",
            recent: [new RecentChange { Path = "src/Login.razor", ModifiedAt = Now.AddHours(-2) }, new RecentChange { Path = "todo.md", ModifiedAt = Now.AddHours(-3), Untracked = true }], stashes: 1);
        var widget = Row("widget", (Machine("laptop", laptop), laptop), (Machine("desktop", desktop), desktop));
        var gadget = Row("gadget", Repo("gadget", commit: Now.AddDays(-1)));
        var old = Row("old", Repo("old", commit: Now.AddDays(-20)));
        var folder = new RepoRow { Key = "local:m:/dev/docs", Name = "docs", Kind = EntryKind.NonGitFolder, Clones = [new CloneView(Machine("m"), new DevEntry { Root = "/dev", Name = "docs", Path = "/dev/docs", Kind = EntryKind.NonGitFolder, LastModified = Now }, null)] };

        var items = LeftOff.Build([old, gadget, widget, folder], Notes(), new LeftOffQuery(), Now, Utc);

        Assert.Equal(["widget", "gadget"], items.Select(i => i.Row.Name));
        var top = items[0];
        Assert.Equal("desktop", top.WorkingTree.Machine.MachineId);
        Assert.Equal(LeftOffGroup.Today, top.Group);
        Assert.Equal(TouchKind.Edit, top.Touched!.Kind);
        Assert.Equal("feature/login", top.Branch);
        Assert.Equal((2, 1, 1, 2), (top.Uncommitted, top.Untracked, top.Stashes, top.Unpushed));
        Assert.Equal(["src/Login.razor", "todo.md"], top.RecentFiles.Select(f => f.Path));
        Assert.Equal("laptop", Assert.Single(top.AlsoTouched).Clone.Machine.MachineId);
        Assert.Equal(LeftOffGroup.Yesterday, items[1].Group);

        // A wider window brings the old one in; a limit cuts the list.
        Assert.Equal(3, LeftOff.Build([old, gadget, widget], Notes(), new LeftOffQuery { Days = 30 }, Now, Utc).Count);
        Assert.Single(LeftOff.Build([old, gadget, widget], Notes(), new LeftOffQuery { Limit = 1 }, Now, Utc));
    }

    [Fact]
    public void Unpushed_is_the_agents_count_else_ahead_of_the_upstream_else_nothing()
    {
        Assert.Equal(4, LeftOff.Build([Row("a", Repo("a", commit: Now, ahead: 1, unpushed: 4))], Notes(), new(), Now, Utc)[0].Unpushed);
        Assert.Equal(3, LeftOff.Build([Row("a", Repo("a", commit: Now, ahead: 3))], Notes(), new(), Now, Utc)[0].Unpushed);
        Assert.Equal(0, LeftOff.Build([Row("a", Repo("a", commit: Now, upstream: null))], Notes(), new(), Now, Utc)[0].Unpushed);
    }

    [Fact]
    public void The_branchs_open_PR_and_T3_threads_come_with_it()
    {
        var entry = Repo("widget", commit: Now.AddHours(-4), branch: "feature/login", upstream: "origin/feature/login",
            threads: [new T3Thread { Id = "t1", Title = "Finish login", Branch = "feature/login", UpdatedAt = Now.AddMinutes(-10), PendingRequests = 1 }, new T3Thread { Id = "t2", Branch = "other", UpdatedAt = Now }]);
        var row = Row("widget", entry) with
        {
            GitHub = new GitHubRepo { NameWithOwner = "acme/widget", Url = "https://github.com/acme/widget" },
            PullRequests =
            [
                new PullRequestInfo { Repo = "acme/widget", Number = 7, Title = "Login", Url = "https://github.com/acme/widget/pull/7", HeadRef = "feature/login", Ci = CiState.Failure },
                new PullRequestInfo { Repo = "acme/widget", Number = 8, Title = "Other", Url = "https://github.com/acme/widget/pull/8", HeadRef = "other" },
            ],
        };

        var item = Assert.Single(LeftOff.Build([row], Notes(), new(), Now, Utc));

        Assert.Equal(7, item.PullRequest!.Number);
        Assert.Equal("t1", Assert.Single(item.Threads).Id);
        Assert.Equal(new Touch(Now.AddMinutes(-10), TouchKind.T3), item.Touched);
        Assert.Equal("https://github.com/acme/widget/pull/7", LeftOff.GitHubUrl(item));
        Assert.Equal("https://github.com/acme/widget/tree/feature/login", LeftOff.GitHubUrl(item with { PullRequest = null }));
    }

    [Fact]
    public void Pinned_repos_come_first_whenever_touched_and_quiet_ones_only_when_asked_and_tags_filter()
    {
        var rows = new[]
        {
            Row("recent", Repo("recent", commit: Now.AddHours(-1))),
            Row("pinned", Repo("pinned", commit: Now.AddDays(-60))),
            Row("archived", Repo("archived", commit: Now.AddHours(-2))),
            Row("client", Repo("client", commit: Now.AddDays(-2))),
        };
        var notes = Notes(
            new RepoNote("github.com/acme/pinned", ["client"], "", true),
            new RepoNote("GitHub.com/acme/ARCHIVED", ["archived", "client"], "done", false),
            new RepoNote("github.com/acme/client", ["client"], "invoice by friday", false));

        var items = LeftOff.Build(rows, notes, new(), Now, Utc);
        Assert.Equal(["pinned", "recent", "client"], items.Select(i => i.Row.Name));
        Assert.Equal(LeftOffGroup.Pinned, items[0].Group);
        Assert.Equal("invoice by friday", items[2].Note.Note);

        Assert.Equal(["pinned", "recent", "archived", "client"], LeftOff.Build(rows, notes, new() { IncludeQuiet = true }, Now, Utc).Select(i => i.Row.Name));
        Assert.Equal(["pinned", "client"], LeftOff.Build(rows, notes, new() { Tag = "#Client" }, Now, Utc).Select(i => i.Row.Name));
        Assert.Equal(["pinned", "archived", "client"], LeftOff.Build(rows, notes, new() { Tag = "client", IncludeQuiet = true }, Now, Utc).Select(i => i.Row.Name));
    }

    // ---- tag and note rules ---------------------------------------------------------------------

    [Fact]
    public void Tags_are_short_lower_case_words_and_notes_one_short_line()
    {
        Assert.Equal("client-work", RepoTags.Normalize("  #Client Work!! "));
        Assert.Equal("go/v2:x_y.z", RepoTags.Normalize("go/v2:x_y.z"));
        Assert.Null(RepoTags.Normalize("#"));
        Assert.Null(RepoTags.Normalize("!!!"));
        Assert.Equal(RepoTags.MaxTagLength, RepoTags.Normalize(new string('a', 100))!.Length);

        Assert.Equal(["client", "acme", "archived"], RepoTags.Parse("#Client, acme  client;ARCHIVED"));
        Assert.Equal(RepoTags.MaxTags, RepoTags.Parse(string.Join(' ', Enumerable.Range(0, 30).Select(i => "t" + i))).Count);
        Assert.Empty(RepoTags.Parse(null));

        Assert.Equal("Ship the login form, then tidy", RepoTags.NormalizeNote("  Ship the login\nform,\t then   tidy\0 "));
        Assert.Equal(RepoTags.MaxNoteLength, RepoTags.NormalizeNote(new string('x', 500)).Length);
        Assert.Equal("", RepoTags.NormalizeNote("   "));

        Assert.Equal(("client", "widget"), RepoTags.SplitSearch("widget #Client"));
        Assert.Equal((null, "#12 fix"), RepoTags.SplitSearch("#12 fix")); // a PR number, not a tag
        Assert.Equal((null, "#"), RepoTags.SplitSearch("#"));

        Assert.Equal([2, 4, 1, 3], RepoTags.PinnedFirst([1, 2, 3, 4], n => n % 2 == 0));
    }

    [Fact]
    public void A_note_set_ignores_key_case_and_knows_its_quiet_repos_and_tag_counts()
    {
        var set = new RepoNoteSet(
            [
                new RepoNote("github.com/acme/a", ["client", "archived"], "", false),
                new RepoNote("local:mac:/Dev/B", ["client"], "n", true),
                new RepoNote("github.com/acme/empty", [], "", false),
            ],
            ["Archived", "#old"]);

        Assert.True(set.IsPinned("LOCAL:MAC:/dev/b"));
        Assert.True(set.IsQuiet("GitHub.com/acme/a"));
        Assert.False(set.IsQuiet("local:mac:/Dev/B"));
        Assert.Equal(["archived", "old"], set.QuietTags.Order(StringComparer.Ordinal));
        Assert.Contains("github.com/ACME/a", set.QuietKeys);
        Assert.Equal([("client", 2), ("archived", 1)], set.TagCounts());
        Assert.True(set.Matches("anything", null));
        Assert.False(set.Matches("github.com/acme/empty", "client"));
        Assert.Equal(2, set.All.Count); // an empty note is no note
    }

    // ---- storage and schema ---------------------------------------------------------------------

    [Fact]
    public async Task Notes_are_saved_per_repo_key_whatever_its_case_and_an_empty_one_removes_the_row()
    {
        using var factory = new HubFactory();
        var notes = factory.Services.GetRequiredService<RepoNotes>();
        var changes = new List<DashboardChange>();
        factory.Services.GetRequiredService<DashboardNotifier>().Changed += changes.Add;

        var saved = await notes.SaveAsync("GitHub.com/Acme/Widget", ["#Client", "client", "Big Project"], "  Next: the\nlogin form ", pinned: true, "me@example.com");

        Assert.Equal(["client", "big-project"], saved.Tags);
        Assert.Equal("Next: the login form", saved.Note);
        Assert.Contains(DashboardChange.Notes, changes);

        var set = await notes.GetAsync();
        var read = set.For("github.com/acme/widget");
        Assert.Equal(["client", "big-project"], read.Tags);
        Assert.Equal(("Next: the login form", true, "me@example.com"), (read.Note, read.Pinned, read.UpdatedBy));
        Assert.True(set.IsPinned("github.com/ACME/widget"));

        // A second hub process reads the same rows.
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<DevDashDbContext>>().CreateDbContextAsync())
        {
            var row = await db.RepoNotes.SingleAsync();
            Assert.Equal(("github.com/acme/widget", "client big-project"), (row.Key, row.Tags));
        }

        await notes.SetPinnedAsync(read, false, "me");
        Assert.False((await notes.GetAsync()).IsPinned("github.com/acme/widget"));

        await notes.SaveAsync("github.com/acme/widget", [], "", false, "me");
        Assert.Empty((await notes.GetAsync()).All);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<DevDashDbContext>>().CreateDbContextAsync())
        {
            Assert.Equal(0, await db.RepoNotes.CountAsync());
        }
    }

    [Fact]
    public async Task A_database_from_before_notes_gains_the_RepoNotes_table_on_start()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DevDashDbContext>().UseSqlite(connection).Options;

        await using (var db = new DevDashDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Cache.Add(new CacheEntity { Key = "kept", Json = "{}" });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"RepoNotes\""); // what a 0.7 hub's database looks like
        }

        await using (var db = new DevDashDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await Assert.ThrowsAsync<SqliteException>(() => db.RepoNotes.CountAsync());

            Assert.Contains("RepoNotes", await SchemaUpgrade.CreateMissingTablesAsync(db));

            db.RepoNotes.Add(new RepoNoteEntity { Key = "github.com/acme/widget", Tags = "client", Pinned = true });
            await db.SaveChangesAsync();
            Assert.Equal(1, await db.RepoNotes.CountAsync());
            Assert.Equal("kept", (await db.Cache.SingleAsync()).Key);
            Assert.Empty(await SchemaUpgrade.CreateMissingTablesAsync(db));
        }
    }

    // ---- alerts ---------------------------------------------------------------------------------

    [Fact]
    public void A_quiet_tag_silences_every_alert_about_that_repo_and_nothing_else()
    {
        var entry = AlertEvaluationTests.Repo("widget", Now, unpushedDays: 10);
        var machine = new MachineView(AlertEvaluationTests.Snapshot("laptop", Now, entry), Now.AddDays(-10), Now.AddDays(-2));
        var inputs = new AlertInputs
        {
            Machines = [machine],
            Safety = Core.Safety.RepoSafety.AssessAll(machine.Snapshot, Now).Select(c => new SafetyRow(machine, c)).ToList(),
            PullRequests = [new PullRequestInfo { Repo = "acme/widget", Number = 3, Title = "x", Url = "https://github.com/acme/widget/pull/3", Authored = true, Ci = CiState.Failure }],
        };
        var triggers = new AlertTriggerOptions();

        var loud = AlertEvaluator.Evaluate(inputs, triggers, Now);
        Assert.Contains(loud, c => c.Trigger == AlertTriggers.AtRisk && c.RepoKey == "github.com/acme/widget");
        Assert.Contains(loud, c => c.Trigger == AlertTriggers.CiFailed && c.RepoKey == "github.com/acme/widget");
        Assert.Contains(loud, c => c.Trigger == AlertTriggers.MachineOffline);

        var quiet = AlertEvaluator.Evaluate(inputs with { QuietRepos = Notes(new RepoNote("github.com/acme/widget", ["archived"], "", false)).QuietKeys }, triggers, Now);
        Assert.Equal([AlertTriggers.MachineOffline], quiet.Select(c => c.Trigger)); // the machine is not the repo
    }

    [Fact]
    public async Task Tagging_a_repo_archived_clears_its_open_alert_on_the_next_evaluation()
    {
        using var factory = new HubFactory();
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        await factory.Services.GetRequiredService<SnapshotStore>().IngestAsync(AlertEvaluationTests.Snapshot("laptop", now, AlertEvaluationTests.Repo("widget", now, unpushedDays: 10)));
        var alerts = factory.Services.GetRequiredService<AlertService>();

        Assert.Equal(1, (await alerts.EvaluateAsync()).Active);

        await factory.Services.GetRequiredService<RepoNotes>().SaveAsync("github.com/acme/widget", ["archived"], "", false, "me");
        var run = await alerts.EvaluateAsync();
        Assert.Equal((0, 1), (run.Active, run.Cleared));

        // The quiet list is configuration: with another list, "archived" is just a tag.
        using var other = new HubFactory();
        other.Settings["Hub:Tags:QuietTags"] = "parked, #Old";
        await other.Services.GetRequiredService<SnapshotStore>().IngestAsync(AlertEvaluationTests.Snapshot("laptop", now, AlertEvaluationTests.Repo("widget", now, unpushedDays: 10)));
        await other.Services.GetRequiredService<RepoNotes>().SaveAsync("github.com/acme/widget", ["archived"], "", false, "me");
        Assert.Equal(1, (await other.Services.GetRequiredService<AlertService>().EvaluateAsync()).Active);
        Assert.Equal(["old", "parked"], (await other.Services.GetRequiredService<RepoNotes>().GetAsync()).QuietTags.Order(StringComparer.Ordinal));
    }

    // ---- ⌘K -------------------------------------------------------------------------------------

    [Fact]
    public void The_palette_finds_repos_by_hash_tag_and_note_and_ranks_pinned_ones_higher()
    {
        var machine = Machine("laptop");
        var rows = new[] { Row("alpha", Repo("alpha", commit: Now)), Row("alpine", Repo("alpine", commit: Now)), Row("beta", Repo("beta", commit: Now)) };
        var notes = Notes(
            new RepoNote("github.com/acme/beta", ["client"], "Invoice before Friday", false),
            new RepoNote("github.com/acme/alpine", ["client"], "", true));
        var items = Palette.Items(rows, [], [machine], _ => false, 0, 0, null, notes);

        var tagged = Palette.Search("#client", items, []);
        Assert.Equal(["alpine", "beta"], tagged.Select(i => i.Title)); // only the tagged repos, the pinned one first
        Assert.Contains("#client", tagged[1].Detail);
        Assert.Equal("beta", Palette.Search("invoice", items, [])[0].Title);
        Assert.Equal("alpine", Palette.Search("alp", items, [])[0].Title); // equal matches: pinned wins
        Assert.Equal("Where I left off", Palette.Search("left off", items, [])[0].Title);
        Assert.Equal("page:left-off", Palette.IdOf("left-off?days=7"));

        // Without notes, as before.
        Assert.Empty(Palette.Search("#client", Palette.Items(rows, [], [machine], _ => false, 0, 0), []));
    }

    // ---- pages ----------------------------------------------------------------------------------

    private static async Task<HubFactory> SeededAsync()
    {
        var factory = new HubFactory();
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var snapshot = HubFactory.Snapshot("mac", now) with
        {
            Machine = new MachineInfo { MachineId = "mac", Hostname = "mac-host", Os = "macOS", AgentVersion = "0.7.0", T3 = new T3Info { Available = true } },
        };
        var widget = snapshot.Entries[0];
        var fresh = widget with
        {
            Git = widget.Git! with
            {
                NewestTrackedChangeAt = now.AddMinutes(-30),
                RecentChanges = [new RecentChange { Path = "src/LoginForm.razor", ModifiedAt = now.AddMinutes(-30) }],
                Stashes = [new StashInfo { Index = 0, Message = "On feature/login: parked", Date = now.AddDays(-1) }],
            },
            T3 = new T3Project { ProjectId = "p1", Threads = [new T3Thread { Id = "th-1", Title = "Finish the login form", Branch = "feature/login", Link = "https://app.t3.codes/t/th-1", UpdatedAt = now.AddMinutes(-5) }] },
        };
        var parked = HubFactory.Snapshot("mac", now).Entries[0] with { Name = "parked", Path = "/dev/parked" };
        parked = parked with
        {
            Git = parked.Git! with
            {
                NewestTrackedChangeAt = now.AddHours(-1),
                CommonDir = "/dev/parked/.git",
                Remotes = [new RemoteInfo { Name = "origin", Url = "https://github.com/acme/parked.git", Normalized = "github.com/acme/parked" }],
            },
        };
        await factory.Services.GetRequiredService<SnapshotStore>().IngestAsync(snapshot with { Entries = [fresh, parked, snapshot.Entries[1]] });

        var repo = new GitHubRepo { NameWithOwner = "acme/widget", Url = "https://github.com/acme/widget", PushedAt = now.AddHours(-3), DefaultBranch = "main" };
        await factory.Services.GetRequiredService<GitHubStore>().SetAsync(new GitHubSnapshot
        {
            ViewerLogin = "me",
            FetchedAt = now,
            LocalRemotes = new Dictionary<string, RemoteRepoLookup> { [repo.Key] = new() { Key = repo.Key, Found = true, Repo = repo, CheckedAt = now } },
            PullRequests = [new PullRequestInfo { Repo = "acme/widget", Number = 7, Title = "Add login form", Url = "https://github.com/acme/widget/pull/7", HeadRef = "feature/login", Authored = true, Ci = CiState.Failure, UpdatedAt = now }],
        });

        var notes = factory.Services.GetRequiredService<RepoNotes>();
        await notes.SaveAsync("github.com/acme/widget", ["client"], "Finish the login form before Friday", false, "me");
        await notes.SaveAsync("github.com/acme/parked", ["archived"], "", false, "me");
        return factory;
    }

    private static async Task<string> PageAsync(HubFactory factory, string url)
    {
        var response = await factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_left_off_page_shows_each_project_with_what_it_takes_to_pick_it_up()
    {
        using var factory = await SeededAsync();
        var html = await PageAsync(factory, "/left-off");

        Assert.Contains("Where I left off", html);
        Assert.Contains("Today", html);
        Assert.Contains("widget", html);
        Assert.Contains("feature/login", html);
        Assert.Contains("mac-host", html);
        Assert.Contains("Add login form", html); // the last commit's subject and the PR
        Assert.Contains("3 uncommitted", html);
        Assert.Contains("1 stash", html);
        Assert.Contains("↑2 unpushed", html);
        Assert.Contains("src/LoginForm.razor", html);
        Assert.Contains("#7", html);
        Assert.Contains("T3 · ", html);
        Assert.Contains("#client", html);
        Assert.Contains("Finish the login form before Friday", html);
        Assert.Contains("data-copy=\"cd '/dev/widget'\"", html);
        Assert.Contains("Open in T3", html);
        Assert.Contains("https://github.com/acme/widget/pull/7", html);
        Assert.Contains("href=\"repo?key=github.com%2Facme%2Fwidget\"", html);

        // Archived work stays out unless asked for.
        Assert.DoesNotContain(">parked<", html);
        Assert.Contains(">parked<", await PageAsync(factory, "/left-off?quiet=1"));
        Assert.Contains("Nothing was committed", await PageAsync(factory, "/left-off?tag=nope"));
    }

    [Fact]
    public async Task Overview_Local_and_the_repo_page_show_tags_notes_and_pins_and_filter_by_tag()
    {
        using var factory = await SeededAsync();
        await factory.Services.GetRequiredService<RepoNotes>().SaveAsync("local:mac:/dev/Reference Docs", ["docs"], "", true, "me");

        var overview = await PageAsync(factory, "/");
        Assert.Contains("Where I left off", overview);
        Assert.Contains("src/LoginForm.razor", overview);
        Assert.Contains("#client", overview);
        Assert.Contains("Left off", overview); // the navigation

        var local = await PageAsync(factory, "/local");
        Assert.Contains("#client", local);
        Assert.Contains("Finish the login form before Friday", local);
        Assert.Contains("All tags", local);

        // ?tag= keeps the tagged ones; pinned rows come first.
        var tagged = await PageAsync(factory, "/local?tag=client");
        Assert.Contains("widget", tagged);
        Assert.DoesNotContain(">parked<", tagged);
        Assert.Contains("title=\"Pinned: kept at the top of the lists\"", local); // the pinned folder

        var repo = await PageAsync(factory, "/repo?key=github.com%2Facme%2Fwidget");
        Assert.Contains("Edit tags and note", repo);
        Assert.Contains("Finish the login form before Friday", repo);
        Assert.Contains(">Pin<", repo);

        await factory.Services.GetRequiredService<RepoNotes>().SaveAsync("github.com/acme/parked", [], "", false, "me");
        Assert.Contains("Add tags or a note", await PageAsync(factory, "/repo?key=github.com%2Facme%2Fparked"));
    }

    // ---- MCP ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_mcp_project_tools_carry_tags_notes_and_pins_and_list_projects_filters_by_tag()
    {
        using var factory = await SeededAsync();
        await factory.Services.GetRequiredService<RepoNotes>().SaveAsync("github.com/acme/widget", ["client"], "Finish the login form before Friday", true, "me");
        var token = (await factory.Services.GetRequiredService<McpTokenService>().IssueAsync("t", McpScopes.Read, "me")).Plaintext;
        var http = factory.CreateClient();
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
            },
            http,
            ownsHttpClient: true));

        async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> args)
        {
            var result = await client.CallToolAsync(tool, args);
            var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));
            Assert.False(result.IsError == true, text);
            return JsonDocument.Parse(text).RootElement;
        }

        var tagged = await CallAsync("list_projects", new() { ["tag"] = "#client" });
        var project = Assert.Single(tagged.GetProperty("projects").EnumerateArray());
        Assert.Equal("widget", project.GetProperty("project").GetString());
        Assert.Equal(["client"], project.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("Finish the login form before Friday", project.GetProperty("note").GetString());
        Assert.True(project.GetProperty("pinned").GetBoolean());

        var all = await CallAsync("list_projects", new());
        var docs = all.GetProperty("projects").EnumerateArray().Single(p => p.GetProperty("project").GetString() == "Reference Docs");
        Assert.False(docs.TryGetProperty("tags", out _)); // nothing written: nothing sent

        var detail = await CallAsync("get_project", new() { ["project"] = "acme/widget" });
        Assert.Equal("client", detail.GetProperty("tags")[0].GetString());
        Assert.True(detail.GetProperty("pinned").GetBoolean());
        Assert.Equal(0, (await CallAsync("list_projects", new() { ["tag"] = "nope" })).GetProperty("total").GetInt32());
    }
}
