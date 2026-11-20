using System.Security.Cryptography;
using DevDash.Hub.Auth;
using DevDash.Hub.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DevDash.Hub.Mcp;

/// <summary>What an MCP token may do. Stored by these exact strings.</summary>
public static class McpScopes
{
    /// <summary>Every read tool, and <c>action_status</c>.</summary>
    public const string Read = "read";

    /// <summary>As <see cref="Read"/>, plus the tools that queue an agent action.</summary>
    public const string ReadActions = "read+actions";

    public static bool IsValid(string? scope) => scope is Read or ReadActions;
}

/// <summary>A token an MCP client (Claude Code, Claude in T3 Code) presents as <c>Authorization: Bearer</c>. Only the SHA-256 hash is stored.</summary>
public sealed class McpTokenEntity
{
    public int Id { get; set; }
    public required string KeyHash { get; set; }

    /// <summary>First characters of the token, to tell tokens apart in the UI.</summary>
    public string Prefix { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary><see cref="McpScopes.Read"/> or <see cref="McpScopes.ReadActions"/>.</summary>
    public string Scope { get; set; } = McpScopes.Read;

    /// <summary>Login of the browser user who created it.</summary>
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public bool AllowsActions => Scope == McpScopes.ReadActions;

    internal static void Configure(EntityTypeBuilder<McpTokenEntity> e)
    {
        e.HasKey(t => t.Id);
        e.HasIndex(t => t.KeyHash).IsUnique();
        e.Property(t => t.KeyHash).HasMaxLength(64);
        e.Property(t => t.Scope).HasMaxLength(16);
    }
}

public sealed record IssuedMcpToken(McpTokenEntity Entity, string Plaintext);

/// <summary>
/// Issues, validates and revokes MCP tokens: random 32-byte tokens with a <c>ddm_</c> prefix, stored as
/// SHA-256 hashes, the same way as agent keys but in their own table, so neither kind works as the other.
/// </summary>
public sealed class McpTokenService(IDbContextFactory<DevDashDbContext> dbFactory, TimeProvider time)
{
    public const string TokenPrefix = "ddm_";

    /// <summary>"Last used" is written at most this often per token, not on every call.</summary>
    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);

    public async Task<IssuedMcpToken> IssueAsync(string name, string scope, string createdBy, CancellationToken ct = default)
    {
        if (!McpScopes.IsValid(scope))
        {
            throw new ArgumentException($"Unknown scope '{scope}'.", nameof(scope));
        }

        var plaintext = TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var entity = new McpTokenEntity
        {
            KeyHash = ApiKeyService.Hash(plaintext),
            Prefix = plaintext[..10],
            Name = string.IsNullOrWhiteSpace(name) ? "claude" : name.Trim()[..Math.Min(name.Trim().Length, 100)],
            Scope = scope,
            CreatedBy = createdBy,
            CreatedAt = time.GetUtcNow(),
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.McpTokens.Add(entity);
        await db.SaveChangesAsync(ct);
        return new IssuedMcpToken(entity, plaintext);
    }

    /// <summary>The token record for a presented token, or null when it is unknown or revoked. Records the use.</summary>
    public async Task<McpTokenEntity?> ValidateAsync(string presented, CancellationToken ct = default)
    {
        presented = presented.Trim();
        if (!presented.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var hash = ApiKeyService.Hash(presented);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var token = await db.McpTokens.FirstOrDefaultAsync(t => t.KeyHash == hash, ct);
        if (token is null || token.RevokedAt is not null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (token.LastUsedAt is null || now - token.LastUsedAt >= TouchEvery)
        {
            token.LastUsedAt = now;
            await db.SaveChangesAsync(ct);
        }

        return token;
    }

    public async Task<IReadOnlyList<McpTokenEntity>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tokens = await db.McpTokens.AsNoTracking().ToListAsync(ct);
        return tokens.OrderByDescending(t => t.CreatedAt).ToList();
    }

    public async Task RevokeAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var token = await db.McpTokens.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (token is not null && token.RevokedAt is null)
        {
            token.RevokedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }
}
