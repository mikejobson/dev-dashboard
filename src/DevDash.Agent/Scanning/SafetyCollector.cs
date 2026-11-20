using System.Collections.Concurrent;
using System.Globalization;
using DevDash.Agent.Git;
using DevDash.Contracts;
using DevDash.Core;
using DevDash.Core.Git;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.Scanning;

/// <summary>What the scanner already knows about a working tree when it asks for the safety facts.</summary>
public sealed record SafetyRequest
{
    public required string CommonDir { get; init; }
    public required StatusResult Status { get; init; }
    public required IReadOnlyList<BranchInfo> Branches { get; init; }
    public required IReadOnlyList<RemoteInfo> Remotes { get; init; }

    /// <summary>True when this scan may use the network: fetching is allowed for it and switched on for the machine.</summary>
    public bool MayAskRemote { get; init; }

    /// <summary>True when this scan has just fetched the repository, so the remote's tags are worth listing again.</summary>
    public bool Fetched { get; init; }
}

/// <summary>
/// Collects the facts behind the "only copy" analysis (<see cref="SafetyFacts"/>) with read-only git
/// calls and file names. It never opens a file in the working tree.
/// <para>Cost per scan of an ordinary repository: one <c>git log</c> (which branches are on a remote)
/// and one <c>for-each-ref</c> (tags). Submodules cost three calls each, and only repositories with a
/// <c>.gitmodules</c> pay it. The remote's tags are listed over the network no more often than the
/// repository is fetched, and never by a scan that may not fetch.</para>
/// </summary>
public sealed class SafetyCollector(GitRunner git, IOptionsMonitor<AgentOptions> options, TimeProvider? time = null)
{
    /// <summary>Most unpushed commits walked to count them per branch. Beyond it, branches are still flagged but not counted.</summary>
    public const int MaxUnpushedCommits = 2000;

    /// <summary>Most submodules looked inside. The rest are reported as not checked.</summary>
    public const int MaxSubmodules = 25;

    /// <summary>Most files looked at inside untracked folders when searching for secret-looking names.</summary>
    public const int MaxUntrackedWalk = 5000;

    private const int MaxRemotes = 4;
    private const char Sep = '\u001f';

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The remotes' tags as last listed, per repository. In memory only: an agent that restarts asks again.</summary>
    private readonly ConcurrentDictionary<string, RemoteTags> _remoteTags = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    private sealed record RemoteTags(DateTimeOffset AttemptedAt, DateTimeOffset? CheckedAt, IReadOnlyDictionary<string, string> Tags);

    /// <summary>Returns the facts, and the branches with <see cref="BranchInfo.OnRemote"/> and the unpushed counts filled in.</summary>
    public async Task<(SafetyFacts Facts, IReadOnlyList<BranchInfo> Branches)> CollectAsync(string path, SafetyRequest request, TimeSpan timeout, CancellationToken ct)
    {
        Task<GitResult> Run(params string[] args) => git.RunAsync(path, args, timeout, ct);

        var (branches, headOnRemote) = await OnRemoteAsync(Run, request).ConfigureAwait(false);
        var tags = await TagsAsync(path, Run, request, ct).ConfigureAwait(false);
        var walkBudget = MaxUntrackedWalk;
        var ignored = request.Status.Ignored
            .Where(p => !IsBuildOutput(path, p) && !(p.EndsWith('/') && HoldsOnlyBuildOutput(Path.Combine(path, p), ref walkBudget, path)))
            .Order(StringComparer.Ordinal)
            .ToList();
        var secrets = SecretNames(path, request.Status);

        var facts = new SafetyFacts
        {
            TagCount = tags.Count,
            LocalOnlyTags = tags.LocalOnly.Take(SafetyFacts.MaxNames).ToList(),
            LocalOnlyTagCount = tags.LocalOnly.Count,
            RemoteTagsCheckedAt = tags.CheckedAt,
            BranchesChecked = branches.All(b => b.OnRemote is not null),
            HeadOnRemote = headOnRemote,
            IgnoredFiles = ignored.Take(SafetyFacts.MaxNames).ToList(),
            IgnoredFileCount = ignored.Count,
            SecretFiles = secrets.Take(SafetyFacts.MaxNames).ToList(),
            SecretFileCount = secrets.Count,
            Submodules = await SubmodulesAsync(path, Run, timeout, ct).ConfigureAwait(false),
        };
        return (facts, branches);
    }

