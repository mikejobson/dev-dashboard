using System.Diagnostics;
using DevDash.Agent.Actions;
using DevDash.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.T3Code;

/// <summary>
/// Runs the two T3 Code actions, after <see cref="ActionExecutor"/> has checked that actions are on and
/// <see cref="ActionValidator"/> has checked the repository, the new branch and the base. What reaches T3
/// is the fixed argument list of <see cref="T3Cli"/>; the prompt goes on standard input.
/// </summary>
public sealed class T3Actions(T3Service t3, IOptionsMonitor<AgentOptions> options, ILogger<T3Actions> logger)
{
    public async Task<ActionOutcome> RunAsync(AgentAction action, Stopwatch stopwatch, CancellationToken ct)
    {
        ActionOutcome Refuse(string why) => new(false, null, "Refused: " + why, [], stopwatch.ElapsedMilliseconds);

        var settings = options.CurrentValue.T3;
        if (!settings.Enabled)
        {
            return Refuse("the T3 Code integration is switched off on this machine (Agent:T3:Enabled).");
        }

        if (action.Kind == AgentActionKind.StartT3Thread && !settings.AllowStartFromHub)
        {
            logger.LogWarning("Refused to start a T3 Code thread in {Repo}: Agent:T3:AllowStartFromHub is off", action.RepoPath);
            return Refuse(AgentActions.StartFromHubOff(Environment.MachineName));
        }

        if (await t3.CliAsync(ct).ConfigureAwait(false) is not { } cli)
        {
            return Refuse($"T3 Code is not available here: {t3.Current?.Problem ?? "its CLI was not found"}.");
        }

        var repo = action.RepoPath!;
        if (T3Locator.IsBatchFile(cli.FileName))
        {
            // cmd.exe runs a .cmd and reads some characters even inside quotes. The prompt is safe (stdin); the arguments must be plain.
            string?[] values = [repo, action.Title, action.WorktreeBase, action.NewBranch];
            if (values.Any(v => v is not null && T3Locator.HasBatchMetacharacters(v)))
            {
                return Refuse("T3 Code's CLI here is a .cmd file, and the path, title or branch contains a character cmd.exe would act on (\" % ! ^ & | < >). Set Agent:T3:Path to t3.exe, or choose another title or branch.");
            }
        }

        if (action.Kind == AgentActionKind.OpenInT3)
        {
            logger.LogInformation("Opening {Repo} in T3 Code, as asked from the dashboard", repo);
            var opened = await cli.OpenAppAsync(repo, TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)), ct).ConfigureAwait(false);
            return new ActionOutcome(opened.Ok, opened.Started ? opened.ExitCode : null, AgentActions.Truncate(Output(opened)), [], stopwatch.ElapsedMilliseconds);
        }

        logger.LogInformation("Starting a T3 Code thread in {Repo} on new branch {Branch} from {Base}, as asked from the dashboard", repo, action.NewBranch, action.WorktreeBase);
        var result = await cli.StartThreadAsync(repo, action.Prompt!, action.Title!, action.WorktreeBase!, action.NewBranch!,
            TimeSpan.FromSeconds(Math.Max(5, settings.StartTimeoutSeconds)), ct).ConfigureAwait(false);
        if (!result.Started)
        {
            return Refuse("T3 Code's CLI could not be started: " + result.StdErr);
        }

        var thread = result.Ok ? T3Parser.ParseStarted(result.StdOut) : null;
        var message = result.Ok
            ? thread is null ? "T3 Code started the thread but did not say its id: " + Output(result) : $"Started T3 thread {thread.ThreadId}."
            : Output(result);
        logger.LogInformation("T3 thread start in {Repo} {Outcome}", repo, result.Ok ? "succeeded" : "failed");

        // The repository is rescanned straight away, so the new thread shows on the dashboard.
        return new ActionOutcome(result.Ok, result.ExitCode, AgentActions.Truncate(message), [repo], stopwatch.ElapsedMilliseconds) { Thread = thread };
    }

    private static string Output(Toolchains.ToolResult result) =>
        result.TimedOut
            ? "T3 Code's CLI did not answer in time."
            : string.Join('\n', new[] { result.StdOut.Trim(), result.StdErr.Trim() }.Where(s => s.Length > 0));
}
