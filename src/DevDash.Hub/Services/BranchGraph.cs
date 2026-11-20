using DevDash.Contracts;
using DevDash.Core.Git;
using DevDash.Core.GitHub;

namespace DevDash.Hub.Services;

/// <summary>Whether a device has a commit, and how.</summary>
public enum MapPresence
{
    /// <summary>Not on the device, as far as its agent has reported.</summary>
    None,

    /// <summary>Fetched: reachable from a remote-tracking ref, but from no local branch or checkout.</summary>
    Fetched,

    /// <summary>On a local branch or checked out.</summary>
    Local,
}

public enum MapRowKind
{
    Commit,

    /// <summary>Commits of the default branch that no agent sent: "… 212 commits".</summary>
    Gap,

    /// <summary>A plain run of known commits, collapsed. Can be expanded.</summary>
    Fold,

    /// <summary>Uncommitted changes sitting on a checked out commit.</summary>
    Ghost,
}

public enum MapLineStyle
{
    Solid,
    Dashed,
}

public enum MapRefKind
{
    Local,
    Remote,
    Detached,
}

/// <summary>Colour keys that are not a source index.</summary>
public static class MapColor
{
    /// <summary>On more than one device.</summary>
    public const int Shared = -1;

    /// <summary>On no device's local branches: only known from a remote.</summary>
    public const int Remote = -2;
}

/// <summary>A piece of a lane in one row. <see cref="Color"/> is a source index or a <see cref="MapColor"/> key.</summary>
public sealed record MapLine(int Lane, int Color, MapLineStyle Style);

/// <summary>One repository on one machine: a "device" in the map. Two clones on one machine are two sources.</summary>
public sealed record MapSource
{
    public required string Id { get; init; }
    public required string MachineId { get; init; }
    public required string Label { get; init; }

    /// <summary>Index into the device palette. Follows the machine, so a machine keeps its colour from repo to repo.</summary>
    public int Color { get; init; }

    /// <summary>False when the machine's agent is too old to send a commit graph.</summary>
    public bool HasGraph { get; init; }
    public string AgentVersion { get; init; } = "";
    public bool Visible { get; init; } = true;
    public DateTimeOffset? LastFetchAt { get; init; }
    public int Branches { get; init; }

    /// <summary>Commits on this device that are on no remote.</summary>
    public int Unpushed { get; init; }

    /// <summary>Working trees with uncommitted changes.</summary>
    public int Dirty { get; init; }

    /// <summary>Branches that could not be drawn because their commit is unknown to the hub.</summary>
    public int Unplaced { get; init; }

    /// <summary>The agent had more commits off the default branch than it sends.</summary>
    public bool Truncated { get; init; }
    public int? SideCommitCount { get; init; }
}

/// <summary>A working tree of a source: the main one or a linked worktree.</summary>
public sealed record MapTree
{
    public int Source { get; init; }
    public required string Label { get; init; }
    public required string Path { get; init; }
    public string? Branch { get; init; }
    public string? Head { get; init; }
    public bool Detached { get; init; }
    public bool Linked { get; init; }
    public int Staged { get; init; }
    public int Unstaged { get; init; }
    public int Untracked { get; init; }
    public int Conflicted { get; init; }
    public DateTimeOffset? ChangedAt { get; init; }

    public int Changes => Staged + Unstaged + Untracked + Conflicted;
}

/// <summary>A name pointing at a commit, with the devices it does so on.</summary>
public sealed record MapRef
{
    public MapRefKind Kind { get; init; }
    public required string Name { get; init; }

    /// <summary>Source indexes where this name points at this commit.</summary>
    public IReadOnlyList<int> Sources { get; init; } = [];

    /// <summary>Source indexes where it is checked out.</summary>
    public IReadOnlyList<int> HeadOf { get; init; } = [];
    public bool IsDefault { get; init; }
    public bool Merged { get; init; }
    public bool UpstreamGone { get; init; }
    public bool NoUpstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }

    /// <summary>The same name points at another commit on another device.</summary>
    public bool Split { get; init; }

    /// <summary>Split, and neither device simply has to catch up: each has commits the other lacks.</summary>
    public bool Diverged { get; init; }

    /// <summary>For a split remote ref: when the devices holding this one last fetched.</summary>
    public DateTimeOffset? FetchedAt { get; init; }

    /// <summary>For a detached HEAD: the working tree.</summary>
    public string? Tree { get; init; }
}

public sealed record MapStash(int Source, StashInfo Stash);

public sealed record MapRow
{
    public required string Key { get; init; }
    public MapRowKind Kind { get; init; }
    public int Lane { get; init; }
    public int Color { get; init; }
    public GraphCommit? Commit { get; init; }
    public DateTimeOffset? Date { get; init; }

    /// <summary>One entry per source, in <see cref="BranchMap.Sources"/> order.</summary>
    public IReadOnlyList<MapPresence> Presence { get; init; } = [];
    public bool Unpushed { get; init; }
    public bool IsMerge { get; init; }
    public bool OnTrunk { get; init; }

    /// <summary>History that is already merged and only shown because a branch still points at it.</summary>
    public bool Dim { get; init; }
    public IReadOnlyList<MapRef> Refs { get; init; } = [];
    public IReadOnlyList<MapStash> Stashes { get; init; } = [];
    public IReadOnlyList<PullRequestInfo> PullRequests { get; init; } = [];

    /// <summary>Working trees checked out at this commit.</summary>
    public IReadOnlyList<MapTree> Heads { get; init; } = [];

    /// <summary>For a ghost row: the working tree with the changes.</summary>
    public MapTree? Tree { get; init; }

    /// <summary>For a gap or fold: how many commits it stands for. 0 when unknown.</summary>
    public int Hidden { get; init; }

    /// <summary>For a gap: it is the end of the map ("older commits").</summary>
    public bool Older { get; init; }

    // ---- geometry ----

    /// <summary>The node's lane has a line arriving from the row above.</summary>
    public bool Up { get; init; }

    /// <summary>The node's lane continues to the row below.</summary>
    public MapLine? Down { get; init; }

    /// <summary>The commit has parents, but none are in the map.</summary>
    public bool Stub { get; init; }

    /// <summary>Lanes that pass this row untouched.</summary>
    public IReadOnlyList<MapLine> Through { get; init; } = [];