    /// <summary>
    /// Which branch tips are reachable from a remote-tracking ref, from one <c>git log</c> of everything
    /// that is not. A commit on a remote has all its ancestors there too, so walking a branch's parents
    /// inside that list counts exactly the commits that exist only here.
    /// </summary>
    private static async Task<(IReadOnlyList<BranchInfo> Branches, bool? HeadOnRemote)> OnRemoteAsync(Func<string[], Task<GitResult>> run, SafetyRequest request)
    {
        var head = request.Status.HeadSha;
        if (request.Remotes.Count == 0)
        {
            return (request.Branches.Select(b => b with { OnRemote = false }).ToList(), head is null ? null : false);
        }

        if (request.Branches.Count == 0 && head is null)
        {
            return (request.Branches, null);
        }

        string[] tips = head is null ? ["--branches"] : ["--branches", "HEAD"];
        var log = await run(["log", "--format=%H%x1f%P%x1f%cI", "-n", (MaxUnpushedCommits + 1).ToString(CultureInfo.InvariantCulture), .. tips, "--not", "--remotes"]).ConfigureAwait(false);
        if (!log.Ok)
        {
            return (request.Branches, null);
        }

        var only = new Dictionary<string, (string[] Parents, DateTimeOffset? Date)>(StringComparer.Ordinal);
        foreach (var line in log.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.TrimEnd('\r').Split(Sep);
            if (f.Length == 3 && CommitLogParser.IsSha(f[0]))
            {
                only[f[0]] = (
                    f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    DateTimeOffset.TryParse(f[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null);
            }
        }

        // With a cut-off list, a tip that is missing from it may be on a remote or may be beyond the cut.
        var complete = only.Count <= MaxUnpushedCommits;
        bool? OnRemote(string sha) => only.ContainsKey(sha) ? false : complete ? true : null;

        var branches = request.Branches.Select(branch =>
        {
            var onRemote = OnRemote(branch.Sha);
            if (onRemote != false || !complete)
            {
                return branch with { OnRemote = onRemote };
            }

            var seen = new HashSet<string>(StringComparer.Ordinal) { branch.Sha };
            var stack = new Stack<string>([branch.Sha]);
            DateTimeOffset? oldest = null;
            while (stack.Count > 0)
            {
                var (parents, date) = only[stack.Pop()];
                if (date is not null && (oldest is null || date < oldest))
                {
                    oldest = date;
                }

                foreach (var parent in parents)
                {
                    if (only.ContainsKey(parent) && seen.Add(parent))
                    {
                        stack.Push(parent);
                    }
                }
            }

            return branch with { OnRemote = false, UnpushedCount = seen.Count, OldestUnpushedAt = oldest };
        }).ToList();

        return (branches, head is null ? null : OnRemote(head));
    }

    private sealed record TagFacts(int Count, IReadOnlyList<string> LocalOnly, DateTimeOffset? CheckedAt);

    private async Task<TagFacts> TagsAsync(string path, Func<string[], Task<GitResult>> run, SafetyRequest request, CancellationToken ct)
    {
        var listed = await run(["for-each-ref", "--format=%(objectname)%1f%(refname)", "refs/tags"]).ConfigureAwait(false);
        var local = new SortedDictionary<string, string>(StringComparer.Ordinal);
        const string prefix = "refs/tags/";
        if (listed.Ok)
        {
            foreach (var line in listed.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.TrimEnd('\r').Split(Sep);
                if (f.Length == 2 && f[1].StartsWith(prefix, StringComparison.Ordinal))
                {
                    local[f[1][prefix.Length..]] = f[0];
                }
            }
        }

        if (local.Count == 0)
        {
            return new TagFacts(0, [], null);
        }

        if (request.Remotes.Count == 0)
        {
            return new TagFacts(local.Count, local.Keys.ToList(), null);
        }

        var remote = await RemoteTagsAsync(path, request, ct).ConfigureAwait(false);
        if (remote.CheckedAt is null)
        {
            return new TagFacts(local.Count, [], null);
        }

        // On a remote means the same name pointing at the same object there.
        var localOnly = local.Where(t => !remote.Tags.TryGetValue(t.Key, out var sha) || sha != t.Value).Select(t => t.Key).ToList();
        return new TagFacts(local.Count, localOnly, remote.CheckedAt);
    }

    /// <summary>
    /// The tags on the repository's remotes. Asked over the network only when this scan may fetch, and
    /// then only after a fetch, the first time, or once the fetch interval has passed; otherwise the
    /// last answer stands. A failed attempt keeps the previous answer and is not repeated every scan.
    /// </summary>
    private async Task<RemoteTags> RemoteTagsAsync(string path, SafetyRequest request, CancellationToken ct)
    {
        var fetch = options.CurrentValue.AutoFetch;
        var gate = _locks.GetOrAdd(request.CommonDir, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            _remoteTags.TryGetValue(request.CommonDir, out var known);
            var due = known is null || request.Fetched || now - known.AttemptedAt >= TimeSpan.FromHours(fetch.IntervalHours);
            if (!request.MayAskRemote || !due)
            {
                return known ?? new RemoteTags(now, null, new Dictionary<string, string>());
            }

            var tags = new Dictionary<string, string>(StringComparer.Ordinal);
            var ok = true;
            foreach (var remote in request.Remotes.Select(r => r.Name).Distinct(StringComparer.Ordinal).Take(MaxRemotes))
            {
                var result = await git.ListRemoteTagsAsync(path, remote, TimeSpan.FromSeconds(fetch.TimeoutSeconds), ct).ConfigureAwait(false);
                if (!result.Ok)
                {
                    ok = false;
                    break;
                }

                foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    // "<sha>\trefs/tags/<name>". The same name on two remotes: either object counts.
                    var tab = line.IndexOf('\t');
                    if (tab > 0 && line[(tab + 1)..].TrimEnd('\r') is var name && name.StartsWith("refs/tags/", StringComparison.Ordinal))
                    {
                        tags.TryAdd(name["refs/tags/".Length..], line[..tab]);
                    }
                }
            }

            var updated = ok
                ? new RemoteTags(now, now, tags)
                : new RemoteTags(now, known?.CheckedAt, known?.Tags ?? new Dictionary<string, string>());
            _remoteTags[request.CommonDir] = updated;
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// True for an ignored path that a build or a tool recreates: inside a folder the size scan counts
    /// as reclaimable, or a cache. Uses the same name rules as that scan (<see cref="BuildOutput"/>).
    /// </summary>
    internal static bool IsBuildOutput(string repo, string relativePath)
    {
        var isDirectory = relativePath.EndsWith('/');
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parent = repo;
        for (var i = 0; i < segments.Length; i++)
        {
            var name = segments[i];
            if (BuildOutput.IsCache(name))
            {
                return true;
            }

            var folder = parent;
            if ((isDirectory || i < segments.Length - 1)
                && (BuildOutput.IsReclaimable(name, () => SiblingNames(folder)) || BuildOutput.IsRecreated(name, () => SiblingNames(folder), () => SiblingNames(repo))))
            {
                return true;
            }

            parent = Path.Combine(parent, name);
        }

        return false;
    }

    /// <summary>
    /// Git reports a folder whose whole content is ignored as one line, whatever made it so:
    /// <c>tools/</c> when all it holds is <c>tools/__pycache__/</c>. Such a folder is only worth
    /// mentioning when something in it is not build output. Bounded; a folder too big to finish counts as worth mentioning.
    /// </summary>
    internal static bool HoldsOnlyBuildOutput(string folder, ref int budget, string? repo = null)
    {
        var stack = new Stack<string>([folder]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            FileSystemInfo[] children;
            try
            {
                children = new DirectoryInfo(current).GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            var names = children.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var child in children)
            {
                if (--budget < 0)
                {
                    return false;
                }

                if (BuildOutput.IsCache(child.Name))
                {
                    continue;
                }

                if (child is not DirectoryInfo || (child.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false; // a file somebody put there
                }

                if (!BuildOutput.IsReclaimable(child.Name, names) && !BuildOutput.IsRecreated(child.Name, () => names, repo is null ? null : () => SiblingNames(repo)))
                {
                    stack.Push(child.FullName);
                }
            }
        }

        return true;
    }

    private static IReadOnlySet<string> SiblingNames(string folder)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>();
        }
    }

    /// <summary>
    /// Untracked files with a secret-looking name that no ignore rule covers. Git reports an untracked
    /// folder as one line, so those are walked (names only, bounded) to find what is inside.
    /// </summary>
    internal static IReadOnlyList<string> SecretNames(string repo, StatusResult status)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var ignoredFiles = status.Ignored.Where(p => !p.EndsWith('/')).ToHashSet(StringComparer.Ordinal);
        var ignoredFolders = status.Ignored.Where(p => p.EndsWith('/')).ToList();
        bool Ignored(string relative) => ignoredFiles.Contains(relative) || ignoredFolders.Any(f => relative.StartsWith(f, StringComparison.Ordinal));

        var budget = MaxUntrackedWalk;
        foreach (var untracked in status.Paths.Where(p => !p.Tracked))
        {
            if (!untracked.Path.EndsWith('/'))
            {
                if (SecretFiles.LooksSecret(SecretFiles.FileNameOf(untracked.Path)))
                {
                    found.Add(untracked.Path);
                }

                continue;
            }

            var stack = new Stack<string>([untracked.Path]);
            while (stack.Count > 0 && budget > 0)
            {
                var relative = stack.Pop();
                var directory = new DirectoryInfo(Path.Combine(repo, relative));
                FileSystemInfo[] children;
                try
                {
                    // A repository of its own keeps its own secrets: its files are that repository's business.
                    if (File.Exists(Path.Combine(directory.FullName, ".git")) || Directory.Exists(Path.Combine(directory.FullName, ".git")))
                    {
                        continue;
                    }

                    children = directory.GetFileSystemInfos();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var child in children)
                {
                    budget--;
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    var childPath = relative + child.Name;
                    if (child is DirectoryInfo)
                    {
                        if (!Ignored(childPath + "/") && !BuildOutput.IsCache(child.Name))
                        {
                            stack.Push(childPath + "/");
                        }
                    }
                    else if (SecretFiles.LooksSecret(child.Name) && !Ignored(childPath))
                    {
                        found.Add(childPath);
                    }
                }
            }
        }

        return found.ToList();
    }

    /// <summary>
    /// The state of each submodule. A repository with no <c>.gitmodules</c> costs nothing. Each
    /// checked-out submodule is a repository of its own, so git is asked inside it: uncommitted or
    /// untracked files, commits on no remote, stashes.
    /// </summary>
    private async Task<IReadOnlyList<SubmoduleInfo>> SubmodulesAsync(string path, Func<string[], Task<GitResult>> run, TimeSpan timeout, CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(path, ".gitmodules")))
        {
            return [];
        }

