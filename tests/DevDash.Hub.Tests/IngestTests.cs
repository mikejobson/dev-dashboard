using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevDash.Contracts;
using DevDash.Hub.Auth;
using DevDash.Hub.Data;
using DevDash.Hub.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DevDash.Hub.Tests;

public class IngestTests
{
    private const string Endpoint = "/api/v1/snapshots";

    private static JsonContent Body(MachineSnapshot snapshot) => JsonContent.Create(snapshot, options: SnapshotSchema.JsonOptions);

    [Fact]
    public async Task Rejects_requests_without_a_valid_key()
    {
        using var factory = new HubFactory();

        var none = await factory.CreateClient().PostAsync(Endpoint, Body(HubFactory.Snapshot()));
        var wrong = await factory.CreateAgentClient("ddk_not-a-real-key").PostAsync(Endpoint, Body(HubFactory.Snapshot()));

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<SnapshotStore>().Machines);
    }

    [Fact]
    public async Task Accepts_a_snapshot_stores_it_and_notifies_the_dashboard()
    {
        using var factory = new HubFactory();
        var client = factory.CreateAgentClient(await factory.IssueKeyAsync());
        var changes = new List<DashboardChange>();
        factory.Services.GetRequiredService<DashboardNotifier>().Changed += changes.Add;

        var response = await client.PostAsync(Endpoint, Body(HubFactory.Snapshot()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<IngestResponse>(SnapshotSchema.JsonOptions);
        Assert.Equal("mac", result!.MachineId);
        Assert.Equal(2, result.Entries);
        Assert.Contains(DashboardChange.Snapshot, changes);

        var store = factory.Services.GetRequiredService<SnapshotStore>();
        var machine = Assert.Single(store.Machines);
        Assert.True(store.IsOnline(machine));
        Assert.Equal("feature/login", machine.Snapshot.Entries[0].Git!.Branch);

        // It is in SQLite too, so a restart (a fresh store) sees the same thing.
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<DevDashDbContext>>().CreateDbContextAsync();
        var row = await db.Machines.SingleAsync();
        Assert.Equal(2, row.EntryCount);
        var restored = ActivatorUtilities.CreateInstance<SnapshotStore>(factory.Services);
        await restored.LoadAsync();
        Assert.Equal("widget", Assert.Single(restored.Machines).Snapshot.Entries[0].Name);
    }

    [Fact]
    public async Task A_second_snapshot_replaces_the_first()
    {
        using var factory = new HubFactory();
        var client = factory.CreateAgentClient(await factory.IssueKeyAsync());

        await client.PostAsync(Endpoint, Body(HubFactory.Snapshot()));
        var second = HubFactory.Snapshot() with { Entries = [] };
        await client.PostAsync(Endpoint, Body(second));

        var machine = Assert.Single(factory.Services.GetRequiredService<SnapshotStore>().Machines);
        Assert.Empty(machine.Snapshot.Entries);
    }

    [Fact]
    public async Task A_key_binds_to_the_first_machine_that_uses_it()
    {
        using var factory = new HubFactory();
        var client = factory.CreateAgentClient(await factory.IssueKeyAsync());

        var first = await client.PostAsync(Endpoint, Body(HubFactory.Snapshot("mac")));
        var again = await client.PostAsync(Endpoint, Body(HubFactory.Snapshot("mac")));
        var other = await client.PostAsync(Endpoint, Body(HubFactory.Snapshot("someone-else")));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Single(factory.Services.GetRequiredService<SnapshotStore>().Machines);

        var key = (await factory.Services.GetRequiredService<ApiKeyService>().ListAsync()).Single(k => k.Label == "test");
        Assert.Equal("mac", key.MachineId);
        Assert.NotNull(key.LastUsedAt);
    }

    [Fact]
    public async Task A_new_key_cannot_take_over_a_machine_another_live_key_owns()
    {
        using var factory = new HubFactory();
        var keys = factory.Services.GetRequiredService<ApiKeyService>();
        var owner = await keys.IssueAsync("owner");
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateAgentClient(owner.Plaintext).PostAsync(Endpoint, Body(HubFactory.Snapshot("mac")))).StatusCode);

        // A fresh key (or the first-run key from the container log) naming the same machine, in any case.
        var intruder = await keys.IssueAsync("intruder");
        var taken = await factory.CreateAgentClient(intruder.Plaintext).PostAsync(Endpoint, Body(HubFactory.Snapshot("MAC")));
        Assert.Equal(HttpStatusCode.Forbidden, taken.StatusCode);
        Assert.Contains("Revoke that key", await taken.Content.ReadAsStringAsync());
        Assert.Null((await keys.ListAsync()).Single(k => k.Label == "intruder").MachineId);

        // Once the old key is revoked, a new key can take the machine over: that is how a key is replaced.
        await keys.RevokeAsync(owner.Entity.Id);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateAgentClient(intruder.Plaintext).PostAsync(Endpoint, Body(HubFactory.Snapshot("mac")))).StatusCode);
    }

    [Fact]
    public async Task A_revoked_key_stops_working()
    {
        using var factory = new HubFactory();
        var plaintext = await factory.IssueKeyAsync();
        var keys = factory.Services.GetRequiredService<ApiKeyService>();
        var client = factory.CreateAgentClient(plaintext);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Endpoint, Body(HubFactory.Snapshot()))).StatusCode);

        await keys.RevokeAsync((await keys.ListAsync()).Single(k => k.Label == "test").Id);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(Endpoint, Body(HubFactory.Snapshot()))).StatusCode);
    }

    [Fact]
    public async Task Rejects_bad_snapshots()
    {
        using var factory = new HubFactory();
        var client = factory.CreateAgentClient(await factory.IssueKeyAsync());

        var future = await client.PostAsync(Endpoint, Body(HubFactory.Snapshot() with { SchemaVersion = SnapshotSchema.Version + 1 }));
        var noMachine = await client.PostAsync(Endpoint, JsonContent.Create(new { schemaVersion = 1, machine = new { machineId = "", hostname = "x" } }));

        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
        Assert.Contains("schema", await future.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, noMachine.StatusCode);
    }

    [Fact]
    public async Task Keys_are_stored_hashed_and_a_first_run_key_is_seeded()
    {
        using var factory = new HubFactory();
        var plaintext = await factory.IssueKeyAsync();

        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<DevDashDbContext>>().CreateDbContextAsync();
        var rows = await db.ApiKeys.ToListAsync();

        Assert.Contains(rows, k => k.Label == "first-run");
        var row = rows.Single(k => k.Label == "test");
        Assert.StartsWith(ApiKeyService.KeyPrefix, plaintext);
        Assert.Equal(ApiKeyService.Hash(plaintext), row.KeyHash);
        Assert.DoesNotContain(plaintext, JsonSerializer.Serialize(rows));
    }

    [Fact]
    public async Task An_agent_key_does_not_open_the_browser_api_and_browser_auth_does_not_open_ingest()
    {
        using var factory = new HubFactory { RequireLoopback = true };
        var agent = factory.CreateAgentClient(await factory.IssueKeyAsync());

        // TestServer requests have no remote address, so the local-dev identity provider refuses them.
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.GetAsync("/api/v1/machines")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsync(Endpoint, Body(HubFactory.Snapshot()))).StatusCode);
    }
}
