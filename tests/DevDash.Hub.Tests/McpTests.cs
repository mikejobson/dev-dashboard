using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DevDash.Contracts;
using DevDash.Hub.Data;
using DevDash.Hub.Mcp;
using DevDash.Hub.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DevDash.Hub.Tests;

/// <summary>The MCP server at /mcp: who gets in, what each scope sees, and that actions go through the same queue as the buttons.</summary>
public class McpTests
{
    private static readonly string[] ReadTools =
        ["list_machines", "list_projects", "get_project", "at_risk_work", "safe_to_remove", "findings", "pull_requests", "compare_clones", "recent_activity", "action_status", "t3_threads", "list_issues"];

    private static readonly string[] ActionToolNames = ["refresh_project", "fetch", "push_branch", "pull_fast_forward", "clean_build_dirs"];

    private const string Initialize = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
        """;

    /// <summary>A hub with one machine reporting (the fixture's widget repo, two unpushed commits on feature/login).</summary>
    private static async Task<HubFactory> HubAsync(bool actions = true, Action<HubFactory>? configure = null)
    {
        var factory = new HubFactory();
        configure?.Invoke(factory);
        var agent = factory.CreateAgentClient(await factory.IssueKeyAsync("mac"));
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        (await agent.PostAsync("/api/v1/snapshots", JsonContent.Create(HubFactory.Snapshot("mac", now, actions), options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();
        return factory;
    }

    private static async Task<string> TokenAsync(HubFactory factory, string scope = McpScopes.Read, string name = "test-claude") =>
        (await factory.Services.GetRequiredService<McpTokenService>().IssueAsync(name, scope, "me@example.com")).Plaintext;

    private static async Task<McpClient> ClientAsync(HubFactory factory, string token)
    {
        var http = factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
            },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private static Task<HttpResponseMessage> RawPostAsync(HubFactory factory, string? authorization)
    {
        var http = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(Initialize, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return http.SendAsync(request);
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static async Task<JsonElement> CallAsync(McpClient client, string tool, Dictionary<string, object?>? args = null)
    {
        var result = await client.CallToolAsync(tool, args ?? []);
        Assert.False(result.IsError == true, Text(result));
        return JsonDocument.Parse(Text(result)).RootElement;
    }

    /// <summary>The error text of a call that must fail, however the SDK reports it (an error result or a protocol error).</summary>
    private static async Task<string> RefusedAsync(McpClient client, string tool, Dictionary<string, object?> args)
    {
        try
        {
            var result = await client.CallToolAsync(tool, args);
            Assert.True(result.IsError == true, "Expected an error, got: " + Text(result));
            return Text(result);
        }
        catch (McpException e)
        {
            return e.Message;
        }
    }

    // ---- who gets in ------------------------------------------------------------------------------

    [Fact]
    public async Task No_token_is_401_even_though_the_browser_identity_would_be_accepted()
    {
        using var factory = await HubAsync();
        // In tests every request is the local browser user: the dashboard itself answers...
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/claude")).StatusCode);

        // ...but /mcp wants a token, and says so plainly, not with the not-found page.
        var response = await RawPostAsync(factory, null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
        Assert.DoesNotContain("<html", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_wrong_token_an_agent_key_and_a_revoked_token_are_all_401()
    {
        using var factory = await HubAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await RawPostAsync(factory, "Bearer ddm_not-a-real-token")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RawPostAsync(factory, "Bearer " + await factory.IssueKeyAsync("agent"))).StatusCode);

        var token = await TokenAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await RawPostAsync(factory, "Bearer " + token)).StatusCode);

        var tokens = factory.Services.GetRequiredService<McpTokenService>();
        var row = Assert.Single(await tokens.ListAsync());
        Assert.NotNull(row.LastUsedAt);
        Assert.Equal(ApiKeyHash(token), row.KeyHash); // only the hash is kept
        Assert.DoesNotContain(token, JsonSerializer.Serialize(row));

        await tokens.RevokeAsync(row.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RawPostAsync(factory, "Bearer " + token)).StatusCode);
    }

    [Fact]
    public async Task An_mcp_token_is_not_an_agent_key()
    {
        using var factory = await HubAsync();
        var agent = factory.CreateAgentClient(await TokenAsync(factory, McpScopes.ReadActions));
        var response = await agent.PostAsync("/api/v1/actions/claim", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Requests_past_the_per_token_limit_get_429()
    {
        using var factory = await HubAsync(configure: f => f.Settings["Hub:Mcp:RequestsPerMinute"] = "2");
        var token = await TokenAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await RawPostAsync(factory, "Bearer " + token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RawPostAsync(factory, "Bearer " + token)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await RawPostAsync(factory, "Bearer " + token)).StatusCode);

        // Per token: another token has its own allowance.
        Assert.Equal(HttpStatusCode.OK, (await RawPostAsync(factory, "Bearer " + await TokenAsync(factory, name: "other"))).StatusCode);
    }

    // ---- read scope -------------------------------------------------------------------------------

    [Fact]
    public async Task A_read_token_lists_the_read_tools_only()
    {
        using var factory = await HubAsync();
        await using var client = await ClientAsync(factory, await TokenAsync(factory));

        var tools = await client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet();
        Assert.All(ReadTools, name => Assert.Contains(name, names));
        Assert.All(ActionToolNames, name => Assert.DoesNotContain(name, names));
        // Never exposed to anyone.
        Assert.DoesNotContain(names, n => n.Contains("stash") || n.Contains("delete") || n.Contains("worktree") || n.Contains("update") || n.StartsWith("remove") || n.Contains("clone_back"));

        var getProject = tools.Single(t => t.Name == "get_project");
        Assert.False(string.IsNullOrWhiteSpace(getProject.Description));
        var schema = getProject.JsonSchema;
        Assert.Equal("project", Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());
        Assert.False(schema.GetProperty("properties").TryGetProperty("ct", out _)); // the cancellation token is not an argument
    }

    [Fact]
    public async Task Read_tools_answer_from_the_seeded_snapshot()
    {
        using var factory = await HubAsync();
        await using var client = await ClientAsync(factory, await TokenAsync(factory));

        var machines = await CallAsync(client, "list_machines");
        var mac = Assert.Single(machines.GetProperty("machines").EnumerateArray());
        Assert.Equal("mac-host", mac.GetProperty("name").GetString());
        Assert.True(mac.GetProperty("online").GetBoolean());
        Assert.True(mac.GetProperty("capabilities").GetProperty("actions").GetBoolean());

        var projects = await CallAsync(client, "list_projects", new() { ["machine"] = "mac-host" });
        var widget = projects.GetProperty("projects").EnumerateArray().Single(p => p.GetProperty("project").GetString() == "widget");
        Assert.Equal("/dev/widget", widget.GetProperty("path").GetString());
        Assert.Equal("feature/login", widget.GetProperty("branch").GetString());
        Assert.Equal(2, widget.GetProperty("ahead").GetInt32());
        Assert.Equal(3, widget.GetProperty("uncommitted").GetInt32());
        Assert.Equal("at risk", widget.GetProperty("safety").GetString());

        var dirty = await CallAsync(client, "list_projects", new() { ["only_dirty"] = true });
        Assert.Equal(1, dirty.GetProperty("total").GetInt32());
        Assert.Equal(0, (await CallAsync(client, "list_projects", new() { ["machine"] = "nowhere" })).GetProperty("total").GetInt32());

        var project = await CallAsync(client, "get_project", new() { ["project"] = "acme/widget" });
        Assert.Equal("github.com/acme/widget", project.GetProperty("key").GetString());
        var clone = Assert.Single(project.GetProperty("clones").EnumerateArray());
        Assert.Contains(clone.GetProperty("git").GetProperty("branches").EnumerateArray(), b => b.GetProperty("name").GetString() == "old-fix");
        Assert.Equal("at risk", clone.GetProperty("safety").GetProperty("verdict").GetString());
        var push = Assert.Single(project.GetProperty("sync").EnumerateArray());
        Assert.Equal("push", push.GetProperty("kind").GetString());
        Assert.Contains(project.GetProperty("findings").EnumerateArray(), f => f.GetProperty("rule").GetString() == "merged-branch");

        // The same project by the path of its clone.
        Assert.Equal("github.com/acme/widget", (await CallAsync(client, "get_project", new() { ["project"] = "/dev/widget" })).GetProperty("key").GetString());

        var atRisk = await CallAsync(client, "at_risk_work");
        var risk = atRisk.GetProperty("atRisk").EnumerateArray().Single(r => r.GetProperty("path").GetString() == "/dev/widget");
        Assert.Contains(risk.GetProperty("risks").EnumerateArray(), r => r.GetProperty("kind").GetString() == "UncommittedChanges");

        var findings = await CallAsync(client, "findings", new() { ["rule"] = "merged-branch" });
        Assert.True(findings.GetProperty("total").GetInt32() >= 1);
        Assert.Contains("git -C '/dev/widget' branch -d", findings.GetRawText());

        Assert.Equal(0, (await CallAsync(client, "safe_to_remove")).GetProperty("candidates").GetArrayLength());
        Assert.Equal(0, (await CallAsync(client, "pull_requests")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await CallAsync(client, "compare_clones")).GetProperty("total").GetInt32()); // one machine: nothing to compare
        Assert.True((await CallAsync(client, "recent_activity", new() { ["days"] = 7 })).TryGetProperty("perDay", out _));
        Assert.Equal(0, (await CallAsync(client, "t3_threads")).GetProperty("threads").GetArrayLength());
        Assert.Equal(0, (await CallAsync(client, "list_issues")).GetProperty("issues").GetArrayLength());
    }

    [Fact]
    public async Task An_unknown_project_is_an_error_that_says_what_to_do()
    {
        using var factory = await HubAsync();
        await using var client = await ClientAsync(factory, await TokenAsync(factory));
        Assert.Contains("list_projects", await RefusedAsync(client, "get_project", new() { ["project"] = "no-such-thing" }));
    }

    [Fact]
    public async Task A_read_token_cannot_call_an_action_tool()
    {
        using var factory = await HubAsync();
        await using var client = await ClientAsync(factory, await TokenAsync(factory));

        await RefusedAsync(client, "fetch", new() { ["project"] = "widget" });
        await RefusedAsync(client, "push_branch", new() { ["project"] = "widget", ["branch"] = "feature/login" });
        Assert.Empty(await factory.Services.GetRequiredService<ActionQueue>().RecentAsync());
    }

    // ---- actions scope ----------------------------------------------------------------------------

    [Fact]
    public async Task An_actions_token_queues_through_the_same_queue_and_can_follow_it()
    {
        using var factory = await HubAsync();
        await using var client = await ClientAsync(factory, await TokenAsync(factory, McpScopes.ReadActions, "laptop"));
        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        // Exactly these: nothing that removes, drops, deletes or updates is ever exposed (§19).
        Assert.Equal(ReadTools.Concat(ActionToolNames).Order(), names.Order());

        var fetched = await CallAsync(client, "fetch", new() { ["project"] = "widget" });
        var id = fetched.GetProperty("queued").GetProperty("id").GetInt32();

        var pushed = await CallAsync(client, "push_branch", new() { ["project"] = "widget", ["branch"] = "feature/login", ["machine"] = "mac-host" });
        Assert.Equal("Push", pushed.GetProperty("queued").GetProperty("kind").GetString());

        var queue = factory.Services.GetRequiredService<ActionQueue>();
        var rows = await queue.RecentAsync();
        Assert.Equal(2, rows.Count);
        var fetch = rows.Single(r => r.Id == id);
        Assert.Equal(AgentActionKind.Fetch, fetch.Kind);
        Assert.Equal(AgentActionStatus.Queued, fetch.Status);
        Assert.Equal("mcp:laptop", fetch.QueuedBy);
        Assert.Equal("/dev/widget", fetch.RepoPath);
        var push = rows.Single(r => r.Kind == AgentActionKind.Push);
        Assert.Equal(AgentActions.Key(AgentAction.Push("/dev/widget", "origin", "feature/login")), push.ActionJson);

        var status = await CallAsync(client, "action_status", new() { ["id"] = id });
        Assert.Equal("Queued", status.GetProperty("status").GetString());
        Assert.Contains("git", status.GetProperty("command").GetString());
    }

    [Fact]
    public async Task Actions_are_refused_with_the_queues_own_reasons()
    {
        using var factory = await HubAsync(actions: false);
        await using var client = await ClientAsync(factory, await TokenAsync(factory, McpScopes.ReadActions));

        // The machine's agent never opted in: the same refusal the dashboard gives.
        Assert.Contains("has not enabled agent actions", await RefusedAsync(client, "fetch", new() { ["project"] = "widget" }));
        // Nothing to fast-forward: main is not behind, and feature/login is ahead.
        Assert.Contains("not behind", await RefusedAsync(client, "pull_fast_forward", new() { ["project"] = "widget", ["branch"] = "feature/login" }));
        // A branch with no upstream is never pushed from here.
        Assert.Contains("tracks no remote branch", await RefusedAsync(client, "push_branch", new() { ["project"] = "widget", ["branch"] = "old-fix" }));
        // Build output the agent never reported.
        Assert.Contains("no build output", await RefusedAsync(client, "clean_build_dirs", new() { ["project"] = "widget" }));
        Assert.Empty(await factory.Services.GetRequiredService<ActionQueue>().RecentAsync());
    }

    // ---- the page ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_claude_page_shows_the_setup_command_for_the_public_url()
    {
        using var factory = new HubFactory();
        factory.Settings["Hub:PublicUrl"] = "https://devdash.example.ts.net/";
        var html = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/claude"));
        Assert.Contains("claude mcp add --transport http devdash https://devdash.example.ts.net/mcp --header \"Authorization: Bearer <token>\"", html);
    }

    // ---- existing databases -----------------------------------------------------------------------

    [Fact]
    public async Task A_database_from_before_mcp_gains_the_token_table_on_start()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DevDashDbContext>().UseSqlite(connection).Options;

        await using (var db = new DevDashDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Cache.Add(new CacheEntity { Key = "kept", Json = "{}" });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"McpTokens\""); // what a 0.6 hub's database looks like
        }

        await using (var db = new DevDashDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await Assert.ThrowsAsync<SqliteException>(() => db.McpTokens.CountAsync());

            var created = await SchemaUpgrade.CreateMissingTablesAsync(db);
            Assert.Contains("McpTokens", created);
            Assert.Contains(created, name => name.StartsWith("IX_McpTokens"));

            db.McpTokens.Add(new McpTokenEntity { KeyHash = "abc", Name = "t", Scope = McpScopes.Read });
            await db.SaveChangesAsync();
            Assert.Equal(1, await db.McpTokens.CountAsync());
            Assert.Equal("kept", (await db.Cache.SingleAsync()).Key);
            Assert.Empty(await SchemaUpgrade.CreateMissingTablesAsync(db));
        }
    }

    private static string ApiKeyHash(string token) => DevDash.Hub.Auth.ApiKeyService.Hash(token);
}