    /// <summary>Lanes from above that end at this node: children on other lanes.</summary>
    public IReadOnlyList<MapLine> In { get; init; } = [];

    /// <summary>Lines leaving this node for another lane below: the other parents of a merge.</summary>
    public IReadOnlyList<MapLine> Out { get; init; } = [];
}

/// <summary>A branch that is not at the same commit on every device.</summary>
public sealed record MapWarning(string Branch, bool Diverged, string Text);

public sealed record BranchMapOptions
{
    /// <summary><see cref="MapSource.Id"/>s switched off in the legend.</summary>
    public IReadOnlySet<string> HiddenSources { get; init; } = new HashSet<string>();
    public bool HideMerged { get; init; }
    public bool ExpandAll { get; init; }

    /// <summary>Keys of fold rows the user opened.</summary>
    public IReadOnlySet<string> Expanded { get; init; } = new HashSet<string>();

    /// <summary>All machine ids the hub knows, in display order: the palette follows it. Defaults to the machines of this repo.</summary>
    public IReadOnlyList<string>? MachineOrder { get; init; }
}

public sealed record BranchMap
{
    public IReadOnlyList<MapSource> Sources { get; init; } = [];
    public IReadOnlyList<MapRow> Rows { get; init; } = [];
    public int LaneCount { get; init; }
    public IReadOnlyList<MapWarning> Warnings { get; init; } = [];

    /// <summary>Merged branches there are (hidden or dimmed).</summary>
    public int MergedBranches { get; init; }

    /// <summary>Open pull requests whose branch is not on any device.</summary>
    public IReadOnlyList<PullRequestInfo> UnplacedPullRequests { get; init; } = [];
    public bool HasFolds { get; init; }

    /// <summary>Some merged commit is tied to its merge by a dashed line: the commits in between were not sent.</summary>
    public bool HasJoinLinks { get; init; }

    /// <summary>
    /// The branch the map is drawn along, without its remote: usually the default branch, or
    /// <c>develop</c> in a git-flow repository. Null when no device sent a graph.
    /// </summary>
    public string? TrunkName { get; init; }

    public bool HasGraph => Sources.Any(s => s.HasGraph);
}

/// <summary>
/// Merges the commit graphs of every device that has a repository into one picture: which commits exist,
/// which device has each, where every branch, HEAD and remote-tracking ref points, and a row and lane
/// for each commit. Pure: the page only draws what this returns.
/// </summary>
public static class BranchGraph
{
    /// <summary>A plain run of this many commits or more is folded.</summary>
    public const int FoldMinRun = 6;
    private const int FoldHead = 2;
    private const int FoldTail = 1;

    private enum NodeKind
    {
        Commit,
        Gap,
        Ghost,
    }

    private sealed class Node
    {
        public required string Id;
        public NodeKind Kind;
        public GraphCommit? Commit;
        public int? Pos;
        public bool InTrunk;
        public bool Trunkish;
        public int UnpushedBy;
        public int PushedBy;
        public HashSet<int> ReportedBy = [];
        public List<Node> Parents = [];
        public List<Node> Children = [];

        /// <summary>The trunk commit that first contains this one, as an agent reported it.</summary>
        public string? Join;

        /// <summary>Parents this node is only known to lead to: the commits in between were not sent. Drawn dashed.</summary>
        public HashSet<Node>? Links;
        public bool Visible;
        public bool Stub;
        public int Hidden;
        public bool Older;
        public DateTimeOffset Date;
        public MapPresence[] Presence = [];
        public int Color;
        public bool Unpushed;
        public MapTree? Tree;
        public int Row = -1;
        public int Lane;
        public List<MapRef> Refs = [];
        public List<MapStash> Stashes = [];
        public List<PullRequestInfo> Prs = [];
        public List<MapTree> Heads = [];

        public bool OnTrunk => Pos is not null || (Kind == NodeKind.Gap);
        public bool Decorated => Refs.Count > 0 || Stashes.Count > 0 || Prs.Count > 0 || Heads.Count > 0;
    }

    private sealed class Source
    {
        public int Index;
        public required string Id;
        public required string MachineId;
        public required string Label;
        public int Color;
        public bool Visible;
        public required GitState Git;
        public CommitGraph? Graph;

        /// <summary>The branch this device's graph is drawn along, without its remote.</summary>
        public string? TrunkName;
        public string AgentVersion = "";
        public List<MapTree> Trees = [];
        public List<BranchInfo> Branches = [];

        /// <summary>The remote-tracking refs to show: all of the graph's, less the upstreams of hidden merged branches.</summary>
        public List<GraphRef> RemoteRefs = [];
        public int Unplaced;
    }

    private sealed class LaneState
    {
        public required Node Expect;
        public int Color;
        public MapLineStyle Style;
        public bool FirstParent;
    }

    public static BranchMap Build(IReadOnlyList<CloneView> clones, IReadOnlyList<PullRequestInfo> pullRequests, BranchMapOptions? options = null)
    {
        options ??= new BranchMapOptions();
        var sources = BuildSources(clones, options);
        if (sources.Count == 0)
        {
            return new BranchMap();
        }

        var shown = sources.Where(s => s.Visible).ToList();
        int mergedBranches = sources.Sum(s => s.Git.Branches.Count(b => IsMergedAway(s, b)));
        foreach (var source in sources)
        {
            source.Branches = source.Git.Branches.Where(b => !options.HideMerged || !IsMergedAway(source, b)).ToList();
            source.RemoteRefs = (source.Graph?.RemoteRefs ?? []).ToList();
        }

        // ---- 1. every commit any shown device reported, joined by sha ----
        var union = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var source in shown)
        {
            foreach (var commit in source.Graph?.Commits ?? [])
            {
                if (!union.TryGetValue(commit.Sha, out var node))
                {
                    union[commit.Sha] = node = new Node { Id = commit.Sha, Commit = commit, Date = commit.Date };
                }

                node.Pos = commit.TrunkPos is { } pos ? Math.Max(pos, node.Pos ?? 0) : node.Pos;
                node.InTrunk |= commit.InTrunk;
                node.Join ??= commit.JoinsTrunkAt;
                node.ReportedBy.Add(source.Index);
                if (commit.Unpushed)
                {
                    node.UnpushedBy++;
                }
                else
                {
                    node.PushedBy++;
                }
            }
        }

