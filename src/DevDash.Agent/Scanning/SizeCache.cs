using System.Text.Json;
using DevDash.Contracts;

namespace DevDash.Agent.Scanning;

/// <summary>
/// Remembers folder sizes between scans, because measuring them walks every file. Stored outside the
/// scan roots, in the user's local application data folder.
/// </summary>
public sealed class SizeCache
{
    private readonly string _file;
    private readonly Lock _lock = new();
    private Dictionary<string, SizeInfo> _sizes;

    public SizeCache(string? file = null)
    {
        _file = file ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "devdash", "agent-sizes.json");
        _sizes = Load(_file);
    }

    public SizeInfo? Get(string path)
    {
        lock (_lock)
        {
            return _sizes.GetValueOrDefault(path);
        }
    }

    public void Set(string path, SizeInfo size)
    {
        lock (_lock)
        {
            _sizes[path] = size;
        }
    }

    /// <summary>Writes the cache, dropping paths that no longer exist.</summary>
    public void Save()
    {
        lock (_lock)
        {
            _sizes = _sizes.Where(kv => Directory.Exists(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllText(_file, JsonSerializer.Serialize(_sizes, SnapshotSchema.JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The cache is an optimisation; the next scan measures again.
            }
        }
    }

    private static Dictionary<string, SizeInfo> Load(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                return JsonSerializer.Deserialize<Dictionary<string, SizeInfo>>(File.ReadAllText(file), SnapshotSchema.JsonOptions)
                    ?? new(StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // fall through to an empty cache
        }

        return new(StringComparer.Ordinal);
    }
}
