using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using DevDash.Agent;
using DevDash.Agent.Actions;
using DevDash.Agent.Git;
using DevDash.Agent.Scanning;
using DevDash.Contracts;
using DevDash.Hub.Data;
using DevDash.Hub.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.Tests;

/// <summary>
/// The hub side of Refresh: which agents are offered it, queueing without the actions switch, the
/// long poll that delivers it, what the pages show, and the round trip with the real agent code.
/// </summary>
public class RefreshTests
{
    private const string Snapshots = "/api/v1/snapshots";
    private const string Claim = "/api/v1/actions/claim";
    private const string RepoPage = "/repo?key=github.com%2Facme%2Fwidget";

    private static readonly AgentAction RefreshWidget = AgentAction.Refresh("/dev/widget");

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A machine on 0.5.0 that polls (updates on request) with git actions off: the default install.</summary>
    private static Task<HttpClient> AddMachineAsync(HubFactory factory, string machineId = "mac", bool refresh = true, string version = "0.5.0", bool actions = false, string? updateMode = "notify") =>
        AddMachineAsync(factory, HubFactory.Snapshot(machineId, factory.Services.GetRequiredService<TimeProvider>().GetUtcNow(), actions, updateMode, version, refresh));

    private static async Task<HttpClient> AddMachineAsync(HubFactory factory, MachineSnapshot snapshot)
    {
        var agent = factory.CreateAgentClient(await factory.IssueKeyAsync(snapshot.Machine.MachineId));
        (await agent.PostAsync(Snapshots, JsonContent.Create(snapshot, options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();
        return agent;
    }

    private static ActionQueue Queue(HubFactory factory) => factory.Services.GetRequiredService<ActionQueue>();

    private static async Task<List<PendingAgentAction>> ClaimAsync(HttpClient agent, int? wait = null)
    {
        var response = await agent.PostAsync(wait is null ? Claim : $"{Claim}?wait={wait}", null);
        response.EnsureSuccessStatusCode();
        return [.. (await response.Content.ReadFromJsonAsync<ActionClaimResponse>(SnapshotSchema.JsonOptions))!.Actions];
    }

    private static MachineInfo Machine(string version = "0.5.0", bool refresh = true, bool actions = false, string? updateMode = "notify") =>
        new() { MachineId = "mac", Hostname = "work-mac", AgentVersion = version, AcceptsRefresh = refresh, ActionsEnabled = actions, UpdateMode = updateMode };

    // ---- who is offered it ----------------------------------------------------------------------

    [Fact]
    public void Refresh_is_offered_to_agents_that_know_it_and_poll_and_the_rest_are_told_why()
    {
        // The default install: 0.5.0, updates on request, actions off.
        Assert.Equal(new RefreshAvailability(true, null), RefreshGate.For(Machine(), online: true));
        Assert.True(RefreshGate.For(Machine(updateMode: "off", actions: true), online: true).Available);
        Assert.True(RefreshGate.For(Machine(version: "0.6.1"), online: true).Available);

        // Too old to know the action: say which machine to update, and to what.
        var old = RefreshGate.For(Machine(version: "0.4.0", refresh: false), online: true);
        Assert.False(old.Available);
        Assert.Equal("Update the agent on work-mac to 0.5.0 or newer to refresh from here (it is on 0.4.0).", old.WhyNot);
        Assert.Contains("Update the agent on work-mac", RefreshGate.For(Machine(version: "0.4.0", refresh: false, actions: true), online: true).WhyNot); // actions on does not make an old agent new
        Assert.Contains("Update the agent on work-mac", RefreshGate.For(Machine(version: "", refresh: false), online: true).WhyNot);
        Assert.Contains("Update the agent on work-mac", RefreshGate.For(Machine(version: "not-a-version", refresh: false), online: true).WhyNot);

        // New enough, but it never asks the hub for anything.
        var silent = RefreshGate.For(Machine(refresh: false, updateMode: "off"), online: true);
        Assert.False(silent.Available);
        Assert.Equal("The agent on work-mac doesn't check for requests: updates are off and actions are off.", silent.WhyNot);

        // Fine, but not here right now.
        var offline = RefreshGate.For(Machine(), online: false);
        Assert.False(offline.Available);
        Assert.Contains("work-mac is offline", offline.WhyNot);
        Assert.Contains("Update the agent", RefreshGate.For(Machine(version: "0.4.0", refresh: false), online: false).WhyNot); // the lasting reason wins
    }

    [Fact]
    public void The_status_beside_a_button_follows_the_action()
    {
        var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        AgentActionEntity Row(AgentActionStatus status, int secondsAgo = 0, string output = "", string machine = "work-mac") => new()
        {
            MachineId = machine,
            MachineName = machine,
            Kind = AgentActionKind.Refresh,
            RepoPath = "/dev/widget",
            Status = status,
            QueuedAt = now.AddSeconds(-secondsAgo - 2),
            CompletedAt = status is AgentActionStatus.Queued or AgentActionStatus.Claimed ? null : now.AddSeconds(-secondsAgo),
            Output = output,
        };

        Assert.Equal(RefreshStatus.Idle, RefreshStatus.Of(null, now));

        var queued = RefreshStatus.Of(Row(AgentActionStatus.Queued), now);
        Assert.Equal((RefreshPhase.Refreshing, "Refreshing on work-mac…", true), (queued.Phase, queued.Text, queued.InFlight));
        Assert.Equal("Refreshing on work-mac…", RefreshStatus.Of(Row(AgentActionStatus.Claimed), now).Text);

        var done = RefreshStatus.Of(Row(AgentActionStatus.Succeeded, 3, "Rescanned widget in 412 ms"), now);
        Assert.Equal((RefreshPhase.Done, "Refreshed 3 s ago", false, "Rescanned widget in 412 ms"), (done.Phase, done.Text, done.InFlight, done.Detail));
        Assert.Equal("Refreshed just now", RefreshStatus.Of(Row(AgentActionStatus.Succeeded), now).Text);
        Assert.Equal("Refreshed 2 min ago", RefreshStatus.Of(Row(AgentActionStatus.Succeeded, 150), now).Text);

        var failed = RefreshStatus.Of(Row(AgentActionStatus.Failed, 5, "Refused: /dev/widget is outside this agent's scan roots."), now);
        Assert.Equal((RefreshPhase.Failed, "Refresh failed on work-mac"), (failed.Phase, failed.Text));
        Assert.Contains("outside this agent's scan roots", failed.Detail);
        Assert.Equal("No answer from work-mac", RefreshStatus.Of(Row(AgentActionStatus.Expired), now).Text);

        // The repo page's one button for several machines.
        var other = RefreshStatus.Of(Row(AgentActionStatus.Succeeded, 9, machine: "home-pc"), now);
        Assert.Equal(RefreshStatus.Idle, RefreshStatus.Combine([RefreshStatus.Idle, RefreshStatus.Idle]));
        Assert.Equal("Refreshing on work-mac…", RefreshStatus.Combine([queued, other]).Text); // in flight while any is
        Assert.Equal("Refresh failed on work-mac", RefreshStatus.Combine([failed, other]).Text); // then a failure
        Assert.Equal("Refreshed 9 s ago", RefreshStatus.Combine([done, other]).Text); // all done: as old as the oldest
        Assert.Equal("Refreshed on work-mac 3 s ago", RefreshStatus.Combine([done, RefreshStatus.Idle]).Text); // only some: say which
        Assert.True(RefreshStatus.Combine([queued, RefreshStatus.Of(Row(AgentActionStatus.Claimed, machine: "home-pc"), now)]).Text is "Refreshing on work-mac, home-pc…");
    }

    [Fact]
    public void A_repos_refresh_covers_each_entry_once_and_a_worktrees_main_working_tree()
    {
        var at = DateTimeOffset.UtcNow;
        GitState Git(bool linked) => new()
        {
            Branch = linked ? "feature" : "main",
            IsLinkedWorktree = linked,
            CommonDir = "/dev/widget/.git",
            Worktrees = [new WorktreeInfo { Path = "/dev/widget", Branch = "main" }, new WorktreeInfo { Path = "/dev/widget.worktrees/feature", Branch = "feature" }, new WorktreeInfo { Path = "/dev/widget.worktrees/spike", Branch = "spike" }],
        };
        DevEntry Repo(string name, string path, bool linked) => new() { Root = "/dev", Name = name, Path = path, Kind = EntryKind.GitRepo, Git = Git(linked) };

        var main = Repo("widget", "/dev/widget", false);
        var feature = Repo("feature", "/dev/widget.worktrees/feature", true);
        var spike = Repo("spike", "/dev/widget.worktrees/spike", true);
        var container = new DevEntry { Root = "/dev", Name = "widget.worktrees", Path = "/dev/widget.worktrees", Kind = EntryKind.WorktreeContainer, Children = [feature, spike] };
        MachineView View(string id, params DevEntry[] entries) =>
            new(new MachineSnapshot { Machine = new MachineInfo { MachineId = id, Hostname = id }, Roots = ["/dev"], Entries = entries }, at, at);

        var mac = View("mac", main, container);
        var pc = View("pc", container); // here the main working tree is not under the scan roots

        Assert.Equal("/dev/widget.worktrees", RefreshTargets.TopLevel(mac.Snapshot, "/dev/widget.worktrees/spike"));
        Assert.Equal("/dev/widget", RefreshTargets.TopLevel(mac.Snapshot, "/dev/widget"));
        Assert.Null(RefreshTargets.TopLevel(mac.Snapshot, "/etc"));
        Assert.Equal("/dev/widget.worktrees", RefreshTargets.For(new CloneView(mac, feature, "widget.worktrees"))!.Path);

        // Two worktrees in one folder are one entry to rescan; the main working tree comes along.
        var onlyWorktrees = RefreshTargets.For([new CloneView(mac, feature, "widget.worktrees"), new CloneView(mac, spike, "widget.worktrees")]);
        Assert.Equal([("mac", "/dev/widget.worktrees"), ("mac", "/dev/widget")], onlyWorktrees.Select(t => (t.Machine.MachineId, t.Path)));

        var everywhere = RefreshTargets.For([new CloneView(mac, main, null), new CloneView(mac, feature, "widget.worktrees"), new CloneView(mac, spike, "widget.worktrees"), new CloneView(pc, feature, "widget.worktrees")]);
        Assert.Equal([("mac", "/dev/widget"), ("mac", "/dev/widget.worktrees"), ("pc", "/dev/widget.worktrees")], everywhere.Select(t => (t.Machine.MachineId, t.Path)));
    }

    // ---- queueing and claiming ------------------------------------------------------------------

    [Fact]
    public async Task A_refresh_is_queued_and_handed_out_with_agent_actions_off()
    {
        using var factory = new HubFactory();
        var agent = await AddMachineAsync(factory);

        Assert.False(Queue(factory).CanRun("mac")); // no Run buttons: git actions are off on this machine
        var outcome = await Queue(factory).QueueAsync("mac", RefreshWidget, "me@example.com");

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal((AgentActionKind.Refresh, "refresh '/dev/widget'", "/dev/widget", "me@example.com"),
            (outcome.Action!.Kind, outcome.Action.CommandLine, outcome.Action.RepoPath, outcome.Action.QueuedBy));

        var claimed = Assert.Single(await ClaimAsync(agent));
        Assert.Equal((AgentActionKind.Refresh, "/dev/widget"), (claimed.Action.Kind, claimed.Action.RepoPath));
        Assert.Empty(await ClaimAsync(agent)); // once

        // A git action for the same machine is still refused.
        Assert.Contains("has not enabled agent actions", (await Queue(factory).QueueAsync("mac", AgentAction.Fetch("/dev/widget"), "me")).Error);
    }

    [Fact]
    public async Task The_hub_wide_actions_switch_does_not_stop_a_refresh()
    {
        using var factory = new HubFactory().WithWebHostBuilder(b => b.UseSetting("Hub:Actions:Enabled", "false"));
        var agent = factory.CreateClient();
        var keys = factory.Services.GetRequiredService<DevDash.Hub.Auth.ApiKeyService>();
        agent.DefaultRequestHeaders.Authorization = new("Bearer", (await keys.IssueAsync("mac")).Plaintext);
        (await agent.PostAsync(Snapshots, JsonContent.Create(HubFactory.Snapshot(actions: true, updateMode: "notify", version: "0.5.0", refresh: true), options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();
        var queue = factory.Services.GetRequiredService<ActionQueue>();

        Assert.False((await queue.QueueAsync("mac", AgentAction.Fetch("/dev/widget"), "me")).Ok);
        Assert.True((await queue.QueueAsync("mac", RefreshWidget, "me")).Ok);
        Assert.Equal(AgentActionKind.Refresh, Assert.Single(await ClaimAsync(agent)).Action.Kind);
    }

    [Fact]
    public async Task A_refresh_is_refused_for_agents_that_cannot_take_one_unknown_paths_and_anonymous_users()
    {
        using var factory = new HubFactory();
        var mac = await AddMachineAsync(factory);
        var old = await AddMachineAsync(factory, "old", refresh: false, version: "0.4.0", actions: true);
        await AddMachineAsync(factory, "silent", refresh: false, updateMode: "off");
        var queue = Queue(factory);

        Assert.Contains("Update the agent on old-host to 0.5.0", (await queue.QueueAsync("old", RefreshWidget, "me")).Error);
        Assert.Contains("doesn't check for requests", (await queue.QueueAsync("silent", RefreshWidget, "me")).Error);
        Assert.Contains("no longer reports anything at /etc", (await queue.QueueAsync("mac", AgentAction.Refresh("/etc"), "me")).Error);
        Assert.Contains("must be absolute", (await queue.QueueAsync("mac", AgentAction.Refresh("widget"), "me")).Error);
        Assert.False((await queue.QueueAsync("mac", RefreshWidget with { Branch = "main" }, "me")).Ok);
        Assert.False((await queue.QueueAsync("mac", RefreshWidget, " ")).Ok);
        Assert.Contains("not known", (await queue.QueueAsync("nope", RefreshWidget, "me")).Error);
        Assert.Empty(await queue.RecentAsync());

        // A folder that is not a repository can be refreshed too (its size is re-measured).
        Assert.True((await queue.QueueAsync("mac", AgentAction.Refresh("/dev/Reference Docs"), "me")).Ok);

        // An agent that was offered a refresh and then went back to an old version is never handed one.
        Assert.True((await queue.QueueAsync("mac", RefreshWidget, "me")).Ok);
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        (await mac.PostAsync(Snapshots, JsonContent.Create(HubFactory.Snapshot("mac", now, actions: true, updateMode: "notify", version: "0.4.0"), options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();
        Assert.Empty(await ClaimAsync(mac));
        Assert.Empty(await ClaimAsync(old));
    }

    [Fact]
    public async Task Asking_twice_while_one_is_on_its_way_is_one_refresh()
    {
        using var factory = new HubFactory();
        var agent = await AddMachineAsync(factory);

        var first = await Queue(factory).QueueAsync("mac", RefreshWidget, "me");
        var second = await Queue(factory).QueueAsync("mac", AgentAction.Refresh("/dev/widget"), "someone-else");
        Assert.Equal(first.Action!.Id, second.Action!.Id);

        var claimed = Assert.Single(await ClaimAsync(agent));
        var third = await Queue(factory).QueueAsync("mac", RefreshWidget, "me"); // still running on the agent
        Assert.Equal(first.Action.Id, third.Action!.Id);

        // Once it has reported, the next click is a new refresh.
        Assert.True(await Queue(factory).CompleteAsync("mac", claimed.Id, new ActionResultRequest { Succeeded = true, ExitCode = 0, Output = "Rescanned widget" }));
        var fourth = await Queue(factory).QueueAsync("mac", RefreshWidget, "me");
        Assert.NotEqual(first.Action.Id, fourth.Action!.Id);
    }

    [Fact]
    public async Task An_offline_machine_cannot_be_asked_and_an_unanswered_refresh_expires()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        using var factory = new HubFactory { Time = clock };
        await AddMachineAsync(factory);
        Assert.True((await Queue(factory).QueueAsync("mac", RefreshWidget, "me")).Ok);

        clock.Now += TimeSpan.FromMinutes(11);
        await Queue(factory).ExpireStaleAsync();
        var row = Assert.Single(await Queue(factory).RecentAsync());
        Assert.Equal(AgentActionStatus.Expired, row.Status);
        Assert.Equal("No answer from mac-host", RefreshStatus.Of(row, clock.Now).Text);

        clock.Now += TimeSpan.FromHours(2);
        Assert.Contains("offline", (await Queue(factory).QueueAsync("mac", RefreshWidget, "me")).Error);
        Assert.False(factory.Services.GetRequiredService<RefreshService>().For(factory.Services.GetRequiredService<SnapshotStore>().Get("mac")!).Available);
    }

    // ---- the long poll --------------------------------------------------------------------------

    [Fact]
    public async Task A_waiting_claim_returns_at_once_when_something_is_queued()
    {
        using var factory = new HubFactory();
        var agent = await AddMachineAsync(factory);
        await Queue(factory).QueueAsync("mac", RefreshWidget, "me");

        var stopwatch = Stopwatch.StartNew();
        var claimed = await ClaimAsync(agent, wait: 25);

        Assert.Single(claimed);
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 0, 5);
    }

    [Fact]
    public async Task A_waiting_claim_wakes_when_an_action_is_queued_for_that_machine()
    {
        using var factory = new HubFactory();
        var mac = await AddMachineAsync(factory);
        await AddMachineAsync(factory, "desktop");

        var stopwatch = Stopwatch.StartNew();
        var waiting = ClaimAsync(mac, wait: 25);
        await Task.Delay(400);
        Assert.False(waiting.IsCompleted); // held: nothing is queued

        // Something for another machine does not wake it, or get handed to it.
        await Queue(factory).QueueAsync("desktop", RefreshWidget, "me");
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);

        var queuedAt = stopwatch.Elapsed;
        var queued = await Queue(factory).QueueAsync("mac", RefreshWidget, "me");
        var claimed = Assert.Single(await waiting.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(ActionQueue.Wire(queued.Action!.Id), claimed.Id);
        Assert.InRange((stopwatch.Elapsed - queuedAt).TotalSeconds, 0, 5); // not the 25 s it asked for
        Assert.Equal(AgentActionStatus.Claimed, (await Queue(factory).RecentAsync()).Single(a => a.MachineId == "mac").Status);
    }

    [Fact]
    public async Task A_waiting_claim_times_out_with_nothing()
    {
        using var factory = new HubFactory();
        var agent = await AddMachineAsync(factory);

        var stopwatch = Stopwatch.StartNew();
        var claimed = await ClaimAsync(agent, wait: 1);

        Assert.Empty(claimed);
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 0.9, 10);
    }

    [Fact]
    public async Task A_claim_without_wait_answers_at_once_as_older_agents_expect()
    {
        using var factory = new HubFactory();
        var agent = await AddMachineAsync(factory, version: "0.4.0", refresh: false, actions: true);

        var stopwatch = Stopwatch.StartNew();
        Assert.Empty(await ClaimAsync(agent));
        Assert.Empty(await ClaimAsync(agent, wait: 0));
        Assert.Empty(await ClaimAsync(agent, wait: -5));
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 0, 3);

        Assert.Equal(HttpStatusCode.BadRequest, (await agent.PostAsync($"{Claim}?wait=soon", null)).StatusCode);
    }

    [Fact]
    public async Task A_waiting_claim_is_held_even_for_a_machine_that_accepts_nothing_and_ends_when_the_hub_stops_or_the_agent_goes()
    {
        using var factory = new HubFactory();
        await AddMachineAsync(factory, refresh: false, updateMode: "off"); // would be an instant, empty answer: a tight loop for an agent that long-polls
        var queue = Queue(factory);

        var stopwatch = Stopwatch.StartNew();
        Assert.Empty(await queue.ClaimAsync("mac", TimeSpan.FromSeconds(1)));
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 0.9, 10);

        // The hub is stopping: held polls are let go with an empty answer, so shutdown does not wait for them.
        using var stopping = new CancellationTokenSource();
        var held = queue.ClaimAsync("mac", TimeSpan.FromSeconds(60), CancellationToken.None, stopping.Token);
        await Task.Delay(200);
        Assert.False(held.IsCompleted);
        stopping.Cancel();
        Assert.Empty(await held.WaitAsync(TimeSpan.FromSeconds(5)));

        // The agent hung up: the request ends, and nothing was claimed on its behalf.
        using var gone = new CancellationTokenSource();
        var abandoned = queue.ClaimAsync("mac", TimeSpan.FromSeconds(60), gone.Token);
        await Task.Delay(200);
        gone.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // ---- the audit trail ------------------------------------------------------------------------

    [Fact]
    public async Task Refreshes_are_kept_out_of_the_audit_list_and_cannot_push_real_actions_out_of_it()
    {
        using var factory = new HubFactory();
        var agent = await AddMachineAsync(factory, actions: true);
        var queue = Queue(factory);
        Assert.True((await queue.QueueAsync("mac", AgentAction.DeleteBranches("/dev/widget", ["old-fix"]), "me@example.com")).Ok);

        for (var i = 0; i < ActionQueue.KeepRefreshes + 5; i++)
        {
            var queued = await queue.QueueAsync("mac", RefreshWidget, "me@example.com");
            Assert.True(queued.Ok, queued.Error);
            var claimed = (await queue.ClaimAsync("mac")).Single(c => c.Action.Kind == AgentActionKind.Refresh);
            Assert.True(await queue.CompleteAsync("mac", claimed.Id, new ActionResultRequest { Succeeded = i != 3, ExitCode = 0, Output = $"Rescanned widget ({i})" }));
        }

        var fixes = await queue.RecentAsync(refreshes: false, 500);
        var refreshes = await queue.RecentAsync(refreshes: true, 500);
        Assert.Equal(AgentActionKind.DeleteBranches, Assert.Single(fixes).Kind); // still there, 105 refreshes later
        Assert.Equal(ActionQueue.KeepRefreshes, refreshes.Count);
        Assert.Contains($"({ActionQueue.KeepRefreshes + 4})", refreshes[0].Output); // the newest are the ones kept

        var html = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/machines"));
        Assert.Contains("git -C '/dev/widget' branch -d -- 'old-fix'", html);
        Assert.Contains($"{ActionQueue.KeepRefreshes} refreshes", html); // one summary line, folded
        Assert.Contains("read-only rescans asked for from the dashboard", html);
        Assert.DoesNotContain("refresh '/dev/widget'", html); // never as a row among the fixes

        // The API is the complete record.
        var api = await factory.CreateClient().GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/actions?take=500");
        Assert.Equal(ActionQueue.KeepRefreshes, api.EnumerateArray().Count(a => a.GetProperty("kind").GetString() == "Refresh"));
        _ = agent;
    }

    // ---- the pages ------------------------------------------------------------------------------

    [Fact]
    public async Task The_repo_and_local_pages_offer_refresh_for_an_agent_that_takes_it()
    {
        using var factory = new HubFactory();
        await AddMachineAsync(factory);

        var repo = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync(RepoPage));
        Assert.Contains("<span>Refresh</span>", repo); // the button at the top
        Assert.Contains("Rescan this repo on mac-host now", repo);
        Assert.Contains("Rescan widget on mac-host now", repo); // and the one on the machine's row
        Assert.Contains("aria-disabled=\"false\"", repo);
        Assert.DoesNotContain("refresh-btn is-disabled", repo); // (the push button is disabled: this machine has actions off)

        var local = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/local"));
        Assert.Contains("Rescan widget on mac-host now", local);
        Assert.Contains("Rescan Reference Docs on mac-host now", local);
        Assert.DoesNotContain("is-disabled", local);

        // While one is on its way the buttons say so and take no more clicks.
        await Queue(factory).QueueAsync("mac", RefreshWidget, "me");
        repo = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync(RepoPage));
        Assert.Contains("Refreshing on mac-host…", repo);
        Assert.Contains("refresh-btn is-disabled", repo);
        Assert.Contains("aria-busy=\"true\"", repo);

        var claimed = Assert.Single(await Queue(factory).ClaimAsync("mac"));
        await Queue(factory).CompleteAsync("mac", claimed.Id, new ActionResultRequest { Succeeded = true, ExitCode = 0, Output = "Rescanned widget in 12 ms" });
        repo = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync(RepoPage));
        Assert.Matches("Refreshed (just now|\\d+ s ago)", repo);
        Assert.DoesNotContain("refresh-btn is-disabled", repo);
    }

    [Theory]
    [InlineData("0.4.0", "notify", "Update the agent on mac-host to 0.5.0 or newer to refresh from here (it is on 0.4.0).")]
    [InlineData("0.5.0", "off", "The agent on mac-host doesn't check for requests: updates are off and actions are off.")]
    public async Task The_pages_show_refresh_disabled_with_the_reason_for_an_agent_that_cannot_take_it(string version, string updateMode, string reason)
    {
        using var factory = new HubFactory();
        await AddMachineAsync(factory, refresh: false, version: version, updateMode: updateMode);

        foreach (var page in (string[])[RepoPage, "/local"])
        {
            var html = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync(page));
            Assert.Contains("refresh-btn is-disabled", html);
            Assert.Contains("aria-disabled=\"true\"", html);
            Assert.Contains(reason, html);
            Assert.DoesNotContain("aria-disabled=\"false\"", html);
        }
    }

    // ---- end to end -----------------------------------------------------------------------------

    /// <summary>
    /// The real agent code against the real hub: the agent's scan loop and its poll loop's long poll
    /// are running, a refresh is queued the way the pages queue it, and the hub ends up with the
    /// entry's new state and a succeeded action, without the agent having git actions enabled.
    /// </summary>
    [Fact]
    public async Task Click_long_poll_rescan_push_report()
    {
        var root = Directory.CreateTempSubdirectory("devdash-refresh-loop-").FullName;
        try
        {
            var dev = Directory.CreateDirectory(Path.Combine(root, "Developer")).FullName;
            var widget = Directory.CreateDirectory(Path.Combine(dev, "widget")).FullName;
            Git(widget, "init", "-q", "-b", "main");
            File.WriteAllText(Path.Combine(widget, "README.md"), "# Widget\n");
            Git(widget, "add", ".");
            Git(widget, "commit", "-q", "-m", "initial");

            using var factory = new HubFactory();
            var options = new StaticOptions(new AgentOptions
            {
                Roots = [dev],
                MachineId = "loop-box",
                HubUrl = "http://localhost/",
                ApiKey = await factory.IssueKeyAsync("loop"),
                Watch = false, // so only the refresh can be what rescans
                AutoFetch = new AutoFetchOptions { Enabled = false },
            });
            Assert.False(options.CurrentValue.Actions.Enabled);

            var git = new GitRunner();
            var scanner = new MachineScanner(
                new RepoScanner(git, new FetchService(git, options, NullLogger<FetchService>.Instance), options),
                new SizeCache(Path.Combine(root, "sizes.json")), options, NullLogger<MachineScanner>.Instance);
            var rescans = new RescanRequests(options);
            using var scanLoop = new AgentWorker(scanner, new HubClient(factory.CreateClient(), options), options, NullLogger<AgentWorker>.Instance, rescans);
            var poller = new ActionWorker(
                new ActionsClient(factory.CreateClient(), options),
                new ActionExecutor(git, new ActionValidator(git, options), options, NullLogger<ActionExecutor>.Instance, rescans: rescans),
                rescans, options, NullLogger<ActionWorker>.Instance);

            var store = factory.Services.GetRequiredService<SnapshotStore>();
            GitState? Hub() => store.Get("loop-box")?.Snapshot.Entries.Single(e => e.Name == "widget").Git;

            await scanLoop.StartAsync(CancellationToken.None);
            try
            {
                await UntilAsync(() => Hub() is not null);
                Assert.Equal(0, Hub()!.Untracked);
                Assert.True(store.Get("loop-box")!.Machine.AcceptsRefresh);

                // The agent is waiting on the hub; then something changes on disk and someone clicks Refresh.
                var poll = poller.PollAsync(waitSeconds: 25, CancellationToken.None);
                await Task.Delay(300);
                Assert.False(poll.IsCompleted);
                File.WriteAllText(Path.Combine(widget, "notes.txt"), "new");

                var stopwatch = Stopwatch.StartNew();
                var target = RefreshTargets.For(new CloneView(store.Get("loop-box")!, store.Get("loop-box")!.Snapshot.Entries.Single(e => e.Name == "widget"), null))!;
                var queued = await factory.Services.GetRequiredService<RefreshService>().QueueAsync(target, "me@example.com");
                Assert.True(queued.Ok, queued.Error);

                var result = await poll.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(new ActionWorker.PollResult(1, false), result);
                Assert.InRange(stopwatch.Elapsed.TotalSeconds, 0, 15); // seconds, not the 25 s of the poll or the 15 min of the scan

                Assert.Equal(1, Hub()!.Untracked); // the snapshot is there by the time the action reads Succeeded
                var row = Assert.Single(await Queue(factory).RecentAsync());
                Assert.Equal(AgentActionStatus.Succeeded, row.Status);
                Assert.Contains("Rescanned widget", row.Output);
                Assert.Contains("auto-fetch is off on this machine", row.Output);
                Assert.Matches("Refreshed (just now|\\d+ s ago)", WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/repo?key=" + Uri.EscapeDataString($"local:loop-box:{widget}"))));
            }
            finally
            {
                await scanLoop.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            try
            {
                // Git marks object files read-only on Windows, which Directory.Delete refuses.
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort
            }
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), "Timed out waiting for the agent's first snapshot.");
            await Task.Delay(50);
        }
    }

    private static string Git(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in (string[])["-c", "user.name=DevDash Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", .. args])
        {
            psi.ArgumentList.Add(arg);
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

    private sealed class StaticOptions(AgentOptions value) : IOptionsMonitor<AgentOptions>
    {
        public AgentOptions CurrentValue => value;

        public AgentOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<AgentOptions, string?> listener) => null;
    }
}
