using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace DevDash.Hub.Data;

/// <summary>
/// The hub creates its schema with <c>EnsureCreated</c>, which does nothing once a database exists.
/// So a table added in a later version would be missing from a database made by an earlier one.
/// This creates whatever tables and indexes the model has and the database lacks, using the same SQL
/// EF would have used for a new database. It is safe to run on every start.
/// It does not alter existing tables: a new column on an old table still needs a migration or a reset.
/// </summary>
public static partial class SchemaUpgrade
{
    [GeneratedRegex(@"^CREATE TABLE ""(?<name>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex CreateTable();

    [GeneratedRegex(@"^CREATE (?:UNIQUE )?INDEX ""(?<name>[^""]+)"" ON ""(?<table>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex CreateIndex();

    /// <summary>Returns the names of the tables and indexes it created.</summary>
    public static async Task<IReadOnlyList<string>> CreateMissingTablesAsync(DevDashDbContext db, CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
        {
            return [];
        }

        var existing = (await db.Database
                .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type IN ('table', 'index')")
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var created = new List<string>();
        foreach (var statement in Regex.Split(db.Database.GenerateCreateScript(), @";\s*(?=CREATE |$)").Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            var name = CreateTable().Match(statement) is { Success: true } table ? table.Groups["name"].Value
                : CreateIndex().Match(statement) is { Success: true } index ? index.Groups["name"].Value
                : null;
            if (name is null || existing.Contains(name))
            {
                continue;
            }

            // The statement comes from EF's own model, not from user input.
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync(statement, ct);
#pragma warning restore EF1002
            existing.Add(name);
            created.Add(name);
        }

        return created;
    }
}
