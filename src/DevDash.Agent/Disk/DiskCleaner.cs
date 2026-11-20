using System.Diagnostics;
using DevDash.Agent.Actions;
using DevDash.Agent.Toolchains;
using DevDash.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.Disk;

/// <summary>
/// Runs <see cref="AgentActionKind.CleanCache"/>, after <see cref="ActionExecutor"/> has checked that actions are on.
/// The action names a cache id and nothing else: where the cache is and what to run come from this agent
/// (<see cref="DiskCollector.LocateAsync"/> and <see cref="DiskCaches"/>), found again right before cleaning.
/// </summary>
public sealed class DiskCleaner(
    DiskCollector collector,
    DiskService disk,
    IToolRunner runner,
    IOptionsMonitor<AgentOptions> options,
    ILogger<DiskCleaner> logger,
    ITrash? trash = null,
    RescanRequests? rescans = null)
{
    public async Task<ActionOutcome> RunAsync(AgentAction action, Stopwatch stopwatch, CancellationToken ct)
    {
        ActionOutcome Refuse(string why) => new(false, null, "Refused: " + why, [], stopwatch.ElapsedMilliseconds);

        if (AgentActions.Validate(action) is { } shape || action.Kind != AgentActionKind.CleanCache)
        {
            return Refuse(AgentActions.Validate(action) ?? "Not a cache clean.");
        }

        var settings = options.CurrentValue.Disk;
        if (!settings.Enabled)
        {
            return Refuse("the disk report is switched off on this machine (Agent:Disk:Enabled).");
        }

        var info = DiskCaches.Find(action.CacheId)!;
        if (info.Recipe is not { } recipe)
        {
            return Refuse($"{info.Title} is reported only; the agent never cleans it.");
        }

        if (settings.Skip.Contains(info.Id.ToString()))
        {
            return Refuse($"{info.Title} is left out on this machine (Agent:Disk:Skip).");
        }

        if (!collector.Applicable().Contains(info.Id))
        {
            return Refuse($"{info.Title} is a macOS cache.");
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 3600));
        var location = await collector.LocateAsync(info.Id, timeout, ct).ConfigureAwait(false);
        if (location is null || (location.Path is { } path && !Directory.Exists(path)))
        {
            return Refuse($"{info.Title} was not found on this machine, so there is nothing to clean.");
        }

        ActionOutcome outcome;
        if (recipe.TrashContents)
        {
            outcome = TrashContents(info, location.Path!, stopwatch);
        }
        else
        {
            if (location.Tool is not { } tool)
            {
                return Refuse($"{recipe.Tool} was not found on this machine, so {info.Title} cannot be cleaned from here.");
            }

            logger.LogInformation("Cleaning {Cache}, as asked from the dashboard: {Command}", info.Title, DiskCaches.CommandLine(info.Id));
            var result = await runner.RunAsync(tool.Command([.. recipe.Args]), TimeSpan.FromSeconds(Math.Clamp(settings.CleanTimeoutSeconds, 10, 7200)), ct).ConfigureAwait(false);
            if (!result.Started)
            {
                return Refuse($"{recipe.Tool} could not be started: {result.StdErr}");
            }

            var output = result.TimedOut
                ? $"{recipe.Tool} did not finish in time."
                : string.Join('\n', new[] { result.StdOut.Trim(), result.StdErr.Trim() }.Where(s => s.Length > 0));
            outcome = new ActionOutcome(result.Ok, result.ExitCode, AgentActions.Truncate(output.Length == 0 ? "Done." : output), [], stopwatch.ElapsedMilliseconds);
        }

        logger.LogInformation("Cleaning {Cache} {Outcome}", info.Title, outcome.Succeeded ? "succeeded" : "failed");

        // Measured again straight away, and pushed, so the Disk page shows what it freed.
        try
        {
            await disk.RefreshItemAsync(info.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not measure {Cache} again after cleaning: {Error}", info.Title, ex.Message);
        }

        rescans?.RequestMachine();
        return outcome with { DurationMs = stopwatch.ElapsedMilliseconds };
    }

    /// <summary>Moves each entry inside the folder to the trash, one at a time; the folder itself stays. Never through a link.</summary>
    private ActionOutcome TrashContents(DiskCaches.Info info, string folder, Stopwatch stopwatch)
    {
        if (trash is null)
        {
            return new ActionOutcome(false, null, "Refused: this agent has no trash to move things into.", [], stopwatch.ElapsedMilliseconds);
        }

        var dir = new DirectoryInfo(folder);
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0 || dir.LinkTarget is not null)
        {
            return new ActionOutcome(false, null, $"Refused: {folder} is a link, so the agent leaves it alone.", [], stopwatch.ElapsedMilliseconds);
        }

        List<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(folder).Where(e => !Path.GetFileName(e).StartsWith('.')).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ActionOutcome(false, 1, $"Could not read {folder}: {ex.Message}", [], stopwatch.ElapsedMilliseconds);
        }

        if (entries.Count == 0)
        {
            return new ActionOutcome(true, 0, $"{info.Title} is already empty.", [], stopwatch.ElapsedMilliseconds);
        }

        logger.LogInformation("Moving the {Count} entries of {Folder} to {Trash}, as asked from the dashboard", entries.Count, folder, trash.Name);
        var failed = new List<string>();
        foreach (var entry in entries)
        {
            var moved = trash.Move(entry);
            if (!moved.Ok)
            {
                failed.Add($"{Path.GetFileName(entry)}: {moved.Error}");
            }
        }

        var done = entries.Count - failed.Count;
        var text = failed.Count == 0
            ? $"Moved {done} folder(s) from {folder} to {trash.Name}."
            : $"Moved {done} of {entries.Count} to {trash.Name}. Not moved:\n{string.Join('\n', failed)}";
        return new ActionOutcome(failed.Count == 0, failed.Count == 0 ? 0 : 1, AgentActions.Truncate(text), [], stopwatch.ElapsedMilliseconds);
    }
}
