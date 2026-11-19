using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DevDash.Agent;
using DevDash.Agent.Actions;
using DevDash.Agent.Git;
using DevDash.Agent.Updates;
using DevDash.Contracts;
using DevDash.Hub.Data;
using DevDash.Hub.GitHub;
using DevDash.Hub.Services;
using DevDash.Hub.Updates;
using DevDash.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.Tests;

/// <summary>
/// The hub's part in agent updates: knowing the latest release, showing who is behind, and queueing
/// an update that only an agent whose own settings allow it will ever be handed.
/// </summary>
public class UpdateTests
{
    private const string Snapshots = "/api/v1/snapshots";
    private const string Claim = "/api/v1/actions/claim";

    private static async Task<HttpClient> AddMachineAsync(HubFactory factory, string machineId, string? updateMode, bool actions = false, string version = "0.2.0")
    {
        var agent = factory.CreateAgentClient(await factory.IssueKeyAsync(machineId));
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var snapshot = HubFactory.Snapshot(machineId, now, actions, updateMode, version);
        (await agent.PostAsync(Snapshots, JsonContent.Create(snapshot, options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();
        return agent;
    }

    private static async Task<List<PendingAgentAction>> ClaimAsync(HttpClient agent)
    {
        var response = await agent.PostAsync(Claim, null);
        response.EnsureSuccessStatusCode();
        return [.. (await response.Content.ReadFromJsonAsync<ActionClaimResponse>(SnapshotSchema.JsonOptions))!.Actions];
    }

    private static AgentUpdates Updates(HubFactory factory) => factory.Services.GetRequiredService<AgentUpdates>();

    private static ActionQueue Queue(HubFactory factory) => factory.Services.GetRequiredService<ActionQueue>();

    // ---- which release is the latest ------------------------------------------------------------

    private static LatestRelease? Latest(string json, bool prereleases = false)
    {
        using var document = JsonDocument.Parse(json);
        return ReleaseParser.Latest(document.RootElement, prereleases);
    }

    [Fact]
    public void The_latest_release_is_the_highest_version_not_the_first_listed()
    {
        var json = $"[{HubFactory.ReleaseJson("v0.9.0")},{HubFactory.ReleaseJson("v0.10.0")},{HubFactory.ReleaseJson("v0.2.0")}]";

        var latest = Latest(json);

        Assert.NotNull(latest);
        Assert.Equal(("0.10.0", "v0.10.0", true, false), (latest.Version, latest.Tag, latest.Signed, latest.Prerelease));
        Assert.Equal("https://github.com/mikejobson/dev-dashboard/releases/tag/v0.10.0", latest.Url);
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero), latest.PublishedAt);
    }

    [Fact]
    public void Drafts_are_never_counted_and_prereleases_only_when_asked_for()
    {
        var json = "[" + string.Join(",",
            HubFactory.ReleaseJson("v0.2.0"),
            HubFactory.ReleaseJson("v0.5.0", draft: true),
            HubFactory.ReleaseJson("v0.4.0", prerelease: true),
            HubFactory.ReleaseJson("v0.3.0-rc.1"),          // a pre-release by its tag, even if nobody ticked the box
            HubFactory.ReleaseJson("nightly"),
            HubFactory.ReleaseJson("v1"),
            """{ "tag_name": 7 }""", "null", "\"text\"") + "]";

        Assert.Equal("0.2.0", Latest(json)!.Version);
        var withPrereleases = Latest(json, prereleases: true)!;
        Assert.Equal(("0.4.0", true), (withPrereleases.Version, withPrereleases.Prerelease));
    }

    [Fact]
    public void A_release_without_a_signature_is_known_but_marked_unsigned()
    {
        Assert.False(Latest($"[{HubFactory.ReleaseJson("v0.1.0", signed: false)}]")!.Signed);
        Assert.Null(Latest("[]"));
        Assert.Null(Latest("""{ "message": "Not Found" }"""));
        Assert.Equal("0.3.0", Latest("""[ { "tag_name": "0.3.0" } ]""")!.Version); // no v, no assets listed
    }

    // ---- asking GitHub --------------------------------------------------------------------------

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string? Auth)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Token(string? token) : IGitHubTokenProvider
    {
        public Task<string?> GetTokenAsync(CancellationToken ct) => Task.FromResult(token);

        public void Invalidate()
        {
        }
    }

    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static ReleaseClient Client(Stub stub, string? token, HubUpdateOptions? updates = null) =>
        new(new HttpClient(stub), new Token(token), new Monitor<HubOptions>(new HubOptions { Updates = updates ?? new HubUpdateOptions() }),
            new Monitor<GitHubOptions>(new GitHubOptions()), NullLogger<ReleaseClient>.Instance);