        var status = await run(["submodule", "status", "--recursive"]).ConfigureAwait(false);
        if (!status.Ok)
        {
            // Something is registered but cannot be listed: say so, so the repository is not called safe.
            return [new SubmoduleInfo { Path = ".gitmodules", Initialized = true, Checked = false }];
        }

        var result = new List<SubmoduleInfo>();
        foreach (var raw in status.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<state><sha> <path>[ (<describe>)]"; state is ' ', '+', '-' (not checked out) or 'U'.
            var line = raw.TrimEnd('\r');
            var space = line.IndexOf(' ', 1);
            if (line.Length < 3 || space < 0)
            {
                continue;
            }

            var initialized = line[0] != '-';
            var sub = line[(space + 1)..];
            if (initialized && sub.EndsWith(')') && sub.LastIndexOf(" (", StringComparison.Ordinal) is > 0 and var describe)
            {
                sub = sub[..describe];
            }

            if (!initialized)
            {
                result.Add(new SubmoduleInfo { Path = sub, Initialized = false, Checked = true });
                continue;
            }

            if (result.Count(s => s.Initialized) >= MaxSubmodules)
            {
                result.Add(new SubmoduleInfo { Path = sub, Initialized = true, Checked = false });
                continue;
            }

            var inside = Path.Combine(path, sub);
            Task<GitResult> In(params string[] args) => git.RunAsync(inside, args, timeout, ct);
            if (!Directory.Exists(inside))
            {
                result.Add(new SubmoduleInfo { Path = sub, Initialized = true, Checked = false });
                continue;
            }

            var changes = await In("status", "--porcelain=v2").ConfigureAwait(false);
            var unpushed = await In("log", "--format=%H", "-n", "1", "--branches", "HEAD", "--not", "--remotes").ConfigureAwait(false);
            var stashes = await In("stash", "list", "--format=%H").ConfigureAwait(false);
            result.Add(new SubmoduleInfo
            {
                Path = sub,
                Initialized = true,
                Checked = changes.Ok && unpushed.Ok && stashes.Ok,
                Dirty = changes.Ok && StatusParser.Parse(changes.StdOut) is var parsed && (parsed.Staged + parsed.Unstaged + parsed.Conflicted + parsed.Untracked) > 0,
                Unpushed = unpushed.Ok && unpushed.StdOut.Trim().Length > 0,
                Stashes = stashes.Ok ? stashes.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length : 0,
            });
        }

        return result;
    }
}
