using Microsoft.Extensions.Options;

namespace DevDash.Agent.Actions;

/// <summary>How a refresh asked for from the dashboard went.</summary>
public sealed record RefreshOutcome(bool Succeeded, string Message);

/// <summary>One entry the scan loop has been asked to refresh, and where to say how it went.</summary>
public sealed record RefreshRequest(string Root, string Name, TaskCompletionSource<RefreshOutcome> Completion);

/// <summary>
/// Lets the action worker ask the scan loop to rescan the entries an action touched, straight away,
/// so the dashboard stops showing a finding that has just been fixed. A refresh goes the same way and
/// waits for the answer: the scan loop stays the only thing that scans, one scan at a time.
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

    /// <summary>Raised when something about the machine rather than an entry has changed (§21): the scan loop pushes again.</summary>
    public event Action? MachineChanged;

    /// <summary>Asks the scan loop to push the machine's facts (disk report, dev servers) again soon.</summary>
    public void RequestMachine() => MachineChanged?.Invoke();

    /// <summary>Raised with a batch of entries to refresh (forced sizes, a fetch if this machine fetches) and report on.</summary>
    public event Action<IReadOnlyList<RefreshRequest>>? RefreshRequested;

    /// <summary>
    /// Asks the scan loop to refresh the top-level entry holding each path, and returns one task per path
    /// that completes once that entry has been rescanned and pushed. The whole batch is handed over at
    /// once, so paths in the same entry share one rescan.
    /// </summary>
    public IReadOnlyList<Task<RefreshOutcome>> Refresh(IReadOnlyList<string> paths)
    {
        var roots = options.CurrentValue.ResolveRoots();
        var results = new Task<RefreshOutcome>[paths.Count];
        var requests = new List<RefreshRequest>();
        var listeners = RefreshRequested;
        for (var i = 0; i < paths.Count; i++)
        {
            if (Find(roots, paths[i]) is not { } entry)
            {
                results[i] = Task.FromResult(new RefreshOutcome(false, $"{paths[i]} is outside this agent's scan roots."));
            }
            else if (listeners is null)
            {
                results[i] = Task.FromResult(new RefreshOutcome(false, "This agent is not running its scan loop, so it cannot rescan on request."));
            }
            else
            {
                var request = new RefreshRequest(entry.Root, entry.Name, new TaskCompletionSource<RefreshOutcome>(TaskCreationOptions.RunContinuationsAsynchronously));
                requests.Add(request);
                results[i] = request.Completion.Task;
            }
        }

        if (requests.Count > 0)
        {
            listeners!.Invoke(requests);
        }

        return results;
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
