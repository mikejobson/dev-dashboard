using System.Text.Json;
using DevDash.Contracts;

namespace DevDash.Agent.Actions;

/// <summary>A project this agent moved to the trash: where it was and where it can be cloned from again.</summary>
public sealed record RemovedProject
{
    public required string Path { get; init; }

    /// <summary>The fetch URLs of its remotes, as the scan reported them.</summary>
    public IReadOnlyList<string> Urls { get; init; } = [];
    public DateTimeOffset RemovedAt { get; init; }
}

/// <summary>
/// The agent's own record of the projects it removed, kept outside the scan roots beside the size
/// cache. It is what lets the agent, not the hub, decide which URL may be cloned into which folder:
/// a clone is only accepted into a folder this agent emptied, from a URL that folder's project had.
/// </summary>
public sealed class RemovalLog
{
    private const int Keep = 500;

    private readonly string _file;
    private readonly Lock _lock = new();
    private List<RemovedProject> _removed;

    public RemovalLog(string? file = null)
    {
        _file = file ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "devdash", "agent-removed.json");
        _removed = Load(_file);
    }

    /// <summary>Records a removal. A later removal of the same folder replaces the earlier one.</summary>
    public void Record(string path, IEnumerable<string> urls, DateTimeOffset at)
    {
        lock (_lock)
        {
            _removed.RemoveAll(r => PathSafety.SamePath(r.Path, path));
            _removed.Add(new RemovedProject { Path = path, Urls = urls.Distinct(StringComparer.Ordinal).ToList(), RemovedAt = at });
            if (_removed.Count > Keep)
            {
                _removed = _removed.OrderByDescending(r => r.RemovedAt).Take(Keep).ToList();
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllText(_file, JsonSerializer.Serialize(_removed, SnapshotSchema.JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Kept in memory until the agent restarts. Without the file, a re-clone after that is refused, which is the safe way round.
            }
        }
    }

    /// <summary>True when this agent removed a project from <paramref name="path"/> that had <paramref name="url"/> as a remote.</summary>
    public bool Allows(string path, string url)
    {
        lock (_lock)
        {
            return _removed.Any(r => PathSafety.SamePath(r.Path, path) && r.Urls.Contains(url, StringComparer.Ordinal));
        }
    }

    private static List<RemovedProject> Load(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                return JsonSerializer.Deserialize<List<RemovedProject>>(File.ReadAllText(file), SnapshotSchema.JsonOptions) ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // fall through to an empty record
        }

        return [];
    }
}

/// <summary>
/// The remote URLs of the repositories in this agent's last scan. A clone may also come from one of
/// these: a URL the machine's owner already uses here, never one the hub made up.
/// </summary>
public sealed class KnownRemotes
{
    private volatile HashSet<string> _urls = new(StringComparer.Ordinal);

    public void Update(MachineSnapshot snapshot) =>
        _urls = DevDash.Core.RepoIdentity.Flatten(snapshot.Entries)
            .SelectMany(e => e.Git?.Remotes ?? [])
            .Select(r => r.Url)
            .ToHashSet(StringComparer.Ordinal);

    public bool Contains(string url) => _urls.Contains(url);
}
