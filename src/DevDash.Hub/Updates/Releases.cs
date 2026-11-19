using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevDash.Contracts;
using DevDash.Hub.Data;
using DevDash.Hub.GitHub;
using DevDash.Hub.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.Updates;

/// <summary>The newest agent release the hub knows of.</summary>
/// <param name="Version">Strict version without the tag's leading <c>v</c>.</param>
/// <param name="Signed">True when the release has a <c>checksums.txt.sig</c>. An agent refuses a release without one, so the hub does not offer it.</param>
public sealed record LatestRelease(string Version, string Tag, string? Url, DateTimeOffset? PublishedAt, bool Signed, bool Prerelease);

public sealed record ReleaseStatus(bool Enabled, DateTimeOffset? LastCheckedAt, string? LastError);

public sealed class ReleaseQueryException(string message) : Exception(message);

/// <summary>Picks the latest release out of GitHub's <c>GET /repos/{owner}/{repo}/releases</c> answer.</summary>
public static class ReleaseParser
{
    public const string SignatureAsset = "checksums.txt.sig";

    /// <summary>
    /// The highest version among the published releases. Drafts are never considered. Pre-releases
    /// (flagged as such on GitHub, or with a <c>-suffix</c> in the tag) only when asked for. Tags that
    /// are not <c>vMAJOR.MINOR.PATCH</c> are ignored.
    /// </summary>
    public static LatestRelease? Latest(JsonElement releases, bool includePrereleases)
    {
        if (releases.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        (SemVer Version, LatestRelease Release)? best = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object || Flag(release, "draft"))
            {
                continue;
            }

            var tag = Text(release, "tag_name");
            if (tag is null || !SemVer.TryParse(tag.StartsWith('v') ? tag[1..] : tag, out var version))
            {
                continue;
            }

            var prerelease = Flag(release, "prerelease") || version.IsPrerelease;
            if (prerelease && !includePrereleases)
            {
                continue;
            }

            if (best is { } current && version <= current.Version)
            {
                continue;
            }

            var signed = release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
                && assets.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.Object && Text(a, "name") == SignatureAsset);
            DateTimeOffset? published = Text(release, "published_at") is { } at && DateTimeOffset.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
            best = (version, new LatestRelease(version.ToString(), tag, Text(release, "html_url"), published, signed, prerelease));
        }

        return best?.Release;

        static bool Flag(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}

/// <summary>
/// Asks GitHub for the agent's releases. Read-only, and it works without a token: the repository is
/// public, so an unauthenticated request is fine (GitHub allows 60 an hour per address, and this asks
/// once an hour). A token, when the hub has one, is used only to get a higher limit.
/// </summary>
public sealed partial class ReleaseClient(HttpClient http, IGitHubTokenProvider tokens, IOptionsMonitor<HubOptions> hub, IOptionsMonitor<GitHubOptions> github, ILogger<ReleaseClient> logger)
{
    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}\z")]
    private static partial Regex RepositoryName();

    public async Task<LatestRelease?> GetLatestAsync(CancellationToken ct)
    {
        var settings = hub.CurrentValue.Updates;
        var repository = settings.Repository.Trim();
        if (!RepositoryName().IsMatch(repository))
        {
            throw new ReleaseQueryException($"Hub:Updates:Repository '{repository}' is not an owner/name repository.");
        }

        var url = $"{github.CurrentValue.RestUrl.TrimEnd('/')}/repos/{repository}/releases?per_page=30";
        string? token = null;
        try
        {
            token = await tokens.GetTokenAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("No GitHub token for the release check: {Error}", ex.Message);
        }

        using var response = await SendAsync(url, token, ct);
        if (response.IsSuccessStatusCode)
        {
            return await ReadAsync(response, settings, ct);
        }

        // A token that is expired, or scoped to other repositories, must not stop a public repository being read.
        if (token is not null && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound && !RateLimited(response))
        {
            using var anonymous = await SendAsync(url, null, ct);
            if (anonymous.IsSuccessStatusCode)
            {
                return await ReadAsync(anonymous, settings, ct);
            }

            throw Failure(anonymous, repository);
        }

        throw Failure(response, repository);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("devdash-hub");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await http.SendAsync(request, ct);
    }

    private static async Task<LatestRelease?> ReadAsync(HttpResponseMessage response, HubUpdateOptions settings, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ReleaseParser.Latest(document.RootElement, settings.IncludePrereleases);
    }

    private static bool RateLimited(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests
        || (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0");

    private static ReleaseQueryException Failure(HttpResponseMessage response, string repository) =>
        RateLimited(response)
            ? new ReleaseQueryException("GitHub's rate limit for release lookups is used up for now; the hub will ask again later. Setting GITHUB_TOKEN raises the limit.")
            : response.StatusCode == HttpStatusCode.NotFound
                ? new ReleaseQueryException($"GitHub has no public repository {repository} (Hub:Updates:Repository).")
                : new ReleaseQueryException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase} for the releases of {repository}.");
}