        // Two commits cannot share a place on the default branch. If devices disagree about what the
        // default branch is, the line more of them reported wins and the other is drawn as a branch.
        foreach (var clash in union.Values.Where(n => n.Pos is not null).GroupBy(n => n.Pos).Where(g => g.Count() > 1))
        {
            foreach (var loser in clash.OrderByDescending(n => n.ReportedBy.Count).ThenBy(n => n.Id, StringComparer.Ordinal).Skip(1))
            {
                loser.Pos = null;
            }
        }

        Node? Find(string? sha) => sha is not null && union.TryGetValue(sha, out var node) ? node : null;

        // The upstream of a hidden merged branch goes with it, unless it has moved on to commits that are not merged.
        if (options.HideMerged)
        {
            foreach (var source in sources)
            {
                var kept = source.Branches.Select(b => b.Upstream).OfType<string>().ToHashSet(StringComparer.Ordinal);
                var gone = source.Git.Branches.Where(b => IsMergedAway(source, b)).Select(b => b.Upstream).OfType<string>().ToHashSet(StringComparer.Ordinal);
                source.RemoteRefs.RemoveAll(r => gone.Contains(r.Name) && !kept.Contains(r.Name) && r.Name != source.Graph!.TrunkRef
                    && Find(r.Sha) is { } at && (at.InTrunk || at.Pos is not null));
            }
        }

        // ---- 2. keep what a shown ref leads to ----
        var roots = new List<Node>();
        foreach (var source in shown)
        {
            foreach (var branch in source.Branches)
            {
                if (Find(branch.Sha) is { } node)
                {
                    roots.Add(node);
                }
                else
                {
                    source.Unplaced++;
                }
            }

            roots.AddRange(source.Trees.Select(t => Find(t.Head)).OfType<Node>());
            roots.AddRange(source.RemoteRefs.Select(r => Find(r.Sha)).OfType<Node>());
            roots.AddRange(source.Git.Stashes.Select(s => Find(s.BaseSha)).OfType<Node>());
        }

        static bool MergedIn(Node n) => n is { InTrunk: true, Pos: null };

        var pending = new Stack<Node>(roots);
        while (pending.TryPop(out var node))
        {
            if (node.Visible)
            {
                continue;
            }

            node.Visible = true;
            foreach (var parent in node.Commit!.Parents)
            {
                // With merged branches hidden, a merge on the trunk does not pull in what it merged:
                // that history is only shown where something still shown needs it (below).
                if (Find(parent) is { Visible: false } next && !(options.HideMerged && node.Pos is not null && MergedIn(next)))
                {
                    pending.Push(next);
                }
            }
        }

        if (options.HideMerged)
        {
            ShowWaysUp(union);
        }

        // A merged commit whose way up was not sent still knows which trunk commit it joined at.
        foreach (var node in union.Values.Where(n => n.Visible && n.Join is not null).ToList())
        {
            if (Find(node.Join) is { Pos: not null } join)
            {
                join.Visible = true;
            }
        }

        var nodes = union.Values.Where(n => n.Visible).ToList();

        // ---- 3. edges: real parents, and the default branch strung together across what was not sent ----
        var trunk = nodes.Where(n => n.Pos is not null).OrderByDescending(n => n.Pos).ToList();
        var nextOnTrunk = new Dictionary<Node, Node>();
        for (var i = 0; i < trunk.Count; i++)
        {
            var above = trunk[i];
            if (i + 1 < trunk.Count)
            {
                var below = trunk[i + 1];
                if (above.Commit!.Parents.FirstOrDefault() == below.Id)
                {
                    nextOnTrunk[above] = below;
                }
                else
                {
                    var gap = new Node { Id = "gap:" + above.Id, Kind = NodeKind.Gap, Hidden = Math.Max(0, above.Pos!.Value - below.Pos!.Value - 1), Date = below.Date, Visible = true };
                    gap.Parents.Add(below);
                    nodes.Add(gap);
                    nextOnTrunk[above] = gap;
                }
            }
            else if (above.Pos > 1)
            {
                var older = new Node { Id = "older", Kind = NodeKind.Gap, Hidden = above.Pos.Value - 1, Older = true, Date = DateTimeOffset.MinValue, Visible = true };
                nodes.Add(older);
                nextOnTrunk[above] = older;
            }
        }

        foreach (var node in nodes.Where(n => n.Kind == NodeKind.Commit))
        {
            var real = node.Commit!.Parents.Select(Find).OfType<Node>().Where(p => p.Visible).ToList();
            if (nextOnTrunk.TryGetValue(node, out var next))
            {
                node.Parents.Add(next);
                node.Parents.AddRange(real.Where(p => p != next && !(next.Kind == NodeKind.Gap && p.Pos is not null)));
            }
            else
            {
                node.Parents.AddRange(real);
                node.Stub = node.Commit.Parents.Count > 0 && real.Count == 0;
            }
        }

        LinkToJoins(nodes, trunk, Find);

        // Uncommitted work is drawn as a commit that does not exist yet, on top of the HEAD it sits on.
        foreach (var source in shown)
        {
            foreach (var tree in source.Trees.Where(t => t.Changes > 0))
            {
                if (Find(tree.Head) is { Visible: true } head)
                {
                    var ghost = new Node { Id = $"ghost:{source.Id}:{tree.Path}", Kind = NodeKind.Ghost, Tree = tree, Date = tree.ChangedAt ?? head.Date, Visible = true };
                    ghost.Parents.Add(head);
                    nodes.Add(ghost);
                }
            }
        }

        foreach (var node in nodes)
        {
            node.Presence = new MapPresence[sources.Count];
            foreach (var parent in node.Parents)
            {
                parent.Children.Add(node);
            }
        }

