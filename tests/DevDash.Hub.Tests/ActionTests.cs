using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevDash.Contracts;
using DevDash.Hub.Data;
using DevDash.Hub.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DevDash.Hub.Tests;

/// <summary>The hub side of agent actions: who may queue, who may claim, at most once, expiry, and the audit trail.</summary>
public class ActionTests
{
    private const string Snapshots = "/api/v1/snapshots";
    private const string Claim = "/api/v1/actions/claim";

    private static readonly AgentAction DeleteOldFix = AgentAction.DeleteBranches("/dev/widget", ["old-fix"]);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A hub with one machine reporting, and that machine's agent client.</summary>
    private static async Task<(HubFactory Factory, HttpClient Agent)> HubAsync(bool actions = true, string machineId = "mac", HubFactory? factory = null)
    {
        factory ??= new HubFactory();
        var agent = await AddMachineAsync(factory, machineId, actions);
        return (factory, agent);
    }

    private static async Task<HttpClient> AddMachineAsync(HubFactory factory, string machineId, bool actions)
    {
        var agent = factory.CreateAgentClient(await factory.IssueKeyAsync(machineId));
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var response = await agent.PostAsync(Snapshots, JsonContent.Create(HubFactory.Snapshot(machineId, now, actions), options: SnapshotSchema.JsonOptions));
        response.EnsureSuccessStatusCode();
        return agent;
    }

    private static ActionQueue Queue(HubFactory factory) => factory.Services.GetRequiredService<ActionQueue>();

    private static async Task<List<PendingAgentAction>> ClaimAsync(HttpClient agent)
    {
        var response = await agent.PostAsync(Claim, null);
        response.EnsureSuccessStatusCode();
        return [.. (await response.Content.ReadFromJsonAsync<ActionClaimResponse>(SnapshotSchema.JsonOptions))!.Actions];
    }

    private static Task<HttpResponseMessage> ReportAsync(HttpClient agent, string id, bool succeeded, string output = "Deleted branch old-fix (was 1111111).") =>
        agent.PostAsync($"/api/v1/actions/{id}/result",
            JsonContent.Create(new ActionResultRequest { Succeeded = succeeded, ExitCode = succeeded ? 0 : 1, Output = output }, options: SnapshotSchema.JsonOptions));

    private static async Task<AgentActionEntity> OnlyAsync(HubFactory factory) => Assert.Single(await Queue(factory).RecentAsync());

    // ---- off by default -------------------------------------------------------------------------

    [Fact]
    public async Task A_machine_that_has_not_enabled_actions_gets_no_run_button_and_cannot_be_queued_for()
    {
        var (factory, agent) = await HubAsync(actions: false);
        using var _ = factory;

        var html = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/cleanup?open=all"));
        Assert.Contains("git -C '/dev/widget' branch -d 'old-fix'", html); // the command is there to copy
        Assert.DoesNotContain("Run on", html);
        Assert.DoesNotContain("class=\"run\"", html);

        var outcome = await Queue(factory).QueueAsync("mac", DeleteOldFix, "me@example.com");
        Assert.False(outcome.Ok);
        Assert.Contains("has not enabled agent actions", outcome.Error);
        Assert.Empty(await Queue(factory).RecentAsync());
        Assert.Empty(await ClaimAsync(agent));

        var findings = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/findings");
        Assert.DoesNotContain(findings.EnumerateArray().SelectMany(f => f.GetProperty("commands").EnumerateArray()), c => c.GetProperty("canRun").GetBoolean());
    }

    [Fact]
    public async Task A_machine_that_enabled_actions_gets_run_buttons_only_on_commands_with_a_typed_action()
    {
        var (factory, _) = await HubAsync(actions: true);
        using var __ = factory;

        var html = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/cleanup?open=all"));
        Assert.Contains("Run on mac-host", html);

        var findings = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/findings");
        var commands = findings.EnumerateArray().SelectMany(f => f.GetProperty("commands").EnumerateArray()).ToList();
        var runnable = commands.Where(c => c.GetProperty("canRun").GetBoolean()).ToList();
        Assert.NotEmpty(runnable);
        Assert.All(runnable, c => Assert.True(c.TryGetProperty("action", out var _)));
        // Arbitrary shell (mv, rm, gh) is never runnable, whatever the machine allows.
        Assert.All(commands.Where(c => !c.GetProperty("command").GetString()!.StartsWith("git -C")), c => Assert.False(c.GetProperty("canRun").GetBoolean()));

        var machines = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/machines"));
        Assert.Contains("This agent runs allowlisted fixes", machines);
    }

