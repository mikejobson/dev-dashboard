using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevDash.Core.GitHub;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.GitHub;

public sealed class GitHubAuthException(string message) : Exception(message);

/// <summary>Read-only GitHub GraphQL queries. Nothing here mutates anything on GitHub.</summary>
public sealed partial class GitHubClient(HttpClient http, IGitHubTokenProvider tokens, IOptionsMonitor<GitHubOptions> options, TimeProvider time)
{
    private const int SearchPageSize = 20;
    private const int SearchMaxResults = 100;
    private const int LookupBatchSize = 25;

    private const string RepoFields = """
        fragment RepoFields on Repository {
          nameWithOwner url description pushedAt isArchived isPrivate isFork
          primaryLanguage { name }
          pullRequests(states: OPEN) { totalCount }
          defaultBranchRef { name target { ... on Commit { oid committedDate messageHeadline author { name user { login } } } } }
        }
        """;

    private const string PullRequestFields = """
        fragment PrFields on PullRequest {
          number title url isDraft createdAt updatedAt reviewDecision mergeable headRefName headRefOid isCrossRepository baseRefName
          author { login }
          repository { nameWithOwner }
          commits(last: 1) { nodes { commit { statusCheckRollup { state contexts(first: 25) { nodes { __typename ... on CheckRun { name conclusion detailsUrl } ... on StatusContext { context state targetUrl } } } } } } }
        }
        """;

    private const string IssueFields = """
        fragment IssueFields on Issue {
          number title url createdAt updatedAt
          author { login }
          repository { nameWithOwner }
          labels(first: 10) { nodes { name } }
        }
        """;

    /// <summary>A merged pull request needs no CI or review state: what it was, and the commit it was at.</summary>
    private const string MergedPullRequestFields = """
        fragment MergedPrFields on PullRequest {
          number title url createdAt updatedAt mergedAt headRefName headRefOid isCrossRepository baseRefName
          author { login }
          repository { nameWithOwner }
        }
        """;

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$")]
    private static partial Regex SafeName();

