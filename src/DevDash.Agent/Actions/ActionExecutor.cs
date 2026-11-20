using System.Diagnostics;
using DevDash.Agent.Git;
using DevDash.Agent.Updates;
using DevDash.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.Actions;

/// <summary>What happened to one action, and which folders to rescan because of it.</summary>
public sealed record ActionOutcome(bool Succeeded, int? ExitCode, string Output, IReadOnlyList<string> AffectedPaths, long DurationMs)
{
    /// <summary>True when the agent replaced its own program files and must restart once the result is reported.</summary>
    public bool RestartRequired { get; init; }

    /// <summary>True when the agent declined before running anything that changes the repository.</summary>
    public bool Refused => ExitCode is null;

    public ActionResultRequest ToResult() => new() { Succeeded = Succeeded, ExitCode = ExitCode, Output = Output, DurationMs = DurationMs };
}

/// <summary>
/// Runs one allowlisted action. The only commands that can leave here are the fixed ones in
/// <see cref="AgentActions.GitArgs"/> and <see cref="ProjectActions"/>: there is no path by which text
/// from the hub becomes a command.
/// Git is started through <see cref="GitRunner"/>, so arguments go as a list (no shell), credential
/// prompts are off and every call has a timeout.
/// <para>
/// Two separate switches, both local to this machine: git actions need <c>Agent:Actions:Enabled</c>;
/// <see cref="AgentActionKind.UpdateAgent"/> needs <c>Agent:Updates:Mode</c> to be notify or auto.
/// Neither turns the other on. <see cref="AgentActionKind.Refresh"/> needs neither: it runs no command
/// here at all, only asks the scan loop for a scan of one entry sooner than it would have done it.
/// </para>
/// </summary>
public sealed class ActionExecutor(
    GitRunner git,
    ActionValidator validator,
    IOptionsMonitor<AgentOptions> options,
    ILogger<ActionExecutor> logger,
    AgentGate? gate = null,
    AgentUpdater? updater = null,
    RescanRequests? rescans = null,
    ProjectActions? projects = null)
{
    /// <summary>No background maintenance and no fsmonitor daemon as a side effect of an action.</summary>
    private static readonly string[] Prefix = ["-c", "core.fsmonitor=false", "-c", "gc.auto=0", "-c", "maintenance.auto=false"];

    // Shared with auto-update, so an update never starts while an action is running, and the reverse.
    private readonly AgentGate _gate = gate ?? new AgentGate();

    // Without a scanner and a trash (an agent built by hand in a test), removal is refused; the rest works.
    private readonly ProjectActions _projects = projects ?? new ProjectActions(git, validator, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectActions>.Instance);

    /// <summary>
    /// Refreshes: each path's top-level entry is rescanned by the scan loop (forced sizes, and a fetch
    /// only if this machine has auto-fetch on) and pushed. Allowed whatever <c>Actions:Enabled</c> says.
    /// The batch is handed to the scan loop in one go, so several requests for one entry are one rescan.
    /// Returns one outcome per action, in order.
    /// </summary>
    public async Task<IReadOnlyList<ActionOutcome>> RefreshAsync(IReadOnlyList<AgentAction?> actions, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcomes = new ActionOutcome?[actions.Count];
        var accepted = new List<int>();
        for (var i = 0; i < actions.Count; i++)
        {
            var refusal = validator.RefreshRefusal(actions[i]) ?? (rescans is null ? "this agent cannot rescan on request." : null);
            if (refusal is null)
            {
                accepted.Add(i);
            }
            else
            {
                logger.LogWarning("Refused refresh of {Path}: {Reason}", actions[i]?.RepoPath, refusal);
                outcomes[i] = new ActionOutcome(false, null, "Refused: " + refusal, [], stopwatch.ElapsedMilliseconds);
            }
        }

        if (accepted.Count > 0)
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(5, options.CurrentValue.Actions.RefreshTimeoutSeconds));
            var tasks = rescans!.Refresh([.. accepted.Select(i => actions[i]!.RepoPath!)]);
            for (var n = 0; n < accepted.Count; n++)
            {
                RefreshOutcome result;
                try
                {
                    // Every wait started at the same moment: this is one deadline for the batch, not one each.
                    var remaining = timeout - stopwatch.Elapsed;
                    result = await tasks[n].WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    result = new RefreshOutcome(false, "The agent is busy with another scan. This entry is rescanned as soon as that finishes, and the result arrives with it.");
                }

                logger.LogInformation("Refresh of {Path} asked for from the dashboard: {Message}", actions[accepted[n]]!.RepoPath, result.Message);
                outcomes[accepted[n]] = new ActionOutcome(result.Succeeded, result.Succeeded ? 0 : 1, AgentActions.Truncate(result.Message), [], stopwatch.ElapsedMilliseconds);
            }
        }

        return outcomes!;
    }

    public async Task<ActionOutcome> ExecuteAsync(AgentAction? action, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        ActionOutcome Refuse(string why) => new(false, null, "Refused: " + why, [], stopwatch.ElapsedMilliseconds);

        if (action is { Kind: AgentActionKind.Refresh })
        {
            // Not a git action: nothing is run in the repository, so Actions:Enabled does not come into it.
            return (await RefreshAsync([action], ct).ConfigureAwait(false))[0];
        }

        if (action is { Kind: AgentActionKind.UpdateAgent })
        {
            // Not a git action: allowed by this machine's update mode, whatever Actions:Enabled says.
            if (!options.CurrentValue.Updates.AcceptsRequests)
            {
                return Refuse("updates are switched off on this machine (Agent:Updates:Mode is off).");
            }

            if (AgentActions.Validate(action) is { } shape)
            {
                return Refuse(shape);
            }

            if (updater is null)
            {
                return Refuse("this agent cannot update itself.");
            }

            using (await _gate.EnterAsync(ct).ConfigureAwait(false))
            {
                var update = await updater.RunAsync(new UpdateRequest(action.Version), ct).ConfigureAwait(false);
                logger.LogInformation("Update to {Version} requested from the dashboard: {Status}: {Message}", action.Version, update.Status, update.Message);
                return update.Status switch
                {
                    UpdateStatus.Installed => new ActionOutcome(true, 0, AgentActions.Truncate(update.Message + " Restarting now."), [], stopwatch.ElapsedMilliseconds) { RestartRequired = true },
                    UpdateStatus.UpToDate => new ActionOutcome(true, 0, update.Message, [], stopwatch.ElapsedMilliseconds),
                    UpdateStatus.Refused => Refuse(update.Message),
                    _ => new ActionOutcome(false, 1, AgentActions.Truncate(update.Message), [], stopwatch.ElapsedMilliseconds),
                };
            }
        }

        // Checked here as well as in the worker, so nothing can run git actions on a machine that has not opted in.
        if (!options.CurrentValue.Actions.Enabled)
        {
            return Refuse("agent actions are not enabled on this machine.");
        }

        var held = await _gate.EnterAsync(ct).ConfigureAwait(false);
        try
        {
            if (await validator.RefusalAsync(action, ct).ConfigureAwait(false) is { } refusal)
            {
                logger.LogWarning("Refused action {Kind} in {Repo}: {Reason}", action?.Kind, action?.RepoPath, refusal);
                return Refuse(refusal);
            }

            var current = options.CurrentValue;
            var local = TimeSpan.FromSeconds(current.GitTimeoutSeconds);
            var network = TimeSpan.FromSeconds(Math.Max(10, current.Actions.NetworkTimeoutSeconds));
            var repo = action!.RepoPath!;

            if (action.Kind == AgentActionKind.Push && await BehindRefusalAsync(action, local, network, ct).ConfigureAwait(false) is { } behind)
            {
                logger.LogWarning("Refused push of {Branch} in {Repo}: {Reason}", action.Branch, repo, behind);
                return Refuse(behind);
            }

            // The kinds that are more than one fixed git command: remove, clone, build output, fast-forward.
            if (action.Kind is AgentActionKind.RemoveProject or AgentActionKind.Clone or AgentActionKind.CleanBuildDirs or AgentActionKind.PullFastForward)
            {
                var outcome = await _projects.RunAsync(action, stopwatch, ct).ConfigureAwait(false);
                if (outcome.Refused)
                {
                    logger.LogWarning("Refused action {Kind} in {Repo}: {Reason}", action.Kind, repo, outcome.Output);
                }
                else
                {
                    logger.LogInformation("Action {Kind} in {Repo} {Outcome}", action.Kind, repo, outcome.Succeeded ? "succeeded" : "failed");
                }

                return outcome;
            }

            logger.LogInformation("Running action: {Command}", AgentActions.CommandLine(action));
            var result = action.Kind is AgentActionKind.Fetch
                ? await git.FetchAsync(repo, network, ct).ConfigureAwait(false)
                : await git.ExecuteAsync(repo, [.. Prefix, .. AgentActions.GitArgs(action)], action.Kind is AgentActionKind.Push ? network : local, ct).ConfigureAwait(false);

            var output = string.Join('\n', new[] { result.StdOut.Trim(), result.StdErr.Trim() }.Where(s => s.Length > 0));
            logger.LogInformation("Action {Kind} in {Repo} {Outcome} (exit {Exit})", action.Kind, repo, result.Ok ? "succeeded" : "failed", result.ExitCode);

            string[] affected = action.WorktreePath is null ? [repo] : [repo, action.WorktreePath];
            return new ActionOutcome(result.Ok, result.ExitCode, AgentActions.Truncate(output), affected, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Action {Kind} failed unexpectedly", action?.Kind);
            return Refuse("the agent hit an unexpected error: " + ex.Message);
        }
        finally
        {
            held.Dispose();
        }
    }

    /// <summary>
    /// A push is only attempted when it would be a fast-forward. Fetch the remote first so the answer
    /// is current, then refuse if the remote branch has commits the local branch lacks. (The push
    /// itself is also unforced, so the remote would reject it anyway.)
    /// </summary>
    private async Task<string?> BehindRefusalAsync(AgentAction action, TimeSpan local, TimeSpan network, CancellationToken ct)
    {
        var repo = action.RepoPath!;
        var fetch = await git.ExecuteAsync(repo, [.. Prefix, "fetch", "--prune", "--quiet", "--", action.Remote!], network, ct).ConfigureAwait(false);
        if (!fetch.Ok)
        {
            return $"could not fetch {action.Remote} to check whether {action.Branch} is behind: {AgentActions.Truncate(fetch.StdErr)}";
        }

        var tracking = $"refs/remotes/{action.Remote}/{action.Branch}";
        var exists = await git.RunAsync(repo, ["rev-parse", "--verify", "--quiet", tracking], local, ct).ConfigureAwait(false);
        if (!exists.Ok)
        {
            return null; // the branch is new on the remote
        }

        var missing = await git.RunAsync(repo, ["log", "--format=%h", $"refs/heads/{action.Branch}..{tracking}"], local, ct).ConfigureAwait(false);
        if (!missing.Ok)
        {
            return $"could not compare {action.Branch} with {action.Remote}/{action.Branch}.";
        }

        var count = missing.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        return count == 0
            ? null
            : $"{action.Branch} is {count} commit(s) behind {action.Remote}/{action.Branch}. Rebase or merge by hand first; the agent never does that for you.";
    }
}