        // ---- 4. who has what ----
        var onRemote = new HashSet<Node>();
        foreach (var source in shown)
        {
            var localTips = source.Git.Branches.Select(b => Find(b.Sha))
                .Concat(source.Trees.Select(t => Find(t.Head)))
                .OfType<Node>()
                .ToList();
            var remoteTips = source.RemoteRefs.Select(r => Find(r.Sha)).OfType<Node>().ToList();
            var local = Reach(
                localTips.Where(n => n.Visible)
                    .Concat(nodes.Where(n => n.Kind == NodeKind.Ghost && n.Tree!.Source == source.Index))
                    .Concat(TrunkAtOrBelow(trunk, localTips)));
            var hasTrunkLocally = localTips.Any(n => n.Pos is not null) || local.Any(n => n.Pos is not null);
            var remote = Reach(remoteTips.Where(n => n.Visible).Concat(TrunkAtOrBelow(trunk, remoteTips)));
            onRemote.UnionWith(remote);
            var known = Reach(nodes.Where(n => n.ReportedBy.Contains(source.Index)).Concat(remote));
            var hasTrunk = hasTrunkLocally || known.Any(n => n.Pos is not null);

            foreach (var node in nodes)
            {
                node.Presence[source.Index] =
                    local.Contains(node) || (hasTrunkLocally && node is { InTrunk: true, Pos: null }) ? MapPresence.Local
                    : known.Contains(node) || (hasTrunk && node is { InTrunk: true, Pos: null }) ? MapPresence.Fetched
                    : MapPresence.None;
            }
        }

        foreach (var node in nodes)
        {
            var owners = Enumerable.Range(0, sources.Count).Where(i => node.Presence[i] == MapPresence.Local).ToList();
            node.Color = node.Kind == NodeKind.Ghost ? node.Tree!.Source
                : owners.Count == 0 ? MapColor.Remote
                : owners.Count == 1 ? owners[0]
                : MapColor.Shared;
            node.Unpushed = node.Kind == NodeKind.Commit && node.UnpushedBy > 0 && node.PushedBy == 0 && !onRemote.Contains(node);
        }

        // Commits a device made on top of the default branch and has not pushed continue the trunk's own lane.
        foreach (var source in shown)
        {
            var tip = source.Branches.FirstOrDefault(b => b.Name == source.Git.DefaultBranch);
            var line = new List<Node>();
            for (var node = Find(tip?.Sha); node is { Visible: true, Pos: null, Trunkish: false }; node = node.Parents.FirstOrDefault())
            {
                line.Add(node);
                if (node.Parents.FirstOrDefault() is { } parent && (parent.Trunkish || (trunk.Count > 0 && parent == trunk[0])))
                {
                    line.ForEach(n => n.Trunkish = true);
                    break;
                }
            }
        }

        // ---- 5. names on commits ----
        var warnings = PlaceRefs(shown, Find);
        foreach (var source in shown)
        {
            foreach (var stash in source.Git.Stashes)
            {
                if (Find(stash.BaseSha) is { Visible: true } node)
                {
                    node.Stashes.Add(new MapStash(source.Index, stash));
                }
            }

            foreach (var tree in source.Trees)
            {
                if (Find(tree.Head) is { Visible: true } node)
                {
                    node.Heads.Add(tree);
                }
            }
        }

        // ---- 6. rows, then lanes ----
        var order = Order(nodes);
        var unplacedPrs = PlacePullRequests(pullRequests, order);
        var rows = AssignLanes(order, hasTrunk: trunk.Count > 0, out var laneCount);
        var hasFolds = Fold(rows, order, new HashSet<string>()).Count != rows.Count;
        var folded = options.ExpandAll ? rows : Fold(rows, order, options.Expanded);