    /// <summary>Runs one full poll. <paramref name="localRemoteKeys"/> are the <c>github.com/owner/repo</c> keys agents reported.</summary>
    public async Task<GitHubSnapshot> PollAsync(IReadOnlyCollection<string> localRemoteKeys, CancellationToken ct)
    {
        var settings = options.CurrentValue;
        var warnings = new List<string>();
        var now = time.GetUtcNow();

        // Who am I, and which orgs am I in?
        var viewer = await QueryAsync("query { viewer { login organizations(first: 100) { nodes { login } } } }", null, warnings, ct);
        var viewerNode = viewer.GetProperty("viewer");
        var login = viewerNode.GetProperty("login").GetString()!;
        // GraphQL only lists some memberships for OAuth tokens, so ask REST as well and merge.
        var orgNames = Nodes(viewerNode, "organizations").Select(n => n.GetProperty("login").GetString()!).ToList();
        try
        {
            orgNames.AddRange(await ListOrgsViaRestAsync(ct));
        }
        catch (Exception ex) when (ex is GitHubQueryException or HttpRequestException or JsonException)
        {
            warnings.Add($"Organisation list (REST): {ex.Message}");
        }

        var orgs = orgNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(o => !settings.ExcludeOrgs.Contains(o, StringComparer.OrdinalIgnoreCase))
            .OrderBy(o => o, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Pull requests: authored, review requested, assigned. Each search is its own paged request:
        // CI rollups are expensive, and GitHub answers 502 when one query asks for too many at once.
        var prs = new Dictionary<string, PullRequestInfo>(StringComparer.OrdinalIgnoreCase);
        var prSearches = new (string Query, int Relation)[]
        {
            ("is:open is:pr author:@me archived:false sort:updated-desc", 0),
            ("is:open is:pr review-requested:@me archived:false sort:updated-desc", 1),
            ("is:open is:pr assignee:@me archived:false sort:updated-desc", 2),
        };
        foreach (var (search, relation) in prSearches)
        {
            try
            {
                await foreach (var node in SearchAsync(search, "PrFields", PullRequestFields, warnings, ct))
                {
                    if (ParsePullRequest(node) is not { } pr)
                    {
                        continue;
                    }

                    var existing = prs.GetValueOrDefault(pr.Url) ?? pr;
                    prs[pr.Url] = existing with
                    {
                        Authored = existing.Authored || relation == 0,
                        ReviewRequested = existing.ReviewRequested || relation == 1,
                        Assigned = existing.Assigned || relation == 2,
                    };
                }
            }
            catch (GitHubQueryException ex)
            {
                warnings.Add($"Pull request search \"{search}\": {ex.Message}");
            }
        }

        // My recently merged pull requests: what a squash merge leaves behind locally can be matched by commit.
        var merged = new Dictionary<string, PullRequestInfo>(StringComparer.OrdinalIgnoreCase);
        var mergedSince = now.AddDays(-settings.MergedDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var mergedSearch = $"is:pr is:merged author:@me merged:>={mergedSince} archived:false sort:updated-desc";
        try
        {
            await foreach (var node in SearchAsync(mergedSearch, "MergedPrFields", MergedPullRequestFields, warnings, ct))
            {
                if (ParsePullRequest(node) is { } pr)
                {
                    merged[pr.Url] = pr with { Authored = true, MergedAt = Date(node, "mergedAt") };
                }
            }
        }
        catch (GitHubQueryException ex)
        {
            warnings.Add($"Merged pull request search: {ex.Message}");
        }

        // Issues: assigned to me, and recent mentions.
        var since = now.AddDays(-settings.MentionDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var issues = new Dictionary<string, IssueInfo>(StringComparer.OrdinalIgnoreCase);
        var issueSearches = new (string Query, bool Mentioned)[]
        {
            ("is:open is:issue assignee:@me archived:false sort:updated-desc", false),
            ($"is:open is:issue mentions:@me updated:>={since} archived:false sort:updated-desc", true),
        };
        foreach (var (search, mentioned) in issueSearches)
        {
            try
            {
                await foreach (var node in SearchAsync(search, "IssueFields", IssueFields, warnings, ct))
                {
                    if (ParseIssue(node) is not { } issue)
                    {
                        continue;
                    }

                    var existing = issues.GetValueOrDefault(issue.Url) ?? issue;
                    issues[issue.Url] = existing with
                    {
                        Assigned = existing.Assigned || !mentioned,
                        Mentioned = existing.Mentioned || mentioned,
                    };
                }
            }
            catch (GitHubQueryException ex)
            {
                warnings.Add($"Issue search \"{search}\": {ex.Message}");
            }
        }

        // Recently pushed repos: mine, then each org. One request per org so a failure stays contained.
        var cutoff = now.AddDays(-settings.RecentDays);
        var repos = new Dictionary<string, GitHubRepo>(StringComparer.OrdinalIgnoreCase);
        void AddRepos(IEnumerable<JsonElement> nodes, bool recentOnly)
        {
            foreach (var node in nodes)
            {
                if (ParseRepo(node) is { } repo && (!recentOnly || repo.PushedAt >= cutoff))
                {
                    repos[repo.Key] = repo;
                }
            }
        }

        try
        {
            var mine = await QueryAsync(
                """
                query($n: Int!) { viewer { repositories(first: $n, ownerAffiliations: OWNER, orderBy: {field: PUSHED_AT, direction: DESC}) { nodes { ...RepoFields } } } }
                """ + RepoFields,
                new { n = settings.ReposPerOrg }, warnings, ct);
            AddRepos(Nodes(mine.GetProperty("viewer"), "repositories"), recentOnly: true);
        }
        catch (GitHubQueryException ex)
        {
            warnings.Add($"Your repositories: {ex.Message}");
        }

        foreach (var org in orgs)
        {
            try
            {
                var data = await QueryAsync(
                    """
                    query($login: String!, $n: Int!) { organization(login: $login) { repositories(first: $n, orderBy: {field: PUSHED_AT, direction: DESC}) { nodes { ...RepoFields } } } }
                    """ + RepoFields,
                    new { login = org, n = settings.ReposPerOrg }, warnings, ct, warningPrefix: org);
                if (data.TryGetProperty("organization", out var orgNode) && orgNode.ValueKind == JsonValueKind.Object)
                {
                    AddRepos(Nodes(orgNode, "repositories"), recentOnly: true);
                }
            }
            catch (GitHubQueryException ex)
            {
                warnings.Add($"{org}: {ex.Message}");
            }
        }

        var recent = repos.Values.OrderByDescending(r => r.PushedAt).ToList();

        // Join to local clones: look up every reported remote we have not already seen.
        var lookups = new Dictionary<string, RemoteRepoLookup>(StringComparer.OrdinalIgnoreCase);
        var toQuery = new List<(string Key, string Owner, string Name)>();
        foreach (var key in localRemoteKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (repos.TryGetValue(key, out var known))
            {
                lookups[key] = new RemoteRepoLookup { Key = key, Found = true, Repo = known, CheckedAt = now };
            }
            else if (GitHubKeys.TrySplit(key, out var owner, out var name) && SafeName().IsMatch(owner) && SafeName().IsMatch(name))
            {
                toQuery.Add((key, owner, name));
            }
        }

        foreach (var batch in toQuery.Chunk(LookupBatchSize))
        {
            var query = new StringBuilder("query {\n");
            for (var i = 0; i < batch.Length; i++)
            {
                // Owner and name are validated against SafeName, so they are safe to inline.
                query.Append(CultureInfo.InvariantCulture, $"  r{i}: repository(owner: \"{batch[i].Owner}\", name: \"{batch[i].Name}\") {{ ...RepoFields }}\n");
            }

            query.Append("}\n").Append(RepoFields);

            try
            {
                var result = await SendAsync(query.ToString(), null, ct);
                var failed = result.Errors
                    .Where(e => e.Path.Count > 0)
                    .ToDictionary(e => e.Path[0], e => e.Type, StringComparer.Ordinal);

                for (var i = 0; i < batch.Length; i++)
                {
                    var alias = $"r{i}";
                    if (result.Data.ValueKind == JsonValueKind.Object
                        && result.Data.TryGetProperty(alias, out var node) && node.ValueKind == JsonValueKind.Object
                        && ParseRepo(node) is { } repo)
                    {
                        lookups[batch[i].Key] = new RemoteRepoLookup { Key = batch[i].Key, Found = true, Repo = repo, CheckedAt = now };
                    }
                    else if (failed.TryGetValue(alias, out var type) && type == "NOT_FOUND")
                    {
                        lookups[batch[i].Key] = new RemoteRepoLookup { Key = batch[i].Key, Found = false, CheckedAt = now };
                    }
                    else
                    {
                        // Forbidden (e.g. SSO not authorised for this token): unknown, so say nothing.
                        warnings.Add($"{batch[i].Owner}/{batch[i].Name}: could not be checked ({failed.GetValueOrDefault(alias) ?? "no data"}).");
                    }
                }
            }
            catch (GitHubQueryException ex)
            {
                warnings.Add($"Repository lookup: {ex.Message}");
            }
        }

        return new GitHubSnapshot
        {
            ViewerLogin = login,
            FetchedAt = now,
            Organizations = orgs,
            PullRequests = prs.Values.OrderByDescending(p => p.UpdatedAt).ToList(),
            MergedPullRequests = merged.Values.OrderByDescending(p => p.MergedAt).ToList(),
            Issues = issues.Values.OrderByDescending(i => i.UpdatedAt).ToList(),
            RecentRepos = recent,
            LocalRemotes = lookups,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    // ---- parsing -------------------------------------------------------------------------------

    internal static GitHubRepo? ParseRepo(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object || Str(node, "nameWithOwner") is not { } nameWithOwner)
        {
            return null;
        }

        string? branch = null, sha = null, message = null, pusher = null;
        DateTimeOffset? committed = null;
        if (node.TryGetProperty("defaultBranchRef", out var def) && def.ValueKind == JsonValueKind.Object)
        {
            branch = Str(def, "name");
            if (def.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object)
            {
                sha = Str(target, "oid");
                message = Str(target, "messageHeadline");
                committed = Date(target, "committedDate");
                if (target.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object)
                {
                    pusher = author.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                        ? Str(user, "login")
                        : null;
                    pusher ??= Str(author, "name");
                }
            }
        }

        return new GitHubRepo
        {
            NameWithOwner = nameWithOwner,
            Url = Str(node, "url") ?? "",
            Description = string.IsNullOrWhiteSpace(Str(node, "description")) ? null : Str(node, "description"),
            PushedAt = Date(node, "pushedAt"),
            IsArchived = Bool(node, "isArchived"),
            IsPrivate = Bool(node, "isPrivate"),
            IsFork = Bool(node, "isFork"),
            DefaultBranch = branch,
            HeadSha = sha,
            HeadCommittedAt = committed,
            HeadMessage = message,
            LastPusher = pusher,
            OpenPullRequests = node.TryGetProperty("pullRequests", out var prs) && prs.ValueKind == JsonValueKind.Object
                && prs.TryGetProperty("totalCount", out var count) ? count.GetInt32() : 0,
            PrimaryLanguage = node.TryGetProperty("primaryLanguage", out var lang) && lang.ValueKind == JsonValueKind.Object ? Str(lang, "name") : null,
        };
    }

    internal static PullRequestInfo? ParsePullRequest(JsonElement node)
    {
        // Search results can contain empty objects for node types the fragment does not match.
        if (node.ValueKind != JsonValueKind.Object || Str(node, "url") is not { } url || !node.TryGetProperty("repository", out var repo))
        {
            return null;
        }

        var ci = CiState.None;
        string? failedUrl = null, failedName = null;
        var commit = Nodes(node, "commits").FirstOrDefault();
        if (commit.ValueKind == JsonValueKind.Object
            && commit.TryGetProperty("commit", out var c) && c.ValueKind == JsonValueKind.Object
            && c.TryGetProperty("statusCheckRollup", out var rollup) && rollup.ValueKind == JsonValueKind.Object)
        {
            ci = Str(rollup, "state") switch
            {
                "SUCCESS" => CiState.Success,
                "FAILURE" or "ERROR" => CiState.Failure,
                "PENDING" or "EXPECTED" => CiState.Pending,
                _ => CiState.None,
            };

            // The first failing check, for "Fix in T3": an Actions run's page, or a commit status's target.
            if (ci == CiState.Failure)
            {
                foreach (var context in Nodes(rollup, "contexts"))
                {
                    var failed = Str(context, "conclusion") is "FAILURE" or "TIMED_OUT" or "STARTUP_FAILURE" or "ACTION_REQUIRED"
                        || Str(context, "state") is "FAILURE" or "ERROR";
                    var checkUrl = Str(context, "detailsUrl") ?? Str(context, "targetUrl");
                    if (failed && Uri.TryCreate(checkUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                    {
                        failedUrl = uri.ToString();
                        failedName = Str(context, "name") ?? Str(context, "context");
                        break;
                    }
                }
            }
        }

        return new PullRequestInfo
        {
            Repo = Str(repo, "nameWithOwner") ?? "",
            Number = node.GetProperty("number").GetInt32(),
            Title = Str(node, "title") ?? "",
            Url = url,
            Author = node.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object ? Str(author, "login") : null,
            IsDraft = Bool(node, "isDraft"),
            CreatedAt = Date(node, "createdAt") ?? default,
            UpdatedAt = Date(node, "updatedAt") ?? default,
            ReviewDecision = Str(node, "reviewDecision"),
            Mergeable = Str(node, "mergeable"),
            Ci = ci,
            FailedCheckUrl = failedUrl,
            FailedCheckName = failedName,
            HeadRef = Str(node, "headRefName"),
            HeadSha = Str(node, "headRefOid"),
            IsCrossRepository = Bool(node, "isCrossRepository"),
            BaseRef = Str(node, "baseRefName"),
        };
    }

    internal static IssueInfo? ParseIssue(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object || Str(node, "url") is not { } url || !node.TryGetProperty("repository", out var repo))
        {
            return null;
        }

        return new IssueInfo
        {
            Repo = Str(repo, "nameWithOwner") ?? "",
            Number = node.GetProperty("number").GetInt32(),
            Title = Str(node, "title") ?? "",
            Url = url,
            Author = node.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object ? Str(author, "login") : null,
            CreatedAt = Date(node, "createdAt") ?? default,
            UpdatedAt = Date(node, "updatedAt") ?? default,
            Labels = Nodes(node, "labels").Select(l => Str(l, "name") ?? "").Where(l => l.Length > 0).ToList(),
        };
    }

    private static IEnumerable<JsonElement> Nodes(JsonElement parent, string property)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(property, out var connection) && connection.ValueKind == JsonValueKind.Object
            && connection.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind == JsonValueKind.Object)
                {
                    yield return node;
                }
            }
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    // ---- transport -----------------------------------------------------------------------------

    private sealed record GraphQlError(string Type, string Message, IReadOnlyList<string> Path);

    private sealed record GraphQlResult(JsonElement Data, IReadOnlyList<GraphQlError> Errors);

    /// <summary>Pages through a search, <see cref="SearchPageSize"/> at a time, up to <see cref="SearchMaxResults"/>.</summary>
    private async IAsyncEnumerable<JsonElement> SearchAsync(
        string search, string fragmentName, string fragment, List<string> warnings,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var query = $$"""
            query($q: String!, $n: Int!, $after: String) {
              search(query: $q, type: ISSUE, first: $n, after: $after) {
                pageInfo { hasNextPage endCursor }
                nodes { ...{{fragmentName}} }
              }
            }
            """ + fragment;

        string? after = null;
        for (var fetched = 0; fetched < SearchMaxResults; fetched += SearchPageSize)
        {
            var data = await QueryAsync(query, new { q = search, n = SearchPageSize, after }, warnings, ct);
            foreach (var node in Nodes(data, "search"))
            {
                yield return node;
            }

            var page = data.GetProperty("search").GetProperty("pageInfo");
            if (!Bool(page, "hasNextPage") || Str(page, "endCursor") is not { } cursor)
            {
                yield break;
            }

            after = cursor;
        }

        warnings.Add($"Search \"{search}\" has more than {SearchMaxResults} results; only the most recently updated are shown.");
    }

    /// <summary>Runs a query that must return data. Partial errors become warnings.</summary>
    private async Task<JsonElement> QueryAsync(string query, object? variables, List<string> warnings, CancellationToken ct, string? warningPrefix = null)
    {
        var result = await SendAsync(query, variables, ct);
        if (result.Data.ValueKind != JsonValueKind.Object)
        {
            throw new GitHubQueryException(result.Errors.Count > 0 ? result.Errors[0].Message : "GitHub returned no data.");
        }

        foreach (var error in result.Errors)
        {
            warnings.Add(warningPrefix is null ? error.Message : $"{warningPrefix}: {error.Message}");
        }

        return result.Data;
    }

    /// <summary><c>GET /user/orgs</c>: every org the token's user belongs to.</summary>
    private async Task<List<string>> ListOrgsViaRestAsync(CancellationToken ct)
    {
        var token = await tokens.GetTokenAsync(ct)
            ?? throw new GitHubAuthException("No GitHub token. Set GITHUB_TOKEN, or log in with `gh auth login`.");
        var result = new List<string>();
        for (var page = 1; page <= 5; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.CurrentValue.RestUrl.TrimEnd('/')}/user/orgs?per_page=100&page={page}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.ParseAdd("devdash-hub");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new GitHubQueryException($"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var count = 0;
            foreach (var org in doc.RootElement.EnumerateArray())
            {
                count++;
                if (Str(org, "login") is { } login)
                {
                    result.Add(login);
                }
            }

            if (count < 100)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>Posts the query, retrying once when GitHub's gateway times out (502/503/504).</summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(string query, object? variables, string token, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, options.CurrentValue.ApiUrl)
            {
                Content = JsonContent.Create(new { query, variables }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.ParseAdd("devdash-hub");
            request.Headers.Accept.ParseAdd("application/json");

            var response = await http.SendAsync(request, ct);
            var transient = response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
            if (!transient || attempt >= 1)
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task<GraphQlResult> SendAsync(string query, object? variables, CancellationToken ct)
    {
        var token = await tokens.GetTokenAsync(ct)
            ?? throw new GitHubAuthException("No GitHub token. Set GITHUB_TOKEN, or log in with `gh auth login`.");

        using var response = await SendWithRetryAsync(query, variables, token, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            tokens.Invalidate();
            throw new GitHubAuthException("GitHub rejected the token (401).");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubQueryException($"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
        var errors = new List<GraphQlError>();
        if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in errs.EnumerateArray())
            {
                var path = e.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.Array
                    ? p.EnumerateArray().Select(x => x.ToString()).ToList()
                    : [];
                errors.Add(new GraphQlError(Str(e, "type") ?? "", Str(e, "message") ?? "Unknown error", path));
            }
        }

        return new GraphQlResult(data, errors);
    }
}

public sealed class GitHubQueryException(string message) : Exception(message);
