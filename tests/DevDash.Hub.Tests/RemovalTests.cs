using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevDash.Contracts;
using DevDash.Core.Safety;
using DevDash.Hub.Data;
using DevDash.Hub.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DevDash.Hub.Tests;

/// <summary>
/// The hub side of the 0.6.0 actions: what it will and will not ask an agent for (remove, clone back,
/// clean, fast-forward), the removed-projects list, and the buttons. No agent runs here; the tests
/// play the agent through its two endpoints.
/// </summary>
public class RemovalTests
{
    private const string Snapshots = "/api/v1/snapshots";
    private const string Claim = "/api/v1/actions/claim";
    private const string CleanUrl = "https://github.com/acme/clean.git";
    private const string Sha = "1111111111111111111111111111111111111111";

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <param name="modern">False for an agent from before 0.6.0: actions on, none of the new kinds.</param>
    private static MachineInfo Machine(string id, bool actions = true, bool modern = true) => new()
    {
        MachineId = id,
        Hostname = id + "-host",
        Os = "macOS",
        AgentVersion = modern ? "0.6.0" : "0.5.0",
        ActionsEnabled = actions,
        AcceptsRefresh = true,
        AcceptsRemoveProject = actions && modern,
        AcceptsClone = actions && modern,
        AcceptsCleanBuildDirs = actions && modern,
        AcceptsPull = actions && modern,
    };

    private static GitState Git(string path, string repo, Func<GitState, GitState>? with = null, SafetyFacts? facts = null)
    {
        var git = new GitState
        {
            Branch = "main",
            HeadSha = Sha,
            Upstream = "origin/main",
            DefaultBranch = "main",
            CommonDir = path + "/.git",
            LastCommit = new CommitInfo { Sha = Sha, Date = DateTimeOffset.UtcNow.AddDays(-30), Subject = "work", Author = "me" },
            Remotes = [new RemoteInfo { Name = "origin", Url = $"https://github.com/acme/{repo}.git", Normalized = $"github.com/acme/{repo}" }],
            Branches = [new BranchInfo { Name = "main", Sha = Sha, Upstream = "origin/main", IsCurrent = true, OnRemote = true, LastCommitDate = DateTimeOffset.UtcNow.AddDays(-30) }],
            Worktrees = [new WorktreeInfo { Path = path, Branch = "main" }],
            Safety = facts ?? new SafetyFacts { BranchesChecked = true, HeadOnRemote = true },
        };
        return with is null ? git : with(git);
    }

    private static DevEntry Repo(string name, GitState git, SizeInfo? size = null) => new()
    {
        Root = "/dev",
        Name = name,
        Path = "/dev/" + name,
        Kind = EntryKind.GitRepo,
        Git = git,
        LastModified = DateTimeOffset.UtcNow.AddDays(-30),
        Size = size ?? new SizeInfo { TotalBytes = 3L * 1024 * 1024, MeasuredAt = DateTimeOffset.UtcNow },
    };

