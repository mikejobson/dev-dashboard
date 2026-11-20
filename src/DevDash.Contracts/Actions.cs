using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevDash.Contracts;

/// <summary>
/// The complete list of things the hub may ask an agent to do. There is deliberately no "run this
/// command" kind: an agent builds the git arguments itself from these typed fields, and nothing here
/// can discard work (no force, no <c>-D</c>, no reset, no rebase, and the only pull is a fast-forward).
/// <para>
/// Every kind but <see cref="UpdateAgent"/> and <see cref="Refresh"/> changes something under the scan
/// roots, and only runs on a machine that turned on <c>Agent:Actions:Enabled</c>. <see cref="UpdateAgent"/>
/// touches no repository and is gated by the machine's own <c>Agent:Updates:Mode</c> instead.
/// <see cref="Refresh"/> only asks for a scan the agent would do anyway, sooner, so it needs neither.
/// </para>
/// <para>
/// New kinds go at the end, and the hub hands one out only to an agent that says it knows it
/// (<see cref="AgentActions.Accepts"/>): an agent cannot read a claim that names a kind it has never heard of.
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

    /// <summary>
    /// Rescan one top-level entry now and push the result, instead of waiting for the next full scan.
    /// Read-only: the scan fetches only if this machine has auto-fetch on (the one write its owner
    /// already opted into), and re-measures the entry's size. It runs no other command.
    /// </summary>
    Refresh,

    /// <summary>
    /// Remove a whole project folder: into the operating system's trash, never deleted. The agent
    /// fetches, scans the folder again and judges it with the same safety rules as the hub; it refuses
    /// unless that verdict is "safe" (or "safe with caveats", each one acknowledged) and matches the
    /// fingerprint the user was shown. A linked worktree goes through <c>git worktree remove</c> instead.
    /// </summary>
    RemoveProject,

    /// <summary>
    /// <c>git clone</c> into a folder a project was removed from. Only into a scan root, only when the
    /// folder does not exist, and only from a URL the agent itself knows as a remote of that project.
    /// </summary>
    Clone,

    /// <summary>
    /// Delete build output inside one repository (<c>node_modules</c>, <c>bin</c>/<c>obj</c>…). A real
    /// delete, not the trash: a build recreates it. Only folders with a known build-output name that
    /// git tracks nothing in, and never through a link.
    /// </summary>
    CleanBuildDirs,

    /// <summary>
    /// Bring one branch up to its remote branch of the same name, as a fast-forward only: fetch, then
    /// <c>git merge --ff-only @{u}</c> where the branch is checked out (and that working tree has no
    /// uncommitted changes), or <c>git fetch remote branch:branch</c> where it is not.
    /// </summary>
    PullFastForward,

    /// <summary>
    /// Open a repository in the T3 Code desktop app on that machine (<c>t3 app &lt;path&gt;</c>). Changes nothing
    /// in the repository. Needs actions on and T3 Code's CLI on the machine.
    /// </summary>
    OpenInT3,

    /// <summary>
    /// Start a T3 Code thread in a repository, in a new worktree on a new branch:
    /// <c>t3 thread start &lt;path&gt; - --title … --worktree &lt;base&gt; --branch &lt;new&gt; --json</c>, with the
    /// prompt on standard input. The repository's own working tree is not touched; the new branch must not exist.
    /// The thread runs with whatever permissions T3 Code gives it, which is why it needs actions on.
    /// </summary>
    StartT3Thread,
}

/// <summary>One allowlisted action. Fields that a kind does not use must be null.</summary>
public sealed record AgentAction
{
    public required AgentActionKind Kind { get; init; }

    /// <summary>
    /// Git actions: absolute path of the working tree to run in, as the agent reported it.
    /// <see cref="AgentActionKind.Refresh"/>: the path of an entry (or of anything inside one), as the agent reported it.
    /// <see cref="AgentActionKind.RemoveProject"/>: the folder to remove. <see cref="AgentActionKind.Clone"/>: the folder to create.
    /// Null for <see cref="AgentActionKind.UpdateAgent"/>, which is not about a repository.
    /// </summary>
    public string? RepoPath { get; init; }

