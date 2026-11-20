using System.Diagnostics;
using DevDash.Agent.Git;
using DevDash.Agent.Scanning;
using DevDash.Contracts;
using DevDash.Core.Git;
using DevDash.Core.Safety;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.Actions;

/// <summary>
/// The actions from 0.6.0 that are more than one git command: removing a project, cloning one back,
/// deleting build output, and a fast-forward pull. <see cref="ActionExecutor"/> calls these after its own
/// checks (actions enabled, one action at a time, <see cref="ActionValidator"/>), so everything here
/// starts from a path that is known to be a repository this agent scans, or, for a clone, a free
/// folder it may clone into.
/// </summary>
public sealed class ProjectActions(
    GitRunner git,
    ActionValidator validator,
    IOptionsMonitor<AgentOptions> options,
    ILogger<ProjectActions> logger,
    MachineScanner? scanner = null,
    ITrash? trash = null,
    RemovalLog? removals = null,
    TimeProvider? time = null)
{
    /// <summary>No background maintenance and no fsmonitor daemon as a side effect of an action.</summary>
    private static readonly string[] Prefix = ["-c", "core.fsmonitor=false", "-c", "gc.auto=0", "-c", "maintenance.auto=false"];

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private TimeSpan Local => TimeSpan.FromSeconds(options.CurrentValue.GitTimeoutSeconds);

    private TimeSpan Network => TimeSpan.FromSeconds(Math.Max(10, options.CurrentValue.Actions.NetworkTimeoutSeconds));

    public Task<ActionOutcome> RunAsync(AgentAction action, Stopwatch stopwatch, CancellationToken ct) => action.Kind switch
    {
        AgentActionKind.RemoveProject => RemoveAsync(action, stopwatch, ct),
        AgentActionKind.Clone => CloneAsync(action, stopwatch, ct),
        AgentActionKind.CleanBuildDirs => Task.FromResult(Clean(action, stopwatch)),
        AgentActionKind.PullFastForward => PullAsync(action, stopwatch, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action.Kind, "Not a project action."),
    };

    private static ActionOutcome Refuse(Stopwatch stopwatch, string why) => new(false, null, "Refused: " + why, [], stopwatch.ElapsedMilliseconds);

    private static string Output(GitResult result) =>
        AgentActions.Truncate(string.Join('\n', new[] { result.StdOut.Trim(), result.StdErr.Trim() }.Where(s => s.Length > 0)));

    // ---- remove ---------------------------------------------------------------------------------

    /// <summary>
    /// Fetch, scan the folder again, judge it with <see cref="RepoSafety"/> exactly as the hub did, and
    /// only when that says safe, and nothing has changed since the user looked, move it to the trash.
    /// A linked worktree is handed to <c>git worktree remove</c> instead: its folder is git's to delete.
    /// </summary>
    private async Task<ActionOutcome> RemoveAsync(AgentAction action, Stopwatch stopwatch, CancellationToken ct)
    {
        var path = action.RepoPath!;
        if (scanner is null || trash is null)
        {
            return Refuse(stopwatch, "this agent cannot remove projects.");
        }

        if (RescanRequests.Find(options.CurrentValue.ResolveRoots(), path) is not { } found)
        {
            return Refuse(stopwatch, $"{path} is outside this agent's scan roots.");
        }

        // The verdict is only as fresh as the last fetch, so fetch now, from every remote: "on a remote"
        // means any of them. No remote means nothing to fetch, and the assessment below then says
        // "no remote", which is never safe.
        var remotes = RemoteListParser.Parse((await git.RunAsync(path, ["remote", "-v"], Local, ct).ConfigureAwait(false)).StdOut);
        if (remotes.Count > 0)
        {
            var fetch = await git.ExecuteAsync(path, [.. Prefix, "fetch", "--all", "--prune", "--quiet"], Network, ct).ConfigureAwait(false);
            if (!fetch.Ok)
            {
                return Refuse(stopwatch, $"could not fetch, so there is no telling what the remote has now: {Output(fetch)}. Nothing was removed.");
            }
        }

        var entry = await scanner.ScanRepoAsync(found.Root, path, new ScanRequest(AllowFetch: false, SizeMode.CachedOnly) { JustFetched = remotes.Count > 0 }, ct).ConfigureAwait(false);
        if (entry is not { Kind: EntryKind.GitRepo, Git: { } state })
        {
            return Refuse(stopwatch, $"{path} could not be read as a git repository{(entry.Error is { } error ? ": " + error : "")}. Nothing was removed.");
        }

        // A linked worktree is judged on its own folder; its branches stay in the main repository. That
        // only holds when the main working tree is one this agent scans too, which is also where git runs.
        string? main = null;
        if (state.IsLinkedWorktree)
        {
            main = state.MainWorktreePath;
            if (main is null || await validator.RepoRefusalAsync(main, ct).ConfigureAwait(false) is not null)
            {
                return Refuse(stopwatch, $"{path} is a linked worktree whose main working tree ({main ?? "unknown"}) is not one this agent scans. Remove it by hand with git worktree remove.");
            }
        }

        var safety = RepoSafety.Assess(entry, _time.GetUtcNow(), includeRepository: main is null);
        if (!safety.IsRemovable)
        {
            return Refuse(stopwatch, $"{path} is not safe to remove: {string.Join("; ", safety.Reasons.Select(r => r.Text))}. Nothing was removed.");
        }

        var acknowledged = (action.AcknowledgedCaveats ?? []).ToHashSet(StringComparer.Ordinal);
        var unseen = safety.Caveats.SelectMany(c => c.Names).Where(n => !acknowledged.Contains(n)).ToList();
        if (unseen.Count > 0)
        {
            return Refuse(stopwatch, $"{path} has ignored files that were not acknowledged: {string.Join(", ", unseen.Take(10))}{(unseen.Count > 10 ? $" and {unseen.Count - 10} more" : "")}. Nothing was removed.");
        }

        if (!string.Equals(safety.Fingerprint, action.ExpectedFingerprint, StringComparison.Ordinal))
        {
            return Refuse(stopwatch, $"{path} has changed since it was shown in the dashboard (a commit, a branch or its files). Nothing was removed. Refresh it and look again.");
        }

        if (main is not null)
        {
            logger.LogInformation("Removing linked worktree {Path} of {Main}", path, main);
            var removed = await git.ExecuteAsync(main, [.. Prefix, "worktree", "remove", "--", path], Local, ct).ConfigureAwait(false);
            logger.LogInformation("git worktree remove {Path} {Outcome} (exit {Exit})", path, removed.Ok ? "succeeded" : "failed", removed.ExitCode);
            var text = removed.Ok ? $"Removed the linked worktree {path} with git worktree remove. Its branch is still in {main}." : Output(removed);
            return new ActionOutcome(removed.Ok, removed.ExitCode, AgentActions.Truncate(text), [main, path], stopwatch.ElapsedMilliseconds);
        }

        logger.LogWarning("Moving {Path} to {Trash}: safe to remove, asked for from the dashboard", path, trash.Name);
        var moved = trash.Move(path);
        if (!moved.Ok)
        {
            logger.LogWarning("Could not move {Path} to {Trash}: {Error}", path, trash.Name, moved.Error);
            return new ActionOutcome(false, 1, AgentActions.Truncate($"Not removed: {moved.Error}"), [path], stopwatch.ElapsedMilliseconds);
        }

        // What makes a later clone back into this folder acceptable to this agent.
        removals?.Record(path, state.Remotes.Select(r => r.Url), _time.GetUtcNow());
        var where = moved.Location is null || moved.Location == trash.Name ? trash.Name : $"{trash.Name} ({moved.Location})";
        return new ActionOutcome(true, 0, $"Moved {path} to {where}. Nothing was deleted: it can be put back from there.", [path], stopwatch.ElapsedMilliseconds);
    }

    // ---- clone ----------------------------------------------------------------------------------

    private async Task<ActionOutcome> CloneAsync(AgentAction action, Stopwatch stopwatch, CancellationToken ct)
    {
        var path = action.RepoPath!;
        var parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;
        string[] origin = action.Remote is null ? [] : ["--origin", action.Remote];
        var timeout = TimeSpan.FromSeconds(Math.Max(30, options.CurrentValue.Actions.CloneTimeoutSeconds));

        logger.LogInformation("Running action: {Command}", AgentActions.CommandLine(action));
        var result = await git.ExecuteAsync(parent, [.. Prefix, "clone", .. origin, "--", action.Url!, path], timeout, ct).ConfigureAwait(false);
        logger.LogInformation("Clone into {Path} {Outcome} (exit {Exit})", path, result.Ok ? "succeeded" : "failed", result.ExitCode);

        var output = Output(result);
        if (result.TimedOut && Directory.Exists(path) && trash is not null)
        {
            // Git was stopped part way, so it could not tidy up. The folder did not exist before this
            // clone, so all of it is the clone's; it still goes to the trash rather than being deleted.
            var moved = trash.Move(path);
            output += moved.Ok ? $"\nThe unfinished clone was moved to {trash.Name}." : $"\nThe unfinished clone is still at {path}: {moved.Error}";
        }

        return new ActionOutcome(result.Ok, result.ExitCode, AgentActions.Truncate(result.Ok && output.Length == 0 ? $"Cloned into {path}." : output), [path], stopwatch.ElapsedMilliseconds);
    }

    // ---- build output ---------------------------------------------------------------------------

    private ActionOutcome Clean(AgentAction action, Stopwatch stopwatch)
    {
        var deleted = new List<string>();
        var failed = new List<string>();
        foreach (var dir in action.BuildDirs!)
        {
            // Listed twice over (a folder inside one already deleted) is not a failure.
            if (!Directory.Exists(dir))
            {
                continue;
            }

            try
            {
                logger.LogInformation("Deleting build output {Path}", dir);
                DeleteTree(new DirectoryInfo(dir));
                deleted.Add(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not delete {Path}: {Error}", dir, ex.Message);
                failed.Add($"{dir}: {ex.Message}");
            }
        }

        var lines = new List<string>();
        if (deleted.Count > 0)
        {
            lines.Add($"Deleted {string.Join(", ", deleted)}.");
        }

        if (failed.Count > 0)
        {
            lines.Add("Could not delete all of " + string.Join("; ", failed));
        }

        if (lines.Count == 0)
        {
            lines.Add("Nothing to delete: the build output was already gone.");
        }

        return new ActionOutcome(failed.Count == 0, failed.Count == 0 ? 0 : 1, AgentActions.Truncate(string.Join('\n', lines)), [action.RepoPath!], stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Deletes a folder and everything in it without ever following a link: a link inside
    /// <c>node_modules</c> is removed as a link, and what it points at is left alone.
    /// </summary>
    internal static void DeleteTree(DirectoryInfo directory)
    {
        foreach (var child in directory.EnumerateFileSystemInfos())
        {
            var link = (child.Attributes & FileAttributes.ReparsePoint) != 0;
            if (child is DirectoryInfo sub && !link)
            {
                DeleteTree(sub);
                continue;
            }

            // Read-only files (Windows) cannot be deleted until the flag is cleared. Never on a link: that would change its target.
            if (!link && (child.Attributes & FileAttributes.ReadOnly) != 0)
            {
                child.Attributes = FileAttributes.Normal;
            }

            child.Delete();
        }

        directory.Delete();
    }

    // ---- fast-forward ---------------------------------------------------------------------------

    /// <summary>
    /// Fetch the remote, then move the branch only if that is a plain fast-forward. Where the branch is
    /// checked out, that working tree's files move with it, so it must have no uncommitted changes and
    /// the move is <c>git merge --ff-only</c> there. Where it is checked out nowhere, <c>git fetch
    /// remote branch:branch</c> moves the ref, and git itself refuses anything but a fast-forward.
    /// </summary>
    private async Task<ActionOutcome> PullAsync(AgentAction action, Stopwatch stopwatch, CancellationToken ct)
    {
        var repo = action.RepoPath!;
        var (remote, branch) = (action.Remote!, action.Branch!);
        var fetch = await git.ExecuteAsync(repo, [.. Prefix, "fetch", "--prune", "--quiet", "--", remote], Network, ct).ConfigureAwait(false);
        if (!fetch.Ok)
        {
            return Refuse(stopwatch, $"could not fetch {remote}: {Output(fetch)}");
        }

        var tracking = $"refs/remotes/{remote}/{branch}";
        if (!(await git.RunAsync(repo, ["rev-parse", "--verify", "--quiet", tracking], Local, ct).ConfigureAwait(false)).Ok)
        {
            return Refuse(stopwatch, $"{remote} no longer has a branch named {branch}.");
        }

        async Task<int?> CountAsync(string range)
        {
            var count = await git.RunAsync(repo, ["rev-list", "--count", range], Local, ct).ConfigureAwait(false);
            return count.Ok && int.TryParse(count.StdOut.Trim(), out var n) ? n : null;
        }

        var behind = await CountAsync($"refs/heads/{branch}..{tracking}").ConfigureAwait(false);
        var ahead = await CountAsync($"{tracking}..refs/heads/{branch}").ConfigureAwait(false);
        if (behind is null || ahead is null)
        {
            return Refuse(stopwatch, $"could not compare {branch} with {remote}/{branch}.");
        }

        if (behind == 0)
        {
            return new ActionOutcome(true, 0, $"{branch} already has everything on {remote}/{branch}.", [repo], stopwatch.ElapsedMilliseconds);
        }

        if (ahead > 0)
        {
            return Refuse(stopwatch, $"{branch} is {behind} behind {remote}/{branch} and has {ahead} commit(s) of its own, so it cannot be fast-forwarded. Rebase or merge by hand; the agent never does that for you.");
        }

        var worktrees = WorktreeListParser.Parse((await git.RunAsync(repo, ["worktree", "list", "--porcelain"], Local, ct).ConfigureAwait(false)).StdOut);
        if (worktrees.FirstOrDefault(w => w.Branch == branch && !w.Bare && !w.Prunable) is not { } checkedOut)
        {
            logger.LogInformation("Fast-forwarding {Branch} in {Repo} (not checked out)", branch, repo);
            var moved = await git.ExecuteAsync(repo, [.. Prefix, "fetch", "--quiet", "--", remote, $"refs/heads/{branch}:refs/heads/{branch}"], Network, ct).ConfigureAwait(false);
            return new ActionOutcome(moved.Ok, moved.ExitCode,
                moved.Ok ? $"Fast-forwarded {branch} by {behind} commit(s) to {remote}/{branch}." : Output(moved), [repo], stopwatch.ElapsedMilliseconds);
        }

        // The working tree that has the branch checked out is where the files change, so it must be one this agent scans.
        var tree = checkedOut.Path;
        var elsewhere = !PathSafety.SamePath(PathSafety.Resolve(tree), PathSafety.Resolve(repo));
        if (elsewhere && await validator.RepoRefusalAsync(tree, ct).ConfigureAwait(false) is { } outside)
        {
            return Refuse(stopwatch, $"{branch} is checked out at {tree}: {outside}");
        }

        var status = await git.RunAsync(tree, ["status", "--porcelain=v2", "--branch"], Local, ct).ConfigureAwait(false);
        if (!status.Ok)
        {
            return Refuse(stopwatch, $"could not read the state of {tree}.");
        }

        var parsed = StatusParser.Parse(status.StdOut);
        var changes = parsed.Staged + parsed.Unstaged + parsed.Conflicted;
        if (changes > 0)
        {
            return Refuse(stopwatch, $"{branch} is checked out at {tree}, which has {changes} uncommitted change(s). Commit or stash them first; the agent does not pull into a working tree with changes.");
        }

        if (parsed.Branch != branch)
        {
            return Refuse(stopwatch, $"{tree} no longer has {branch} checked out.");
        }

        logger.LogInformation("Fast-forwarding {Branch} in {Tree} (checked out)", branch, tree);
        var merged = await git.ExecuteAsync(tree, [.. Prefix, "merge", "--ff-only", "@{u}"], Local, ct).ConfigureAwait(false);
        string[] affected = elsewhere ? [repo, tree] : [repo];
        return new ActionOutcome(merged.Ok, merged.ExitCode,
            merged.Ok ? AgentActions.Truncate($"Fast-forwarded {branch} by {behind} commit(s) to {remote}/{branch}.\n{Output(merged)}") : Output(merged), affected, stopwatch.ElapsedMilliseconds);
    }
}
