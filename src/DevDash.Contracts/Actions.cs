using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevDash.Contracts;

/// <summary>
/// The complete list of things the hub may ask an agent to do. There is deliberately no "run this
/// command" kind: an agent builds the git arguments itself from these typed fields, and nothing here
/// can discard work (no force, no <c>-D</c>, no reset, no rebase, no pull).
/// <para>
/// Every kind but <see cref="UpdateAgent"/> is a git action on one repository, and only runs on a
/// machine that turned on <c>Agent:Actions:Enabled</c>. <see cref="UpdateAgent"/> touches no repository
/// and is gated by the machine's own <c>Agent:Updates:Mode</c> instead.
/// </para>
/// </summary>
public enum AgentActionKind
{
    /// <summary><c>git branch -d</c>: git only deletes branches it considers fully merged.</summary>
    DeleteBranches,

    /// <summary><c>git worktree remove</c> without <c>--force</c>: git refuses when the worktree has changes.</summary>
    RemoveWorktree,

    /// <summary><c>git worktree prune</c>: drops bookkeeping for worktrees whose folder is gone.</summary>
    PruneWorktrees,

    /// <summary><c>git fetch --prune --quiet</c>: updates remote-tracking refs only.</summary>
    Fetch,

    /// <summary><c>git push</c> of one branch to the branch of the same name, never forced.</summary>
    Push,

    /// <summary><c>git stash drop</c> of one stash, after checking it is still the stash that was shown.</summary>
    DropStash,

    /// <summary>
    /// Replace the agent with a named release. The action carries a version and nothing else: the
    /// agent downloads that release from GitHub itself and checks its signature against the public
    /// key it was built with, so the hub can never supply a binary or a URL.
    /// </summary>
    UpdateAgent,
}

/// <summary>One allowlisted action. Fields that a kind does not use must be null.</summary>
public sealed record AgentAction
{
    public required AgentActionKind Kind { get; init; }

    /// <summary>
    /// Git actions: absolute path of the working tree to run in, as the agent reported it.
    /// Null for <see cref="AgentActionKind.UpdateAgent"/>, which is not about a repository.
    /// </summary>
    public string? RepoPath { get; init; }

    /// <summary><see cref="AgentActionKind.UpdateAgent"/>: the release to install, a strict <see cref="SemVer"/> such as <c>0.2.0</c>.</summary>
    public string? Version { get; init; }

    /// <summary><see cref="AgentActionKind.DeleteBranches"/>: local branch names.</summary>
    public IReadOnlyList<string>? Branches { get; init; }

    /// <summary><see cref="AgentActionKind.RemoveWorktree"/>: the linked worktree's path.</summary>
    public string? WorktreePath { get; init; }

    /// <summary><see cref="AgentActionKind.Push"/>: remote name.</summary>
    public string? Remote { get; init; }

    /// <summary><see cref="AgentActionKind.Push"/>: local branch, pushed to the same name on the remote.</summary>
    public string? Branch { get; init; }

    /// <summary><see cref="AgentActionKind.Push"/>: also record the remote branch as upstream (<c>--set-upstream</c>).</summary>
    public bool SetUpstream { get; init; }

    /// <summary><see cref="AgentActionKind.DropStash"/>: position in <c>git stash list</c>.</summary>
    public int? StashIndex { get; init; }

    /// <summary><see cref="AgentActionKind.DropStash"/>: the stash commit the user was shown. The agent refuses if it no longer matches.</summary>
    public string? ExpectedSha { get; init; }

    public static AgentAction DeleteBranches(string repoPath, IEnumerable<string> branches) =>
        new() { Kind = AgentActionKind.DeleteBranches, RepoPath = repoPath, Branches = branches.ToList() };

    public static AgentAction RemoveWorktree(string repoPath, string worktreePath) =>
        new() { Kind = AgentActionKind.RemoveWorktree, RepoPath = repoPath, WorktreePath = worktreePath };

    public static AgentAction PruneWorktrees(string repoPath) =>
        new() { Kind = AgentActionKind.PruneWorktrees, RepoPath = repoPath };

    public static AgentAction Fetch(string repoPath) =>
        new() { Kind = AgentActionKind.Fetch, RepoPath = repoPath };

    public static AgentAction Push(string repoPath, string remote, string branch, bool setUpstream = false) =>
        new() { Kind = AgentActionKind.Push, RepoPath = repoPath, Remote = remote, Branch = branch, SetUpstream = setUpstream };

    public static AgentAction DropStash(string repoPath, int index, string expectedSha) =>
        new() { Kind = AgentActionKind.DropStash, RepoPath = repoPath, StashIndex = index, ExpectedSha = expectedSha };

    public static AgentAction UpdateAgent(string version) =>
        new() { Kind = AgentActionKind.UpdateAgent, Version = version };
}