    /// <summary><see cref="AgentActionKind.UpdateAgent"/>: the release to install, a strict <see cref="SemVer"/> such as <c>0.2.0</c>.</summary>
    public string? Version { get; init; }

    /// <summary><see cref="AgentActionKind.DeleteBranches"/>: local branch names.</summary>
    public IReadOnlyList<string>? Branches { get; init; }

    /// <summary><see cref="AgentActionKind.RemoveWorktree"/>: the linked worktree's path.</summary>
    public string? WorktreePath { get; init; }

    /// <summary>
    /// <see cref="AgentActionKind.Push"/> and <see cref="AgentActionKind.PullFastForward"/>: remote name.
    /// <see cref="AgentActionKind.Clone"/>: the name to give the remote, when it is not <c>origin</c>.
    /// </summary>
    public string? Remote { get; init; }

    /// <summary>
    /// <see cref="AgentActionKind.Push"/>: local branch, pushed to the same name on the remote.
    /// <see cref="AgentActionKind.PullFastForward"/>: local branch, fast-forwarded to the same name on the remote.
    /// </summary>
    public string? Branch { get; init; }

    /// <summary><see cref="AgentActionKind.Push"/>: also record the remote branch as upstream (<c>--set-upstream</c>).</summary>
    public bool SetUpstream { get; init; }

    /// <summary><see cref="AgentActionKind.DropStash"/>: position in <c>git stash list</c>.</summary>
    public int? StashIndex { get; init; }

    /// <summary><see cref="AgentActionKind.DropStash"/>: the stash commit the user was shown. The agent refuses if it no longer matches.</summary>
    public string? ExpectedSha { get; init; }

    /// <summary>
    /// <see cref="AgentActionKind.RemoveProject"/>: the fingerprint of the safety assessment the user was
    /// shown (<c>SafetyAssessment.Fingerprint</c>). The agent assesses again and refuses if it differs.
    /// </summary>
    public string? ExpectedFingerprint { get; init; }

    /// <summary>
    /// <see cref="AgentActionKind.RemoveProject"/>: the ignored files and folders the user ticked as "fine to
    /// lose". Null when there were none. The agent refuses if it finds one that is not listed here.
    /// </summary>
    public IReadOnlyList<string>? AcknowledgedCaveats { get; init; }

    /// <summary><see cref="AgentActionKind.Clone"/>: the URL to clone from. The agent only accepts one it knows itself.</summary>
    public string? Url { get; init; }

    /// <summary><see cref="AgentActionKind.CleanBuildDirs"/>: absolute paths of the build folders to delete, each inside <see cref="RepoPath"/>.</summary>
    public IReadOnlyList<string>? BuildDirs { get; init; }

    /// <summary><see cref="AgentActionKind.StartT3Thread"/>: the first message to the thread. Sent to T3 on standard input, never as an argument.</summary>
    public string? Prompt { get; init; }

    /// <summary><see cref="AgentActionKind.StartT3Thread"/>: the thread's title.</summary>
    public string? Title { get; init; }

    /// <summary><see cref="AgentActionKind.StartT3Thread"/>: the branch, tag or commit the new worktree starts from, e.g. <c>origin/main</c>.</summary>
    public string? WorktreeBase { get; init; }

    /// <summary><see cref="AgentActionKind.StartT3Thread"/>: the branch to create for the new worktree. It must not exist yet.</summary>
    public string? NewBranch { get; init; }

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

    public static AgentAction Refresh(string entryPath) =>
        new() { Kind = AgentActionKind.Refresh, RepoPath = entryPath };

    /// <param name="acknowledgedCaveats">The caveat names the user ticked. None for a project that is plainly safe.</param>
    public static AgentAction RemoveProject(string path, string expectedFingerprint, IEnumerable<string>? acknowledgedCaveats = null) =>
        new()
        {
            Kind = AgentActionKind.RemoveProject,
            RepoPath = path,
            ExpectedFingerprint = expectedFingerprint,
            AcknowledgedCaveats = acknowledgedCaveats?.ToList() is { Count: > 0 } list ? list : null,
        };

    public static AgentAction CloneInto(string path, string url, string? remote = null) =>
        new() { Kind = AgentActionKind.Clone, RepoPath = path, Url = url, Remote = remote is null or "origin" ? null : remote };

