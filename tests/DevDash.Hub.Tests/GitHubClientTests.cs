using System.Net;
using System.Text;
using System.Text.Json;
using DevDash.Core.GitHub;
using DevDash.Hub.GitHub;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.Tests;

public class GitHubClientTests
{
    private const string Repo = """
        { "nameWithOwner": "acme/widget", "url": "https://github.com/acme/widget", "description": "Widgets", "pushedAt": "2026-11-16T10:00:00Z",
          "isArchived": false, "isPrivate": true, "isFork": false, "primaryLanguage": { "name": "C#" }, "pullRequests": { "totalCount": 4 },
          "defaultBranchRef": { "name": "main", "target": { "oid": "abc123", "committedDate": "2026-11-16T09:59:00Z", "messageHeadline": "Ship it", "author": { "name": "Octo Cat", "user": { "login": "octocat" } } } } }
        """;

    private static string Pr(int number, string state, bool draft = false) => $$"""
        { "number": {{number}}, "title": "PR {{number}}", "url": "https://github.com/acme/widget/pull/{{number}}", "isDraft": {{(draft ? "true" : "false")}},
          "createdAt": "2026-11-01T00:00:00Z", "updatedAt": "2026-11-10T00:00:00Z", "reviewDecision": "REVIEW_REQUIRED", "mergeable": "MERGEABLE",
          "headRefName": "feature", "headRefOid": "abc{{number}}def", "isCrossRepository": {{(number == 4 ? "true" : "false")}}, "baseRefName": "main", "author": { "login": "me" }, "repository": { "nameWithOwner": "acme/widget" },
          "commits": { "nodes": [ { "commit": { "statusCheckRollup": {{(state == "null" ? "null" : $"{{ \"state\": \"{state}\" }}")}} } } ] } }
        """;

    private static string Search(params string[] nodes) =>
        $$"""{ "data": { "search": { "pageInfo": { "hasNextPage": false, "endCursor": null }, "nodes": [ {{string.Join(",", nodes)}} ] } } }""";

    /// <summary>Answers GraphQL and REST requests from canned JSON, and records what was asked.</summary>
    private sealed class StubHandler(Func<string, string, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        public List<(string Url, string Body, string? Auth)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
            var (status, json) = respond(request.RequestUri.ToString(), body);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FixedToken(string? token) : IGitHubTokenProvider
    {
        public int Invalidated { get; private set; }

        public Task<string?> GetTokenAsync(CancellationToken ct) => Task.FromResult(token);

        public void Invalidate() => Invalidated++;
    }

    private sealed class Monitor(GitHubOptions value) : IOptionsMonitor<GitHubOptions>
    {
        public GitHubOptions CurrentValue => value;

        public GitHubOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<GitHubOptions, string?> listener) => null;
    }