        return new BranchMap
        {
            Sources = sources.Select(s => new MapSource
            {
                Id = s.Id,
                MachineId = s.MachineId,
                Label = s.Label,
                Color = s.Color,
                HasGraph = s.Graph is not null,
                AgentVersion = s.AgentVersion,
                Visible = s.Visible,
                LastFetchAt = s.Git.LastFetchAt,
                Branches = s.Git.Branches.Count,
                Unpushed = nodes.Count(n => n.Unpushed && n.Presence[s.Index] == MapPresence.Local),
                Dirty = s.Trees.Count(t => t.Changes > 0),
                Unplaced = s.Unplaced,
                Truncated = s.Graph?.Truncated ?? false,
                SideCommitCount = s.Graph?.SideCommitCount,
            }).ToList(),
            Rows = folded,
            TrunkName = TrunkNameOf(sources.Select(s => s.Graph?.TrunkRef)),
            LaneCount = laneCount,
            Warnings = warnings,
            MergedBranches = mergedBranches,
            UnplacedPullRequests = unplacedPrs,
            HasFolds = hasFolds,
            HasJoinLinks = nodes.Any(n => n.Links is not null),
        };
    }

    /// <summary>
    /// With merged branches hidden, merged history is not followed down from the trunk. What is still
    /// shown inside it (a branch somebody has checked out, the commit an open branch forks from) keeps
    /// its way up to the merge that brought it in, when the agents sent one.
    /// </summary>
    private static void ShowWaysUp(Dictionary<string, Node> union)
    {
        static bool MergedIn(Node n) => n is { InTrunk: true, Pos: null };

        var childrenOf = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
        foreach (var node in union.Values)
        {
            foreach (var parent in node.Commit!.Parents)
            {
                if (!childrenOf.TryGetValue(parent, out var list))
                {
                    childrenOf[parent] = list = [];
                }

                list.Add(node);
            }
        }

        foreach (var start in union.Values.Where(n => n.Visible && MergedIn(n)).OrderByDescending(n => n.Date).ThenBy(n => n.Id, StringComparer.Ordinal).ToList())
        {
            // Breadth first through merged history, to the oldest trunk commit that can be reached.
            var from = new Dictionary<Node, Node?> { [start] = null };
            var queue = new Queue<Node>([start]);
            Node? target = null;
            while (queue.TryDequeue(out var node))
            {
                foreach (var child in childrenOf.GetValueOrDefault(node.Id) ?? [])
                {
                    if (from.ContainsKey(child))
                    {
                        continue;
                    }

                    if (child.Pos is not null)
                    {
                        if (target is null || child.Pos < target.Pos)
                        {
                            from[child] = node;
                            target = child;
                        }
                    }
                    else if (MergedIn(child))
                    {
                        from[child] = node;
                        queue.Enqueue(child);
                    }
                }
            }

            for (var node = target; node is not null; node = from[node])
            {
                node.Visible = true;
            }
        }
    }

    /// <summary>
    /// A merged commit with no line up to the trunk (its merge is many commits away, so the agent sent
    /// only where it joins) is hung under that trunk commit.
    /// </summary>
    private static void LinkToJoins(List<Node> nodes, List<Node> trunk, Func<string?, Node?> find)
    {
        if (trunk.Count == 0)
        {
            return;
        }

        var anchored = Reach([trunk[0]]);
        foreach (var node in nodes.Where(n => n is { Kind: NodeKind.Commit, Join: not null, Pos: null }).OrderByDescending(n => n.Date).ThenBy(n => n.Id, StringComparer.Ordinal).ToList())
        {
            if (anchored.Contains(node) || find(node.Join) is not { Visible: true, Pos: not null } join)
            {
                continue;
            }

            join.Parents.Add(node);
            (join.Links ??= []).Add(node);
            anchored.UnionWith(Reach([node]));
        }
    }

    /// <summary>A merged branch that nothing is checked out on: the ones "hide merged" hides.</summary>
    private static bool IsMergedAway(Source source, BranchInfo branch) =>
        IsMerged(source, branch) && source.Trees.All(t => t.Branch != branch.Name);

    /// <summary>
    /// Merged, as the map means it: into the line the map is drawn along. That is the default branch
    /// unless the agent draws along another (develop, in git-flow) and says what is merged into that.
    /// Neither the default branch nor the trunk itself ever counts as a merged branch.
    /// </summary>
    private static bool IsMerged(Source source, BranchInfo branch) =>
        (branch.MergedIntoTrunk ?? branch.MergedIntoDefault) == true
        && branch.Name != source.Git.DefaultBranch
        && branch.Name != source.TrunkName;

    /// <summary>The trunk most devices reported, as a branch name: <c>origin/develop</c> becomes <c>develop</c>.</summary>
    public static string? TrunkNameOf(IEnumerable<string?> trunkRefs)
    {
        var top = trunkRefs.OfType<string>()
            .Select(r => r.IndexOf('/') is var i and >= 0 ? r[(i + 1)..] : r)
            .GroupBy(n => n, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        return top?.Key;
    }

    private static List<Source> BuildSources(IReadOnlyList<CloneView> clones, BranchMapOptions options)
    {
        var groups = clones
            .Where(c => c.Git is not null)
            .GroupBy(c => (c.Machine.MachineId, Dir: c.Git!.CommonDir ?? c.Entry.Path))
            .Select(g => g.OrderBy(c => c.Git!.IsLinkedWorktree).ThenBy(c => c.Entry.Path, StringComparer.Ordinal).ToList())
            .OrderBy(g => g[0].Machine.Machine.Hostname, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g[0].Entry.Path, StringComparer.Ordinal)
            .ToList();

        var machineOrder = (options.MachineOrder ?? []).Concat(groups.Select(g => g[0].Machine.MachineId)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var perMachine = groups.GroupBy(g => g[0].Machine.MachineId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var spare = machineOrder.Count;

        var sources = new List<Source>();
        foreach (var group in groups)
        {
            var main = group[0];
            var git = main.Git!;
            var host = ShortHost(main.Machine.Machine.Hostname);
            var id = $"{main.Machine.MachineId}|{git.CommonDir ?? main.Entry.Path}";
            var source = new Source
            {
                Index = sources.Count,
                Id = id,
                MachineId = main.Machine.MachineId,
                Label = perMachine[main.Machine.MachineId] > 1 ? $"{host} · {main.DisplayName}" : host,
                // A second clone on the same machine takes a colour no machine uses.
                Color = seen.Add(main.Machine.MachineId) ? machineOrder.FindIndex(m => m.Equals(main.Machine.MachineId, StringComparison.OrdinalIgnoreCase)) : spare++,
                Visible = !options.HiddenSources.Contains(id),
                Git = git,
                Graph = group.Select(c => c.Git!.Graph).FirstOrDefault(g => g is not null),
                AgentVersion = main.Machine.Machine.AgentVersion,
            };
            source.TrunkName = TrunkNameOf([source.Graph?.TrunkRef]);

            foreach (var clone in group)
            {
                var g = clone.Git!;
                source.Trees.Add(new MapTree
                {
                    Source = source.Index,
                    Label = clone.DisplayName,
                    Path = clone.Entry.Path,
                    Branch = g.Detached ? null : g.Branch,
                    Head = g.HeadSha,
                    Detached = g.Detached,
                    Linked = g.IsLinkedWorktree,
                    Staged = g.Staged,
                    Unstaged = g.Unstaged,
                    Untracked = g.Untracked,
                    Conflicted = g.Conflicted,
                    ChangedAt = Latest(g.NewestTrackedChangeAt, g.NewestUntrackedAt),
                });
            }

            // Worktrees the agent knows of but does not scan (outside its roots): no change counts, but a HEAD.
            foreach (var (worktree, index) in git.Worktrees.Select((w, i) => (w, i)))
            {
                if (worktree.Bare || worktree.Prunable || worktree.Head is null || source.Trees.Any(t => SamePath(t.Path, worktree.Path)))
                {
                    continue;
                }

                source.Trees.Add(new MapTree
                {
                    Source = source.Index,
                    Label = worktree.Path.TrimEnd('/', '\\').Split('/', '\\')[^1],
                    Path = worktree.Path,
                    Branch = worktree.Detached ? null : worktree.Branch,
                    Head = worktree.Head,
                    Detached = worktree.Detached,
                    Linked = index > 0,
                });
            }

            sources.Add(source);
        }

        return sources;
    }

    /// <summary>Puts branch, remote and HEAD names on their commits and reports branches that differ between devices.</summary>
    private static List<MapWarning> PlaceRefs(List<Source> shown, Func<string?, Node?> find)
    {
        var warnings = new List<MapWarning>();

        // What git itself counted: a branch at one commit whose upstream is at another, on some device.
        var counted = new Dictionary<(string Tip, string Other), (int TipOnly, int OtherOnly)>();
        foreach (var source in shown)
        {
            var refs = (source.Graph?.RemoteRefs ?? []).GroupBy(r => r.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Sha, StringComparer.Ordinal);
            foreach (var branch in source.Git.Branches)
            {
                if (branch is { Upstream: { } upstream, UpstreamGone: false } && refs.TryGetValue(upstream, out var other) && other != branch.Sha)
                {
                    counted[(branch.Sha, other)] = (branch.Ahead, branch.Behind);
                }
            }
        }

        var locals = shown.SelectMany(s => s.Branches.Select(b => (Source: s, Branch: b))).GroupBy(x => x.Branch.Name, StringComparer.Ordinal);
        foreach (var name in locals)
        {
            var tips = name.GroupBy(x => x.Branch.Sha, StringComparer.Ordinal).ToList();
            var warning = tips.Count > 1 ? Describe(name.Key, tips.Select(t => (find(t.Key), Labels: t.Select(x => x.Source.Label).ToList())).ToList(), counted, find) : null;
            if (warning is not null)
            {
                warnings.Add(warning);
            }

            foreach (var tip in tips)
            {
                if (find(tip.Key) is not { Visible: true } node)
                {
                    continue;
                }

                var first = tip.First().Branch;
                node.Refs.Add(new MapRef
                {
                    Kind = MapRefKind.Local,
                    Name = name.Key,
                    Sources = tip.Select(x => x.Source.Index).ToList(),
                    HeadOf = tip.Where(x => x.Source.Trees.Any(t => t.Branch == name.Key)).Select(x => x.Source.Index).ToList(),
                    IsDefault = tip.Any(x => x.Source.Git.DefaultBranch == name.Key),
                    Merged = tip.All(x => IsMerged(x.Source, x.Branch)),
                    UpstreamGone = tip.Any(x => x.Branch.UpstreamGone),
                    NoUpstream = tip.All(x => x.Branch.Upstream is null),
                    Ahead = tip.Max(x => x.Branch.Ahead),
                    Behind = tip.Max(x => x.Branch.Behind),
                    Split = tips.Count > 1,
                    Diverged = warning is { Diverged: true },
                });
            }
        }

        var remotes = shown.SelectMany(s => s.RemoteRefs.Select(r => (Source: s, Ref: r))).GroupBy(x => x.Ref.Name, StringComparer.Ordinal);
        foreach (var name in remotes)
        {
            var tips = name.GroupBy(x => x.Ref.Sha, StringComparer.Ordinal).ToList();
            foreach (var tip in tips)
            {
                if (find(tip.Key) is not { Visible: true } node)
                {
                    continue;
                }

                node.Refs.Add(new MapRef
                {
                    Kind = MapRefKind.Remote,
                    Name = name.Key,
                    Sources = tip.Select(x => x.Source.Index).ToList(),
                    IsDefault = tip.Any(x => x.Source.Graph!.TrunkRef == name.Key),
                    Split = tips.Count > 1,
                    FetchedAt = tips.Count > 1 ? tip.Min(x => x.Source.Git.LastFetchAt) : null,
                });
            }
        }

        foreach (var tree in shown.SelectMany(s => s.Trees).Where(t => t.Detached))
        {
            if (find(tree.Head) is { Visible: true } node)
            {
                node.Refs.Add(new MapRef { Kind = MapRefKind.Detached, Name = "HEAD", Sources = [tree.Source], HeadOf = [tree.Source], Tree = tree.Label });
            }
        }

        return warnings.OrderByDescending(w => w.Diverged).ThenBy(w => w.Branch, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Says how two devices' copies of a branch differ. The numbers are git's own when some device
    /// counted exactly this pair of commits (a branch against its upstream). Otherwise they are counted
    /// on the map, which holds only part of the history: "at least" unless every commit in between is in it.
    /// </summary>
    private static MapWarning Describe(
        string branch,
        List<(Node? Node, List<string> Labels)> tips,
        Dictionary<(string Tip, string Other), (int TipOnly, int OtherOnly)> counted,
        Func<string?, Node?> find)
    {
        static string Names(List<string> labels) => string.Join(" and ", labels);
        static string Commits(int n, bool exact) => (exact ? "" : "at least ") + (n == 1 ? "1 commit" : $"{n} commits");

        if (tips.Count == 2 && tips[0].Node is { Visible: true } a && tips[1].Node is { Visible: true } b)
        {
            var aAll = Reach([a]);
            var bAll = Reach([b]);
            var aRest = aAll.Where(n => !bAll.Contains(n)).ToList();
            var bRest = bAll.Where(n => !aAll.Contains(n)).ToList();
            var (aOnly, bOnly) = (aRest.Sum(Weight), bRest.Sum(Weight));
            var (aExact, bExact) = (aRest.All(Whole), bRest.All(Whole));
            if (counted.TryGetValue((a.Id, b.Id), out var ab))
            {
                (aOnly, bOnly, aExact, bExact) = (ab.TipOnly, ab.OtherOnly, true, true);
            }
            else if (counted.TryGetValue((b.Id, a.Id), out var ba))
            {
                (aOnly, bOnly, aExact, bExact) = (ba.OtherOnly, ba.TipOnly, true, true);
            }

            var (aNames, bNames) = (Names(tips[0].Labels), Names(tips[1].Labels));
            if (bAll.Contains(a) || (aOnly == 0 && bOnly > 0 && aExact))
            {
                return new MapWarning(branch, false, $"{bNames} {(tips[1].Labels.Count == 1 ? "is" : "are")} {Commits(bOnly, bExact)} ahead of {aNames}.");
            }

            if (aAll.Contains(b) || (bOnly == 0 && aOnly > 0 && bExact))
            {
                return new MapWarning(branch, false, $"{aNames} {(tips[0].Labels.Count == 1 ? "is" : "are")} {Commits(aOnly, aExact)} ahead of {bNames}.");
            }

            return new MapWarning(branch, true, $"has diverged: {aNames} has {Commits(aOnly, aExact)} that {bNames} does not, and {bNames} has {Commits(bOnly, bExact)} of its own.");
        }

        return new MapWarning(branch, true, $"is at {tips.Count} different commits: {string.Join("; ", tips.Select(t => Names(t.Labels)))}.");

        static int Weight(Node node) => node.Kind == NodeKind.Gap ? node.Hidden : node.Kind == NodeKind.Commit ? 1 : 0;

        // A commit is all there when each of its parents is in the map too. A gap stands for the
        // first-parent commits only: whatever those merged is not counted.
        bool Whole(Node node) => node.Kind != NodeKind.Gap && (node.Commit is null || node.Commit.Parents.All(p => find(p) is { Visible: true }));
    }

    /// <summary>
    /// A pull request goes on the commit it is at on GitHub when the map has that commit and a name on
    /// it. Otherwise on the commit its branch points at: the remote branch if a device tracks it, else a
    /// local one of that name. A pull request from a fork is only ever placed by its commit.
    /// </summary>
    private static List<PullRequestInfo> PlacePullRequests(IReadOnlyList<PullRequestInfo> pullRequests, List<Node> order)
    {
        var unplaced = new List<PullRequestInfo>();
        foreach (var pr in pullRequests)
        {
            if (pr.HeadRef is not { Length: > 0 } head)
            {
                continue;
            }

            var home = order.FirstOrDefault(n => n.Kind == NodeKind.Commit && n.Refs.Count > 0 && BranchPullRequests.SameCommit(pr.HeadSha, n.Commit?.Sha) == true);
            if (home is null && !pr.IsCrossRepository)
            {
                home = order.FirstOrDefault(n => n.Refs.Any(r => r.Kind == MapRefKind.Remote && r.Name.EndsWith("/" + head, StringComparison.Ordinal) && r.Name.IndexOf('/') == r.Name.Length - head.Length - 1))
                    ?? order.FirstOrDefault(n => n.Refs.Any(r => r.Kind == MapRefKind.Local && r.Name == head));
            }

            if (home is null)
            {
                unplaced.Add(pr);
            }
            else
            {
                home.Prs.Add(pr);
            }
        }

        return unplaced;
    }

    /// <summary>
    /// Newest first, the way <c>git log --graph --date-order</c> reads: a row only goes in once every row
    /// that descends from it is above it, and among the rows that are free to go next the newest wins.
    /// An uncommitted-changes row or a gap sits right on top of the commit below it. "Older commits" closes the map.
    /// </summary>
    private static List<Node> Order(List<Node> nodes)
    {
        var older = nodes.FirstOrDefault(n => n.Older);
        var inMap = nodes.Where(n => n != older).ToHashSet();

        // When a row is due: a commit at its own date, a ghost or a gap with the commit it sits on.
        DateTimeOffset Due(Node n) => n.Kind == NodeKind.Commit
            ? n.Date
            : n.Parents.Where(inMap.Contains).Select(p => p.Date).DefaultIfEmpty(n.Date).Max();
        static int Rank(Node n) => n.Kind switch { NodeKind.Ghost => 0, NodeKind.Gap => 1, _ => 2 };

        var waiting = new Dictionary<Node, int>();
        foreach (var node in inMap)
        {
            waiting[node] = node.Children.Where(inMap.Contains).Distinct().Count();
        }

        var ready = new PriorityQueue<Node, (long Due, int Rank, string Id)>(
            Comparer<(long Due, int Rank, string Id)>.Create((x, y) =>
                x.Due != y.Due ? y.Due.CompareTo(x.Due)
                : x.Rank != y.Rank ? x.Rank.CompareTo(y.Rank)
                : string.CompareOrdinal(x.Id, y.Id)));
        void Offer(Node n) => ready.Enqueue(n, (Due(n).UtcTicks, Rank(n), n.Id));

        foreach (var (node, count) in waiting)
        {
            if (count == 0)
            {
                Offer(node);
            }
        }

        var order = new List<Node>(nodes.Count);
        while (ready.TryDequeue(out var node, out _))
        {
            order.Add(node);
            foreach (var parent in node.Parents.Where(inMap.Contains).Distinct())
            {
                if (--waiting[parent] == 0)
                {
                    Offer(parent);
                }
            }
        }

        // A cycle cannot happen in git history; if the data is odd anyway, nothing is dropped.
        if (order.Count < inMap.Count)
        {
            var placed = order.ToHashSet();
            order.AddRange(inMap.Where(n => !placed.Contains(n)).OrderByDescending(Due).ThenBy(n => n.Id, StringComparer.Ordinal));
        }

        if (older is not null)
        {
            order.Add(older);
        }

        for (var i = 0; i < order.Count; i++)
        {
            order[i].Row = i;
        }

        return order;
    }

    private static List<MapRow> AssignLanes(List<Node> order, bool hasTrunk, out int laneCount)
    {
        var lanes = new List<LaneState?>();
        if (hasTrunk)
        {
            lanes.Add(null); // lane 0 is the default branch's, whether or not it is in use on a given row
        }

        var rows = new List<MapRow>(order.Count);
        laneCount = hasTrunk ? 1 : 0;

        foreach (var node in order)
        {
            var incoming = Enumerable.Range(0, lanes.Count).Where(i => lanes[i]?.Expect == node).ToList();
            var freedHere = new HashSet<int>();

            int Allocate()
            {
                for (var i = hasTrunk ? 1 : 0; i < lanes.Count; i++)
                {
                    if (lanes[i] is null && !freedHere.Contains(i))
                    {
                        return i;
                    }
                }

                lanes.Add(null);
                return lanes.Count - 1;
            }

            int lane;
            if (hasTrunk && node.OnTrunk)
            {
                lane = 0;
            }
            else if (hasTrunk && incoming.Contains(0))
            {
                lane = 0;
            }
            else if (hasTrunk && lanes[0] is null && incoming.Count == 0 && BelongsOnTrunkLane(node))
            {
                lane = 0;
            }
            else if (incoming.Count > 0)
            {
                var straight = incoming.Where(i => lanes[i]!.FirstParent).ToList();
                lane = (straight.Count > 0 ? straight : incoming).Min();
            }
            else
            {
                lane = Allocate();
            }

            var up = incoming.Contains(lane);
            var into = new List<MapLine>();
            foreach (var i in incoming)
            {
                if (i != lane)
                {
                    into.Add(new MapLine(i, lanes[i]!.Color, lanes[i]!.Style));
                    freedHere.Add(i);
                }

                lanes[i] = null;
            }

            var through = new List<MapLine>();
            for (var i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] is { } state)
                {
                    through.Add(new MapLine(i, state.Color, state.Style));
                }
            }

            var style = node.Kind == NodeKind.Ghost ? MapLineStyle.Dashed : MapLineStyle.Solid;
            MapLine? down = null;
            var outs = new List<MapLine>();
            for (var p = 0; p < node.Parents.Count; p++)
            {
                var parent = node.Parents[p];
                if (p == 0)
                {
                    lanes[lane] = new LaneState { Expect = parent, Color = node.Color, Style = style, FirstParent = true };
                    down = new MapLine(lane, node.Color, style);
                    continue;
                }

                var existing = lanes.FindIndex(l => l?.Expect == parent);
                if (existing >= 0 && existing != lane)
                {
                    outs.Add(new MapLine(existing, lanes[existing]!.Color, lanes[existing]!.Style));
                    through.RemoveAll(l => l.Lane == existing);
                    through.Add(new MapLine(existing, lanes[existing]!.Color, lanes[existing]!.Style));
                }
                else if (existing < 0)
                {
                    var fresh = Allocate();
                    var link = node.Links?.Contains(parent) == true ? MapLineStyle.Dashed : style;
                    lanes[fresh] = new LaneState { Expect = parent, Color = node.Color, Style = link };
                    outs.Add(new MapLine(fresh, node.Color, link));
                }
            }

            laneCount = Math.Max(laneCount, lanes.Count);
            rows.Add(new MapRow
            {
                Key = node.Id,
                Kind = node.Kind switch { NodeKind.Gap => MapRowKind.Gap, NodeKind.Ghost => MapRowKind.Ghost, _ => MapRowKind.Commit },
                Lane = lane,
                Color = node.Color,
                Commit = node.Commit,
                Date = node.Kind == NodeKind.Gap ? null : node.Date,
                Presence = node.Presence,
                Unpushed = node.Unpushed,
                IsMerge = node.Commit is { Parents.Count: > 1 },
                OnTrunk = node.OnTrunk,
                Dim = node is { InTrunk: true, Pos: null } || (node.Refs.Count > 0 && node.Refs.All(r => r.Merged) && node.Heads.Count == 0),
                Refs = SortRefs(node.Refs),
                Stashes = node.Stashes,
                PullRequests = node.Prs,
                Heads = node.Heads,
                Tree = node.Tree,
                Hidden = node.Hidden,
                Older = node.Older,
                Up = up,
                Down = down,
                Stub = node.Stub,
                Through = through.OrderBy(l => l.Lane).ToList(),
                In = into,
                Out = outs,
            });
            node.Lane = lane;
        }

        // Trailing lanes that were allocated but hold nothing by the end still count: they were drawn.
        return rows;
    }

    /// <summary>
    /// Whether a commit nothing points down at may start on the trunk's lane: a device's own unpushed
    /// commits on the default branch, or uncommitted work on top of the trunk.
    /// </summary>
    private static bool BelongsOnTrunkLane(Node node) =>
        node.Trunkish || (node.Kind == NodeKind.Ghost && node.Parents is [{ } head] && (head.Trunkish || (head.OnTrunk && head.Children.Count == 1)));

    private static List<MapRef> SortRefs(List<MapRef> refs) => refs
        .OrderByDescending(r => r.HeadOf.Count > 0)
        .ThenBy(r => r.Kind == MapRefKind.Remote)
        .ThenByDescending(r => r.IsDefault)
        .ThenBy(r => r.Merged)
        .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Replaces the middle of long runs of unremarkable commits with one row.</summary>
    private static List<MapRow> Fold(List<MapRow> rows, List<Node> order, IReadOnlySet<string> expanded)
    {
        bool Plain(int i)
        {
            var (row, node) = (rows[i], order[i]);
            return row.Kind == MapRowKind.Commit && !node.Decorated && row is { Up: true, Down: not null, In.Count: 0, Out.Count: 0 } && node.Parents.Count == 1
                && node.Children.All(c => c.Kind != NodeKind.Ghost);
        }

        bool Continues(int i) => i + 1 < rows.Count && order[i].Parents[0] == order[i + 1] && Plain(i + 1)
            && rows[i].Lane == rows[i + 1].Lane && rows[i].Color == rows[i + 1].Color && rows[i].Unpushed == rows[i + 1].Unpushed;

        var result = new List<MapRow>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            if (!Plain(i))
            {
                result.Add(rows[i]);
                continue;
            }

            var end = i;
            while (Continues(end))
            {
                end++;
            }

            var length = end - i + 1;
            var key = "fold:" + rows[Math.Min(i + FoldHead, end)].Key;
            if (length < FoldMinRun || expanded.Contains(key))
            {
                result.AddRange(rows.Skip(i).Take(length));
            }
            else
            {
                var first = rows[i + FoldHead];
                result.AddRange(rows.Skip(i).Take(FoldHead));
                result.Add(new MapRow
                {
                    Key = key,
                    Kind = MapRowKind.Fold,
                    Lane = first.Lane,
                    Color = first.Color,
                    Presence = first.Presence,
                    Unpushed = first.Unpushed,
                    OnTrunk = first.OnTrunk,
                    Hidden = length - FoldHead - FoldTail,
                    Up = true,
                    Down = first.Down,
                    Through = first.Through,
                });
                result.AddRange(rows.Skip(end - FoldTail + 1).Take(FoldTail));
            }

            i = end;
        }

        return result;
    }

    /// <summary>Every node reachable from the given ones by following parents, themselves included.</summary>
    private static HashSet<Node> Reach(IEnumerable<Node> from)
    {
        var seen = new HashSet<Node>();
        var stack = new Stack<Node>(from);
        while (stack.TryPop(out var node))
        {
            if (seen.Add(node))
            {
                foreach (var parent in node.Parents)
                {
                    stack.Push(parent);
                }
            }
        }

        return seen;
    }

    /// <summary>
    /// A device whose branch points somewhere on the default branch has every commit below that point,
    /// including ones only another device reported: the highest shown trunk commit at or below its tips.
    /// </summary>
    private static IEnumerable<Node> TrunkAtOrBelow(List<Node> trunk, List<Node> tips)
    {
        var top = tips.Where(n => n.Pos is not null).Select(n => n.Pos!.Value).DefaultIfEmpty(0).Max();
        var node = trunk.FirstOrDefault(n => n.Pos <= top);
        return node is null ? [] : [node];
    }

    private static string ShortHost(string hostname)
    {
        foreach (var suffix in (string[])[".local", ".lan", ".home"])
        {
            if (hostname.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && hostname.Length > suffix.Length)
            {
                return hostname[..^suffix.Length];
            }
        }

        return hostname;
    }

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
}