    public static AgentAction CleanBuildDirs(string repoPath, IEnumerable<string> buildDirs) =>
        new() { Kind = AgentActionKind.CleanBuildDirs, RepoPath = repoPath, BuildDirs = buildDirs.ToList() };

    public static AgentAction PullFastForward(string repoPath, string remote, string branch) =>
        new() { Kind = AgentActionKind.PullFastForward, RepoPath = repoPath, Remote = remote, Branch = branch };

    public static AgentAction OpenInT3(string repoPath) =>
        new() { Kind = AgentActionKind.OpenInT3, RepoPath = repoPath };

    public static AgentAction StartT3Thread(string repoPath, string prompt, string title, string worktreeBase, string newBranch) =>
        new() { Kind = AgentActionKind.StartT3Thread, RepoPath = repoPath, Prompt = prompt, Title = title, WorktreeBase = worktreeBase, NewBranch = newBranch };
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

    /// <summary>The first agent release that knows <see cref="AgentActionKind.Refresh"/> and long-polls for requests.</summary>
    public const string RefreshMinAgentVersion = "0.5.0";

    /// <summary>The first agent release that knows the removal, clone, build-output and fast-forward actions.</summary>
    public const string RemovalMinAgentVersion = "0.6.0";

    /// <summary>The first agent release that knows the T3 Code actions.</summary>
    public const string T3MinAgentVersion = "0.7.0";

    /// <summary>Longest prompt a <see cref="AgentActionKind.StartT3Thread"/> may carry, in UTF-8 bytes.</summary>
    public const int MaxPromptBytes = 32 * 1024;

    /// <summary>Longest thread title, in characters.</summary>
    public const int MaxTitleChars = 200;

    /// <summary>Most build folders one <see cref="AgentActionKind.CleanBuildDirs"/> may name: as many as a scan reports.</summary>
    public const int MaxBuildDirs = 25;

    /// <summary>Most caveats one <see cref="AgentActionKind.RemoveProject"/> may acknowledge: as many names as a scan sends.</summary>
    public const int MaxCaveats = SafetyFacts.MaxNames;

    [GeneratedRegex(@"^[0-9a-f]{24}$")]
    private static partial Regex Fingerprint();

    /// <summary>The longest an agent may ask the hub to hold its poll for work open (<c>?wait=</c> on the claim endpoint).</summary>
    public const int MaxClaimWaitSeconds = 60;

    /// <summary>
    /// True for the kinds that change something under the scan roots, which need <c>Agent:Actions:Enabled</c>. False for
    /// <see cref="AgentActionKind.UpdateAgent"/> and <see cref="AgentActionKind.Refresh"/>, which change no repository.
    /// </summary>
    public static bool IsRepoAction(AgentActionKind kind) => kind is not (AgentActionKind.UpdateAgent or AgentActionKind.Refresh);

    /// <summary>
    /// Whether the agent that sent this <see cref="MachineInfo"/> takes actions of a kind at all: its own
    /// switch for that kind is on, and it is new enough to know the kind. The hub offers, queues and hands
    /// out nothing else. An agent that predates a kind never set that kind's flag, so it reads as false.
    /// </summary>
    public static bool Accepts(MachineInfo machine, AgentActionKind kind) => kind switch
    {
        AgentActionKind.UpdateAgent => AgentUpdateModes.AcceptsRequests(machine.UpdateMode),
        AgentActionKind.Refresh => machine.AcceptsRefresh,
        AgentActionKind.RemoveProject => machine.ActionsEnabled && machine.AcceptsRemoveProject,
        AgentActionKind.Clone => machine.ActionsEnabled && machine.AcceptsClone,
        AgentActionKind.CleanBuildDirs => machine.ActionsEnabled && machine.AcceptsCleanBuildDirs,
        AgentActionKind.PullFastForward => machine.ActionsEnabled && machine.AcceptsPull,
        AgentActionKind.OpenInT3 or AgentActionKind.StartT3Thread => machine.ActionsEnabled && machine.AcceptsT3,
        _ => machine.ActionsEnabled,
    };

