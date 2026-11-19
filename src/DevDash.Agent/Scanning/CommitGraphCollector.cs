using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DevDash.Agent.Git;
using DevDash.Contracts;
using DevDash.Core.Git;

namespace DevDash.Agent.Scanning;

/// <summary>What the scanner already knows about a repository when it asks for the graph.</summary>
public sealed record GraphRequest
{
    public required IReadOnlyList<BranchInfo> Branches { get; init; }

    /// <summary>The default branch as a ref name: <c>origin/main</c>, or a local branch when the remote has no HEAD.</summary>
    public string? TrunkRef { get; init; }

    /// <summary>Commits to show although no branch points at them: the HEAD of every working tree, and the commits stashes were made on.</summary>
    public IReadOnlyList<string> ExtraTips { get; init; } = [];

    public bool HasRemotes { get; init; }
}

/// <summary>
/// Reads the compact commit graph the branch map is drawn from, with read-only <c>git log</c> calls:
/// <list type="number">
/// <item>every commit reachable from a local branch, its upstream or a HEAD, but not from the default branch (newest first, capped);</item>
/// <item>the newest commits on the default branch's first-parent line, for context;</item>
/// <item>the commits those hang off (fork points, merged branch tips), without walking further.</item>
/// </list>
/// The result is cached per repository against a hash of its refs, so a repository where nothing moved
/// costs one <c>for-each-ref</c>.
/// </summary>
public sealed class CommitGraphCollector(GitRunner git)
{
    /// <summary>Most commits off the default branch to send for one repository.</summary>
    public const int MaxSideCommits = 250;

    /// <summary>How many of the default branch's newest commits to send.</summary>
    public const int TrunkContext = 20;

    /// <summary>How far down the default branch a fork point's position is still worked out.</summary>
    public const int TrunkDepth = 2000;

    private const int MaxExtraCommits = 400;
    private const int ShasPerCall = 300;
    private const char Sep = '\u001f';

