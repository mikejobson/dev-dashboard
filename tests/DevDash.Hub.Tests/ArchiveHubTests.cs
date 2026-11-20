using System.Net;
using System.Net.Http.Json;
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
/// The hub side of archiving (§23): what it will queue, the archived tombstone and its restore, the long-action
/// claim and lease rules, progress, the buttons, and an old database gaining the new columns. No agent runs
/// here; the tests play the agent through its endpoints.
/// </summary>
public class ArchiveHubTests
{
    private const string Snapshots = "/api/v1/snapshots";
    private const string Claim = "/api/v1/actions/claim";
    private const string Sha = "1111111111111111111111111111111111111111";
    private const string ArchiveSha = "abababababababababababababababababababababababababababababababab";

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static MachineInfo Machine(string id = "mac", bool archive = true, string version = "0.8.0", long free = 500L * 1024 * 1024 * 1024) => new()
    {
        MachineId = id,
        Hostname = id + "-host",
        Os = "macOS",
        AgentVersion = version,
        ActionsEnabled = true,
        AcceptsRefresh = true,
        AcceptsRemoveProject = true,
        AcceptsClone = true,
        AcceptsCleanBuildDirs = true,
        AcceptsPull = true,
        Archive = archive ? new ArchiveInfo { Folder = "/Volumes/nas/archive", Available = true, Writable = true, FreeBytes = free, MarginBytes = 512L * 1024 * 1024, Format = ArchiveFormats.TarGz } : null,
        AcceptsArchiveProject = archive,
        AcceptsRestoreArchive = archive,
    };

    private static DevEntry Repo(string name, bool atRisk = false, long size = 3L * 1024 * 1024, long reclaimable = 0) => new()
    {
        Root = "/dev",
        Name = name,
        Path = "/dev/" + name,
        Kind = EntryKind.GitRepo,
        LastModified = DateTimeOffset.UtcNow.AddDays(-30),
        Size = new SizeInfo { TotalBytes = size, ReclaimableBytes = reclaimable, MeasuredAt = DateTimeOffset.UtcNow },
        Git = new GitState
        {
            Branch = "main",
            HeadSha = Sha,
            Upstream = "origin/main",
            DefaultBranch = "main",
            CommonDir = $"/dev/{name}/.git",
            Ahead = atRisk ? 1 : 0,
            Unstaged = atRisk ? 2 : 0,
            LastCommit = new CommitInfo { Sha = Sha, Date = DateTimeOffset.UtcNow.AddDays(-30), Subject = "work", Author = "me" },
            Remotes = [new RemoteInfo { Name = "origin", Url = $"https://github.com/acme/{name}.git", Normalized = $"github.com/acme/{name}" }],
            Branches = [new BranchInfo { Name = "main", Sha = Sha, Upstream = "origin/main", Ahead = atRisk ? 1 : 0, IsCurrent = true, OnRemote = !atRisk, UnpushedCount = atRisk ? 1 : 0, LastCommitDate = DateTimeOffset.UtcNow.AddDays(-30) }],
            Worktrees = [new WorktreeInfo { Path = $"/dev/{name}", Branch = "main" }],
            Safety = new SafetyFacts { BranchesChecked = true, HeadOnRemote = !atRisk },
        },
    };

    private static DevEntry[] Entries() => [Repo("old", atRisk: true), Repo("clean"), Repo("big", size: 900L * 1024 * 1024 * 1024)];

    private static MachineSnapshot Snapshot(MachineInfo machine, params DevEntry[] entries) => new()
    {
        Machine = machine,
        ScannedAt = DateTimeOffset.UtcNow,
        Roots = ["/dev"],
        Entries = entries,
    };

    private static async Task<(HubFactory Factory, HttpClient Agent)> HubAsync(MachineInfo? machine = null, HubFactory? factory = null)
    {
        factory ??= new HubFactory();
        machine ??= Machine();
        var agent = factory.CreateAgentClient(await factory.IssueKeyAsync(machine.MachineId));
        await ReportAsync(agent, Snapshot(machine, Entries()));
        return (factory, agent);
    }