    /// <summary>True for the kinds that run T3 Code's CLI rather than git.</summary>
    public static bool IsT3Action(AgentActionKind kind) => kind is AgentActionKind.OpenInT3 or AgentActionKind.StartT3Thread;

    /// <summary>
    /// Why the agent on a machine cannot take a kind of action, in words for a tooltip, or null when it can.
    /// Tells "the agent is too old" from "actions are off there", because the fix is different.
    /// </summary>
    public static string? WhyNotAccepted(MachineInfo machine, AgentActionKind kind)
    {
        if (Accepts(machine, kind))
        {
            return null;
        }

        if (kind is AgentActionKind.UpdateAgent or AgentActionKind.Refresh)
        {
            return $"The agent on {machine.Hostname} does not take this.";
        }

        if (!machine.ActionsEnabled)
        {
            return $"Agent actions are off on {machine.Hostname}. Start its agent with --enable-actions to allow this.";
        }

        var on = string.IsNullOrWhiteSpace(machine.AgentVersion) ? "" : $" (it is on {machine.AgentVersion})";
        if (IsT3Action(kind))
        {
            // A 0.7.0 agent that does not accept these has looked for T3 Code and not found it (or has it switched off).
            var minimum = SemVer.ParseOrNull(T3MinAgentVersion)!;
            if (SemVer.ParseOrNull(machine.AgentVersion) is { } version && version.CompareTo(minimum) >= 0)
            {
                return machine.T3 is { Available: false } t3
                    ? $"T3 Code is not available on {machine.Hostname}: {t3.Problem ?? "its CLI was not found"}."
                    : $"The agent on {machine.Hostname} has its T3 Code integration switched off.";
            }

            return $"Update the agent on {machine.Hostname} to {T3MinAgentVersion} or newer to use T3 Code from here{on}.";
        }

        return $"Update the agent on {machine.Hostname} to {RemovalMinAgentVersion} or newer to do this from here{on}.";
    }

    /// <summary>True for the kinds that name a path on the machine: everything but <see cref="AgentActionKind.UpdateAgent"/>.</summary>
    public static bool TakesPath(AgentActionKind kind) => kind != AgentActionKind.UpdateAgent;

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
        if (TakesPath(kind))
        {
            var what = kind switch
            {
                AgentActionKind.Refresh => "Entry path",
                AgentActionKind.RemoveProject => "Project path",
                AgentActionKind.Clone => "Clone path",
                _ => "Repository path",
            };
            if (PathProblem(action.RepoPath, what) is { } repoProblem)
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
            // A clone may name its remote or leave it as origin.
            || (kind != AgentActionKind.Clone && (action.Remote is not null) != (kind is AgentActionKind.Push or AgentActionKind.PullFastForward))
            || (action.Branch is not null) != (kind is AgentActionKind.Push or AgentActionKind.PullFastForward)
            || (action.SetUpstream && kind != AgentActionKind.Push)
            || (action.StashIndex is not null) != (kind == AgentActionKind.DropStash)
            || (action.ExpectedSha is not null) != (kind == AgentActionKind.DropStash)
            || (action.ExpectedFingerprint is not null) != (kind == AgentActionKind.RemoveProject)
            || (action.AcknowledgedCaveats is not null && kind != AgentActionKind.RemoveProject)
            || (action.Url is not null) != (kind == AgentActionKind.Clone)
            || (action.BuildDirs is not null) != (kind == AgentActionKind.CleanBuildDirs)
            || (action.Prompt is not null) != (kind == AgentActionKind.StartT3Thread)
            || (action.Title is not null) != (kind == AgentActionKind.StartT3Thread)
            || (action.WorktreeBase is not null) != (kind == AgentActionKind.StartT3Thread)
            || (action.NewBranch is not null) != (kind == AgentActionKind.StartT3Thread))
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
            case AgentActionKind.PullFastForward:
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

            case AgentActionKind.RemoveProject:
                if (!Fingerprint().IsMatch(action.ExpectedFingerprint!))
                {
                    return "The expected fingerprint is not a safety fingerprint.";
                }

                if (action.AcknowledgedCaveats is { } caveats
                    && (caveats.Count is 0 or > MaxCaveats || caveats.Any(c => string.IsNullOrEmpty(c) || c.Length > 1024 || c.Any(char.IsControl))))
                {
                    return "The acknowledged caveats are not a list of file names.";
                }

