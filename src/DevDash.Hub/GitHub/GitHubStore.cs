using System.Text.Json;
using DevDash.Contracts;
using DevDash.Core.GitHub;
using DevDash.Hub.Data;
using DevDash.Hub.Services;
using Microsoft.EntityFrameworkCore;

namespace DevDash.Hub.GitHub;

public sealed record GitHubStatus(bool Enabled, bool Polling, DateTimeOffset? LastAttemptAt, string? LastError);

/// <summary>Holds the result of the last GitHub poll, and persists it so a restart shows data immediately.</summary>
public sealed class GitHubStore(IDbContextFactory<DevDashDbContext> dbFactory, DashboardNotifier notifier, TimeProvider time)
{
    private const string CacheKey = "github";

    public GitHubSnapshot Current { get; private set; } = GitHubSnapshot.Empty;

    public GitHubStatus Status { get; private set; } = new(false, false, null, null);

    public bool HasData => Current.ViewerLogin is not null;

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
            Current = JsonSerializer.Deserialize<GitHubSnapshot>(row.Json, SnapshotSchema.JsonOptions) ?? GitHubSnapshot.Empty;
        }
        catch (JsonException)
        {
            // An older cache format; the next poll replaces it.
        }
    }

    public async Task SetAsync(GitHubSnapshot snapshot, CancellationToken ct = default)
    {
        Current = snapshot;
        Status = Status with { Polling = false, LastAttemptAt = time.GetUtcNow(), LastError = null };

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

    public void SetStatus(Func<GitHubStatus, GitHubStatus> update)
    {
        Status = update(Status);
        notifier.Notify(DashboardChange.GitHub);
    }
}