    private static async Task ReportAsync(HttpClient agent, MachineSnapshot snapshot) =>
        (await agent.PostAsync(Snapshots, JsonContent.Create(snapshot, options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();

    private static ActionQueue Queue(HubFactory factory) => factory.Services.GetRequiredService<ActionQueue>();

    private static SafetyRow Row(HubFactory factory, string name) =>
        factory.Services.GetRequiredService<SafetyService>().Get().Single(r => r.Entry.Name == name);

    private static AgentAction ArchiveOf(HubFactory factory, string name, bool includeBuildOutput = false) =>
        AgentAction.ArchiveProject("/dev/" + name, Row(factory, name).Safety.Fingerprint, includeBuildOutput);

    private static async Task<List<PendingAgentAction>> ClaimAsync(HttpClient agent)
    {
        var response = await agent.PostAsync(Claim, null);
        response.EnsureSuccessStatusCode();
        return [.. (await response.Content.ReadFromJsonAsync<ActionClaimResponse>(SnapshotSchema.JsonOptions))!.Actions];
    }

    private static Task<HttpResponseMessage> ResultAsync(HttpClient agent, string id, ActionResultRequest result) =>
        agent.PostAsync($"/api/v1/actions/{id}/result", JsonContent.Create(result, options: SnapshotSchema.JsonOptions));

    private static ArchiveResult Archived(string name) => new()
    {
        ArchivePath = $"/Volumes/nas/archive/{name}-20261120-101112.tar.gz",
        ManifestPath = $"/Volumes/nas/archive/{name}-20261120-101112.json",
        Manifest = new ArchiveManifest
        {
            RootName = name,
            SourcePath = "/dev/" + name,
            ArchiveFile = $"{name}-20261120-101112.tar.gz",
            Format = ArchiveFormats.TarGz,
            ArchiveSha256 = ArchiveSha,
            ArchiveBytes = 1024,
            FileCount = 42,
            TotalBytes = 4096,
            ContentSha256 = new string('c', 64),
            HeadBranch = "main",
            HeadSha = Sha,
            ExcludedBuildOutput = ["node_modules"],
            CreatedAt = DateTimeOffset.UtcNow,
        },
    };

    /// <summary>Queues the archive of <paramref name="name"/>, has the agent claim it, report it done and stop reporting the folder.</summary>
    private static async Task ArchiveAsync(HubFactory factory, HttpClient agent, string name)
    {
        var outcome = await Queue(factory).QueueAsync("mac", ArchiveOf(factory, name), "me@example.com");
        Assert.True(outcome.Ok, outcome.Error);
        var claimed = Assert.Single(await ClaimAsync(agent));
        (await ResultAsync(agent, claimed.Id, new ActionResultRequest { Succeeded = true, ExitCode = 0, Output = "Archived.", Archive = Archived(name) })).EnsureSuccessStatusCode();
        await ReportAsync(agent, Snapshot(Machine(), Entries().Where(e => e.Name != name).ToArray()));
    }

    private static async Task<string> PageAsync(HubFactory factory, string url)
    {
        var response = await factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static async Task<List<RemovedProjectEntity>> TombstoneRowsAsync(HubFactory factory)
    {
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<DevDashDbContext>>().CreateDbContextAsync();
        return await db.RemovedProjects.AsNoTracking().ToListAsync();
    }

    // ---- queueing ---------------------------------------------------------------------------------

    [Fact]
    public async Task An_at_risk_project_can_be_archived_and_its_tombstone_records_the_archive()
    {
        var (factory, agent) = await HubAsync();
        using var _ = factory;
        Assert.Equal(SafetyVerdict.AtRisk, Row(factory, "old").Safety.Verdict);

        // Removal is refused for it; archiving is not.
        Assert.False((await Queue(factory).QueueAsync("mac", AgentAction.RemoveProject("/dev/old", Row(factory, "old").Safety.Fingerprint), "me")).Ok);
        var outcome = await Queue(factory).QueueAsync("mac", ArchiveOf(factory, "old"), "me@example.com");
        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal("archive '/dev/old' && trash '/dev/old'", outcome.Action!.CommandLine);

        var pending = Assert.Single(await TombstoneRowsAsync(factory));
        Assert.Equal(RemovedProjectKinds.Archived, pending.Kind);
        Assert.Null(pending.RemovedAt);
        Assert.Empty(await factory.Services.GetRequiredService<TombstoneStore>().ListAsync());

        var claimed = Assert.Single(await ClaimAsync(agent));
        Assert.Equal(AgentActionKind.ArchiveProject, claimed.Action.Kind);
        Assert.Equal(Row(factory, "old").Safety.Fingerprint, claimed.Action.ExpectedFingerprint);
        Assert.False(claimed.Action.IncludeBuildOutput);

        // Progress while it runs is shown in place of the output.
        var progress = await agent.PostAsync($"/api/v1/actions/{claimed.Id}/progress", JsonContent.Create(new ActionProgressRequest { Output = "Archiving /dev/old: 10 of 42 files" }, options: SnapshotSchema.JsonOptions));
        Assert.Equal(HttpStatusCode.NoContent, progress.StatusCode);
        Assert.Contains("10 of 42 files", await PageAsync(factory, "/safety"));

        (await ResultAsync(agent, claimed.Id, new ActionResultRequest { Succeeded = true, ExitCode = 0, Output = "Archived.", Archive = Archived("old") })).EnsureSuccessStatusCode();
        // Progress for an action that has finished is not taken.
        Assert.Equal(HttpStatusCode.NotFound, (await agent.PostAsync($"/api/v1/actions/{claimed.Id}/progress", JsonContent.Create(new ActionProgressRequest { Output = "late" }, options: SnapshotSchema.JsonOptions))).StatusCode);

        var row = Assert.Single(await TombstoneRowsAsync(factory));
        Assert.NotNull(row.RemovedAt);
        Assert.Equal("/Volumes/nas/archive/old-20261120-101112.tar.gz", row.ArchivePath);
        Assert.Equal(ArchiveSha, row.ArchiveSha256);
        Assert.Equal(42, TombstoneStore.ManifestOf(row)!.FileCount);

        var listed = Assert.Single(await factory.Services.GetRequiredService<TombstoneStore>().ListAsync());
        Assert.True(listed.IsArchived);
        Assert.Equal(AgentAction.RestoreArchive(row.ArchivePath!, ArchiveSha, "/dev/elsewhere"), listed.RestoreAction("/dev/elsewhere"));
        Assert.NotNull(listed.CloneAction); // it can still be cloned back as well

        // Still listed while the folder is reported (the agent has not rescanned yet): the archive is there regardless.
        var page = await PageAsync(factory, "/safety");
        Assert.Contains("archived", page);
        Assert.Contains("→ /Volumes/nas/archive/old-20261120-101112.tar.gz", page);
        Assert.Contains(">Restore</button>", page);
    }

    [Fact]
    public async Task The_hub_will_not_ask_for_an_archive_that_is_stale_too_big_or_unsupported()
    {
        var (factory, agent) = await HubAsync();
        using var _ = factory;
        var queue = Queue(factory);

        async Task Refused(string machine, AgentAction action, string because)
        {
            var outcome = await queue.QueueAsync(machine, action, "me");
            Assert.False(outcome.Ok);
            Assert.Contains(because, outcome.Error);
        }

        await Refused("mac", AgentAction.ArchiveProject("/dev/old", "0123456789abcdef01234567"), "has changed since this page was drawn");
        await Refused("mac", AgentAction.ArchiveProject("/dev/gone", "0123456789abcdef01234567"), "no longer reports a repository");
        await Refused("mac", ArchiveOf(factory, "big"), "free; this needs about");
        // Keeping the build output counts it in.
        Assert.Null(Archives.Estimate(Row(factory, "clean").Safety with { TotalBytes = null }, false));
        Assert.Equal(100, Archives.Estimate(Row(factory, "clean").Safety with { TotalBytes = 300, ReclaimableBytes = 200 }, false));
        Assert.Equal(300, Archives.Estimate(Row(factory, "clean").Safety with { TotalBytes = 300, ReclaimableBytes = 200 }, true));

        // A machine without an archive folder, and one whose agent predates archiving.
        var lan = factory.CreateAgentClient(await factory.IssueKeyAsync("lan"));
        await ReportAsync(lan, Snapshot(Machine("lan", archive: false), Repo("clean")));
        await Refused("lan", AgentAction.ArchiveProject("/dev/clean", "0123456789abcdef01234567"), "Archiving is not set up on lan-host");
        var old = factory.CreateAgentClient(await factory.IssueKeyAsync("old"));
        await ReportAsync(old, Snapshot(Machine("old", archive: false, version: "0.7.0"), Repo("clean")));
        await Refused("old", AgentAction.ArchiveProject("/dev/clean", "0123456789abcdef01234567"), "to 0.8.0 or newer");
        // A share that is not mounted.
        var nas = factory.CreateAgentClient(await factory.IssueKeyAsync("nas"));
        await ReportAsync(nas, Snapshot(Machine("nas") with { Archive = new ArchiveInfo { Folder = "/Volumes/nas", Problem = "/Volumes/nas does not exist" }, AcceptsArchiveProject = false, AcceptsRestoreArchive = false }, Repo("clean")));
        await Refused("nas", AgentAction.ArchiveProject("/dev/clean", "0123456789abcdef01234567"), "/Volumes/nas does not exist");

        Assert.Empty(await ClaimAsync(agent));
        Assert.Empty(await TombstoneRowsAsync(factory));
    }

    [Fact]
    public async Task A_failed_archive_leaves_no_tombstone()
    {
        var (factory, agent) = await HubAsync();
        using var _ = factory;
        Assert.True((await Queue(factory).QueueAsync("mac", ArchiveOf(factory, "old"), "me")).Ok);
        var claimed = Assert.Single(await ClaimAsync(agent));
        (await ResultAsync(agent, claimed.Id, new ActionResultRequest { Succeeded = false, ExitCode = 1, Output = "The archive did not read back as written." })).EnsureSuccessStatusCode();
        Assert.Empty(await TombstoneRowsAsync(factory));
    }

    // ---- claiming and leases ----------------------------------------------------------------------

    [Fact]
    public async Task A_long_action_ends_a_claim_and_gets_a_long_lease()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var (factory, agent) = await HubAsync(factory: new HubFactory { Time = clock });
        using var _ = factory;
        var queue = Queue(factory);

        Assert.True((await queue.QueueAsync("mac", AgentAction.Fetch("/dev/clean"), "me")).Ok);
        Assert.True((await queue.QueueAsync("mac", ArchiveOf(factory, "old"), "me")).Ok);
        Assert.True((await queue.QueueAsync("mac", AgentAction.Fetch("/dev/old"), "me")).Ok);

        // The fetch before it comes along; the one after waits for the next claim, not behind the archive.
        var first = await ClaimAsync(agent);
        Assert.Equal([AgentActionKind.Fetch, AgentActionKind.ArchiveProject], first.Select(a => a.Action.Kind));
        var second = Assert.Single(await ClaimAsync(agent));
        Assert.Equal("/dev/old", second.Action.RepoPath);

        // An hour later the archive is still running, not "never reported"; the plain fetch is long given up on.
        clock.Now += TimeSpan.FromMinutes(60);
        await queue.ExpireStaleAsync();
        var rows = await queue.RecentAsync();
        Assert.Equal(AgentActionStatus.Claimed, rows.Single(r => r.Kind == AgentActionKind.ArchiveProject).Status);
        Assert.All(rows.Where(r => r.Kind == AgentActionKind.Fetch), r => Assert.Equal(AgentActionStatus.Failed, r.Status));

        clock.Now += TimeSpan.FromMinutes(200);
        await queue.ExpireStaleAsync();
        Assert.Equal(AgentActionStatus.Failed, (await queue.RecentAsync()).Single(r => r.Kind == AgentActionKind.ArchiveProject).Status);
        Assert.Empty(await TombstoneRowsAsync(factory));
    }

    // ---- restore ----------------------------------------------------------------------------------

    [Fact]
    public async Task Restore_only_puts_back_an_archive_this_hub_saw_made_into_a_free_folder()
    {
        var (factory, agent) = await HubAsync();
        using var _ = factory;
        await ArchiveAsync(factory, agent, "old");
        var archive = Archived("old").ArchivePath;
        var queue = Queue(factory);

        async Task Refused(AgentAction action, string because)
        {
            var outcome = await queue.QueueAsync("mac", action, "me");
            Assert.False(outcome.Ok);
            Assert.Contains(because, outcome.Error);
        }

        await Refused(AgentAction.RestoreArchive(archive, ArchiveSha, "/dev/clean"), "reports something at /dev/clean");
        await Refused(AgentAction.RestoreArchive(archive, new string('0', 64), "/dev/old"), "not the checksum");
        await Refused(AgentAction.RestoreArchive("/Volumes/nas/archive/other.tar.gz", ArchiveSha, "/dev/old"), "no record of an archive");
        await Refused(AgentAction.RestoreArchive(archive, ArchiveSha, "/tmp/old"), "not inside one of the folders");
        await Refused(AgentAction.RestoreArchive(archive, ArchiveSha, "/devx/old"), "not inside one of the folders");

        var outcome = await queue.QueueAsync("mac", AgentAction.RestoreArchive(archive, ArchiveSha, "/dev/old-restored"), "me");
        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal($"restore '{archive}' '/dev/old-restored'", outcome.Action!.CommandLine);
        var claimed = Assert.Single(await ClaimAsync(agent));
        Assert.Equal(ArchiveSha, claimed.Action.ArchiveSha256);

        // A failed restore keeps the tombstone; a successful one ends it (the archive itself stays on the machine).
        (await ResultAsync(agent, claimed.Id, new ActionResultRequest { Succeeded = false, ExitCode = null, Output = "Refused: the archive is missing." })).EnsureSuccessStatusCode();
        Assert.Single(await TombstoneRowsAsync(factory));
        Assert.True((await queue.QueueAsync("mac", AgentAction.RestoreArchive(archive, ArchiveSha, "/dev/old"), "me")).Ok);
        (await ResultAsync(agent, Assert.Single(await ClaimAsync(agent)).Id, new ActionResultRequest { Succeeded = true, ExitCode = 0, Output = "Restored." })).EnsureSuccessStatusCode();
        Assert.Empty(await TombstoneRowsAsync(factory));
    }

    [Fact]
    public async Task A_removal_or_clone_at_the_same_path_does_not_drop_the_archive_record()
    {
        var (factory, agent) = await HubAsync();
        using var _ = factory;
        await ArchiveAsync(factory, agent, "clean");

        // Cloned back by hand later, and that clone removed from the dashboard: the archive is still listed.
        await ReportAsync(agent, Snapshot(Machine(), Entries()));
        Assert.True((await Queue(factory).QueueAsync("mac", AgentAction.RemoveProject("/dev/clean", Row(factory, "clean").Safety.Fingerprint), "me")).Ok);
        (await ResultAsync(agent, Assert.Single(await ClaimAsync(agent)).Id, new ActionResultRequest { Succeeded = true, ExitCode = 0, Output = "Moved." })).EnsureSuccessStatusCode();

        var rows = await TombstoneRowsAsync(factory);
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.IsArchived && r.ArchivePath is not null);
    }

    // ---- pages ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_safety_and_repo_pages_offer_archive_where_the_machine_has_a_folder()
    {
        var (factory, _) = await HubAsync();
        using var __ = factory;

        var safety = await PageAsync(factory, "/safety");
        // The at-risk row, the safe row and the one with no room (shown, its dialog refuses).
        Assert.Equal(3, Regex.Matches(safety, ">Archive</button>").Count);
        Assert.Contains("Archive old on mac-host, then move the folder to the trash. You confirm first.", safety);

        var repo = await PageAsync(factory, "/repo?key=github.com%2Facme%2Fold");
        Assert.Contains(">Archive</button>", repo);

        // A machine with no archive folder gets no Archive button at all.
        var lonely = new HubFactory();
        using var ___ = lonely;
        var agent = lonely.CreateAgentClient(await lonely.IssueKeyAsync("lan"));
        await ReportAsync(agent, Snapshot(Machine("lan", archive: false), Entries()));
        Assert.DoesNotContain(">Archive</button>", await PageAsync(lonely, "/safety"));
    }

    // ---- existing databases ---------------------------------------------------------------------

    [Fact]
    public async Task A_database_from_before_archiving_gains_the_columns_on_start()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DevDashDbContext>().UseSqlite(connection).Options;

        await using (var db = new DevDashDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            // What a 0.7.0 hub's table looks like: no Kind, ArchivePath, ArchiveSha256 or ManifestJson.
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"RemovedProjects\"");
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "RemovedProjects" ("Id" INTEGER NOT NULL CONSTRAINT "PK_RemovedProjects" PRIMARY KEY AUTOINCREMENT, "MachineId" TEXT NOT NULL,
                "MachineName" TEXT NOT NULL, "Path" TEXT NOT NULL, "Name" TEXT NOT NULL, "RepoKey" TEXT NOT NULL, "RemotesJson" TEXT NOT NULL,
                "DefaultBranch" TEXT NULL, "Branch" TEXT NULL, "HeadSha" TEXT NULL, "SizeBytes" INTEGER NULL, "ActionId" INTEGER NOT NULL,
                "RemovedBy" TEXT NOT NULL, "QueuedAt" TEXT NOT NULL, "RemovedAt" TEXT NULL)
                """);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "RemovedProjects" ("MachineId", "MachineName", "Path", "Name", "RepoKey", "RemotesJson", "ActionId", "RemovedBy", "QueuedAt", "RemovedAt")
                VALUES ('mac', 'mac-host', '/dev/x', 'x', '', '[]', 1, 'me', '2026-11-19 10:00:00+00:00', '2026-11-19 10:00:00+00:00')
                """);
        }

        await using (var db = new DevDashDbContext(options))
        {
            var added = await SchemaUpgrade.CreateMissingTablesAsync(db);
            // The dropped table's indexes come back too; the columns are the point.
            Assert.Equal(["RemovedProjects.ArchivePath", "RemovedProjects.ArchiveSha256", "RemovedProjects.Kind", "RemovedProjects.ManifestJson"], added.Where(a => a.Contains('.')).Order());

            var old = Assert.Single(await db.RemovedProjects.ToListAsync());
            Assert.Equal("/dev/x", old.Path);
            Assert.False(old.IsArchived);
            db.RemovedProjects.Add(new RemovedProjectEntity { MachineId = "mac", Path = "/dev/y", Kind = RemovedProjectKinds.Archived, ArchivePath = "/a/y.tar.gz", RemovedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            Assert.Equal(2, await db.RemovedProjects.CountAsync());
            Assert.Empty(await SchemaUpgrade.CreateMissingTablesAsync(db));
        }
    }
}