                return null;

            case AgentActionKind.Clone:
                if (action.Remote is not null && !RemoteName().IsMatch(action.Remote))
                {
                    return "The remote name is not valid.";
                }

                return UrlProblem(action.Url!);

            case AgentActionKind.CleanBuildDirs:
                if (action.BuildDirs!.Count is 0 or > MaxBuildDirs)
                {
                    return $"Between 1 and {MaxBuildDirs} build folders can be deleted at once.";
                }

                if (action.BuildDirs.Distinct(StringComparer.Ordinal).Count() != action.BuildDirs.Count)
                {
                    return "A build folder is listed twice.";
                }

                return action.BuildDirs.Select(d => PathProblem(d, "Build folder path")).FirstOrDefault(p => p is not null);

            case AgentActionKind.StartT3Thread:
                return PromptProblem(action.Prompt!)
                    ?? TitleProblem(action.Title!)
                    ?? RefProblem(action.WorktreeBase!)
                    ?? BranchProblem(action.NewBranch!);

            default:
                return null;
        }
    }

    /// <summary>
    /// The arguments the agent passes to git (as a list, never through a shell), run inside <see cref="AgentAction.RepoPath"/>.
    /// Throws for the kinds that are not one fixed git command: <see cref="AgentActionKind.UpdateAgent"/>,
    /// <see cref="AgentActionKind.Refresh"/>, and the kinds from 0.6.0, whose steps depend on what the agent finds.
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

        // Not something to paste: there is no command for it. This is the line the audit trail shows.
        if (action.Kind == AgentActionKind.Refresh)
        {
            return $"refresh {ShellQuote(action.RepoPath ?? "")}";
        }

        // What these come to, as a person would type it. The agent does the same thing step by step,
        // with its own checks in between, so these are for reading and for the audit trail.
        switch (action.Kind)
        {
            case AgentActionKind.RemoveProject:
                return $"trash {ShellQuote(action.RepoPath ?? "")}";
            case AgentActionKind.Clone:
                return string.Join(' ', ["git", "clone", .. action.Remote is null ? Array.Empty<string>() : ["--origin", ShellQuote(action.Remote)], "--", ShellQuote(action.Url ?? ""), ShellQuote(action.RepoPath ?? "")]);
            case AgentActionKind.CleanBuildDirs:
                return "rm -rf " + string.Join(' ', (action.BuildDirs ?? []).Select(ShellQuote));
            case AgentActionKind.PullFastForward:
                return $"git -C {ShellQuote(action.RepoPath ?? "")} pull --ff-only {ShellQuote(action.Remote ?? "")} {ShellQuote(action.Branch ?? "")}";
            case AgentActionKind.OpenInT3:
                return $"t3 app {ShellQuote(action.RepoPath ?? "")}";
            case AgentActionKind.StartT3Thread:
                // The prompt goes on standard input, so the line says how long it is rather than repeating it.
                return string.Join(' ',
                    "t3", "thread", "start", ShellQuote(action.RepoPath ?? ""), "-",
                    "--title", ShellQuote(action.Title ?? ""), "--worktree", ShellQuote(action.WorktreeBase ?? ""),
                    "--branch", ShellQuote(action.NewBranch ?? ""), "--json",
                    $"< prompt ({(action.Prompt ?? "").Length} characters)");
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
        AgentActionKind.Refresh => "Read-only: the agent rescans this one entry and reports. It fetches only if that machine has auto-fetch on.",
        AgentActionKind.RemoveProject => "Moved to the trash, never deleted. The agent fetches and checks the project again first, and refuses if anything has changed since this was shown or it is no longer safe to remove. A linked worktree is removed with git worktree remove (no --force) instead.",
        AgentActionKind.Clone => "Only into the folder the project was removed from, only if that folder does not exist, and only from a URL the agent itself knows as a remote of this project.",
        AgentActionKind.CleanBuildDirs => "Deleted for good, not moved to the trash: a build or install recreates it. The agent refuses a folder that is not known build output, that git tracks files in, or that is reached through a link.",
        AgentActionKind.PullFastForward => "Fast-forward only: no merge commit, no rebase, no reset. The agent fetches first. A checked-out branch only moves when its working tree has no uncommitted changes; any other branch is moved with git fetch, which refuses anything but a fast-forward.",
        AgentActionKind.OpenInT3 => "Only opens the project in the T3 Code app on that machine. Nothing in the repository changes.",
        AgentActionKind.StartT3Thread => "T3 Code makes a new worktree on a new branch, so the checked-out working tree is not touched. The agent refuses a branch that already exists. The thread then runs with the permissions T3 Code gives its threads on that machine, which is often full access: read the prompt before you start it.",
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
        AgentActionKind.Refresh => $"Refresh {action.RepoPath}",
        AgentActionKind.RemoveProject => $"Remove {action.RepoPath}",
        AgentActionKind.Clone => $"Clone {action.Url} into {action.RepoPath}",
        AgentActionKind.CleanBuildDirs => action.BuildDirs is { Count: 1 } ? $"Delete build output {action.BuildDirs[0]}" : $"Delete {action.BuildDirs?.Count} build folders",
        AgentActionKind.PullFastForward => $"Fast-forward {action.Branch} from {action.Remote}",
        AgentActionKind.OpenInT3 => $"Open {action.RepoPath} in T3 Code",
        AgentActionKind.StartT3Thread => $"Start a T3 thread \"{action.Title}\" on {action.NewBranch}",
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

    /// <summary>
    /// A clone URL has to look like one: a scheme git speaks, or the <c>user@host:path</c> form, or an
    /// absolute path (a remote on a local disk). Nothing that could be read as an option, and no
    /// <c>transport::address</c> form, which has git run a helper program. Which URLs an agent will
    /// actually clone is decided by the agent, from what it knows itself; this only rejects nonsense.
    /// </summary>
    private static string? UrlProblem(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048)
        {
            return "The clone URL is missing or too long.";
        }

        if (url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || url[0] == '-' || url.Contains("::", StringComparison.Ordinal))
        {
            return "The clone URL is not a plain URL.";
        }

        string[] schemes = ["https://", "http://", "ssh://", "git://", "file://"];
        var known = schemes.Any(s => url.StartsWith(s, StringComparison.OrdinalIgnoreCase))
            || ScpLike().IsMatch(url)
            || PathProblem(url, "url") is null;
        return known ? null : "The clone URL is not a kind of URL the agent clones from.";
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+@[A-Za-z0-9.-]+:[^:]+$")]
    private static partial Regex ScpLike();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._/-]{0,199}$")]
    private static partial Regex RefName();

    /// <summary>A prompt is text: anything but NUL and the control characters that are not line breaks or tabs.</summary>
    private static string? PromptProblem(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "The prompt is empty.";
        }

        if (System.Text.Encoding.UTF8.GetByteCount(prompt) > MaxPromptBytes)
        {
            return $"The prompt is longer than {MaxPromptBytes / 1024} KB.";
        }

        return prompt.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) ? "The prompt contains control characters." : null;
    }

    private static string? TitleProblem(string title) =>
        string.IsNullOrWhiteSpace(title) || title.Length > MaxTitleChars || title.Any(char.IsControl)
            ? $"The thread title must be one line of 1 to {MaxTitleChars} characters."
            : null;

    /// <summary>
    /// A base for a worktree: a branch, a remote-tracking branch, a tag or a commit, written plainly
    /// (<c>origin/main</c>, <c>v1.2.0</c>, a sha). No revision syntax, nothing that could be read as an option.
    /// The agent also asks git that it names a commit.
    /// </summary>
    private static string? RefProblem(string reference) =>
        RefName().IsMatch(reference) && !reference.Contains("..", StringComparison.Ordinal) && !reference.EndsWith(".lock", StringComparison.Ordinal)
            && !reference.EndsWith('/') && !reference.EndsWith('.') && !reference.Contains("//", StringComparison.Ordinal)
            ? null
            : $"'{reference}' is not a branch, tag or commit the agent will start a worktree from.";

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

    /// <summary><see cref="AgentActionKind.StartT3Thread"/>: the thread T3 Code started, when it said. Older hubs ignore it.</summary>
    public T3StartedThread? Thread { get; init; }
}