    [Fact]
    public async Task Without_a_token_the_public_repository_is_read_anonymously()
    {
        var stub = new Stub(_ => Json($"[{HubFactory.ReleaseJson("v0.2.0")}]"));

        var latest = await Client(stub, token: null).GetLatestAsync(CancellationToken.None);

        Assert.Equal("0.2.0", latest!.Version);
        Assert.Equal([("https://api.github.com/repos/mikejobson/dev-dashboard/releases?per_page=30", null)], stub.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]   // a fine-grained token that cannot see this repository
    public async Task A_token_github_rejects_does_not_stop_the_anonymous_lookup(HttpStatusCode rejection)
    {
        var stub = new Stub(request => request.Headers.Authorization is null ? Json($"[{HubFactory.ReleaseJson("v0.2.0")}]") : Json("""{ "message": "no" }""", rejection));

        var latest = await Client(stub, token: "expired").GetLatestAsync(CancellationToken.None);

        Assert.Equal("0.2.0", latest!.Version);
        Assert.Equal(["Bearer expired", null], stub.Requests.Select(r => r.Auth));
    }

    [Fact]
    public async Task The_configured_repository_and_prerelease_setting_are_used()
    {
        var stub = new Stub(_ => Json($"[{HubFactory.ReleaseJson("v0.2.0")},{HubFactory.ReleaseJson("v0.3.0-rc.1", prerelease: true)}]"));

        var latest = await Client(stub, "token-123", new HubUpdateOptions { Repository = "someone/fork", IncludePrereleases = true }).GetLatestAsync(CancellationToken.None);

        Assert.Equal("0.3.0-rc.1", latest!.Version);
        Assert.Equal(("https://api.github.com/repos/someone/fork/releases?per_page=30", "Bearer token-123"), stub.Requests.Single());

        var bad = await Assert.ThrowsAsync<ReleaseQueryException>(() => Client(stub, null, new HubUpdateOptions { Repository = "not a repo/../x" }).GetLatestAsync(CancellationToken.None));
        Assert.Contains("not an owner/name repository", bad.Message);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Being_rate_limited_is_reported_plainly()
    {
        var stub = new Stub(_ =>
        {
            var response = Json("""{ "message": "API rate limit exceeded" }""", HttpStatusCode.Forbidden);
            response.Headers.Add("x-ratelimit-remaining", "0");
            return response;
        });

        var error = await Assert.ThrowsAsync<ReleaseQueryException>(() => Client(stub, token: null).GetLatestAsync(CancellationToken.None));

        Assert.Contains("rate limit", error.Message);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task A_failed_check_keeps_the_release_already_known_and_says_what_went_wrong()
    {
        using var factory = new HubFactory();
        Assert.Equal("0.2.0", (await factory.CheckReleasesAsync(HubFactory.ReleaseJson("v0.2.0")))!.Version);
        var store = factory.Services.GetRequiredService<ReleaseStore>();
        Assert.Null(store.Status.LastError);
        Assert.Contains(factory.ReleaseRequests, r => r is { Url: "https://api.github.com/repos/mikejobson/dev-dashboard/releases?per_page=30", Auth: null });

        factory.ReleasesJson = "this is not json";
        await factory.Services.GetRequiredService<ReleasePoller>().PollOnceAsync(CancellationToken.None);

        Assert.Equal("0.2.0", store.Current!.Version);
        Assert.NotNull(store.Status.LastError);
        Assert.Contains("Could not check for a new agent release", WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/machines")));

        // And it survives a restart: the answer is kept in the database.
        var reloaded = new ReleaseStore(
            factory.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<DevDashDbContext>>(),
            factory.Services.GetRequiredService<DashboardNotifier>(), TimeProvider.System);
        await reloaded.LoadAsync();
        Assert.Equal(store.Current, reloaded.Current);
    }

    // ---- who is behind, and who gets an Update button --------------------------------------------

    [Fact]
    public async Task Machines_shows_each_agent_against_the_latest_release()
    {
        using var factory = new HubFactory();
        await AddMachineAsync(factory, "current", AgentUpdateModes.Notify, version: "0.3.0");
        await AddMachineAsync(factory, "behind", AgentUpdateModes.Notify, version: "0.2.0");
        await AddMachineAsync(factory, "auto", AgentUpdateModes.Auto, version: "0.2.1");
        await AddMachineAsync(factory, "locked", AgentUpdateModes.Off, version: "0.2.0");
        await AddMachineAsync(factory, "old", updateMode: null, version: "0.1.0"); // a v0.1.0 agent says nothing about updates
        await factory.CheckReleasesAsync(HubFactory.ReleaseJson("v0.3.0"), HubFactory.ReleaseJson("v0.2.0"));

        var updates = Updates(factory);
        var store = factory.Services.GetRequiredService<SnapshotStore>();
        MachineUpdateView View(string id) => updates.For(store.Get(id)!);

        Assert.Equal((AgentUpdateState.UpToDate, false), (View("current").State, View("current").CanUpdate));
        Assert.Equal((AgentUpdateState.Behind, true, null), (View("behind").State, View("behind").CanUpdate, View("behind").WhyNot));
        Assert.True(View("auto").CanUpdate);
        Assert.Equal((AgentUpdateState.Behind, false), (View("locked").State, View("locked").CanUpdate));
        Assert.Contains("Updates are off on this machine", View("locked").WhyNot);
        Assert.Equal((AgentUpdateState.Behind, false), (View("old").State, View("old").CanUpdate));
        Assert.Contains("predates self-update", View("old").WhyNot);
        Assert.Equal(["auto", "behind"], updates.Updatable().Select(m => m.MachineId).Order());
        Assert.Equal(4, updates.BehindCount());

        var html = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/machines"));
        Assert.Contains("Latest agent release", html);
        Assert.Contains("Update available: 0.3.0", html);
        Assert.Contains("up to date", html);
        Assert.Contains("Update all (2)", html);
        Assert.Contains("updates: not supported by this version", html);
        Assert.Contains("Run the install one-liner on that machine once", html);
        Assert.Equal(2, html.Split("Ask this machine's agent to install 0.3.0").Length - 1); // an Update button for the two that accept one

        var overview = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/"));
        Assert.Contains("4 agents are behind the latest release (0.3.0)", overview);

        var api = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/machines");
        var old = api.EnumerateArray().Single(m => m.GetProperty("machineId").GetString() == "old");
        Assert.True(old.GetProperty("updateAvailable").GetBoolean());
        Assert.False(old.GetProperty("canUpdate").GetBoolean());
        Assert.Equal("0.3.0", old.GetProperty("latestVersion").GetString());
        Assert.False(old.TryGetProperty("updateMode", out var mode) && mode.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task With_no_release_known_nothing_is_offered()
    {
        using var factory = new HubFactory();
        await AddMachineAsync(factory, "mac", AgentUpdateModes.Notify);
        await factory.CheckReleasesAsync();

        var view = Updates(factory).For(factory.Services.GetRequiredService<SnapshotStore>().Get("mac")!);
        Assert.Equal((AgentUpdateState.Unknown, false), (view.State, view.CanUpdate));
        Assert.False((await Updates(factory).QueueAsync("mac", "me")).Ok);

        var html = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/machines"));
        Assert.DoesNotContain("Update available", html);
        Assert.DoesNotContain("behind the latest release", await factory.CreateClient().GetStringAsync("/"));
    }

    [Fact]
    public async Task An_unsigned_release_is_shown_but_cannot_be_installed_from_the_dashboard()
    {
        using var factory = new HubFactory();
        await AddMachineAsync(factory, "mac", AgentUpdateModes.Notify);
        await factory.CheckReleasesAsync(HubFactory.ReleaseJson("v0.3.0", signed: false));

        var view = Updates(factory).For(factory.Services.GetRequiredService<SnapshotStore>().Get("mac")!);
        Assert.Equal((AgentUpdateState.Behind, false), (view.State, view.CanUpdate));
        Assert.Contains("not signed", view.WhyNot);

        var outcome = await Updates(factory).QueueAsync("mac", "me");
        Assert.False(outcome.Ok);
        Assert.Contains("not signed", outcome.Error);
    }

    [Fact]
    public async Task A_rolled_back_update_is_shown_on_the_machine()
    {
        using var factory = new HubFactory();
        var agent = factory.CreateAgentClient(await factory.IssueKeyAsync("mac"));
        var snapshot = HubFactory.Snapshot("mac", updateMode: AgentUpdateModes.Notify, version: "0.2.0");
        snapshot = snapshot with
        {
            Machine = snapshot.Machine with
            {
                LastUpdate = new AgentUpdateReport
                {
                    Outcome = AgentUpdateOutcomes.RolledBack, FromVersion = "0.2.0", ToVersion = "0.3.0", At = DateTimeOffset.UtcNow,
                    Detail = "Rolled back from 0.3.0 to 0.2.0: it ran without reporting to the hub in time.",
                },
            },
        };
        (await agent.PostAsync(Snapshots, JsonContent.Create(snapshot, options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();

        var html = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/machines"));

        Assert.Contains("rolled back from 0.3.0 to 0.2.0", html);
        Assert.Contains("it ran without reporting to the hub in time", html);
    }

    // ---- queueing and claiming: two separate switches --------------------------------------------

    [Fact]
    public async Task An_update_can_be_queued_and_claimed_by_an_agent_with_actions_off_but_git_actions_cannot()
    {
        using var factory = new HubFactory();
        var agent = await AddMachineAsync(factory, "mac", AgentUpdateModes.Notify, actions: false);
        await factory.CheckReleasesAsync(HubFactory.ReleaseJson("v0.3.0"));

        // Git actions stay exactly as closed as before.
        Assert.False(Queue(factory).CanRun("mac"));
        var git = await Queue(factory).QueueAsync("mac", AgentAction.DeleteBranches("/dev/widget", ["old-fix"]), "me@example.com");
        Assert.False(git.Ok);
        Assert.Contains("has not enabled agent actions", git.Error);

        var queued = await Updates(factory).QueueAsync("mac", "me@example.com");
        Assert.True(queued.Ok, queued.Error);
        Assert.Equal((AgentActionKind.UpdateAgent, "devdash-agent update --version 0.3.0", "", "me@example.com"),
            (queued.Action!.Kind, queued.Action.CommandLine, queued.Action.RepoPath, queued.Action.QueuedBy));
        Assert.Equal("""{"kind":"UpdateAgent","version":"0.3.0","setUpstream":false}""", queued.Action.ActionJson);
        Assert.Equal(queued.Action.Id, (await Updates(factory).QueueAsync("mac", "me@example.com")).Action!.Id); // a double click queues nothing new
        Assert.Single(await Queue(factory).RecentAsync());
        Assert.Contains("update queued", WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/machines")));

        var claimed = Assert.Single(await ClaimAsync(agent));
        Assert.Equal(AgentAction.UpdateAgent("0.3.0"), claimed.Action);
        Assert.Empty(await ClaimAsync(agent)); // handed out once

        var html = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/machines"));
        Assert.Contains("devdash-agent update --version 0.3.0", html);
        Assert.Contains("updating", html);

        (await agent.PostAsync($"/api/v1/actions/{claimed.Id}/result", JsonContent.Create(
            new ActionResultRequest { Succeeded = true, ExitCode = 0, Output = "Installed 0.3.0 (was 0.2.0). Restarting now." }, options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();
        var row = Assert.Single(await Queue(factory).RecentAsync());
        Assert.Equal((AgentActionStatus.Succeeded, 0), (row.Status, row.ExitCode));

        // The new version reports in: the machine is up to date and the button is gone.
        var now = DateTimeOffset.UtcNow;
        (await agent.PostAsync(Snapshots, JsonContent.Create(HubFactory.Snapshot("mac", now, false, AgentUpdateModes.Notify, "0.3.0"), options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();
        Assert.Equal(AgentUpdateState.UpToDate, Updates(factory).For(factory.Services.GetRequiredService<SnapshotStore>().Get("mac")!).State);
        Assert.False((await Updates(factory).QueueAsync("mac", "me")).Ok);
    }

    [Fact]
    public async Task A_git_action_is_never_handed_to_an_agent_that_only_polls_for_updates()
    {
        using var factory = new HubFactory();
        var agent = await AddMachineAsync(factory, "mac", AgentUpdateModes.Notify, actions: true);
        await factory.CheckReleasesAsync(HubFactory.ReleaseJson("v0.3.0"));
        Assert.True((await Queue(factory).QueueAsync("mac", AgentAction.DeleteBranches("/dev/widget", ["old-fix"]), "me")).Ok);
        Assert.True((await Updates(factory).QueueAsync("mac", "me")).Ok);

        // The machine's owner restarts the agent without --enable-actions; it still polls, for updates.
        var now = DateTimeOffset.UtcNow;
        (await agent.PostAsync(Snapshots, JsonContent.Create(HubFactory.Snapshot("mac", now, actions: false, AgentUpdateModes.Notify, "0.2.0"), options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();

        var claimed = Assert.Single(await ClaimAsync(agent));
        Assert.Equal(AgentActionKind.UpdateAgent, claimed.Action.Kind);
        var git = (await Queue(factory).RecentAsync()).Single(a => a.Kind == AgentActionKind.DeleteBranches);
        Assert.Equal(AgentActionStatus.Queued, git.Status); // left to expire, never handed out
    }

    [Theory]
    [InlineData(null)]                   // an agent from before self-update
    [InlineData(AgentUpdateModes.Off)]
    [InlineData("anything-else")]
    public async Task An_update_is_not_queued_for_or_handed_to_an_agent_that_does_not_accept_updates(string? mode)
    {
        using var factory = new HubFactory();
        var agent = await AddMachineAsync(factory, "mac", AgentUpdateModes.Notify, actions: true);
        await factory.CheckReleasesAsync(HubFactory.ReleaseJson("v0.3.0"));
        Assert.True((await Updates(factory).QueueAsync("mac", "me")).Ok);

        // Its next snapshot says otherwise (updates switched off since, or an older agent took its place).
        var now = DateTimeOffset.UtcNow;
        (await agent.PostAsync(Snapshots, JsonContent.Create(HubFactory.Snapshot("mac", now, actions: true, mode, "0.2.0"), options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();

        Assert.Empty(await ClaimAsync(agent)); // having git actions on does not let an update through
        var refused = await Updates(factory).QueueAsync("other", "me");
        Assert.False(refused.Ok);
        var again = await Queue(factory).QueueAsync("mac", AgentAction.UpdateAgent("0.4.0"), "me");
        Assert.False(again.Ok);
        Assert.Contains(mode is null ? "predates self-update" : "updates switched off", again.Error);
    }

    [Fact]
    public async Task The_hub_only_asks_for_the_latest_release_it_knows_and_never_for_a_step_back()
    {
        using var factory = new HubFactory();
        await AddMachineAsync(factory, "mac", AgentUpdateModes.Notify, version: "0.2.0");
        await factory.CheckReleasesAsync(HubFactory.ReleaseJson("v0.3.0"));
        var queue = Queue(factory);

        Assert.Contains("not the latest release", (await queue.QueueAsync("mac", AgentAction.UpdateAgent("9.9.9"), "me")).Error);
        Assert.Contains("not the latest release", (await queue.QueueAsync("mac", AgentAction.UpdateAgent("0.1.0"), "me")).Error);
        Assert.Contains("not a strict", (await queue.QueueAsync("mac", AgentAction.UpdateAgent("v0.3.0"), "me")).Error);
        Assert.Contains("does not take a repository path", (await queue.QueueAsync("mac", AgentAction.UpdateAgent("0.3.0") with { RepoPath = "/dev/widget" }, "me")).Error);
        Assert.Contains("Could not tell who is asking", (await queue.QueueAsync("mac", AgentAction.UpdateAgent("0.3.0"), " ")).Error);
        Assert.Empty(await queue.RecentAsync());
    }

    [Fact]
    public async Task The_two_hub_switches_are_independent()
    {
        var noActions = new HubFactory().WithWebHostBuilder(b => b.UseSetting("Hub:Actions:Enabled", "false"));
        using (noActions)
        {
            var keys = noActions.Services.GetRequiredService<DevDash.Hub.Auth.ApiKeyService>();
            var agent = noActions.CreateClient();
            agent.DefaultRequestHeaders.Authorization = new("Bearer", (await keys.IssueAsync("mac")).Plaintext);
            (await agent.PostAsync(Snapshots, JsonContent.Create(HubFactory.Snapshot(actions: true, updateMode: AgentUpdateModes.Notify, version: "0.2.0"), options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();
            var releases = noActions.Services.GetRequiredService<ReleaseStore>();
            await releases.SetAsync(new LatestRelease("0.3.0", "v0.3.0", null, null, Signed: true, Prerelease: false));

            Assert.True((await noActions.Services.GetRequiredService<AgentUpdates>().QueueAsync("mac", "me")).Ok); // updates still work
            Assert.Equal(AgentActionKind.UpdateAgent, Assert.Single(await ClaimAsync(agent)).Action.Kind);
            Assert.False((await noActions.Services.GetRequiredService<ActionQueue>().QueueAsync("mac", AgentAction.Fetch("/dev/widget"), "me")).Ok);
        }

        var noUpdates = new HubFactory().WithWebHostBuilder(b => b.UseSetting("Hub:Updates:Enabled", "false"));
        using (noUpdates)
        {
            var keys = noUpdates.Services.GetRequiredService<DevDash.Hub.Auth.ApiKeyService>();
            var agent = noUpdates.CreateClient();
            agent.DefaultRequestHeaders.Authorization = new("Bearer", (await keys.IssueAsync("mac")).Plaintext);
            (await agent.PostAsync(Snapshots, JsonContent.Create(HubFactory.Snapshot(actions: true, updateMode: AgentUpdateModes.Notify, version: "0.2.0"), options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();
            await noUpdates.Services.GetRequiredService<ReleaseStore>().SetAsync(new LatestRelease("0.3.0", "v0.3.0", null, null, Signed: true, Prerelease: false));

            var refused = await noUpdates.Services.GetRequiredService<AgentUpdates>().QueueAsync("mac", "me");
            Assert.False(refused.Ok);
            Assert.Contains("Hub:Updates:Enabled", refused.Error);
            Assert.True((await noUpdates.Services.GetRequiredService<ActionQueue>().QueueAsync("mac", AgentAction.Fetch("/dev/widget"), "me")).Ok); // git actions still work
            Assert.DoesNotContain("Ask this machine's agent to install", await noUpdates.CreateClient().GetStringAsync("/machines"));
        }
    }

    [Fact]
    public async Task An_update_has_longer_to_report_back_than_a_git_action()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
        using var factory = new HubFactory { Time = clock };
        var agent = await AddMachineAsync(factory, "mac", AgentUpdateModes.Notify);
        await factory.CheckReleasesAsync(HubFactory.ReleaseJson("v0.3.0"));
        Assert.True((await Updates(factory).QueueAsync("mac", "me")).Ok);
        Assert.Single(await ClaimAsync(agent));

        clock.Now = clock.Now.AddMinutes(10); // downloading a release takes a while; a git action would have been written off by now
        await Queue(factory).ExpireStaleAsync();
        Assert.Equal(AgentActionStatus.Claimed, Assert.Single(await Queue(factory).RecentAsync()).Status);

        clock.Now = clock.Now.AddMinutes(6);
        await Queue(factory).ExpireStaleAsync();
        var row = Assert.Single(await Queue(factory).RecentAsync());
        Assert.Equal(AgentActionStatus.Failed, row.Status);
        Assert.Contains("never reported a result", row.Output);
    }

    // ---- old agents -----------------------------------------------------------------------------

    [Fact]
    public async Task A_snapshot_from_a_0_1_0_agent_is_still_accepted_and_reads_as_no_self_update()
    {
        using var factory = new HubFactory();
        var agent = factory.CreateAgentClient(await factory.IssueKeyAsync("old"));

        // Exactly what v0.1.0 sends: schema 1, no updateMode, no lastUpdate.
        const string body = """
            { "schemaVersion": 1,
              "machine": { "machineId": "old-box", "hostname": "Old-Box", "os": "Linux", "osDescription": "Ubuntu 24.04", "agentVersion": "0.1.0", "actionsEnabled": true },
              "scannedAt": "2026-11-18T09:00:00+00:00", "scanDurationMs": 12, "roots": ["/home/sam/Developer"], "entries": [] }
            """;
        var response = await agent.PostAsync(Snapshots, new StringContent(body, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        var machine = factory.Services.GetRequiredService<SnapshotStore>().Get("old-box")!;
        Assert.Null(machine.Machine.UpdateMode);
        Assert.Null(machine.Machine.LastUpdate);
        Assert.True(machine.Machine.ActionsEnabled);

        await factory.CheckReleasesAsync(HubFactory.ReleaseJson("v0.2.0"));
        var view = Updates(factory).For(machine);
        Assert.Equal((AgentUpdateState.Behind, false), (view.State, view.CanUpdate));
        Assert.False((await Updates(factory).QueueAsync("old-box", "me")).Ok);

        // It never sees an action kind it does not know, even though it polls (it has git actions on).
        Assert.Empty(await ClaimAsync(agent));
    }

    [Fact]
    public void The_hub_accepts_the_current_snapshot_format_and_the_one_before()
    {
        Assert.True(SnapshotSchema.IsSupported(SnapshotSchema.Version));
        Assert.True(SnapshotSchema.IsSupported(SnapshotSchema.MinSupportedVersion));
        Assert.InRange(SnapshotSchema.Version - SnapshotSchema.MinSupportedVersion, 0, 1);
        Assert.False(SnapshotSchema.IsSupported(SnapshotSchema.Version + 1));
        Assert.False(SnapshotSchema.IsSupported(SnapshotSchema.MinSupportedVersion - 1));

        // What a new agent sends that an old hub does not know is optional, so an old hub simply ignores it.
        var json = JsonSerializer.Serialize(HubFactory.Snapshot(updateMode: AgentUpdateModes.Auto), SnapshotSchema.JsonOptions);
        Assert.Contains("\"updateMode\":\"auto\"", json);
        Assert.DoesNotContain("lastUpdate", json);
        Assert.DoesNotContain("updateMode", JsonSerializer.Serialize(HubFactory.Snapshot(), SnapshotSchema.JsonOptions));
    }

    // ---- the whole loop: dashboard to hub to agent to a swapped program --------------------------

    [Fact]
    public async Task Queue_claim_verify_swap_report_and_confirm()
    {
        var root = Directory.CreateTempSubdirectory("devdash-ota-").FullName;
        try
        {
            using var factory = new HubFactory();
            using var site = new FakeRelease();
            var installation = FakeRelease.Install(Path.Combine(root, "bin"), "0.2.0");
            site.Publish("0.3.0", installation);
            await factory.CheckReleasesAsync(HubFactory.ReleaseJson("v0.3.0"));

            // An agent with git actions OFF and updates on request (the defaults).
            var agentOptions = new AgentOptions { MachineId = "ota-box", HubUrl = "http://localhost/", ApiKey = await factory.IssueKeyAsync("ota"), Roots = [Path.Combine(root, "Developer")] };
            Assert.False(agentOptions.Actions.Enabled);
            var options = new TestOptions(agentOptions);
            var http = factory.CreateClient();
            var hub = new HubClient(http, options);

            Task PushAsync(string version, AgentUpdateReport? last = null) => hub.PushAsync(new MachineSnapshot
            {
                Machine = new MachineInfo { MachineId = "ota-box", Hostname = "OTA-Box", Os = "Linux", AgentVersion = version, UpdateMode = agentOptions.Updates.ResolvedMode, LastUpdate = last },
                ScannedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);

            await PushAsync("0.2.0");
            Assert.Contains("Update available: 0.3.0", WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/machines")));
            Assert.True((await Updates(factory).QueueAsync("ota-box", "me@example.com")).Ok);

            var git = new GitRunner();
            var executor = new ActionExecutor(git, new ActionValidator(git, options), options, NullLogger<ActionExecutor>.Instance, new AgentGate(), site.Updater(installation, "0.2.0", agentOptions));
            var worker = new ActionWorker(new ActionsClient(http, options), executor, new RescanRequests(options), options, NullLogger<ActionWorker>.Instance);
            Assert.True(ActionWorker.ShouldPoll(agentOptions));

            Assert.Equal(1, await worker.PollOnceAsync(CancellationToken.None));

            var row = Assert.Single(await Queue(factory).RecentAsync());
            Assert.Equal((AgentActionStatus.Succeeded, 0), (row.Status, row.ExitCode));
            Assert.Contains("Installed 0.3.0 (was 0.2.0)", row.Output);
            Assert.Equal("0.3.0", FakeRelease.VersionAt(installation.ConsoleBinary));
            Assert.Equal("0.2.0", FakeRelease.VersionAt(UpdateInstallation.BackupPath(installation.ConsoleBinary)));
            // The agent fetched the release itself, from its own configured address; the hub supplied a version and nothing more.
            Assert.All(site.Requests, url => Assert.StartsWith(FakeRelease.BaseUrl + "/download/v0.3.0/", url));

            // The new version starts, reaches the hub, and that confirms it.
            var guard = new UpdateGuard(new UpdateStateStore(installation.StateFile), new UpdateFiles(), TimeProvider.System, agentOptions.Updates, installation.Platform, "0.3.0");
            Assert.Equal(GuardStart.Verifying, guard.OnStart());
            await PushAsync("0.3.0");
            var confirmed = guard.Confirm();
            Assert.NotNull(confirmed);
            Assert.False(File.Exists(UpdateInstallation.BackupPath(installation.ConsoleBinary)));
            await PushAsync("0.3.0", confirmed);

            var machine = factory.Services.GetRequiredService<SnapshotStore>().Get("ota-box")!;
            Assert.Equal("0.3.0", machine.Machine.AgentVersion);
            Assert.Equal(AgentUpdateState.UpToDate, Updates(factory).For(machine).State);
            var html = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/machines"));
            Assert.Contains("updated from 0.2.0", html);
            Assert.DoesNotContain("Update available", html);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class HubVersionTests
{
    [Theory]
    [InlineData("0.1.0", "0.2.0", true)]
    [InlineData("0.2.0", "0.2.0", false)]
    [InlineData("0.3.0", "0.2.0", false)]
    [InlineData("0.2.0", "0.2.1-rc.1", true)]
    [InlineData("not-a-version", "0.2.0", false)]
    public void Says_whether_a_newer_hub_release_exists(string current, string latest, bool newer)
    {
        var release = new DevDash.Hub.Updates.LatestRelease(latest, "v" + latest, null, null, Signed: true, Prerelease: false);
        Assert.Equal(newer, DevDash.Hub.Updates.HubVersion.NewerThan(current, release) is not null);
    }

    [Fact]
    public void No_notice_before_the_first_release_check() =>
        Assert.Null(DevDash.Hub.Updates.HubVersion.NewerThan("0.1.0", null));
}
