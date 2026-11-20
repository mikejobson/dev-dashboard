using System.Text.Json;
using DevDash.Contracts;
using DevDash.Hub.Data;
using DevDash.Hub.Services;
using Microsoft.EntityFrameworkCore;

namespace DevDash.Hub.T3;

/// <summary>
/// The Issues page's settings: the prompt template, and the label trigger. Kept as one JSON row of the
/// cache table. Everything automatic is off until switched on, and then only for the repos ticked.
/// </summary>
public sealed class IssueSettings
{
    /// <summary>The issue prompt template; null for <see cref="T3Prompts.DefaultIssueTemplate"/>.</summary>
    public string? Template { get; set; }

    /// <summary>Start a thread when an open issue gets <see cref="Label"/>. Off by default.</summary>
    public bool AutoStart { get; set; }

    public string Label { get; set; } = "t3";

    /// <summary>
    /// GitHub logins allowed to trigger a thread by adding the label (and, on a public repository, to have
    /// written the issue), separated by commas. Empty: only the user the hub's GitHub token belongs to.
    /// </summary>
    public string AllowedLogins { get; set; } = "";

    /// <summary>The repositories the trigger watches, each with an optional preferred machine.</summary>
    public List<IssueRepoSetting> Repos { get; set; } = [];

    /// <summary>Write-back: comment "Started a Claude thread for this issue." once the thread has started. Off by default.</summary>
    public bool CommentOnStart { get; set; }

    /// <summary>Write-back: replace the trigger label with <see cref="StartedLabel"/> once the thread has started. Off by default.</summary>
    public bool SwapLabel { get; set; }

    public string StartedLabel { get; set; } = "t3:started";

    public const string StartedComment = "Started a Claude thread for this issue.";

    public IssueRepoSetting? For(string repoKey) =>
        Repos.FirstOrDefault(r => string.Equals(r.RepoKey, repoKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>The logins the trigger trusts. Empty when none is configured and the token's own user is not known yet.</summary>
    public IReadOnlySet<string> AllowedSet(string? viewerLogin)
    {
        var listed = AllowedLogins.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.TrimStart('@'))
            .Where(l => l.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (listed.Count == 0 && !string.IsNullOrWhiteSpace(viewerLogin))
        {
            listed.Add(viewerLogin);
        }

        return listed;
    }

    public IssueSettings Normalize()
    {
        Label = string.IsNullOrWhiteSpace(Label) ? "t3" : Label.Trim();
        StartedLabel = string.IsNullOrWhiteSpace(StartedLabel) ? "t3:started" : StartedLabel.Trim();
        AllowedLogins = (AllowedLogins ?? "").Trim();
        Template = string.IsNullOrWhiteSpace(Template) || Template.Trim() == T3Prompts.DefaultIssueTemplate.Trim() ? null : Template;
        Repos = Repos.Where(r => !string.IsNullOrWhiteSpace(r.RepoKey))
            .GroupBy(r => r.RepoKey.ToLowerInvariant())
            .Select(g => new IssueRepoSetting { RepoKey = g.Key, AutoStart = g.Any(r => r.AutoStart), PreferredMachine = g.Select(r => r.PreferredMachine).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)) })
            .ToList();
        return this;
    }

    public IssueSettings Copy() => JsonSerializer.Deserialize<IssueSettings>(JsonSerializer.Serialize(this, SnapshotSchema.JsonOptions), SnapshotSchema.JsonOptions)!;
}

public sealed class IssueRepoSetting
{
    public string RepoKey { get; set; } = "";

    /// <summary>The label trigger watches this repository.</summary>
    public bool AutoStart { get; set; }

    /// <summary>The machine to start threads on. Empty: the most recently seen online machine with a clone that can take it.</summary>
    public string? PreferredMachine { get; set; }
}

public sealed class IssueSettingsStore(IDbContextFactory<DevDashDbContext> dbFactory, DashboardNotifier notifier, TimeProvider time)
{
    private const string CacheKey = "issue-settings";

    public async Task<IssueSettings> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Cache.AsNoTracking().FirstOrDefaultAsync(c => c.Key == CacheKey, ct);
        if (row is null)
        {
            return new IssueSettings();
        }

        try
        {
            return (JsonSerializer.Deserialize<IssueSettings>(row.Json, SnapshotSchema.JsonOptions) ?? new IssueSettings()).Normalize();
        }
        catch (JsonException)
        {
            return new IssueSettings();
        }
    }

    public async Task SaveAsync(IssueSettings settings, CancellationToken ct = default)
    {
        settings.Normalize();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Cache.FirstOrDefaultAsync(c => c.Key == CacheKey, ct);
        if (row is null)
        {
            row = new CacheEntity { Key = CacheKey };
            db.Cache.Add(row);
        }

        row.Json = JsonSerializer.Serialize(settings, SnapshotSchema.JsonOptions);
        row.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        notifier.Notify(DashboardChange.GitHub);
    }
}
