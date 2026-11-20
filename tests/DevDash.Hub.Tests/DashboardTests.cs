using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevDash.Contracts;
using DevDash.Core.GitHub;
using DevDash.Hub.Data;
using DevDash.Hub.GitHub;
using DevDash.Hub.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DevDash.Hub.Tests;

public class DashboardTests
{
    private static async Task<HubFactory> SeededAsync()
    {
        var factory = new HubFactory();
        var agent = factory.CreateAgentClient(await factory.IssueKeyAsync());
        var response = await agent.PostAsync("/api/v1/snapshots", JsonContent.Create(HubFactory.Snapshot(), options: SnapshotSchema.JsonOptions));
        response.EnsureSuccessStatusCode();

        var now = DateTimeOffset.UtcNow;
        var repo = new GitHubRepo
        {
            NameWithOwner = "acme/widget", Url = "https://github.com/acme/widget", Description = "Widgets, from GitHub.",
            PushedAt = now.AddHours(-3), DefaultBranch = "main", HeadSha = "9999999", HeadMessage = "Ship it", LastPusher = "octocat", OpenPullRequests = 1,
        };
        var uncloned = new GitHubRepo { NameWithOwner = "acme/gadget", Url = "https://github.com/acme/gadget", PushedAt = now.AddHours(-1) };
        await factory.Services.GetRequiredService<GitHubStore>().SetAsync(new GitHubSnapshot
        {
            ViewerLogin = "me",
            FetchedAt = now,
            Organizations = ["acme"],
            RecentRepos = [uncloned, repo],
            LocalRemotes = new Dictionary<string, RemoteRepoLookup> { [repo.Key] = new() { Key = repo.Key, Found = true, Repo = repo, CheckedAt = now } },
            PullRequests =
            [
                new PullRequestInfo { Repo = "acme/widget", Number = 7, Title = "Add login form", Url = "https://github.com/acme/widget/pull/7", Authored = true, Ci = CiState.Failure, UpdatedAt = now.AddDays(-2), CreatedAt = now.AddDays(-9) },
                new PullRequestInfo { Repo = "acme/gadget", Number = 3, Title = "Please review gadget", Url = "https://github.com/acme/gadget/pull/3", ReviewRequested = true, Author = "octocat", Ci = CiState.Success, UpdatedAt = now.AddDays(-1), CreatedAt = now.AddDays(-2) },
            ],
            Issues = [new IssueInfo { Repo = "acme/widget", Number = 12, Title = "Login is slow", Url = "https://github.com/acme/widget/issues/12", Assigned = true, UpdatedAt = now.AddDays(-3) }],
        });
        return factory;
    }

    [Theory]
    [InlineData("/", "Overview", "mac-host", "Please review gadget")]
    [InlineData("/local", "Local", "widget", "Widgets, from GitHub.", "feature/login", "3 changed", "not a git repo", "Reference Docs")]
    [InlineData("/github", "Needs action", "Needs my review", "Please review gadget", "Mine, CI failing", "Add login form", "CI failing")]
    [InlineData("/github?tab=all", "Needs my review", "Please review gadget", "Add login form", "My issues", "Login is slow", "mac-host")]
    [InlineData("/github?tab=activity", "Recent activity in your orgs", "acme/gadget", "not cloned anywhere", "mac-host")]
    [InlineData("/cleanup?open=all", "Not a git repo: Reference Docs", "widget: uncommitted changes for 40 days", "acme/widget#7: CI failing", "data-copy=", "Snooze 7d",
        "List the folder", "ls -la '/dev/Reference Docs'", "Move it to Documents", "code '/dev/widget'", "Copy the fixes as a script for", "# DevDash cleanup: Non-git folders in Developer on mac-host, generated ")]
    [InlineData("/cleanup", "Non-git folders in Developer", "Expand all")]
    [InlineData("/safety", "Safety", "At risk", "2 commits on feature/login not on any remote", "3 uncommitted changes, last edited 40 days ago", "Safe to remove", "Nothing is known to be safe to remove.", "Update on the Machines page")]
    [InlineData("/machines", "mac-host", "online", "Agent API keys", "first-run", "Agent actions", "No actions have been queued.", "2 entries in 123 ms", "reinstall needed")]
    [InlineData("/repo?key=github.com%2Facme%2Fwidget", "Widgets, from GitHub.", "/dev/widget", "Open pull requests", "Add login form", "Open issues", "Login is slow", "old-fix", "merged", "Open on GitHub",
        "data-copy=\"cd '/dev/widget'\"", "data-copy=\"code '/dev/widget'\"")]
    public async Task Pages_render_the_state_on_the_server(string url, params string[] expected)
    {
        using var factory = await SeededAsync();

        var response = await factory.CreateClient().GetAsync(url);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(expected, text => Assert.Contains(text, html));
    }