    private readonly ConcurrentDictionary<string, (string Key, CommitGraph Graph)> _cache = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<CommitGraph?> CollectAsync(string path, string commonDir, GraphRequest request, TimeSpan timeout, CancellationToken ct)
    {
        // A repository and its linked worktrees are scanned side by side: the first one reads the graph, the rest reuse it.
        var gate = _locks.GetOrAdd(commonDir, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CollectCoreAsync(path, commonDir, request, timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CommitGraph?> CollectCoreAsync(string path, string commonDir, GraphRequest request, TimeSpan timeout, CancellationToken ct)
    {
        Task<GitResult> Run(IReadOnlyList<string> args) => git.RunAsync(path, args, timeout, ct);

        var refList = await Run(["for-each-ref", "--format=%(objectname)%1f%(refname)%1f%(refname:short)", "refs/heads", "refs/remotes"]).ConfigureAwait(false);
        if (!refList.Ok)
        {
            return null;
        }

        var key = Hash($"{refList.StdOut}\n{request.TrunkRef}\n{string.Join(' ', request.ExtraTips)}\n{request.HasRemotes}");
        if (_cache.TryGetValue(commonDir, out var cached) && cached.Key == key)
        {
            return cached.Graph;
        }

        // Short name to sha. A local branch wins over a remote-tracking ref that shortens to the same name.
        var local = new Dictionary<string, string>(StringComparer.Ordinal);
        var remote = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in refList.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.TrimEnd('\r').Split(Sep);
            if (f.Length == 3 && CommitLogParser.IsSha(f[0]))
            {
                (f[1].StartsWith("refs/heads/", StringComparison.Ordinal) ? local : remote)[f[2]] = f[0];
            }
        }

        // The remote-tracking refs worth showing: each branch's upstream and the default branch's remote ref.
        var remoteRefs = new List<GraphRef>();
        void AddRemoteRef(string? name)
        {
            if (name is not null && remote.TryGetValue(name, out var sha) && remoteRefs.All(r => r.Name != name))
            {
                remoteRefs.Add(new GraphRef { Name = name, Sha = sha });
            }
        }

        AddRemoteRef(request.TrunkRef);
        foreach (var branch in request.Branches)
        {
            AddRemoteRef(branch.Upstream);
        }

        string? trunkSha = null;
        if (request.TrunkRef is { } trunkRef)
        {
            trunkSha = remote.GetValueOrDefault(trunkRef) ?? local.GetValueOrDefault(trunkRef);
        }

        var localTips = request.Branches.Select(b => b.Sha)
            .Concat(request.ExtraTips)
            .Where(CommitLogParser.IsSha)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var tips = localTips.Concat(remoteRefs.Select(r => r.Sha)).Distinct(StringComparer.Ordinal).ToList();
        if (tips.Count == 0 && trunkSha is null)
        {
            return Remember(commonDir, key, new CommitGraph { TrunkRef = request.TrunkRef });
        }

        // 1. Everything that is not on the default branch yet, newest first so a cap keeps what is recent.
        var commits = new Dictionary<string, GraphCommit>(StringComparer.Ordinal);
        var side = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;
        int? sideCount = null;
        var sideTips = tips.Where(t => t != trunkSha).ToList();
        if (sideTips.Count > 0)
        {
            string[] exclude = trunkSha is null ? [] : ["--not", trunkSha];
            var sideLog = await Run(["log", "--date-order", "--format=" + CommitLogParser.Format, "-n", (MaxSideCommits + 1).ToString(CultureInfo.InvariantCulture), .. sideTips, .. exclude]).ConfigureAwait(false);
            if (!sideLog.Ok)
            {
                return null;
            }

            var parsed = CommitLogParser.Parse(sideLog.StdOut);
            truncated = parsed.Count > MaxSideCommits;
            foreach (var commit in parsed.Take(MaxSideCommits))
            {
                commits[commit.Sha] = commit;
                side.Add(commit.Sha);
            }

            if (truncated)
            {
                var count = await Run(["rev-list", "--count", .. sideTips, .. exclude]).ConfigureAwait(false);
                if (count.Ok && int.TryParse(count.StdOut.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    sideCount = n;
                }
            }
        }

        // 2. Where each commit of the default branch's first-parent line sits, counted from the root so
        //    that two machines which fetched at different times agree.
        var trunkPos = new Dictionary<string, int>(StringComparer.Ordinal);
        var trunkTop = new List<string>();
        if (trunkSha is not null)
        {
            var line = await Run(["log", "--first-parent", "--format=%H", "-n", TrunkDepth.ToString(CultureInfo.InvariantCulture), trunkSha]).ConfigureAwait(false);
            var chain = line.Ok ? CommitLogParser.ParseShas(line.StdOut) : [];
            var length = chain.Count;
            if (chain.Count == TrunkDepth)
            {
                var count = await Run(["rev-list", "--count", "--first-parent", trunkSha]).ConfigureAwait(false);
                length = count.Ok && int.TryParse(count.StdOut.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
            }

            if (length >= chain.Count)
            {
                for (var i = 0; i < chain.Count; i++)
                {
                    trunkPos[chain[i]] = length - i;
                }
            }

            trunkTop = chain.Take(TrunkContext).ToList();
        }

        // 3. The commits the rest hangs off: default-branch context, branch tips that are already merged,
        //    and the parents side commits fork from. Read one by one, never walked.
        var wanted = trunkTop
            .Concat(tips)
            .Concat(commits.Values.SelectMany(c => c.Parents))
            .Where(sha => !commits.ContainsKey(sha))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxExtraCommits)
            .ToList();
        foreach (var chunk in wanted.Chunk(ShasPerCall))
        {
            var extra = await Run(["log", "--no-walk=unsorted", "--format=" + CommitLogParser.Format, .. chunk]).ConfigureAwait(false);
            if (!extra.Ok)
            {
                continue;
            }

            foreach (var commit in CommitLogParser.Parse(extra.StdOut))
            {
                commits.TryAdd(commit.Sha, commit);
            }
        }

        // 4. Which of them exist on no remote at all.
        var unpushed = new HashSet<string>(StringComparer.Ordinal);
        if (request.HasRemotes && localTips.Count > 0)
        {
            var only = await Run(["log", "--format=%H", "-n", "1000", .. localTips, "--not", "--remotes"]).ConfigureAwait(false);
            if (only.Ok)
            {
                unpushed.UnionWith(CommitLogParser.ParseShas(only.StdOut));
            }
        }

        var list = commits.Values
            .Select(c => c with
            {
                TrunkPos = trunkPos.TryGetValue(c.Sha, out var pos) ? pos : null,
                // Whatever is not off the default branch is on it. With a cut-off list that is no longer certain.
                InTrunk = trunkSha is not null && !truncated && !side.Contains(c.Sha) && !trunkPos.ContainsKey(c.Sha),
                Unpushed = unpushed.Contains(c.Sha),
            })
            .ToList();

        return Remember(commonDir, key, new CommitGraph
        {
            TrunkRef = trunkSha is null ? null : request.TrunkRef,
            Commits = list,
            RemoteRefs = remoteRefs,
            Truncated = truncated,
            SideCommitCount = sideCount,
        });
    }

    private CommitGraph Remember(string commonDir, string key, CommitGraph graph)
    {
        _cache[commonDir] = (key, graph);
        return graph;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