    private static (HttpStatusCode, string) Default(string url, string body)
    {
        if (url.Contains("/user/orgs"))
        {
            return (HttpStatusCode.OK, """[ { "login": "acme" }, { "login": "hidden-from-graphql" }, { "login": "skipme" } ]""");
        }

        if (body.Contains("viewer { login organizations"))
        {
            return (HttpStatusCode.OK, """{ "data": { "viewer": { "login": "me", "organizations": { "nodes": [ { "login": "acme" } ] } } } }""");
        }

        if (body.Contains("is:merged"))
        {
            return (HttpStatusCode.OK, Search("""
                { "number": 40, "title": "Squashed", "url": "https://github.com/acme/widget/pull/40", "createdAt": "2026-11-01T00:00:00Z", "updatedAt": "2026-11-12T00:00:00Z",
                  "mergedAt": "2026-11-12T00:00:00Z", "headRefName": "feature/done", "headRefOid": "abc40def", "isCrossRepository": false, "baseRefName": "main",
                  "author": { "login": "me" }, "repository": { "nameWithOwner": "acme/widget" } }
                """));
        }

        if (body.Contains("author:@me")) { return (HttpStatusCode.OK, Search(Pr(1, "FAILURE"), Pr(2, "SUCCESS", draft: true), "{}")); }
        if (body.Contains("review-requested:@me")) { return (HttpStatusCode.OK, Search(Pr(3, "PENDING"))); }
        if (body.Contains("is:pr assignee:@me")) { return (HttpStatusCode.OK, Search(Pr(1, "FAILURE"), Pr(4, "null"))); }
        if (body.Contains("is:issue assignee:@me"))
        {
            return (HttpStatusCode.OK, Search("""
                { "number": 9, "title": "Bug", "url": "https://github.com/acme/widget/issues/9", "createdAt": "2026-11-01T00:00:00Z", "updatedAt": "2026-11-02T00:00:00Z",
                  "author": { "login": "octocat" }, "repository": { "nameWithOwner": "acme/widget" }, "labels": { "nodes": [ { "name": "bug" } ] } }
                """));
        }

        if (body.Contains("mentions:@me")) { return (HttpStatusCode.OK, Search()); }
        if (body.Contains("ownerAffiliations")) { return (HttpStatusCode.OK, """{ "data": { "viewer": { "repositories": { "nodes": [] } } } }"""); }
        if (body.Contains("organization(login"))
        {
            return body.Contains("hidden-from-graphql")
                ? (HttpStatusCode.OK, """{ "data": { "organization": null }, "errors": [ { "type": "FORBIDDEN", "path": ["organization"], "message": "SSO required" } ] }""")
                : (HttpStatusCode.OK, $$"""{ "data": { "organization": { "repositories": { "nodes": [ {{Repo}}, {{Repo.Replace("acme/widget", "acme/ancient").Replace("2026-11-16T10:00:00Z", "2020-01-01T00:00:00Z")}} ] } } } }""");
        }

        if (body.Contains("r0: repository"))
        {
            return (HttpStatusCode.OK, $$"""
                { "data": { "r0": {{Repo.Replace("acme/widget", "acme/archived").Replace("\"isArchived\": false", "\"isArchived\": true")}}, "r1": null, "r2": null },
                  "errors": [ { "type": "NOT_FOUND", "path": ["r1"], "message": "Could not resolve to a Repository" }, { "type": "FORBIDDEN", "path": ["r2"], "message": "SSO" } ] }
                """);
        }

        return (HttpStatusCode.BadRequest, "{}");
    }