    [Fact]
    public async Task The_hub_wide_switch_turns_everything_off()
    {
        var factory = new HubFactory().WithWebHostBuilder(b => b.UseSetting("Hub:Actions:Enabled", "false"));
        using var _ = factory;
        var agent = factory.CreateClient();
        var keys = factory.Services.GetRequiredService<DevDash.Hub.Auth.ApiKeyService>();
        agent.DefaultRequestHeaders.Authorization = new("Bearer", (await keys.IssueAsync("mac")).Plaintext);
        (await agent.PostAsync(Snapshots, JsonContent.Create(HubFactory.Snapshot(actions: true), options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();

        var queue = factory.Services.GetRequiredService<ActionQueue>();
        Assert.False(queue.CanRun("mac"));
        Assert.False((await queue.QueueAsync("mac", DeleteOldFix, "me")).Ok);
        Assert.DoesNotContain("Run on", await factory.CreateClient().GetStringAsync("/cleanup?open=all"));
    }

    // ---- queueing -------------------------------------------------------------------------------

    [Fact]
    public async Task Queueing_records_who_asked_and_the_exact_command()
    {
        var (factory, _) = await HubAsync();
        using var __ = factory;
        var finding = (await factory.Services.GetRequiredService<FindingsService>().GetAsync()).Single(v => v.Finding.RuleId == "merged-branch");
        var command = finding.Finding.Commands.Single(c => c.Action is not null);
        Assert.True(finding.CanRun(command));
        Assert.Null(finding.ActionFor(command));

        var outcome = await Queue(factory).QueueAsync("mac", command.Action!, "me@example.com", finding.Finding);

        Assert.True(outcome.Ok, outcome.Error);
        var row = await OnlyAsync(factory);
        Assert.Equal(AgentActionStatus.Queued, row.Status);
        Assert.Equal("mac", row.MachineId);
        Assert.Equal("me@example.com", row.QueuedBy);
        Assert.Equal("git -C '/dev/widget' branch -d -- 'old-fix'", row.CommandLine);
        Assert.Equal(finding.Finding.Fingerprint, row.FindingFingerprint);
        Assert.Equal(row.QueuedAt.AddMinutes(10), row.ExpiresAt);

        // The finding now shows it as pending.
        var after = (await factory.Services.GetRequiredService<FindingsService>().GetAsync()).Single(v => v.Finding.RuleId == "merged-branch");
        Assert.Equal(AgentActionStatus.Queued, after.ActionFor(command)!.Status);
        Assert.Contains("queued for mac-host", WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/cleanup?open=all")));
        Assert.Contains("git -C '/dev/widget' branch -d -- 'old-fix'", WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/machines")));
    }

    [Fact]
    public async Task Queueing_refuses_unknown_machines_unknown_repos_malformed_actions_and_anonymous_users()
    {
        var (factory, _) = await HubAsync();
        using var __ = factory;
        var queue = Queue(factory);

        Assert.Contains("not known", (await queue.QueueAsync("nope", DeleteOldFix, "me")).Error);
        Assert.Contains("no longer reports", (await queue.QueueAsync("mac", AgentAction.DeleteBranches("/etc", ["x"]), "me")).Error);
        Assert.False((await queue.QueueAsync("mac", AgentAction.DeleteBranches("/dev/widget", ["--force"]), "me")).Ok);
        Assert.False((await queue.QueueAsync("mac", DeleteOldFix with { Remote = "origin" }, "me")).Ok);
        Assert.False((await queue.QueueAsync("mac", DeleteOldFix, " ")).Ok);
        Assert.Empty(await queue.RecentAsync());
    }

    [Fact]
    public async Task Queueing_the_same_action_twice_does_not_run_it_twice()
    {
        var (factory, agent) = await HubAsync();
        using var _ = factory;

        var first = await Queue(factory).QueueAsync("mac", DeleteOldFix, "me");
        var second = await Queue(factory).QueueAsync("mac", AgentAction.DeleteBranches("/dev/widget", ["old-fix"]), "me");

        Assert.Equal(first.Action!.Id, second.Action!.Id);
        Assert.Single(await ClaimAsync(agent));
    }

    [Fact]
    public async Task An_offline_machine_cannot_be_queued_for()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var (factory, _) = await HubAsync(factory: new HubFactory { Time = clock });
        using var __ = factory;

        clock.Now += TimeSpan.FromHours(2);

        Assert.False(Queue(factory).CanRun("mac"));
        Assert.Contains("offline", (await Queue(factory).QueueAsync("mac", DeleteOldFix, "me")).Error);
    }

    // ---- claiming: bound to the machine, at most once ---------------------------------------------

    [Fact]
    public async Task An_agent_is_handed_its_own_machines_actions_exactly_once()
    {
        var (factory, mac) = await HubAsync();
        using var _ = factory;
        var desktop = await AddMachineAsync(factory, "desktop", actions: true);
        await Queue(factory).QueueAsync("mac", DeleteOldFix, "me");

        Assert.Empty(await ClaimAsync(desktop)); // another machine's key sees nothing
        Assert.Equal(AgentActionStatus.Queued, (await OnlyAsync(factory)).Status);

        var claimed = Assert.Single(await ClaimAsync(mac));
        Assert.Equal(AgentActionKind.DeleteBranches, claimed.Action.Kind);
        Assert.Equal("/dev/widget", claimed.Action.RepoPath);
        Assert.Equal(["old-fix"], claimed.Action.Branches);
        Assert.Equal(AgentActionStatus.Claimed, (await OnlyAsync(factory)).Status);

        Assert.Empty(await ClaimAsync(mac)); // never handed out again
        Assert.Empty(await ClaimAsync(desktop));
    }

    [Fact]
    public async Task Concurrent_polls_do_not_both_get_the_action()
    {
        var (factory, mac) = await HubAsync();
        using var _ = factory;
        await Queue(factory).QueueAsync("mac", DeleteOldFix, "me");

        var polls = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ClaimAsync(mac)));

        Assert.Single(polls.SelectMany(p => p));
    }

    [Fact]
    public async Task Claiming_needs_an_agent_key_that_is_bound_to_a_machine()
    {
        var (factory, _) = await HubAsync();
        using var __ = factory;
        await Queue(factory).QueueAsync("mac", DeleteOldFix, "me");

        // A browser session is not an agent, and a wrong key is nobody.
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsync(Claim, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateAgentClient("ddk_wrong").PostAsync(Claim, null)).StatusCode);

        // A fresh key has never pushed a snapshot, so it belongs to no machine yet.
        var unbound = factory.CreateAgentClient(await factory.IssueKeyAsync("new"));
        Assert.Equal(HttpStatusCode.Conflict, (await unbound.PostAsync(Claim, null)).StatusCode);

        Assert.Equal(AgentActionStatus.Queued, (await OnlyAsync(factory)).Status);
    }

    [Fact]
    public async Task An_agent_restarted_without_actions_is_handed_nothing()
    {
        var (factory, mac) = await HubAsync();
        using var _ = factory;
        await Queue(factory).QueueAsync("mac", DeleteOldFix, "me");

        (await mac.PostAsync(Snapshots, JsonContent.Create(HubFactory.Snapshot("mac", actions: false), options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();

        Assert.Empty(await ClaimAsync(mac));
        Assert.Equal(AgentActionStatus.Queued, (await OnlyAsync(factory)).Status);
    }

    // ---- results and the audit trail --------------------------------------------------------------

    [Fact]
    public async Task A_result_completes_the_action_and_only_its_own_machine_can_report_it()
    {
        var (factory, mac) = await HubAsync();
        using var _ = factory;
        var desktop = await AddMachineAsync(factory, "desktop", actions: true);
        await Queue(factory).QueueAsync("mac", DeleteOldFix, "me");

        var id = ActionQueue.Wire((await OnlyAsync(factory)).Id);
        Assert.Equal(HttpStatusCode.NotFound, (await ReportAsync(mac, id, true)).StatusCode); // not claimed yet

        id = Assert.Single(await ClaimAsync(mac)).Id;
        Assert.Equal(HttpStatusCode.NotFound, (await ReportAsync(desktop, id, true)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ReportAsync(mac, "999", true)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReportAsync(factory.CreateClient(), id, true)).StatusCode);
        Assert.Equal(AgentActionStatus.Claimed, (await OnlyAsync(factory)).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await ReportAsync(mac, id, true)).StatusCode);
        var row = await OnlyAsync(factory);
        Assert.Equal(AgentActionStatus.Succeeded, row.Status);
        Assert.Equal(0, row.ExitCode);
        Assert.Equal("Deleted branch old-fix (was 1111111).", row.Output);
        Assert.NotNull(row.ClaimedAt);
        Assert.NotNull(row.CompletedAt);

        // A result cannot be reported twice or changed afterwards.
        Assert.Equal(HttpStatusCode.NotFound, (await ReportAsync(mac, id, false, "changed my mind")).StatusCode);
        Assert.Equal(AgentActionStatus.Succeeded, (await OnlyAsync(factory)).Status);

        var audit = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/actions");
        var entry = Assert.Single(audit.EnumerateArray());
        Assert.Equal("Succeeded", entry.GetProperty("status").GetString());
        Assert.Equal("me", entry.GetProperty("queuedBy").GetString());
        Assert.Equal("git -C '/dev/widget' branch -d -- 'old-fix'", entry.GetProperty("commandLine").GetString());
        Assert.Contains("done on mac-host", WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/cleanup?open=all")));
    }

    [Fact]
    public async Task A_failure_is_recorded_with_truncated_output_and_can_be_queued_again()
    {
        var (factory, mac) = await HubAsync();
        using var _ = factory;
        await Queue(factory).QueueAsync("mac", DeleteOldFix, "me");
        var id = Assert.Single(await ClaimAsync(mac)).Id;

        await ReportAsync(mac, id, false, new string('x', 20_000) + "error: the branch 'old-fix' is not fully merged");

        var row = await OnlyAsync(factory);
        Assert.Equal(AgentActionStatus.Failed, row.Status);
        Assert.Equal(1, row.ExitCode);
        Assert.Equal(AgentActions.MaxOutputChars, row.Output!.Length);
        Assert.EndsWith("not fully merged", row.Output);

        var html = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/cleanup?open=all"));
        Assert.Contains("failed on mac-host", html);
        Assert.Contains("Run on mac-host", html); // offered again

        var again = await Queue(factory).QueueAsync("mac", DeleteOldFix, "me");
        Assert.True(again.Ok);
        Assert.Equal(2, (await Queue(factory).RecentAsync()).Count);
    }

    // ---- expiry -----------------------------------------------------------------------------------

    [Fact]
    public async Task An_action_nobody_picks_up_expires_and_is_never_handed_out()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var (factory, mac) = await HubAsync(factory: new HubFactory { Time = clock });
        using var _ = factory;
        await Queue(factory).QueueAsync("mac", DeleteOldFix, "me");

        clock.Now += TimeSpan.FromMinutes(9);
        Assert.Equal(0, await Queue(factory).ExpireStaleAsync());

        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Empty(await ClaimAsync(mac)); // the poll itself expires it first

        var row = await OnlyAsync(factory);
        Assert.Equal(AgentActionStatus.Expired, row.Status);
        Assert.Contains("not run", row.Output);
        Assert.Equal(HttpStatusCode.NotFound, (await ReportAsync(mac, ActionQueue.Wire(row.Id), true)).StatusCode);
        Assert.Equal(AgentActionStatus.Expired, (await OnlyAsync(factory)).Status);
    }

    [Fact]
    public async Task A_claimed_action_with_no_result_is_recorded_as_failed_and_is_not_retried()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var (factory, mac) = await HubAsync(factory: new HubFactory { Time = clock });
        using var _ = factory;
        await Queue(factory).QueueAsync("mac", DeleteOldFix, "me");
        Assert.Single(await ClaimAsync(mac));

        clock.Now += TimeSpan.FromMinutes(6);

        Assert.Equal(1, await Queue(factory).ExpireStaleAsync());
        var row = await OnlyAsync(factory);
        Assert.Equal(AgentActionStatus.Failed, row.Status);
        Assert.Contains("never reported a result", row.Output);
        Assert.Empty(await ClaimAsync(mac));
    }

    // ---- existing databases -----------------------------------------------------------------------

    [Fact]
    public async Task A_database_from_before_actions_gains_the_table_on_start_and_keeps_its_data()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DevDashDbContext>().UseSqlite(connection).Options;

        await using (var db = new DevDashDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Cache.Add(new CacheEntity { Key = "kept", Json = "{}" });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"AgentActions\""); // what an older hub's database looks like
        }

        await using (var db = new DevDashDbContext(options))
        {
            await db.Database.EnsureCreatedAsync(); // does nothing: the database exists
            await Assert.ThrowsAsync<SqliteException>(() => db.AgentActions.CountAsync());

            var created = await SchemaUpgrade.CreateMissingTablesAsync(db);
            Assert.Contains("AgentActions", created);
            Assert.Contains(created, name => name.StartsWith("IX_AgentActions"));

            db.AgentActions.Add(new AgentActionEntity { MachineId = "mac", Status = AgentActionStatus.Queued });
            await db.SaveChangesAsync();
            Assert.Equal(1, await db.AgentActions.CountAsync(a => a.MachineId == "mac" && a.Status == AgentActionStatus.Queued));
            Assert.Equal("kept", (await db.Cache.SingleAsync()).Key);

            Assert.Empty(await SchemaUpgrade.CreateMissingTablesAsync(db)); // safe to run on every start
        }
    }
}