    [Fact]
    public async Task Unknown_repo_and_unknown_pages_are_handled()
    {
        using var factory = await SeededAsync();
        var client = factory.CreateClient();

        var repo = await client.GetAsync("/repo?key=github.com%2Fnope%2Fnope");
        Assert.Equal(HttpStatusCode.OK, repo.StatusCode);
        Assert.Contains("Repo not found", await repo.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/no-such-page")).StatusCode);
    }

    [Fact]
    public async Task Repos_api_joins_local_clones_to_github()
    {
        using var factory = await SeededAsync();

        var repos = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/repos");

        var widget = repos.EnumerateArray().Single(r => r.GetProperty("key").GetString() == "github.com/acme/widget");
        Assert.Equal("widget", widget.GetProperty("name").GetString());
        Assert.Equal("acme", widget.GetProperty("owner").GetString());
        Assert.Equal("Widgets, from GitHub.", widget.GetProperty("description").GetString());
        Assert.Equal("GitHub", widget.GetProperty("descriptionSource").GetString());
        Assert.Equal(7, widget.GetProperty("pullRequests")[0].GetProperty("number").GetInt32());
        Assert.Equal("mac", widget.GetProperty("clones")[0].GetProperty("machineId").GetString());
        Assert.Equal(2, widget.GetProperty("clones")[0].GetProperty("ahead").GetInt32());

        var folder = repos.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Reference Docs");
        Assert.Equal("NonGitFolder", folder.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Description_falls_back_from_github_to_readme_to_manifest()
    {
        var provider = new DefaultDescriptionProvider();
        var entry = HubFactory.Snapshot().Entries[0];
        var manifestOnly = entry with { Description = new DescriptionInputs { Manifests = [new ManifestInfo { File = "package.json", Description = "From the manifest." }] } };
        var github = new GitHubRepo { NameWithOwner = "acme/widget", Description = "From GitHub." };

        Assert.Equal(new RepoDescription("From GitHub.", "GitHub"), await provider.DescribeAsync(new("k", github, [entry])));
        Assert.Equal(new RepoDescription("A widget from the README.", "README"), await provider.DescribeAsync(new("k", github with { Description = null }, [entry])));
        Assert.Equal(new RepoDescription("From the manifest.", "package.json"), await provider.DescribeAsync(new("k", null, [manifestOnly])));
        Assert.Null(await provider.DescribeAsync(new("k", null, [entry with { Description = null }])));
    }

    [Fact]
    public async Task Findings_can_be_snoozed_dismissed_and_restored_and_the_decision_persists_by_fingerprint()
    {
        using var factory = await SeededAsync();
        var client = factory.CreateClient();

        async Task<List<JsonElement>> ActiveAsync() =>
            (await client.GetFromJsonAsync<JsonElement>("/api/v1/findings")).EnumerateArray().ToList();

        var before = await ActiveAsync();
        var folder = before.Single(f => f.GetProperty("ruleId").GetString() == "non-git-folder");
        var dirty = before.Single(f => f.GetProperty("ruleId").GetString() == "old-uncommitted");
        var commands = folder.GetProperty("commands").EnumerateArray().ToList();
        Assert.Equal(["Inspect", "Fix", "Open", "Open"], commands.Select(c => c.GetProperty("kind").GetString()));
        Assert.Equal("mv '/dev/Reference Docs' ~/Documents/", commands[1].GetProperty("command").GetString());
        Assert.Equal("cd '/dev/Reference Docs'", commands[2].GetProperty("command").GetString());
        Assert.False(folder.TryGetProperty("suggestedCommand", out _));
        Assert.Contains(before, f => f.GetProperty("ruleId").GetString() == "merged-branch");
        Assert.Contains(before, f => f.GetProperty("ruleId").GetString() == "unpushed");
        Assert.Contains(before, f => f.GetProperty("ruleId").GetString() == "failing-ci-pr");

        var folderId = folder.GetProperty("fingerprint").GetString();
        var dirtyId = dirty.GetProperty("fingerprint").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/findings/{folderId}/dismiss", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/findings/{dirtyId}/snooze?days=3", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/findings/nope/dismiss", null)).StatusCode);

        var after = await ActiveAsync();
        Assert.Equal(before.Count - 2, after.Count);
        Assert.DoesNotContain(after, f => f.GetProperty("fingerprint").GetString() == folderId);

        // A new snapshot of the same state produces the same fingerprints, so the decisions stick.
        var agent = factory.CreateAgentClient(await factory.IssueKeyAsync("second", "mac"));
        (await agent.PostAsync("/api/v1/snapshots", JsonContent.Create(HubFactory.Snapshot(), options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();
        Assert.Equal(after.Count, (await ActiveAsync()).Count);

        var all = (await client.GetFromJsonAsync<JsonElement>("/api/v1/findings?all=true")).EnumerateArray().ToList();
        Assert.Equal("Dismissed", all.Single(f => f.GetProperty("fingerprint").GetString() == folderId).GetProperty("status").GetString());
        Assert.Equal("Snoozed", all.Single(f => f.GetProperty("fingerprint").GetString() == dirtyId).GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/findings/{folderId}/restore", null)).StatusCode);
        Assert.Equal(before.Count - 1, (await ActiveAsync()).Count);
    }

    [Fact]
    public async Task An_expired_snooze_makes_the_finding_active_again()
    {
        using var factory = await SeededAsync();
        var findings = factory.Services.GetRequiredService<FindingsService>();
        var target = (await findings.GetAsync()).First(v => v.Finding.RuleId == "non-git-folder").Finding;

        await findings.SnoozeAsync(target, TimeSpan.FromDays(7));
        Assert.Equal(FindingStatus.Snoozed, (await findings.GetAsync()).Single(v => v.Finding.Fingerprint == target.Fingerprint).Status);

        await findings.SnoozeAsync(target, TimeSpan.FromSeconds(-1));
        Assert.True((await findings.GetAsync()).Single(v => v.Finding.Fingerprint == target.Fingerprint).IsActive);
    }

    [Fact]
    public async Task Github_state_survives_a_restart()
    {
        using var factory = await SeededAsync();

        var restored = ActivatorUtilities.CreateInstance<GitHubStore>(factory.Services);
        await restored.LoadAsync();

        Assert.Equal("me", restored.Current.ViewerLogin);
        Assert.Equal(2, restored.Current.PullRequests.Count);
        Assert.True(restored.Current.LocalRemotes["github.com/acme/widget"].Found);
    }

    [Fact]
    public async Task Forgetting_a_machine_removes_its_data()
    {
        using var factory = await SeededAsync();
        var store = factory.Services.GetRequiredService<SnapshotStore>();

        await store.ForgetAsync("mac");

        Assert.Empty(store.Machines);
        Assert.Equal("[]", await factory.CreateClient().GetStringAsync("/api/v1/machines"));
    }
}
