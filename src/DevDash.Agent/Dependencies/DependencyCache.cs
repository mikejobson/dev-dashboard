using System.Text.Json;
using DevDash.Contracts;

namespace DevDash.Agent.Dependencies;

/// <param name="AttemptedAt">When the tools were last run, whether or not they answered.</param>
/// <param name="Transient">The last run failed for a passing reason (offline, timed out): ask again sooner than a day.</param>
public sealed record DependencyCacheEntry(DependencyHealth Health, DateTimeOffset AttemptedAt, bool Transient = false);

/// <summary>
/// The last dependency check of each repository, kept between runs in the agent's state folder (beside
/// the size cache), so a restart does not start a day's worth of checks again.
/// </summary>
public sealed class DependencyCache
{
    private readonly string _file;
    private readonly Lock _lock = new();
    private Dictionary<string, DependencyCacheEntry> _entries;

    public DependencyCache(string? file = null)
    {
        _file = file ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "devdash", "agent-deps.json");
        _entries = Load(_file);
    }

    public DependencyCacheEntry? Get(string path)
    {
        lock (_lock)
        {
            return _entries.GetValueOrDefault(path);
        }
    }

    /// <summary>What to put in the snapshot for a repository: its last result, when a check found something to check.</summary>
    public DependencyHealth? HealthOf(string path) => Get(path)?.Health is { Reports.Count: > 0 } health ? health : null;

    public void Set(string path, DependencyCacheEntry entry)
    {
        lock (_lock)
        {
            _entries[path] = entry;
        }
    }

    /// <summary>Writes the cache, dropping repositories that no longer exist.</summary>
    public void Save()
    {
        lock (_lock)
        {
            _entries = _entries.Where(kv => Directory.Exists(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllText(_file, JsonSerializer.Serialize(_entries, SnapshotSchema.JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The cache is an optimisation; the checks run again after a restart.
            }
        }
    }

    private static Dictionary<string, DependencyCacheEntry> Load(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, DependencyCacheEntry>>(File.ReadAllText(file), SnapshotSchema.JsonOptions);
                return loaded is null ? new(StringComparer.Ordinal) : new(loaded.Where(kv => kv.Value?.Health is not null), StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // fall through to an empty cache
        }

        return new(StringComparer.Ordinal);
    }
}