    private static GitHubClient Client(StubHandler handler, IGitHubTokenProvider? tokens = null, GitHubOptions? options = null) =>
        new(new HttpClient(handler), tokens ?? new FixedToken("token-123"), new Monitor(options ?? new GitHubOptions { ExcludeOrgs = ["SkipMe"] }), new FixedClock());

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public async Task Poll_collects_prs_issues_recent_repos_and_local_lookups()
    {
        var handler = new StubHandler(Default);

        var snapshot = await Client(handler).PollAsync(
            ["github.com/acme/widget", "github.com/acme/archived", "github.com/acme/deleted", "github.com/acme/sso", "github.com/bad name/x"],
            CancellationToken.None);

        Assert.Equal("me", snapshot.ViewerLogin);
        // Orgs come from GraphQL and REST together, minus the exclude list.
        Assert.Equal(["acme", "hidden-from-graphql"], snapshot.Organizations);

        Assert.Equal([1, 2, 3, 4], snapshot.PullRequests.Select(p => p.Number).Order());
        var one = snapshot.PullRequests.Single(p => p.Number == 1);
        Assert.Equal("abc1def", one.HeadSha); // the commit CI ran on, so a branch can be matched by it
        Assert.False(one.IsCrossRepository);
        Assert.True(snapshot.PullRequests.Single(p => p.Number == 4).IsCrossRepository);
        Assert.True(one is { Authored: true, Assigned: true, ReviewRequested: false, Ci: CiState.Failure, ReviewDecision: "REVIEW_REQUIRED" });
        Assert.True(snapshot.PullRequests.Single(p => p.Number == 2) is { IsDraft: true, Ci: CiState.Success });
        Assert.True(snapshot.PullRequests.Single(p => p.Number == 3) is { ReviewRequested: true, Authored: false, Ci: CiState.Pending });
        Assert.Equal(CiState.None, snapshot.PullRequests.Single(p => p.Number == 4).Ci);

        // Recently merged ones are kept apart, with the commit they were at, for squash-merged branches.
        var merged = Assert.Single(snapshot.MergedPullRequests);
        Assert.True(merged is { Number: 40, HeadSha: "abc40def", HeadRef: "feature/done", IsCrossRepository: false });
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero), merged.MergedAt);
        Assert.DoesNotContain(snapshot.PullRequests, p => p.Number == 40);
        Assert.Contains(handler.Requests, r => r.Body.Contains("is:merged") && r.Body.Contains("2026-10-18")); // the last 30 days

        var issue = Assert.Single(snapshot.Issues);
        Assert.True(issue is { Number: 9, Assigned: true, Mentioned: false });
        Assert.Equal(["bug"], issue.Labels);

        // Only repos pushed within the window count as recent.
        var recent = Assert.Single(snapshot.RecentRepos);
        Assert.Equal("acme/widget", recent.NameWithOwner);
        Assert.Equal("octocat", recent.LastPusher);
        Assert.Equal(4, recent.OpenPullRequests);
        Assert.Equal("main", recent.DefaultBranch);

        // The join: known from the recent list, archived, deleted; forbidden stays unknown.
        Assert.True(snapshot.LocalRemotes["github.com/acme/widget"].Found);
        Assert.True(snapshot.LocalRemotes["github.com/acme/archived"].Repo!.IsArchived);
        Assert.False(snapshot.LocalRemotes["github.com/acme/deleted"].Found);
        Assert.False(snapshot.LocalRemotes.ContainsKey("github.com/acme/sso"));
        Assert.Contains(snapshot.Warnings, w => w.Contains("acme/sso"));
        Assert.Contains(snapshot.Warnings, w => w.Contains("hidden-from-graphql") && w.Contains("SSO required"));

        // Read-only: every GraphQL request is a query, never a mutation, and carries the token.
        Assert.All(handler.Requests, r => Assert.Equal("Bearer token-123", r.Auth));
        Assert.All(handler.Requests.Where(r => r.Url.EndsWith("/graphql")), r => Assert.DoesNotContain("mutation", r.Body));
        Assert.DoesNotContain(handler.Requests, r => r.Body.Contains("bad name"));
    }

    [Fact]
    public async Task Follows_search_pagination()
    {
        var pages = 0;
        var handler = new StubHandler((url, body) =>
        {
            if (!body.Contains("author:@me") || body.Contains("is:merged"))
            {
                return Default(url, body);
            }

            pages++;
            var after = JsonDocument.Parse(body).RootElement.GetProperty("variables").GetProperty("after");
            return after.ValueKind == JsonValueKind.Null
                ? (HttpStatusCode.OK, $$"""{ "data": { "search": { "pageInfo": { "hasNextPage": true, "endCursor": "CURSOR1" }, "nodes": [ {{Pr(10, "SUCCESS")}} ] } } }""")
                : (HttpStatusCode.OK, Search(Pr(11, "SUCCESS")));
        });

        var snapshot = await Client(handler).PollAsync([], CancellationToken.None);

        Assert.Equal(2, pages);
        Assert.Contains(snapshot.PullRequests, p => p.Number == 10);
        Assert.Contains(snapshot.PullRequests, p => p.Number == 11);
        Assert.Contains(handler.Requests, r => r.Body.Contains("CURSOR1"));
    }

    [Fact]
    public async Task A_gateway_error_is_retried_once_and_then_becomes_a_warning_for_that_part_only()
    {
        var attempts = 0;
        var handler = new StubHandler((url, body) =>
        {
            if (body.Contains("review-requested:@me"))
            {
                attempts++;
                return (HttpStatusCode.BadGateway, "");
            }

            return Default(url, body);
        });

        var snapshot = await Client(handler).PollAsync([], CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Contains(snapshot.Warnings, w => w.Contains("review-requested") && w.Contains("502"));
        Assert.Contains(snapshot.PullRequests, p => p.Number == 1); // the other searches still worked
    }

    [Fact]
    public async Task A_rejected_token_is_invalidated_and_reported()
    {
        var tokens = new FixedToken("expired");
        var handler = new StubHandler((_, _) => (HttpStatusCode.Unauthorized, "{}"));

        await Assert.ThrowsAsync<GitHubAuthException>(() => Client(handler, tokens).PollAsync([], CancellationToken.None));

        Assert.Equal(1, tokens.Invalidated);
    }

    [Fact]
    public async Task No_token_means_no_request()
    {
        var handler = new StubHandler(Default);

        await Assert.ThrowsAsync<GitHubAuthException>(() => Client(handler, new FixedToken(null)).PollAsync([], CancellationToken.None));

        Assert.Empty(handler.Requests);
    }
}