    private static readonly SizeInfo WithBuildOutput = new()
    {
        TotalBytes = 900L * 1024 * 1024,
        ReclaimableBytes = 800L * 1024 * 1024,
        ReclaimableDirs = [new ReclaimableDir { Path = "/dev/app/node_modules", Bytes = 700L * 1024 * 1024 }, new ReclaimableDir { Path = "/dev/app/src/App/bin", Bytes = 100L * 1024 * 1024 }],
        MeasuredAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// clean: plainly safe. caveats: safe once .env and local.db are acknowledged. busy: an unpushed
    /// commit on main, and a branch that is behind. app: a repo with build output.
    /// </summary>
    private static DevEntry[] Entries() =>
    [
        Repo("clean", Git("/dev/clean", "clean")),
        Repo("caveats", Git("/dev/caveats", "caveats", facts: new SafetyFacts { BranchesChecked = true, HeadOnRemote = true, IgnoredFiles = [".env", "local.db"], IgnoredFileCount = 2 })),
        Repo("busy", Git("/dev/busy", "busy", g => g with
        {
            Ahead = 1,
            Branches =
            [
                new BranchInfo { Name = "main", Sha = Sha, Upstream = "origin/main", Ahead = 1, IsCurrent = true, OnRemote = false, UnpushedCount = 1, LastCommitDate = DateTimeOffset.UtcNow.AddDays(-2) },
                new BranchInfo { Name = "release", Sha = "2222222222222222222222222222222222222222", Upstream = "origin/release", Behind = 3, OnRemote = true, LastCommitDate = DateTimeOffset.UtcNow.AddDays(-9) },
            ],
        })),
        Repo("app", Git("/dev/app", "app"), WithBuildOutput),
    ];

    private static MachineSnapshot Snapshot(MachineInfo machine, params DevEntry[] entries) => new()
    {
        Machine = machine,
        ScannedAt = DateTimeOffset.UtcNow,
        Roots = ["/dev"],
        Entries = entries,
    };

    private static async Task<(HubFactory Factory, HttpClient Agent)> HubAsync(MachineInfo? machine = null, HubFactory? factory = null, DevEntry[]? entries = null)
    {
        factory ??= new HubFactory();
        machine ??= Machine("mac");
        var agent = factory.CreateAgentClient(await factory.IssueKeyAsync(machine.MachineId));
        await ReportAsync(agent, Snapshot(machine, entries ?? Entries()));
        return (factory, agent);
    }

    private static async Task ReportAsync(HttpClient agent, MachineSnapshot snapshot) =>
        (await agent.PostAsync(Snapshots, JsonContent.Create(snapshot, options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();

    private static ActionQueue Queue(HubFactory factory) => factory.Services.GetRequiredService<ActionQueue>();

    private static TombstoneStore Tombstones(HubFactory factory) => factory.Services.GetRequiredService<TombstoneStore>();

    private static SafetyRow Row(HubFactory factory, string name, string machine = "mac") =>
        factory.Services.GetRequiredService<SafetyService>().Get().Single(r => r.Entry.Name == name && r.Machine.MachineId == machine);

    private static AgentAction Removal(HubFactory factory, string name, IEnumerable<string>? acknowledged = null) =>
        AgentAction.RemoveProject("/dev/" + name, Row(factory, name).Safety.Fingerprint, acknowledged);

    private static async Task<List<PendingAgentAction>> ClaimAsync(HttpClient agent)
    {
        var response = await agent.PostAsync(Claim, null);
        response.EnsureSuccessStatusCode();
        return [.. (await response.Content.ReadFromJsonAsync<ActionClaimResponse>(SnapshotSchema.JsonOptions))!.Actions];
    }

    private static Task<HttpResponseMessage> ResultAsync(HttpClient agent, string id, bool succeeded, string output = "done") =>
        agent.PostAsync($"/api/v1/actions/{id}/result",
            JsonContent.Create(new ActionResultRequest { Succeeded = succeeded, ExitCode = succeeded ? 0 : 1, Output = output }, options: SnapshotSchema.JsonOptions));

    /// <summary>Queues the removal of <paramref name="name"/>, has the agent claim it, report success and stop reporting the folder.</summary>
    private static async Task RemoveAsync(HubFactory factory, HttpClient agent, string name, MachineInfo? machine = null)
    {
        var outcome = await Queue(factory).QueueAsync("mac", Removal(factory, name), "me@example.com");
        Assert.True(outcome.Ok, outcome.Error);
        var claimed = Assert.Single(await ClaimAsync(agent));
        (await ResultAsync(agent, claimed.Id, true, $"Moved /dev/{name} to the Trash.")).EnsureSuccessStatusCode();
        await ReportAsync(agent, Snapshot(machine ?? Machine("mac"), Entries().Where(e => e.Name != name).ToArray()));
    }

    private static async Task<string> PageAsync(HubFactory factory, string url)
    {
        var response = await factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    // ---- remove ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_safe_project_is_queued_with_the_fingerprint_that_was_shown()
    {
        var (factory, agent) = await HubAsync();
        using var _ = factory;
        var row = Row(factory, "clean");
        Assert.Equal(SafetyVerdict.Safe, row.Safety.Verdict);

        var outcome = await Queue(factory).QueueAsync("mac", Removal(factory, "clean"), "me@example.com");

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal(AgentActionKind.RemoveProject, outcome.Action!.Kind);
        Assert.Equal("trash '/dev/clean'", outcome.Action.CommandLine);
        var sent = Assert.Single(await ClaimAsync(agent)).Action;
        Assert.Equal("/dev/clean", sent.RepoPath);
        Assert.Equal(row.Safety.Fingerprint, sent.ExpectedFingerprint);
        Assert.Null(sent.AcknowledgedCaveats);
    }

    [Fact]
    public async Task The_hub_will_not_ask_for_a_removal_that_is_unsafe_stale_or_unacknowledged()
    {
        var (factory, agent) = await HubAsync();
        using var _ = factory;
        var queue = Queue(factory);
        var clean = Row(factory, "clean").Safety.Fingerprint;

        async Task Refused(AgentAction action, string because)
        {
            var outcome = await queue.QueueAsync("mac", action, "me");
            Assert.False(outcome.Ok);
            Assert.Contains(because, outcome.Error);
        }

        await Refused(Removal(factory, "busy"), "not safe to remove");
        await Refused(AgentAction.RemoveProject("/dev/busy", clean), "not safe to remove");
        await Refused(AgentAction.RemoveProject("/dev/clean", "0123456789abcdef01234567"), "has changed since this page was drawn");
        await Refused(AgentAction.RemoveProject("/dev/clean", Row(factory, "caveats").Safety.Fingerprint), "has changed since this page was drawn");
        await Refused(Removal(factory, "caveats"), "have not been acknowledged");
        await Refused(Removal(factory, "caveats", [".env"]), "have not been acknowledged");
        await Refused(AgentAction.RemoveProject("/dev/nope", clean), "no longer reports a repository");
        await Refused(AgentAction.RemoveProject("/etc", clean), "no longer reports a repository");
        await Refused(AgentAction.RemoveProject("/dev/clean", "nonsense"), "not a safety fingerprint");

        Assert.Empty(await queue.RecentAsync());
        Assert.Empty(await ClaimAsync(agent));

        var acknowledged = await queue.QueueAsync("mac", Removal(factory, "caveats", [".env", "local.db"]), "me");
        Assert.True(acknowledged.Ok, acknowledged.Error);
        Assert.Equal([".env", "local.db"], Assert.Single(await ClaimAsync(agent)).Action.AcknowledgedCaveats);
    }

    [Fact]
    public async Task A_project_that_changes_after_the_page_was_drawn_cannot_be_removed_with_the_old_fingerprint()
    {
        var (factory, agent) = await HubAsync();
        using var _ = factory;
        var shown = Removal(factory, "clean");

        // A new commit lands and the machine reports again before the user clicks.
        var moved = Entries().Select(e => e.Name == "clean" ? e with { Git = e.Git! with { HeadSha = "9999999999999999999999999999999999999999" } } : e).ToArray();
        await ReportAsync(agent, Snapshot(Machine("mac"), moved));

        var outcome = await Queue(factory).QueueAsync("mac", shown, "me");
        Assert.False(outcome.Ok);
        Assert.Contains("has changed since this page was drawn", outcome.Error);
    }

    [Fact]
    public async Task An_agent_that_is_too_old_or_has_actions_off_is_never_asked()
    {
        var factory = new HubFactory();
        using var _ = factory;
        var (_, old) = await HubAsync(Machine("old", modern: false), factory);
        var (_, off) = await HubAsync(Machine("off", actions: false), factory);
        var queue = Queue(factory);
        AgentAction[] actions =
        [
            AgentAction.RemoveProject("/dev/clean", Row(factory, "clean", "old").Safety.Fingerprint),
            AgentAction.CleanBuildDirs("/dev/app", ["/dev/app/node_modules"]),
            AgentAction.PullFastForward("/dev/busy", "origin", "release"),
            AgentAction.CloneInto("/dev/clean", CleanUrl),
        ];

        foreach (var action in actions)
        {
            var tooOld = await queue.QueueAsync("old", action, "me");
            Assert.False(tooOld.Ok);
            Assert.Contains("Update the agent on old-host to 0.6.0 or newer", tooOld.Error);

            var switchedOff = await queue.QueueAsync("off", action, "me");
            Assert.False(switchedOff.Ok);
            Assert.Contains("has not enabled agent actions", switchedOff.Error);
        }

        // The kinds it does know still work on the old agent.
        Assert.True((await queue.QueueAsync("old", AgentAction.Fetch("/dev/clean"), "me")).Ok);
        Assert.Equal(AgentActionKind.Fetch, Assert.Single(await ClaimAsync(old)).Action.Kind);
        Assert.Empty(await ClaimAsync(off));
    }

    [Fact]
    public async Task An_agent_that_stops_saying_it_takes_a_kind_is_not_handed_it()
    {
        var (factory, agent) = await HubAsync();
        using var _ = factory;
        Assert.True((await Queue(factory).QueueAsync("mac", Removal(factory, "clean"), "me")).Ok);
        Assert.True((await Queue(factory).QueueAsync("mac", AgentAction.Fetch("/dev/clean"), "me")).Ok);

        // The machine was rolled back to an older agent before it picked the work up: it could not read a RemoveProject.
        await ReportAsync(agent, Snapshot(Machine("mac", modern: false), Entries()));

        Assert.Equal(AgentActionKind.Fetch, Assert.Single(await ClaimAsync(agent)).Action.Kind);
        Assert.Contains(await Queue(factory).RecentAsync(), a => a is { Kind: AgentActionKind.RemoveProject, Status: AgentActionStatus.Queued });
    }

    // ---- tombstones -----------------------------------------------------------------------------

    [Fact]
    public async Task A_removal_that_succeeds_leaves_a_tombstone_with_what_it_takes_to_clone_it_back()
    {
        var (factory, agent) = await HubAsync();
        using var _ = factory;
        Assert.True((await Queue(factory).QueueAsync("mac", Removal(factory, "clean"), "me@example.com")).Ok);
        var claimed = Assert.Single(await ClaimAsync(agent));

        // Queued and claimed is not removed: nothing is listed until the agent says it is done.
        Assert.Empty(await Tombstones(factory).ListAsync());

        (await ResultAsync(agent, claimed.Id, true)).EnsureSuccessStatusCode();
        // Still reported by the machine until its rescan lands, so still not listed as gone.
        Assert.Empty(await Tombstones(factory).ListAsync());
        await ReportAsync(agent, Snapshot(Machine("mac"), Entries().Where(e => e.Name != "clean").ToArray()));

        var removed = Assert.Single(await Tombstones(factory).ListAsync());
        Assert.Equal("mac", removed.Tombstone.MachineId);
        Assert.Equal("mac-host", removed.Tombstone.MachineName);
        Assert.Equal("/dev/clean", removed.Tombstone.Path);
        Assert.Equal("clean", removed.Tombstone.Name);
        Assert.Equal("github.com/acme/clean", removed.Tombstone.RepoKey);
        Assert.Equal("main", removed.Tombstone.DefaultBranch);
        Assert.Equal(Sha, removed.Tombstone.HeadSha);
        Assert.Equal(3L * 1024 * 1024, removed.Tombstone.SizeBytes);
        Assert.Equal("me@example.com", removed.Tombstone.RemovedBy);
        Assert.NotNull(removed.Tombstone.RemovedAt);
        Assert.Equal(CleanUrl, Assert.Single(removed.Remotes).Url);
        Assert.Equal(AgentAction.CloneInto("/dev/clean", CleanUrl), removed.CloneAction);
    }

    [Fact]
    public async Task A_removal_that_fails_or_expires_leaves_no_tombstone()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var (factory, agent) = await HubAsync(factory: new HubFactory { Time = clock });
        using var _ = factory;
        var db = factory.Services.GetRequiredService<IDbContextFactory<DevDashDbContext>>();
        async Task<int> RowsAsync()
        {
            await using var context = await db.CreateDbContextAsync();
            return await context.RemovedProjects.CountAsync();
        }

        // Refused by the agent.
        Assert.True((await Queue(factory).QueueAsync("mac", Removal(factory, "clean"), "me")).Ok);
        Assert.Equal(1, await RowsAsync());
        (await ResultAsync(agent, Assert.Single(await ClaimAsync(agent)).Id, false, "Refused: it has changed.")).EnsureSuccessStatusCode();
        Assert.Equal(0, await RowsAsync());

        // Never picked up.
        Assert.True((await Queue(factory).QueueAsync("mac", Removal(factory, "clean"), "me")).Ok);
        Assert.Equal(1, await RowsAsync());
        clock.Now += TimeSpan.FromMinutes(11);
        Assert.Equal(1, await Queue(factory).ExpireStaleAsync());
        Assert.Equal(0, await RowsAsync());
        Assert.Empty(await Tombstones(factory).ListAsync());
    }

    [Fact]
    public async Task A_tombstone_goes_when_the_folder_is_back_or_it_is_forgotten()
    {
        var (factory, agent) = await HubAsync();
        using var _ = factory;
        await RemoveAsync(factory, agent, "clean");
        var removed = Assert.Single(await Tombstones(factory).ListAsync());

        // Dragged back out of the trash, or cloned by hand: the machine reports the folder again.
        await ReportAsync(agent, Snapshot(Machine("mac"), Entries()));
        Assert.Empty(await Tombstones(factory).ListAsync());

        await ReportAsync(agent, Snapshot(Machine("mac"), Entries().Where(e => e.Name != "clean").ToArray()));
        Assert.Single(await Tombstones(factory).ListAsync());
        await Tombstones(factory).ForgetAsync(removed.Tombstone.Id);
        Assert.Empty(await Tombstones(factory).ListAsync());
    }

    // ---- clone ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_removed_project_can_be_cloned_back_and_that_ends_its_tombstone()
    {
        var (factory, agent) = await HubAsync();
        using var _ = factory;
        await RemoveAsync(factory, agent, "clean");
        var clone = Assert.Single(await Tombstones(factory).ListAsync()).CloneAction!;

        var outcome = await Queue(factory).QueueAsync("mac", clone, "me");

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal($"git clone -- '{CleanUrl}' '/dev/clean'", outcome.Action!.CommandLine);
        var claimed = Assert.Single(await ClaimAsync(agent));
        Assert.Equal(AgentActionKind.Clone, claimed.Action.Kind);
        Assert.Equal(CleanUrl, claimed.Action.Url);

        (await ResultAsync(agent, claimed.Id, true, "Cloning into '/dev/clean'...")).EnsureSuccessStatusCode();
        Assert.Empty(await Tombstones(factory).ListAsync());
    }

    [Fact]
    public async Task The_hub_never_asks_an_agent_to_clone_a_url_the_project_did_not_have()
    {
        var factory = new HubFactory();
        using var _ = factory;
        var (_, agent) = await HubAsync(factory: factory);
        var queue = Queue(factory);

        async Task Refused(AgentAction action, string because)
        {
            var outcome = await queue.QueueAsync("mac", action, "me");
            Assert.False(outcome.Ok);
            Assert.Contains(because, outcome.Error);
        }

        // Nothing has been removed: there is nothing to clone back, whatever the URL.
        await Refused(AgentAction.CloneInto("/dev/clean", CleanUrl), "Nothing was removed");
        await Refused(AgentAction.CloneInto("/dev/brand-new", CleanUrl), "Nothing was removed");

        await RemoveAsync(factory, agent, "clean");
        await Refused(AgentAction.CloneInto("/dev/clean", "https://github.com/evil/clean.git"), "not a remote of the project that was removed");
        await Refused(AgentAction.CloneInto("/dev/clean", "https://github.com/acme/app.git"), "not a remote of the project that was removed"); // a known URL, the wrong project
        await Refused(AgentAction.CloneInto("/dev/elsewhere", CleanUrl), "Nothing was removed");
        await Refused(AgentAction.CloneInto("/dev/clean", "--upload-pack=x"), "not a plain URL");
        Assert.DoesNotContain(await queue.RecentAsync(), a => a.Kind == AgentActionKind.Clone);

        // Another machine's remote for the same repo counts: the same project, spelled the way that machine uses it.
        await HubAsync(Machine("desk"), factory, [Repo("clean", Git("/dev/clean", "clean", g => g with
        {
            Remotes = [new RemoteInfo { Name = "origin", Url = "git@github.com:acme/clean.git", Normalized = "github.com/acme/clean" }],
        }))]);
        Assert.True((await queue.QueueAsync("mac", AgentAction.CloneInto("/dev/clean", "git@github.com:acme/clean.git"), "me")).Ok);

        // A tombstone belongs to its machine: the other machine has no removal to undo.
        var onOther = await queue.QueueAsync("desk", AgentAction.CloneInto("/dev/clean2", CleanUrl), "me");
        Assert.False(onOther.Ok);
        Assert.Contains("Nothing was removed", onOther.Error);
    }

    [Fact]
    public async Task A_clone_is_not_asked_for_once_something_is_at_that_path_again()
    {
        var (factory, agent) = await HubAsync();
        using var _ = factory;
        await RemoveAsync(factory, agent, "clean");
        await ReportAsync(agent, Snapshot(Machine("mac"), Entries()));

        var outcome = await Queue(factory).QueueAsync("mac", AgentAction.CloneInto("/dev/clean", CleanUrl), "me");
        Assert.False(outcome.Ok);
        Assert.Contains("reports something at /dev/clean again", outcome.Error);
    }

    [Fact]
    public async Task A_clone_gets_longer_to_report_than_other_actions()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var (factory, agent) = await HubAsync(factory: new HubFactory { Time = clock });
        using var _ = factory;
        await RemoveAsync(factory, agent, "clean");
        Assert.True((await Queue(factory).QueueAsync("mac", AgentAction.CloneInto("/dev/clean", CleanUrl), "me")).Ok);
        var claimed = Assert.Single(await ClaimAsync(agent));

        clock.Now += TimeSpan.FromMinutes(12); // well past the 5 minutes a git action gets
        Assert.Equal(0, await Queue(factory).ExpireStaleAsync());
        // (The machine is past its online window by now, which does not stop a result being recorded.)
        (await ResultAsync(agent, claimed.Id, true)).EnsureSuccessStatusCode();

        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Contains(await Queue(factory).RecentAsync(), a => a is { Kind: AgentActionKind.Clone, Status: AgentActionStatus.Succeeded });
    }

    // ---- build output ---------------------------------------------------------------------------

    [Fact]
    public async Task Build_output_is_only_cleaned_where_the_machine_reported_it()
    {
        var (factory, agent) = await HubAsync();
        using var _ = factory;
        var queue = Queue(factory);

        async Task Refused(AgentAction action, string because)
        {
            var outcome = await queue.QueueAsync("mac", action, "me");
            Assert.False(outcome.Ok);
            Assert.Contains(because, outcome.Error);
        }

        await Refused(AgentAction.CleanBuildDirs("/dev/app", ["/dev/app/src"]), "does not report that build output");
        await Refused(AgentAction.CleanBuildDirs("/dev/app", ["/dev/app/node_modules", "/etc"]), "does not report that build output");
        await Refused(AgentAction.CleanBuildDirs("/dev/clean", ["/dev/app/node_modules"]), "does not report that build output");
        await Refused(AgentAction.CleanBuildDirs("/dev/nope", ["/dev/nope/node_modules"]), "no longer reports a repository");

        var one = await queue.QueueAsync("mac", AgentAction.CleanBuildDirs("/dev/app", ["/dev/app/node_modules"]), "me");
        Assert.True(one.Ok, one.Error);
        Assert.Equal("rm -rf '/dev/app/node_modules'", one.Action!.CommandLine);
        Assert.Equal(["/dev/app/node_modules"], Assert.Single(await ClaimAsync(agent)).Action.BuildDirs);
    }

    [Fact]
    public async Task The_build_output_finding_can_be_run_on_an_agent_that_knows_how()
    {
        var factory = new HubFactory();
        using var _ = factory;
        await HubAsync(Machine("mac"), factory);
        await HubAsync(Machine("old", modern: false), factory);

        var findings = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/findings");
        var cleans = findings.EnumerateArray()
            .Where(f => f.GetProperty("ruleId").GetString() == "large-build-dirs")
            .SelectMany(f => f.GetProperty("commands").EnumerateArray().Select(c => (Machine: f.GetProperty("machineId").GetString(), Command: c)))
            .Where(c => c.Command.GetProperty("command").GetString()!.StartsWith("rm -rf"))
            .ToList();

        Assert.Equal(2, cleans.Count);
        Assert.All(cleans, c => Assert.Equal("CleanBuildDirs", c.Command.GetProperty("action").GetString()));
        Assert.True(cleans.Single(c => c.Machine == "mac").Command.GetProperty("canRun").GetBoolean());
        Assert.False(cleans.Single(c => c.Machine == "old").Command.GetProperty("canRun").GetBoolean());

        var html = await PageAsync(factory, "/cleanup?open=all");
        Assert.Contains("rm -rf '/dev/app/node_modules' '/dev/app/src/App/bin'", html);
        Assert.Contains("for good: a build recreates it", html);
    }

    [Fact]
    public void Build_output_in_a_folder_that_is_not_a_repository_is_copy_only()
    {
        var snapshot = Snapshot(Machine("mac"),
            new DevEntry { Root = "/dev", Name = "loose", Path = "/dev/loose", Kind = EntryKind.NonGitFolder, Size = WithBuildOutput with { ReclaimableDirs = [new ReclaimableDir { Path = "/dev/loose/node_modules", Bytes = 800L * 1024 * 1024 }] } });

        var finding = Assert.Single(new DevDash.Core.Cleanup.CleanupEngine().Evaluate(new DevDash.Core.Cleanup.CleanupContext
        {
            Machines = [snapshot],
            Now = DateTimeOffset.UtcNow,
            Options = new DevDash.Core.Cleanup.CleanupOptions(),
        }), f => f.RuleId == "large-build-dirs");

        // There is no git there to say nothing in it is tracked, so the agent is never asked.
        Assert.All(finding.Commands, c => Assert.Null(c.Action));
    }

    // ---- push and pull --------------------------------------------------------------------------

    private static CloneView Clone(MachineInfo machine, DevEntry entry) =>
        new(new MachineView(Snapshot(machine, entry), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), entry, null);

    [Fact]
    public void A_push_is_offered_where_a_device_is_ahead_and_a_pull_where_it_is_only_behind()
    {
        var git = Git("/dev/w", "w", g => g with
        {
            Branches =
            [
                new BranchInfo { Name = "ahead", Upstream = "origin/ahead", Ahead = 2 },
                new BranchInfo { Name = "behind", Upstream = "origin/behind", Behind = 3 },
                new BranchInfo { Name = "both", Upstream = "origin/both", Ahead = 1, Behind = 1 },
                new BranchInfo { Name = "level", Upstream = "origin/level" },
                new BranchInfo { Name = "gone", Upstream = "origin/gone", UpstreamGone = true, Ahead = 1 },
                new BranchInfo { Name = "renamed", Upstream = "origin/other-name", Behind = 1 },
                new BranchInfo { Name = "local", Ahead = 0 },
                new BranchInfo { Name = "fork", Upstream = "somewhere/fork", Behind = 1 }, // not one of this repo's remotes
            ],
        });

        var offers = SyncOffers.For([Clone(Machine("mac"), Repo("w", git))]);

        Assert.Equal(2, offers.Count);
        var push = offers.Single(o => o.Kind == SyncKind.Push);
        Assert.Equal("Push ahead from mac-host", push.Label);
        Assert.Equal(AgentAction.Push("/dev/w", "origin", "ahead"), push.Action);
        Assert.Equal(2, push.Count);
        var pull = offers.Single(o => o.Kind == SyncKind.Pull);
        Assert.Equal("Pull behind on mac-host", pull.Label);
        Assert.Equal(AgentAction.PullFastForward("/dev/w", "origin", "behind"), pull.Action);
        Assert.Null(pull.Blocked);
    }

    [Fact]
    public void A_pull_into_a_working_tree_with_changes_is_shown_but_blocked()
    {
        WorktreeInfo[] trees = [new() { Path = "/dev/w", Branch = "main" }, new() { Path = "/dev/w.worktrees/rel", Branch = "release" }];
        BranchInfo[] branches =
        [
            new() { Name = "main", Upstream = "origin/main", Behind = 2, IsCurrent = true },
            new() { Name = "release", Upstream = "origin/release", Behind = 1 },
        ];
        var main = Repo("w", Git("/dev/w", "w", g => g with { Branches = branches, Worktrees = trees, Unstaged = 2 }));
        var linked = Repo("rel", Git("/dev/w", "w", g => g with { Branch = "release", IsLinkedWorktree = true, Branches = branches, Worktrees = trees })) with { Path = "/dev/w.worktrees/rel" };
        var machine = Machine("mac");

        var offers = SyncOffers.For([Clone(machine, linked), Clone(machine, main)]);

        // One offer per branch, through the main working tree, although two working trees report the branches.
        Assert.Equal(2, offers.Count);
        Assert.All(offers, o => Assert.Equal("/dev/w", o.RepoPath));
        Assert.Contains("checked out at /dev/w with 2 uncommitted changes", offers.Single(o => o.Branch == "main").Blocked);
        Assert.Null(offers.Single(o => o.Branch == "release").Blocked); // its own working tree is clean
    }

    [Fact]
    public async Task The_repo_page_offers_push_pull_and_clean_and_says_clean_is_for_good()
    {
        var (factory, agent) = await HubAsync();
        using var _ = factory;

        var busy = await PageAsync(factory, "/repo?key=github.com%2Facme%2Fbusy");
        Assert.Contains(">Push main from mac-host</button>", busy);
        Assert.Contains(">Pull release on mac-host</button>", busy);
        Assert.Contains(">Push ↑1</button>", busy); // and on the branch's own row
        Assert.Contains(">Pull ↓3</button>", busy);
        Assert.Contains("Fast-forward release by 3 commits from origin. You confirm first.", busy);
        Assert.DoesNotContain("class=\"run is-disabled\"", busy);

        var app = await PageAsync(factory, "/repo?key=github.com%2Facme%2Fapp");
        Assert.Contains("Build output", app);
        Assert.Contains("Clean deletes the folder for good on that machine. It is not moved to the trash", app);
        Assert.Equal(2, Regex.Matches(app, ">Clean</button>").Count);
        Assert.Contains(">Clean all build output</button>", app);
        Assert.Contains("src/App/bin", app);

        // Queued: the button gives way to its status.
        Assert.True((await Queue(factory).QueueAsync("mac", AgentAction.PullFastForward("/dev/busy", "origin", "release"), "me")).Ok);
        busy = await PageAsync(factory, "/repo?key=github.com%2Facme%2Fbusy");
        Assert.DoesNotContain(">Pull release on mac-host</button>", busy);
        Assert.Contains("queued for mac-host", busy);
        Assert.Equal("git -C '/dev/busy' pull --ff-only 'origin' 'release'", AgentActions.CommandLine(Assert.Single(await ClaimAsync(agent)).Action));
    }

    [Fact]
    public async Task Buttons_are_disabled_with_the_reason_for_an_old_agent()
    {
        var (factory, _) = await HubAsync(Machine("mac", modern: false));
        using var __ = factory;
        const string Why = "Update the agent on mac-host to 0.6.0 or newer to do this from here (it is on 0.5.0).";

        var busy = await PageAsync(factory, "/repo?key=github.com%2Facme%2Fbusy");
        Assert.Matches(new Regex(@"class=""run ""[^>]*>Push main from mac-host</button>"), busy); // an old agent can push
        Assert.Matches(new Regex(@"class=""run is-disabled"" aria-disabled=""true"" title=""" + Regex.Escape(Why) + @"""[^>]*>Pull release on mac-host</button>"), busy);

        var safety = await PageAsync(factory, "/safety");
        Assert.Contains(Why, safety);
        Assert.DoesNotContain("safe project</button>", safety);
        Assert.Contains("no remove, clean or pull yet", await PageAsync(factory, "/machines"));
    }

    // ---- the Safety page ------------------------------------------------------------------------

    [Fact]
    public async Task The_safety_page_has_a_remove_button_per_row_and_one_for_the_plainly_safe()
    {
        var (factory, agent) = await HubAsync();
        using var _ = factory;

        var html = await PageAsync(factory, "/safety");

        // clean, caveats and app are removable; busy is at risk and has no button.
        Assert.Matches(new Regex(@"Safe to remove <span class=""muted small"">3</span>"), html);
        Assert.Equal(3, Regex.Matches(html, @"<button type=""button"" class=""danger "" aria-disabled=""false""[^>]*>Remove</button>").Count);
        Assert.Contains("Move clean to the trash on mac-host. You confirm first.", html);
        // The bulk button leaves out the one with caveats: those are ticked one project at a time.
        Assert.Contains(">Remove 2 safe projects</button>", html);
        Assert.DoesNotContain("data-safety-placeholder", html);
        Assert.DoesNotContain("<h2 id=\"removed\">", html);

        // On its way: the row shows it and the button is gone; the bulk button no longer counts it.
        Assert.True((await Queue(factory).QueueAsync("mac", Removal(factory, "clean"), "me")).Ok);
        html = await PageAsync(factory, "/safety");
        Assert.Contains("queued for mac-host", html);
        Assert.Equal(2, Regex.Matches(html, @">Remove</button>").Count);
        Assert.Contains(">Remove 1 safe project</button>", html);

        // Refused by the agent: the row says why.
        (await ResultAsync(agent, Assert.Single(await ClaimAsync(agent)).Id, false, "Refused: /dev/clean has changed since it was shown in the dashboard.")).EnsureSuccessStatusCode();
        html = await PageAsync(factory, "/safety");
        Assert.Contains("failed on mac-host", html);
        Assert.Contains("Refused: /dev/clean has changed since it was shown in the dashboard.", html);
        Assert.Equal(3, Regex.Matches(html, @">Remove</button>").Count);
    }

    [Fact]
    public async Task Removed_projects_are_listed_on_the_safety_and_machines_pages_with_a_way_back()
    {
        var (factory, agent) = await HubAsync();
        using var _ = factory;
        await RemoveAsync(factory, agent, "clean");

        foreach (var page in (string[])["/safety", "/machines"])
        {
            var html = await PageAsync(factory, page);
            Assert.Contains("id=\"removed\"", html);
            Assert.Contains(CleanUrl, html);
            Assert.Contains(">Re-clone</button>", html);
            Assert.Contains("Clone clean back into /dev/clean on mac-host. You confirm first.", html);
            Assert.Contains(">Forget</button>", html);
        }

        Assert.Contains("1 removed", await PageAsync(factory, "/machines"));
        // The audit trail has the removal, in words.
        Assert.Matches(new Regex(@"trash '[^']*clean'"), await PageAsync(factory, "/machines"));
    }

    // ---- existing databases ---------------------------------------------------------------------

    [Fact]
    public async Task A_database_from_before_removal_gains_the_table_on_start()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DevDashDbContext>().UseSqlite(connection).Options;

        await using (var db = new DevDashDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.AgentActions.Add(new AgentActionEntity { MachineId = "mac", Status = AgentActionStatus.Succeeded });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"RemovedProjects\""); // what a 0.5.0 hub's database looks like
        }

        await using (var db = new DevDashDbContext(options))
        {
            var created = await SchemaUpgrade.CreateMissingTablesAsync(db);
            Assert.Contains("RemovedProjects", created);
            Assert.Contains(created, name => name.StartsWith("IX_RemovedProjects"));

            db.RemovedProjects.Add(new RemovedProjectEntity { MachineId = "mac", Path = "/dev/x", RemovedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            Assert.Equal(1, await db.RemovedProjects.CountAsync());
            Assert.Equal(1, await db.AgentActions.CountAsync()); // and what was there is kept
            Assert.Empty(await SchemaUpgrade.CreateMissingTablesAsync(db));
        }
    }
}
