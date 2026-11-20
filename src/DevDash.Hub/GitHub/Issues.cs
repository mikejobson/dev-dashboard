using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevDash.Contracts;
using DevDash.Core.GitHub;
using DevDash.Hub.Data;
using DevDash.Hub.Services;
using DevDash.Hub.T3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.GitHub;

/// <summary>An open issue of a repository the hub tracks, from the REST list. The text is the issue author's and untrusted.</summary>
public sealed record TrackedIssue
{
    /// <summary>Lower-cased <c>github.com/owner/repo</c>.</summary>
    public required string RepoKey { get; init; }

    /// <summary><c>owner/repo</c> as GitHub spells it.</summary>
    public required string Repo { get; init; }
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";

    /// <summary>The description, cut to <see cref="GitHubIssuesClient.MaxBodyChars"/>.</summary>
    public string Body { get; init; } = "";
    public string? Author { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
    public int Comments { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public bool HasLabel(string label) => Labels.Contains(label, StringComparer.OrdinalIgnoreCase);
}

/// <summary>The open issues of one repository, as of the last request that changed anything.</summary>
public sealed record RepoIssues
{
    public required string RepoKey { get; init; }
    public required string Repo { get; init; }

    /// <summary>The ETag of the last list, sent back as <c>If-None-Match</c>: an unchanged list costs no rate limit.</summary>
    public string? ETag { get; init; }
    public DateTimeOffset FetchedAt { get; init; }

    /// <summary>Null when the repository's visibility was not known. A repository not known to be private is treated as public.</summary>
    public bool? IsPrivate { get; init; }

    /// <summary>A GitHub Enterprise internal repository: private, but anyone in the enterprise can write issues and comments, so treated as public.</summary>
    public bool IsInternal { get; init; }
    public IReadOnlyList<TrackedIssue> Issues { get; init; } = [];

    /// <summary>More open issues than one page: only the most recently updated are listed.</summary>
    public bool Truncated { get; init; }
    public string? Error { get; init; }
}

public sealed record IssueSnapshot
{
    public DateTimeOffset? PolledAt { get; init; }
    public string? Error { get; init; }
    public IReadOnlyDictionary<string, RepoIssues> Repos { get; init; } = new Dictionary<string, RepoIssues>();

    public IEnumerable<TrackedIssue> All => Repos.Values.SelectMany(r => r.Issues);

    public static IssueSnapshot Empty { get; } = new();
}

public enum IssueListStatus
{
    Changed,
    NotModified,
    Failed,
}

public sealed record IssueListResult(IssueListStatus Status, IReadOnlyList<TrackedIssue> Issues, string? ETag, bool Truncated, string? Error);

/// <summary>What a write to GitHub came to. A refusal (no permission) is reported, not thrown.</summary>
public sealed record GitHubWrite(bool Ok, string? Error);

/// <summary>
/// The REST calls behind the Issues page and the label trigger: list open issues (conditional, by ETag),
/// read who added a label, read the latest comments, and the two optional writes (a comment, swapping a
/// label). The writes happen only when the user turned them on, and a token that may not write is
/// reported as such.
/// </summary>
public sealed partial class GitHubIssuesClient(HttpClient http, IGitHubTokenProvider tokens, IOptionsMonitor<GitHubOptions> options)
{
    public const int PageSize = 50;
    public const int MaxBodyChars = 16_000;

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$")]
    private static partial Regex SafeName();

    public static bool TrySplit(string repoKey, out string owner, out string name) =>
        GitHubKeys.TrySplit(repoKey, out owner, out name) && SafeName().IsMatch(owner) && SafeName().IsMatch(name);

    private string Rest => options.CurrentValue.RestUrl.TrimEnd('/');

    public async Task<IssueListResult> ListOpenAsync(string repoKey, string? etag, CancellationToken ct)
    {
        if (!TrySplit(repoKey, out var owner, out var name))
        {
            return new IssueListResult(IssueListStatus.Failed, [], null, false, "Not a GitHub repository name.");
        }

        using var request = await RequestAsync(HttpMethod.Get, $"/repos/{owner}/{name}/issues?state=open&sort=updated&direction=desc&per_page={PageSize}", ct);
        if (!string.IsNullOrEmpty(etag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new IssueListResult(IssueListStatus.NotModified, [], etag, false, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new IssueListResult(IssueListStatus.Failed, [], null, false, $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var issues = ParseIssues(doc.RootElement, repoKey);
        var truncated = response.Headers.TryGetValues("Link", out var links) && links.Any(l => l.Contains("rel=\"next\"", StringComparison.Ordinal));
        return new IssueListResult(IssueListStatus.Changed, issues, response.Headers.ETag?.ToString(), truncated, null);
    }

    /// <summary>The REST list holds pull requests too; those are left out.</summary>
    public static IReadOnlyList<TrackedIssue> ParseIssues(JsonElement root, string repoKey)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<TrackedIssue>();
        foreach (var node in root.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object || node.TryGetProperty("pull_request", out _) || !node.TryGetProperty("number", out var number) || !number.TryGetInt32(out var n))
            {
                continue;
            }

            var body = Str(node, "body") ?? "";
            result.Add(new TrackedIssue
            {
                RepoKey = repoKey.ToLowerInvariant(),
                Repo = RepoName(node, repoKey),
                Number = n,
                Title = Str(node, "title") ?? "",
                Url = Str(node, "html_url") ?? "",
                Body = body.Length > MaxBodyChars ? body[..MaxBodyChars] : body,
                Author = Obj(node, "user") is { } user ? Str(user, "login") : null,
                Labels = node.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
                    ? labels.EnumerateArray().Select(l => l.ValueKind == JsonValueKind.String ? l.GetString() : Str(l, "name")).Where(l => !string.IsNullOrEmpty(l)).Select(l => l!).ToList()
                    : [],
                Comments = node.TryGetProperty("comments", out var c) && c.TryGetInt32(out var count) ? count : 0,
                CreatedAt = Date(node, "created_at") ?? default,
                UpdatedAt = Date(node, "updated_at") ?? default,
            });
        }

        return result;
    }

    /// <summary>Most pages of issue events read, newest first, when looking for who added a label.</summary>
    public const int MaxEventPages = 3;

    [GeneratedRegex(@"<([^>]*)>\s*;\s*rel=""last""")]
    private static partial Regex LastLink();

    [GeneratedRegex(@"[?&]page=(\d+)")]
    private static partial Regex PageParameter();

    /// <summary>
    /// Who most recently added <paramref name="label"/> to the issue, from its events (<c>labeled</c>, with
    /// that label's name). GitHub lists events oldest first, so this reads the first page only to learn from
    /// its <c>Link</c> header which page is last, then walks back from the last page, newest event first, for
    /// at most <see cref="MaxEventPages"/> pages (the newest 300 events). Null when no such event is among
    /// them, or they could not be read.
    /// </summary>
    public async Task<string?> LatestLabelerAsync(string repoKey, int number, string label, CancellationToken ct)
    {
        if (!TrySplit(repoKey, out var owner, out var name))
        {
            return null;
        }

        var path = $"/repos/{owner}/{name}/issues/{number.ToString(CultureInfo.InvariantCulture)}/events?per_page=100&page=";
        var first = await EventPageAsync(path + "1", ct);
        if (first is null)
        {
            return null;
        }

        var last = first.Value.LastPage ?? 1;
        for (var page = last; page >= 1 && page > last - MaxEventPages; page--)
        {
            var events = page == 1 ? first.Value.Events : (await EventPageAsync(path + page.ToString(CultureInfo.InvariantCulture), ct))?.Events;
            if (events is null)
            {
                return null;
            }

            for (var i = events.Count - 1; i >= 0; i--)
            {
                if (events[i].Event == "labeled" && string.Equals(events[i].Label, label, StringComparison.OrdinalIgnoreCase))
                {
                    return events[i].Actor;
                }
            }
        }

        return null;
    }

    /// <summary>One page of an issue's events, oldest first, and the number of the last page when there is more than one.</summary>
    private async Task<(IReadOnlyList<(string? Event, string? Label, string? Actor)> Events, int? LastPage)?> EventPageAsync(string path, CancellationToken ct)
    {
        using var request = await RequestAsync(HttpMethod.Get, path, ct);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var events = doc.RootElement.EnumerateArray()
            .Select(e => (Str(e, "event"), Obj(e, "label") is { } l ? Str(l, "name") : null, Obj(e, "actor") is { } a ? Str(a, "login") : null))
            .ToList();
        return (events, LastPageOf(response));
    }

    /// <summary>The page number in the <c>rel="last"</c> link, if any. Only the number is used: the link itself is never followed.</summary>
    internal static int? LastPageOf(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var links))
        {
            return null;
        }

        foreach (var link in links)
        {
            if (LastLink().Match(link) is { Success: true } last
                && PageParameter().Match(last.Groups[1].Value) is { Success: true } page
                && int.TryParse(page.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1)
            {
                return n;
            }
        }

        return null;
    }

    /// <summary>The issue's latest comments, oldest of them first.</summary>
    public async Task<IReadOnlyList<IssueComment>> LatestCommentsAsync(string repoKey, int number, int total, int take, CancellationToken ct)
    {
        if (total <= 0 || take <= 0 || !TrySplit(repoKey, out var owner, out var name))
        {
            return [];
        }

        var last = (total - 1) / 100 + 1;
        using var request = await RequestAsync(HttpMethod.Get, $"/repos/{owner}/{name}/issues/{number.ToString(CultureInfo.InvariantCulture)}/comments?per_page=100&page={last.ToString(CultureInfo.InvariantCulture)}", ct);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return doc.RootElement.EnumerateArray()
            .Where(c => c.ValueKind == JsonValueKind.Object)
            .Select(c => new IssueComment(Obj(c, "user") is { } u ? Str(u, "login") : null, Date(c, "created_at") ?? default, Str(c, "body") ?? ""))
            .TakeLast(take)
            .ToList();
    }

    /// <summary>Posts a comment on the issue.</summary>
    public async Task<GitHubWrite> CommentAsync(string repoKey, int number, string body, CancellationToken ct)
    {
        if (!TrySplit(repoKey, out var owner, out var name))
        {
            return new GitHubWrite(false, "Not a GitHub repository name.");
        }

        using var request = await RequestAsync(HttpMethod.Post, $"/repos/{owner}/{name}/issues/{number.ToString(CultureInfo.InvariantCulture)}/comments", ct);
        request.Content = JsonContent.Create(new { body });
        using var response = await http.SendAsync(request, ct);
        return Written(response);
    }

    /// <summary>Adds <paramref name="add"/>, then takes <paramref name="remove"/> off.</summary>
    public async Task<GitHubWrite> SwapLabelAsync(string repoKey, int number, string remove, string add, CancellationToken ct)
    {
        if (!TrySplit(repoKey, out var owner, out var name))
        {
            return new GitHubWrite(false, "Not a GitHub repository name.");
        }

        var issue = $"/repos/{owner}/{name}/issues/{number.ToString(CultureInfo.InvariantCulture)}";
        using (var request = await RequestAsync(HttpMethod.Post, issue + "/labels", ct))
        {
            request.Content = JsonContent.Create(new { labels = new[] { add } });
            using var response = await http.SendAsync(request, ct);
            if (Written(response) is { Ok: false } failed)
            {
                return failed;
            }
        }

        using (var request = await RequestAsync(HttpMethod.Delete, $"{issue}/labels/{Uri.EscapeDataString(remove)}", ct))
        {
            using var response = await http.SendAsync(request, ct);
            return response.StatusCode == HttpStatusCode.NotFound ? new GitHubWrite(true, null) : Written(response);
        }
    }

    private static GitHubWrite Written(HttpResponseMessage response) => response.StatusCode switch
    {
        _ when response.IsSuccessStatusCode => new GitHubWrite(true, null),
        HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized =>
            new GitHubWrite(false, $"the GitHub token may not write to this repository (HTTP {(int)response.StatusCode})"),
        _ => new GitHubWrite(false, $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}"),
    };

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path, CancellationToken ct)
    {
        var token = await tokens.GetTokenAsync(ct)
            ?? throw new GitHubAuthException("No GitHub token. Set GITHUB_TOKEN, or log in with `gh auth login`.");
        var request = new HttpRequestMessage(method, Rest + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("devdash-hub");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return request;
    }

    private static string RepoName(JsonElement node, string repoKey)
    {
        // repository_url: https://api.github.com/repos/<owner>/<repo>
        if (Str(node, "repository_url") is { } url && url.Split('/') is { Length: >= 2 } parts)
        {
            return $"{parts[^2]}/{parts[^1]}";
        }

        return GitHubKeys.TrySplit(repoKey, out var owner, out var name) ? $"{owner}/{name}" : repoKey;
    }

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

/// <summary>The last issue poll, kept in the cache table so a restart shows it at once.</summary>
public sealed class IssueStore(IDbContextFactory<DevDashDbContext> dbFactory, DashboardNotifier notifier, TimeProvider time)
{
    private const string CacheKey = "github-issues";
    private readonly SemaphoreSlim _load = new(1, 1);
    private bool _loaded;

    public IssueSnapshot Current { get; private set; } = IssueSnapshot.Empty;

    public async Task<IssueSnapshot> GetAsync(CancellationToken ct = default)
    {
        if (_loaded)
        {
            return Current;
        }

        await _load.WaitAsync(ct);
        try
        {
            if (!_loaded)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var row = await db.Cache.AsNoTracking().FirstOrDefaultAsync(c => c.Key == CacheKey, ct);
                if (row is not null)
                {
                    try
                    {
                        Current = JsonSerializer.Deserialize<IssueSnapshot>(row.Json, SnapshotSchema.JsonOptions) ?? IssueSnapshot.Empty;
                    }
                    catch (JsonException)
                    {
                        // An older format; the next poll replaces it.
                    }
                }

                _loaded = true;
            }
        }
        finally
        {
            _load.Release();
        }

        return Current;
    }

    public async Task SetAsync(IssueSnapshot snapshot, CancellationToken ct = default)
    {
        Current = snapshot;
        _loaded = true;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Cache.FirstOrDefaultAsync(c => c.Key == CacheKey, ct);
        if (row is null)
        {
            row = new CacheEntity { Key = CacheKey };
            db.Cache.Add(row);
        }

        row.Json = JsonSerializer.Serialize(snapshot, SnapshotSchema.JsonOptions);
        row.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        notifier.Notify(DashboardChange.GitHub);
    }
}