/// <summary>
/// What an action means, shared by both sides so they cannot drift: the hub shows
/// <see cref="CommandLine"/> in the confirmation dialog and the agent runs <see cref="GitArgs"/>.
/// </summary>
public static partial class AgentActions
{
    /// <summary>Longest output kept for the audit trail.</summary>
    public const int MaxOutputChars = 4000;

    public const int MaxBranches = 50;

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._/-]{0,99}$")]
    private static partial Regex RemoteName();

    [GeneratedRegex(@"^(?:[0-9a-f]{40}|[0-9a-f]{64})$")]
    private static partial Regex Sha();

    /// <summary>True for the kinds that run git in a repository; false for <see cref="AgentActionKind.UpdateAgent"/>.</summary>
    public static bool IsRepoAction(AgentActionKind kind) => kind != AgentActionKind.UpdateAgent;

    /// <summary>POSIX single-quote escaping.</summary>
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>Canonical JSON, used to tell whether two actions are the same.</summary>
    public static string Key(AgentAction action) => JsonSerializer.Serialize(action, SnapshotSchema.JsonOptions);

    /// <summary>
    /// Shape checks that need no repository: the right fields for the kind, and nothing that could be
    /// read as an option. Returns the problem, or null. The agent runs this and then its own checks
    /// against the real repository.
    /// </summary>
    public static string? Validate(AgentAction? action)
    {
        if (action is null)
        {
            return "No action given.";
        }

        if (!Enum.IsDefined(action.Kind))
        {
            return "Unknown action kind.";
        }

        var kind = action.Kind;
        if (IsRepoAction(kind))
        {
            if (PathProblem(action.RepoPath, "Repository path") is { } repoProblem)
            {
                return repoProblem;
            }
        }
        else if (action.RepoPath is not null)
        {
            return $"A {kind} action does not take a repository path.";
        }

        if ((action.Version is not null) != (kind == AgentActionKind.UpdateAgent)
            || (action.Branches is not null) != (kind == AgentActionKind.DeleteBranches)
            || (action.WorktreePath is not null) != (kind == AgentActionKind.RemoveWorktree)
            || (action.Remote is not null) != (kind == AgentActionKind.Push)
            || (action.Branch is not null) != (kind == AgentActionKind.Push)
            || (action.SetUpstream && kind != AgentActionKind.Push)
            || (action.StashIndex is not null) != (kind == AgentActionKind.DropStash)
            || (action.ExpectedSha is not null) != (kind == AgentActionKind.DropStash))
        {
            return $"The fields do not match a {kind} action.";
        }

        switch (kind)
        {
            case AgentActionKind.DeleteBranches:
                if (action.Branches!.Count is 0 or > MaxBranches)
                {
                    return $"Between 1 and {MaxBranches} branches can be deleted at once.";
                }

                if (action.Branches.Distinct(StringComparer.Ordinal).Count() != action.Branches.Count)
                {
                    return "A branch is listed twice.";
                }

                return action.Branches.Select(BranchProblem).FirstOrDefault(p => p is not null);

            case AgentActionKind.RemoveWorktree:
                return PathProblem(action.WorktreePath, "Worktree path");

            case AgentActionKind.Push:
                if (!RemoteName().IsMatch(action.Remote!))
                {
                    return "The remote name is not valid.";
                }

                return BranchProblem(action.Branch!);

            case AgentActionKind.DropStash:
                if (action.StashIndex is < 0 or > 9999)
                {
                    return "The stash index is out of range.";
                }

                return Sha().IsMatch(action.ExpectedSha!) ? null : "The expected stash commit is not a full sha.";

            case AgentActionKind.UpdateAgent:
                return SemVer.IsValid(action.Version) ? null : "The version is not a strict MAJOR.MINOR.PATCH[-prerelease] version.";

            default:
                return null;
        }
    }

    /// <summary>
    /// The arguments the agent passes to git (as a list, never through a shell), run inside <see cref="AgentAction.RepoPath"/>.
    /// Throws for <see cref="AgentActionKind.UpdateAgent"/>, which is not a git command.
    /// </summary>
    public static IReadOnlyList<string> GitArgs(AgentAction action)
    {
        var (verb, values) = Parts(action);
        return [.. verb, .. values];
    }

    /// <summary>The same command as something a person could paste into a terminal.</summary>
    public static string CommandLine(AgentAction action)
    {
        if (action.Kind == AgentActionKind.UpdateAgent)
        {
            return $"devdash-agent update --version {action.Version}";
        }

        var (verb, values) = Parts(action);
        return string.Join(' ', ["git", "-C", ShellQuote(action.RepoPath ?? ""), .. verb, .. values.Select(ShellQuote)]);
    }

    /// <summary>One line on what stops this action from losing work, for the confirmation dialog.</summary>
    public static string Safeguard(AgentAction action) => action.Kind switch
    {
        AgentActionKind.DeleteBranches => "Uses -d, never -D: git refuses any branch that is not fully merged or is checked out.",
        AgentActionKind.RemoveWorktree => "No --force: git refuses if the worktree has uncommitted or untracked files. The branch itself is kept.",
        AgentActionKind.PruneWorktrees => "Only removes git's record of worktrees whose folder no longer exists.",
        AgentActionKind.Fetch => "Only updates remote-tracking refs. Branches and working trees are untouched.",
        AgentActionKind.Push => "Never forced. The agent fetches first and refuses if the branch is behind the remote.",
        AgentActionKind.DropStash => "The stash is deleted for good. The agent first checks it is still the stash shown here, and refuses if not.",
        AgentActionKind.UpdateAgent => "The agent downloads the release from GitHub itself, checks its signature and checksum, and goes back to the previous version if the new one does not report in.",
        _ => "",
    };

    /// <summary>Short description for lists.</summary>
    public static string Summary(AgentAction action) => action.Kind switch
    {
        AgentActionKind.DeleteBranches => "Delete " + (action.Branches is { Count: 1 } ? $"branch {action.Branches[0]}" : $"{action.Branches?.Count} merged branches"),
        AgentActionKind.RemoveWorktree => $"Remove worktree {action.WorktreePath}",
        AgentActionKind.PruneWorktrees => "Prune worktrees",
        AgentActionKind.Fetch => "Fetch",
        AgentActionKind.Push => $"Push {action.Branch} to {action.Remote}",
        AgentActionKind.DropStash => $"Drop stash@{{{action.StashIndex}}}",
        AgentActionKind.UpdateAgent => $"Update the agent to {action.Version}",
        _ => action.Kind.ToString(),
    };

    /// <summary>Keeps the end of long output, where git puts its errors.</summary>
    public static string Truncate(string? output)
    {
        var text = (output ?? "").Trim();
        return text.Length <= MaxOutputChars ? text : "…" + text[^(MaxOutputChars - 1)..];
    }

    private static (string[] Verb, string[] Values) Parts(AgentAction action) => action.Kind switch
    {
        AgentActionKind.DeleteBranches => (["branch", "-d", "--"], [.. action.Branches ?? []]),
        AgentActionKind.RemoveWorktree => (["worktree", "remove", "--"], [action.WorktreePath ?? ""]),
        AgentActionKind.PruneWorktrees => (["worktree", "prune"], []),
        AgentActionKind.Fetch => (["fetch", "--prune", "--quiet"], []),
        AgentActionKind.Push => (
            action.SetUpstream ? ["push", "--set-upstream", "--"] : ["push", "--"],
            [action.Remote ?? "", $"refs/heads/{action.Branch}:refs/heads/{action.Branch}"]),
        AgentActionKind.DropStash => (["stash", "drop"], [$"stash@{{{action.StashIndex}}}"]),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action.Kind, "Unknown action kind."),
    };

    private static string? PathProblem(string? path, string what)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024)
        {
            return $"{what} is missing or too long.";
        }

        if (path.Any(char.IsControl))
        {
            return $"{what} contains control characters.";
        }

        var rooted = path[0] == '/'
            || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/');
        return rooted ? null : $"{what} must be absolute.";
    }

    /// <summary>A conservative subset of <c>git check-ref-format</c>; the agent also asks git itself.</summary>
    private static string? BranchProblem(string name)
    {
        var bad = string.IsNullOrEmpty(name)
            || name.Length > 255
            || name[0] is '-' or '/' or '.'
            || name[^1] is '/' or '.'
            || name.EndsWith(".lock", StringComparison.Ordinal)
            || name.Contains("..", StringComparison.Ordinal)
            || name.Contains("@{", StringComparison.Ordinal)
            || name.Contains("//", StringComparison.Ordinal)
            || name == "@"
            || name.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\');
        return bad ? $"'{name}' is not a valid branch name." : null;
    }
}

/// <summary>An action handed to an agent. Once handed over it is never handed out again.</summary>
public sealed record PendingAgentAction
{
    public required string Id { get; init; }
    public required AgentAction Action { get; init; }
    public DateTimeOffset QueuedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Response to an agent's poll for work.</summary>
public sealed record ActionClaimResponse
{
    public IReadOnlyList<PendingAgentAction> Actions { get; init; } = [];
}

/// <summary>What happened when the agent ran (or refused) an action.</summary>
public sealed record ActionResultRequest
{
    public bool Succeeded { get; init; }

    /// <summary>Git's exit code (0 or 1 for an agent update), or null when the agent refused before running anything.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Git's output or the reason for refusing, truncated to <see cref="AgentActions.MaxOutputChars"/>.</summary>
    public string Output { get; init; } = "";
    public long DurationMs { get; init; }
}
