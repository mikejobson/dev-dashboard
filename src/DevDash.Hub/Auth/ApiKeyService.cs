using System.Security.Cryptography;
using System.Text;
using DevDash.Hub.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.Auth;

public sealed record IssuedKey(ApiKeyEntity Entity, string Plaintext);

public enum KeyBinding
{
    Bound,

    /// <summary>The key already belongs to another machine.</summary>
    OtherMachine,

    /// <summary>The key is unbound, but another key that is not revoked already belongs to this machine.</summary>
    MachineHasKey,

    Revoked,
}

/// <summary>Issues, validates and revokes agent API keys. Keys are random 32-byte tokens stored as SHA-256 hashes.</summary>
public sealed class ApiKeyService(
    IDbContextFactory<DevDashDbContext> dbFactory,
    IOptions<HubOptions> hubOptions,
    IHostEnvironment environment,
    TimeProvider time,
    ILogger<ApiKeyService> logger)
{
    public const string KeyPrefix = "ddk_";
    public const string DevKeyFileName = "dev-agent-key.txt";

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public async Task<IssuedKey> IssueAsync(string label, string? machineId = null, CancellationToken ct = default)
    {
        var plaintext = KeyPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var entity = new ApiKeyEntity
        {
            KeyHash = Hash(plaintext),
            Prefix = plaintext[..10],
            Label = string.IsNullOrWhiteSpace(label) ? "agent" : label.Trim(),
            MachineId = string.IsNullOrWhiteSpace(machineId) ? null : machineId.Trim(),
            CreatedAt = time.GetUtcNow(),
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ApiKeys.Add(entity);
        await db.SaveChangesAsync(ct);
        return new IssuedKey(entity, plaintext);
    }

    /// <summary>Returns the key record for a presented token, or null when it is unknown or revoked.</summary>
    public async Task<ApiKeyEntity?> ValidateAsync(string presented, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(presented))
        {
            return null;
        }

        var hash = Hash(presented.Trim());
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == hash, ct);
        return key is null || key.RevokedAt is not null ? null : key;
    }

    /// <summary>
    /// Records use of a key and scopes it to the machine on first use. A key that already belongs to another
    /// machine is refused, and so is an unbound key naming a machine that a live key already owns: otherwise any
    /// fresh key (or the first-run key, which is in the container log) could take over that machine's snapshot
    /// and claim the actions queued for it. To move a machine to a new key, revoke its old one first.
    /// </summary>
    public async Task<KeyBinding> BindAndTouchAsync(int keyId, string machineId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (key is null || key.RevokedAt is not null)
        {
            return KeyBinding.Revoked;
        }

        if (key.MachineId is null)
        {
            // SQLite compares case-sensitively, machine ids do not: compare in memory over the few live keys.
            var owners = await db.ApiKeys.AsNoTracking()
                .Where(k => k.Id != keyId && k.RevokedAt == null && k.MachineId != null)
                .Select(k => k.MachineId!)
                .ToListAsync(ct);
            if (owners.Any(m => string.Equals(m, machineId, StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogWarning("Refused to bind API key {KeyId} to machine {MachineId}: another key that is not revoked belongs to it.", keyId, machineId);
                return KeyBinding.MachineHasKey;
            }

            key.MachineId = machineId;
        }
        else if (!string.Equals(key.MachineId, machineId, StringComparison.OrdinalIgnoreCase))
        {
            return KeyBinding.OtherMachine;
        }

        key.LastUsedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return KeyBinding.Bound;
    }

    public async Task<IReadOnlyList<ApiKeyEntity>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var keys = await db.ApiKeys.AsNoTracking().ToListAsync(ct);
        return keys.OrderByDescending(k => k.CreatedAt).ToList();
    }

    public async Task RevokeAsync(int keyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (key is not null && key.RevokedAt is null)
        {
            key.RevokedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// First run: when no key exists, issue one and print it. In Development it is also written to
    /// <c>devdash-data/dev-agent-key.txt</c> so a local agent can pick it up with <c>--api-key-file</c>.
    /// </summary>
    public async Task EnsureSeedKeyAsync(CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (await db.ApiKeys.AnyAsync(ct))
            {
                return;
            }
        }

        var issued = await IssueAsync("first-run", ct: ct);
        // It stays in the container log; a key cannot take over a machine another key owns, and it can be revoked once used.
        logger.LogWarning("No agent API keys existed, so one was created. It is shown only once:\n\n    {Key}\n", issued.Plaintext);

        if (environment.IsDevelopment())
        {
            var file = Path.Combine(HubPaths.DataDirectory(environment, hubOptions.Value), DevKeyFileName);
            await File.WriteAllTextAsync(file, issued.Plaintext + Environment.NewLine, ct);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            logger.LogWarning("Development: the key was also saved to {File}", file);
        }
    }
}

public static class HubPaths
{
    public static string DataDirectory(IHostEnvironment environment, HubOptions options)
    {
        var dir = Path.IsPathRooted(options.DataDirectory)
            ? options.DataDirectory
            : Path.Combine(environment.ContentRootPath, options.DataDirectory);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