/// <summary>Holds the latest known release, and persists it so a restart does not forget it.</summary>
public sealed class ReleaseStore(IDbContextFactory<DevDashDbContext> dbFactory, DashboardNotifier notifier, TimeProvider time)
{
    private const string CacheKey = "agent-release";

    public LatestRelease? Current { get; private set; }

    public ReleaseStatus Status { get; private set; } = new(false, null, null);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Cache.AsNoTracking().FirstOrDefaultAsync(c => c.Key == CacheKey, ct);
        if (row is null)
        {
            return;
        }

        try
        {
            Current = JsonSerializer.Deserialize<LatestRelease>(row.Json, SnapshotSchema.JsonOptions);
            Status = Status with { LastCheckedAt = row.UpdatedAt };
        }
        catch (JsonException)
        {
            // An older cache format; the next check replaces it.
        }
    }

    /// <summary>Records the answer of a successful check. Null means the repository has no release yet.</summary>
    public async Task SetAsync(LatestRelease? release, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        Current = release;
        Status = Status with { LastCheckedAt = now, LastError = null };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Cache.FirstOrDefaultAsync(c => c.Key == CacheKey, ct);
        if (release is null)
        {
            if (row is not null)
            {
                db.Cache.Remove(row);
            }
        }
        else
        {
            if (row is null)
            {
                row = new CacheEntity { Key = CacheKey };
                db.Cache.Add(row);
            }

            row.Json = JsonSerializer.Serialize(release, SnapshotSchema.JsonOptions);
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        notifier.Notify(DashboardChange.Machines);
    }

    public void SetStatus(Func<ReleaseStatus, ReleaseStatus> update)
    {
        Status = update(Status);
        notifier.Notify(DashboardChange.Machines);
    }
}

/// <summary>Checks for a new agent release on a timer (hourly by default), and sooner when someone asks.</summary>
public sealed class ReleasePoller(ReleaseClient client, ReleaseStore store, IOptionsMonitor<HubOptions> options, TimeProvider time, ILogger<ReleasePoller> logger) : BackgroundService
{
    private static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _onePoll = new(1, 1);

    /// <summary>Ask for a check now (ignored while one is already queued).</summary>
    public void RequestPoll()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // already queued
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.Updates.Enabled)
        {
            store.SetStatus(s => s with { Enabled = false });
            return;
        }

        store.SetStatus(s => s with { Enabled = true });
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await PollOnceAsync(stoppingToken);
                var polledAt = time.GetUtcNow();
                var interval = TimeSpan.FromMinutes(Math.Max(5, options.CurrentValue.Updates.CheckIntervalMinutes));
                if (await _wake.WaitAsync(interval, stoppingToken))
                {
                    var gap = MinimumGap - (time.GetUtcNow() - polledAt);
                    if (gap > TimeSpan.Zero)
                    {
                        await Task.Delay(gap, stoppingToken);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    internal async Task PollOnceAsync(CancellationToken ct)
    {
        // One at a time, so an older answer can never be stored over a newer one.
        await _onePoll.WaitAsync(ct);
        try
        {
            var latest = await client.GetLatestAsync(ct);
            await store.SetAsync(latest, ct);
            logger.LogInformation("Latest agent release: {Version}{Signed}", latest?.Version ?? "none yet", latest is { Signed: false } ? " (not signed)" : "");
        }
        catch (Exception ex) when (ex is ReleaseQueryException or HttpRequestException or JsonException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // Whatever was known before stays: a failed check is no reason to forget the latest release.
            logger.LogWarning("Could not check for a new agent release: {Error}", ex.Message);
            store.SetStatus(s => s with { LastCheckedAt = time.GetUtcNow(), LastError = ex.Message });
        }
        finally
        {
            _onePoll.Release();
        }
    }
}
