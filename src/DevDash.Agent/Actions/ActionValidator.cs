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
public sealed class ActionValidator(GitRunner git, IOptionsMonitor<AgentOptions> options, RemovalLog? removals = null, KnownRemotes? knownRemotes = null)
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

        // A clone is the one action whose path must not exist yet, so it has checks of its own.
        if (action.Kind == AgentActionKind.Clone)
        {
            return CloneRefusal(action);
        }

        // So is a restore: the archive is unpacked into a folder that is not there yet.
        if (action.Kind == AgentActionKind.RestoreArchive)
        {
            return FreeFolderRefusal(repoPath, "restore");
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
            AgentActionKind.RemoveProject or AgentActionKind.ArchiveProject => RemoveProjectRefusal(action),
            AgentActionKind.CleanBuildDirs => await CleanBuildDirsRefusalAsync(action, ct).ConfigureAwait(false),
            AgentActionKind.PullFastForward => await PullRefusalAsync(action, ct).ConfigureAwait(false),
            AgentActionKind.OpenInT3 => null,
            AgentActionKind.StartT3Thread => await StartT3RefusalAsync(action, ct).ConfigureAwait(false),
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
    internal async Task<string?> RepoRefusalAsync(string repoPath, CancellationToken ct)
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

        // T3 Code made it for a thread and removes it when the thread is archived; removing it here would pull it from under the thread.
        if (T3Code.T3Locator.MarkOwned([worktrees[0], worktree], options.CurrentValue.T3.WorktreesFolder())[1].T3Owned == true)
        {
            return "T3 Code made this worktree for a thread. Archive the thread in T3 Code instead; it removes the worktree itself.";
        }

        // Commits made on a detached HEAD belong to no branch, so removing the worktree could strand them.
        return worktree.Branch is null ? "The worktree has a detached HEAD; remove it by hand after checking its commits." : null;
    }

    /// <summary>
    /// A new thread gets a new branch in a new worktree. The branch name must be one git accepts for a branch
    /// and must not exist yet (so no existing work is checked out or moved); the base must name a commit here.
    /// </summary>
    private async Task<string?> StartT3RefusalAsync(AgentAction action, CancellationToken ct)
    {
        var repo = action.RepoPath!;
        var branch = action.NewBranch!;
        var format = await git.ExecuteAsync(repo, ["check-ref-format", "--branch", branch], Timeout, ct).ConfigureAwait(false);
        if (!format.Ok)
        {
            return $"'{branch}' is not a valid branch name.";
        }

        var exists = await git.RunAsync(repo, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], Timeout, ct).ConfigureAwait(false);
        if (exists.Ok)
        {
            return $"A branch named {branch} already exists. Choose another name; the agent only starts a thread on a new branch.";
        }

        var start = await git.RunAsync(repo, ["rev-parse", "--verify", "--quiet", action.WorktreeBase + "^{commit}"], Timeout, ct).ConfigureAwait(false);
        return start.Ok ? null : $"{action.WorktreeBase} does not name a commit in {repo}. Fetch first, or choose another base.";
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

    /// <summary>
    /// What can be said before looking inside: the folder itself must be a real folder, not a link to
    /// one. Whether it is safe to remove is decided afterwards, from a fresh scan (<see cref="ProjectActions"/>).
    /// </summary>
    private static string? RemoveProjectRefusal(AgentAction action)
    {
        var info = new DirectoryInfo(action.RepoPath!);
        return (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null
            ? $"{action.RepoPath} is a link to a folder, not a folder."
            : null;
    }

    /// <summary>
    /// A clone goes into a folder that does not exist, straight under a scan root (or inside a worktree
    /// folder there), and comes from a URL this agent knows itself: one a project it removed from that
    /// very folder had, or one a repository in its last scan has. The hub cannot name any other.
    /// </summary>
    private string? CloneRefusal(AgentAction action)
    {
        if (FreeFolderRefusal(action.RepoPath!, "clone") is { } where)
        {
            return where;
        }

        var url = action.Url!;
        return removals?.Allows(action.RepoPath!, url) == true || knownRemotes?.Contains(url) == true
            ? null
            : "This agent does not know that URL: it is not a remote of a project it removed from that folder, nor of a repository it scans. Clone it by hand.";
    }

    /// <summary>
    /// A folder that does not exist yet, where the scanner would find a repository: straight under a scan root
    /// (or inside a worktree folder there), not a dot-folder, in a parent that exists and is not a repository.
    /// </summary>
    private string? FreeFolderRefusal(string path, string verb)
    {
        if (!Path.IsPathFullyQualified(path) || PathSafety.HasDotSegments(path))
        {
            return $"The {verb} path is not a plain absolute path.";
        }

        var current = options.CurrentValue;
        var parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            return $"The folder {path} would go in does not exist.";
        }

        var resolved = PathSafety.Resolve(path);
        var root = current.ResolveRoots().Select(PathSafety.Resolve).FirstOrDefault(r => PathSafety.IsInside(r, resolved));
        if (root is null)
        {
            return $"{path} is outside this agent's scan roots.";
        }

        var below = PathSafety.SegmentsBelow(root, resolved);
        if (below.Length > 1 + Math.Max(0, current.WorktreeContainerDepth) || below.Any(s => s.StartsWith('.')))
        {
            return $"{path} is not where this agent scans for repositories.";
        }

        // Exists covers files and folders; the link check covers a link that points nowhere.
        if (Path.Exists(path) || new FileInfo(path).LinkTarget is not null)
        {
            return $"{path} already exists. The agent only {(verb == "clone" ? "clones" : "restores")} into a folder that is not there.";
        }

        if (Directory.Exists(Path.Combine(parent, ".git")) || File.Exists(Path.Combine(parent, ".git")))
        {
            return $"{parent} is itself a repository; the agent does not {verb} inside one.";
        }

        return null;
    }

    /// <summary>
    /// Every folder must be build output by the scanner's own rule (its name, and for the ambiguous names
    /// the manifest beside it), really inside the repository with no link anywhere on the way, and hold
    /// nothing git tracks.
    /// </summary>
    private async Task<string?> CleanBuildDirsRefusalAsync(AgentAction action, CancellationToken ct)
    {
        var repo = Path.GetFullPath(action.RepoPath!);
        var resolvedRepo = PathSafety.Resolve(repo);
        var roots = options.CurrentValue.ResolveRoots().Select(PathSafety.Resolve).ToList();
        foreach (var dir in action.BuildDirs!)
        {
            if (!Path.IsPathFullyQualified(dir) || PathSafety.HasDotSegments(dir))
            {
                return $"{dir} is not a plain absolute path.";
            }

            var full = Path.GetFullPath(dir);
            if (!PathSafety.IsInside(repo, full))
            {
                return $"{dir} is not inside {action.RepoPath}.";
            }

            var segments = PathSafety.SegmentsBelow(repo, full);
            if (segments.Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            {
                return $"{dir} is inside git's own folder.";
            }

            // Every step from the repository down to the folder must be a real folder. A link could lead anywhere.
            var step = repo;
            foreach (var segment in segments)
            {
                step = Path.Combine(step, segment);
                var info = new DirectoryInfo(step);
                if (!info.Exists)
                {
                    return $"{dir} does not exist (it may already have been deleted).";
                }

                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
                {
                    return $"{dir} is reached through a link ({step}), so the agent leaves it alone.";
                }
            }

            var resolved = PathSafety.Resolve(full);
            if (!PathSafety.IsInside(resolvedRepo, resolved) || !roots.Any(r => PathSafety.IsInside(r, resolved)))
            {
                return $"{dir} is not really inside {action.RepoPath}.";
            }

            var parent = Path.GetDirectoryName(full)!;
            var name = segments[^1];
            HashSet<string> siblings;
            try
            {
                siblings = Directory.EnumerateFileSystemEntries(parent).Select(e => Path.GetFileName(e)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"Could not read {parent}: {ex.Message}";
            }

            if (!DevDash.Core.BuildOutput.IsReclaimable(name, siblings))
            {
                return $"{dir} is not a build folder the agent knows (node_modules, .venv, a framework cache such as .next or .angular, or bin/obj/target/dist beside the project file that produces them).";
            }

            // Asked from the folder's parent, so a folder inside a submodule is checked against the submodule.
            var tracked = await git.RunAsync(parent, ["ls-files", "--", name], Timeout, ct).ConfigureAwait(false);
            if (!tracked.Ok)
            {
                return $"Could not ask git what it tracks in {dir}: {tracked.StdErr.Trim()}";
            }

            if (tracked.StdOut.Trim().Length > 0)
            {
                return $"git tracks files in {dir}, so it is not just build output.";
            }
        }

        return null;
    }

    /// <summary>
    /// The branch must exist and track the branch of the same name on the named remote. Whether moving
    /// it would be a fast-forward is checked after the fetch (<see cref="ProjectActions"/>).
    /// </summary>
    private async Task<string?> PullRefusalAsync(AgentAction action, CancellationToken ct)
    {
        if (await PushRefusalAsync(action, ct).ConfigureAwait(false) is { } problem)
        {
            return problem;
        }

        var upstream = await git.RunAsync(action.RepoPath!, ["for-each-ref", "--format=%(upstream:short)", $"refs/heads/{action.Branch}"], Timeout, ct).ConfigureAwait(false);
        var tracks = upstream.StdOut.Trim();
        if (!upstream.Ok || tracks.Length == 0)
        {
            return $"{action.Branch} has no upstream to pull from.";
        }

        return tracks == $"{action.Remote}/{action.Branch}"
            ? null
            : $"{action.Branch} tracks {tracks}, not {action.Remote}/{action.Branch}. The agent only fast-forwards a branch from the branch of the same name.";
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
