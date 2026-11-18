using Microsoft.Extensions.Options;

namespace DevDash.Agent.Actions;

/// <summary>
/// Lets the action worker ask the scan loop to rescan the entries an action touched, straight away,
/// so the dashboard stops showing a finding that has just been fixed.
/// </summary>
public sealed class RescanRequests(IOptionsMonitor<AgentOptions> options)
{
    /// <summary>Raised with the scan root and the top-level entry name to rescan.</summary>
    public event Action<string, string>? Requested;

    /// <summary>Requests a rescan of the top-level entry that holds <paramref name="path"/>. Paths outside the scan roots are ignored.</summary>
    public bool Request(string path)
    {
        if (Find(options.CurrentValue.ResolveRoots(), path) is not { } entry)
        {
            return false;
        }

        Requested?.Invoke(entry.Root, entry.Name);
        return true;
    }

    /// <summary>Maps a path to (scan root, top-level entry name). The root is returned as configured, which is how the scanner keys entries.</summary>
    public static (string Root, string Name)? Find(IReadOnlyList<string> roots, string path)
    {
        var resolved = PathSafety.Resolve(path);
        foreach (var root in roots)
        {
            var resolvedRoot = PathSafety.Resolve(root);
            if (PathSafety.IsInside(resolvedRoot, resolved) && PathSafety.SegmentsBelow(resolvedRoot, resolved) is { Length: > 0 } segments)
            {
                return (root, segments[0]);
            }
        }

        return null;
    }
}
