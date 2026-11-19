using DevDash.Agent.Git;
using DevDash.Contracts;
using DevDash.Core.Git;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.Actions;

/// <summary>
/// Decides whether this machine is willing to run an action. Nothing the hub says is taken on trust:
/// the repository must be one this agent scans, and every name and path in the action is checked
/// against the repository as it is right now. All of the checks are read-only.
/// </summary>
public sealed class ActionValidator(GitRunner git, IOptionsMonitor<AgentOptions> options)
{
    private static readonly string[] ConventionalDefaults = ["main", "master", "develop", "trunk"];

    private TimeSpan Timeout => TimeSpan.FromSeconds(options.CurrentValue.GitTimeoutSeconds);

    /// <summary>Returns why the action is refused, or null when it may run.</summary>
    public async Task<string?> RefusalAsync(AgentAction? action, CancellationToken ct)
    {
        if (AgentActions.Validate(action) is { } shape)
        {
            return shape;
        }

        // An agent update or a refresh is not a git action and is never validated, or run, as one.
        if (!AgentActions.IsRepoAction(action!.Kind) || action.RepoPath is not { } repoPath)
        {
            return "Not a repository action.";
        }

        if (await RepoRefusalAsync(repoPath, ct).ConfigureAwait(false) is { } repo)
        {
            return repo;
        }

        return action.Kind switch
        {
            AgentActionKind.DeleteBranches => await DeleteBranchesRefusalAsync(action, ct).ConfigureAwait(false),
            AgentActionKind.RemoveWorktree => await RemoveWorktreeRefusalAsync(action, ct).ConfigureAwait(false),
            AgentActionKind.Push => await PushRefusalAsync(action, ct).ConfigureAwait(false),
            AgentActionKind.DropStash => await DropStashRefusalAsync(action, ct).ConfigureAwait(false),
            AgentActionKind.PruneWorktrees or AgentActionKind.Fetch => null,
            _ => "Unknown action kind.",
        };
    }

    /// <summary>
    /// Why a refresh is refused, or null. A refresh runs no command, so all that matters is that the path
    /// is a plain absolute one inside an entry this agent scans (symlinks resolved): anything else would
    /// have the agent read, and report on, a folder its owner never pointed it at.
    /// </summary>
    public string? RefreshRefusal(AgentAction? action)
    {
        if (AgentActions.Validate(action) is { } shape)
        {
            return shape;
        }

        if (action is not { Kind: AgentActionKind.Refresh, RepoPath: { } path })
        {
            return "Not a refresh.";
        }

        if (!Path.IsPathFullyQualified(path) || PathSafety.HasDotSegments(path))
        {
            return "The entry path is not a plain absolute path.";
        }

        if (RescanRequests.Find(options.CurrentValue.ResolveRoots(), path) is not { } entry)
        {
            return $"{path} is outside this agent's scan roots.";
        }

        return entry.Name.StartsWith('.') ? $"{path} is not an entry this agent scans." : null;
    }

    /// <summary>
    /// The path must be the top of a git working tree, inside a configured scan root, at a depth the
    /// scanner looks at (a top-level entry, or a child of a worktree folder).
    /// </summary>
    private async Task<string?> RepoRefusalAsync(string repoPath, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(repoPath) || PathSafety.HasDotSegments(repoPath))
        {
            return "The repository path is not a plain absolute path.";
        }

        var current = options.CurrentValue;
        var resolved = PathSafety.Resolve(repoPath);
        var root = current.ResolveRoots().Select(PathSafety.Resolve).FirstOrDefault(r => PathSafety.IsInside(r, resolved));
        if (root is null)
        {
            return $"{repoPath} is outside this agent's scan roots.";
        }

        var below = PathSafety.SegmentsBelow(root, resolved);
        if (below.Length > 1 + Math.Max(0, current.WorktreeContainerDepth) || below.Any(s => s.StartsWith('.')))
        {
            return $"{repoPath} is not a repository this agent scans.";
        }

        if (!Directory.Exists(repoPath) || !(Directory.Exists(Path.Combine(repoPath, ".git")) || File.Exists(Path.Combine(repoPath, ".git"))))
        {
            return $"{repoPath} is not a git repository.";
        }

        // An empty prefix means the folder is the top of its working tree, not a subfolder of some other repo.
        var top = await git.RunAsync(repoPath, ["rev-parse", "--is-inside-work-tree", "--show-prefix"], Timeout, ct).ConfigureAwait(false);
        var lines = top.StdOut.Split('\n').Select(l => l.Trim()).ToArray();
        if (!top.Ok || lines.Length < 2 || lines[0] != "true" || lines[1].Length != 0)
        {
            return $"{repoPath} is not the top of a git working tree.";
        }

