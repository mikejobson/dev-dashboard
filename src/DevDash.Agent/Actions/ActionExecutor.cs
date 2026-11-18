using System.Diagnostics;
using DevDash.Agent.Git;
using DevDash.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.Actions;

/// <summary>What happened to one action, and which folders to rescan because of it.</summary>
public sealed record ActionOutcome(bool Succeeded, int? ExitCode, string Output, IReadOnlyList<string> AffectedPaths, long DurationMs)
{
    /// <summary>True when the agent declined before running anything that changes the repository.</summary>
    public bool Refused => ExitCode is null;

    public ActionResultRequest ToResult() => new() { Succeeded = Succeeded, ExitCode = ExitCode, Output = Output, DurationMs = DurationMs };
}

/// <summary>
/// Runs one allowlisted action. The only commands that can leave here are the fixed ones in
/// <see cref="AgentActions.GitArgs"/>: there is no path by which text from the hub becomes a command.
/// Git is started through <see cref="GitRunner"/>, so arguments go as a list (no shell), credential
/// prompts are off and every call has a timeout.
/// </summary>
public sealed class ActionExecutor(GitRunner git, ActionValidator validator, IOptionsMonitor<AgentOptions> options, ILogger<ActionExecutor> logger)
{
    /// <summary>No background maintenance and no fsmonitor daemon as a side effect of an action.</summary>
    private static readonly string[] Prefix = ["-c", "core.fsmonitor=false", "-c", "gc.auto=0", "-c", "maintenance.auto=false"];

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public async Task<ActionOutcome> ExecuteAsync(AgentAction? action, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        ActionOutcome Refuse(string why) => new(false, null, "Refused: " + why, [], stopwatch.ElapsedMilliseconds);

        // Checked here as well as in the worker, so nothing can run actions on a machine that has not opted in.
        if (!options.CurrentValue.Actions.Enabled)
        {
            return Refuse("agent actions are not enabled on this machine.");
        }

        await _oneAtATime.WaitAsync(ct).ConfigureAwait(false);
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
            var repo = action!.RepoPath;

            if (action.Kind == AgentActionKind.Push && await BehindRefusalAsync(action, local, network, ct).ConfigureAwait(false) is { } behind)
            {
                logger.LogWarning("Refused push of {Branch} in {Repo}: {Reason}", action.Branch, repo, behind);
                return Refuse(behind);
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
            _oneAtATime.Release();
        }
    }

    /// <summary>
    /// A push is only attempted when it would be a fast-forward. Fetch the remote first so the answer
    /// is current, then refuse if the remote branch has commits the local branch lacks. (The push
    /// itself is also unforced, so the remote would reject it anyway.)
    /// </summary>
    private async Task<string?> BehindRefusalAsync(AgentAction action, TimeSpan local, TimeSpan network, CancellationToken ct)
    {
        var repo = action.RepoPath;
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