        return null;
    }

    private async Task<string?> DeleteBranchesRefusalAsync(AgentAction action, CancellationToken ct)
    {
        var repo = action.RepoPath!;
        var worktrees = await WorktreesAsync(repo, ct).ConfigureAwait(false);
        var defaults = await DefaultBranchesAsync(repo, ct).ConfigureAwait(false);

        foreach (var branch in action.Branches!)
        {
            if (await BranchRefusalAsync(repo, branch, ct).ConfigureAwait(false) is { } problem)
            {
                return problem;
            }

            if (defaults.Contains(branch))
            {
                return $"{branch} is the default branch; the agent does not delete it.";
            }

            if (worktrees.FirstOrDefault(w => w.Branch == branch) is { } checkedOut)
            {
                return $"{branch} is checked out at {checkedOut.Path}.";
            }
        }

        return null;
    }

    private async Task<string?> RemoveWorktreeRefusalAsync(AgentAction action, CancellationToken ct)
    {
        var target = action.WorktreePath!;
        if (!Path.IsPathFullyQualified(target) || PathSafety.HasDotSegments(target))
        {
            return "The worktree path is not a plain absolute path.";
        }

        var worktrees = await WorktreesAsync(action.RepoPath!, ct).ConfigureAwait(false);
        var resolved = PathSafety.Resolve(target);
        var index = worktrees.ToList().FindIndex(w => PathSafety.SamePath(PathSafety.Resolve(w.Path), resolved));
        if (index < 0)
        {
            return $"{target} is not a worktree of {action.RepoPath}.";
        }

        var worktree = worktrees[index];
        if (index == 0 || worktree.Bare)
        {
            return "That is the repository's main working tree, not a linked worktree.";
        }

        if (worktree.Locked)
        {
            return "The worktree is locked.";
        }

        if (worktree.Prunable)
        {
            return "The worktree's folder is already gone; prune it instead.";
        }

        // Commits made on a detached HEAD belong to no branch, so removing the worktree could strand them.
        return worktree.Branch is null ? "The worktree has a detached HEAD; remove it by hand after checking its commits." : null;
    }

    private async Task<string?> PushRefusalAsync(AgentAction action, CancellationToken ct)
    {
        var remotes = RemoteListParser.Parse((await git.RunAsync(action.RepoPath!, ["remote", "-v"], Timeout, ct).ConfigureAwait(false)).StdOut);
        if (!remotes.Any(r => r.Name == action.Remote))
        {
            return $"The repository has no remote named {action.Remote}.";
        }

        return await BranchRefusalAsync(action.RepoPath!, action.Branch!, ct).ConfigureAwait(false);
    }

    private async Task<string?> DropStashRefusalAsync(AgentAction action, CancellationToken ct)
    {
        var selector = $"stash@{{{action.StashIndex}}}";
        var current = await git.RunAsync(action.RepoPath!, ["rev-parse", "--verify", "--quiet", selector], Timeout, ct).ConfigureAwait(false);
        var sha = current.StdOut.Trim();
        if (!current.Ok || sha.Length == 0)
        {
            return $"There is no {selector} any more.";
        }

        return string.Equals(sha, action.ExpectedSha, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"{selector} is no longer the stash that was shown (the stash list has changed). Nothing was dropped.";
    }

    /// <summary>The name must pass <c>git check-ref-format</c> and be an existing local branch.</summary>
    private async Task<string?> BranchRefusalAsync(string repo, string branch, CancellationToken ct)
    {
        var format = await git.ExecuteAsync(repo, ["check-ref-format", $"refs/heads/{branch}"], Timeout, ct).ConfigureAwait(false);
        if (!format.Ok)
        {
            return $"'{branch}' is not a valid branch name.";
        }

        var exists = await git.RunAsync(repo, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], Timeout, ct).ConfigureAwait(false);
        return exists.Ok ? null : $"There is no local branch named {branch}.";
    }

    private async Task<IReadOnlyList<WorktreeInfo>> WorktreesAsync(string repo, CancellationToken ct) =>
        WorktreeListParser.Parse((await git.RunAsync(repo, ["worktree", "list", "--porcelain"], Timeout, ct).ConfigureAwait(false)).StdOut);

    /// <summary>What <c>origin/HEAD</c> points at, plus the conventional names the scanner falls back to.</summary>
    private async Task<HashSet<string>> DefaultBranchesAsync(string repo, CancellationToken ct)
    {
        var names = new HashSet<string>(ConventionalDefaults, StringComparer.Ordinal);
        var head = await git.RunAsync(repo, ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], Timeout, ct).ConfigureAwait(false);
        if (head.Ok && head.StdOut.Trim() is { Length: > 0 } remoteHead)
        {
            var slash = remoteHead.IndexOf('/');
            names.Add(slash >= 0 ? remoteHead[(slash + 1)..] : remoteHead);
        }

        return names;
    }
}
