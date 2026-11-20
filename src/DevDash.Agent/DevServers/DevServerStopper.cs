using System.Diagnostics;
using DevDash.Agent.Actions;
using DevDash.Agent.Disk;
using DevDash.Agent.Toolchains;
using DevDash.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.DevServers;

/// <summary>
/// Runs <see cref="AgentActionKind.StopDevServer"/>, after <see cref="ActionExecutor"/> has checked that actions are on.
/// The pid alone is never trusted: the process is looked at again, and must have the start time and command line the
/// user was shown (so a pid the system has since given to another process is refused), be listening on a port now
/// (as the dev server list finds them), and run inside the named repository, which must be one this agent scans. Then it is sent SIGTERM, never SIGKILL.
/// </summary>
public sealed class DevServerStopper(
    IProcessProbe probe,
    IProcessSignal signal,
    ActionValidator validator,
    IOptionsMonitor<AgentOptions> options,
    ILogger<DevServerStopper> logger,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<ActionOutcome> RunAsync(AgentAction action, Stopwatch stopwatch, CancellationToken ct)
    {
        ActionOutcome Refuse(string why) => new(false, null, "Refused: " + why, [], stopwatch.ElapsedMilliseconds);

        if (action.Kind != AgentActionKind.StopDevServer)
        {
            return Refuse("Not a dev server stop.");
        }

        if (AgentActions.Validate(action) is { } shape)
        {
            return Refuse(shape);
        }

        if (!options.CurrentValue.DevServers.Enabled)
        {
            return Refuse("the dev server list is switched off on this machine (Agent:DevServers:Enabled).");
        }

        var repo = action.RepoPath!;
        if (await validator.RepoRefusalAsync(repo, ct).ConfigureAwait(false) is { } notRepo)
        {
            return Refuse(notRepo);
        }

        var pid = action.Pid!.Value;
        if (pid == Environment.ProcessId)
        {
            return Refuse("that is the agent itself.");
        }

        // Only a process listening right now, as the dev server list saw it: never any process that merely works in the repository.
        ListenResult listening;
        try
        {
            listening = await probe.ListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Refuse($"could not list listening processes: {ex.Message}. Nothing was stopped.");
        }

        if (!listening.Processes.Any(p => p.Pid == pid))
        {
            return Refuse($"process {pid} is not listening on a port any more, so it is not a dev server this agent lists. Nothing was stopped.");
        }

        if (await probe.InspectAsync(pid, ct).ConfigureAwait(false) is not { } process)
        {
            return Refuse($"no process {pid} is running any more. Nothing was stopped.");
        }

        if (Mismatch(action, process) is { } different)
        {
            return Refuse(different);
        }

        // The process's own working folder decides; on Windows, where that cannot be read, a path on its command line.
        var caseSensitive = OperatingSystem.IsLinux();
        var resolvedRepo = PathSafety.Resolve(repo);
        IEnumerable<string> evidence = process.Cwd is { } cwd ? [cwd] : DevServerCollector.PathsIn(process);
        if (!evidence.Any(p => DevServerCollector.Within(resolvedRepo, PathSafety.Resolve(p), caseSensitive) || DevServerCollector.Within(repo, p, caseSensitive)))
        {
            return Refuse(process.Cwd is { } where
                ? $"process {pid} works in {where}, which is not inside {repo}."
                : $"nothing shows that process {pid} runs inside {repo}.");
        }

        logger.LogInformation("Sending {Signal} to process {Pid} ({Name}) in {Repo}, as asked from the dashboard", signal.Name, pid, process.Name, repo);
        if (signal.Terminate(pid) is { } failed)
        {
            return new ActionOutcome(false, 1, AgentActions.Truncate($"Could not send {signal.Name} to process {pid}: {failed}"), [repo], stopwatch.ElapsedMilliseconds);
        }

        // Give it a moment to shut down cleanly, then say how it went. Nothing stronger is ever sent.
        var deadline = _time.GetUtcNow() + TimeSpan.FromSeconds(Math.Clamp(options.CurrentValue.DevServers.StopWaitSeconds, 0, 60));
        while (true)
        {
            var now = await probe.InspectAsync(pid, ct).ConfigureAwait(false);
            if (now is null || Mismatch(action, now) is not null)
            {
                return new ActionOutcome(true, 0, $"Sent {signal.Name} to {process.Name} (process {pid}); it has stopped.", [repo], stopwatch.ElapsedMilliseconds);
            }

            if (_time.GetUtcNow() >= deadline)
            {
                return new ActionOutcome(true, 0, $"Sent {signal.Name} to {process.Name} (process {pid}). It was still running a few seconds later, so it may still be shutting down; nothing more was sent.", [repo], stopwatch.ElapsedMilliseconds);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Why the process is not the one the user was shown, or null when it is.</summary>
    private static string? Mismatch(AgentAction action, ListeningProcess process)
    {
        if (process.StartedAt is not { } started)
        {
            return $"could not read when process {process.Pid} started, so it cannot be told from a later process with the same id.";
        }

        if (!DevServerText.SameStart(started, action.ExpectedStartedAt!.Value))
        {
            return $"process {process.Pid} is not the one that was shown: it started {started:u}, not {action.ExpectedStartedAt.Value.ToUniversalTime():u}. Its id has been reused; nothing was stopped.";
        }

        var command = process.Args.Count > 0 ? DevServerText.Command(process.Args) : null;
        return command == action.ExpectedCommand
            ? null
            : $"process {process.Pid} is running a different command from the one shown. Nothing was stopped.";
    }
}

public static class MachineFactServices
{
    /// <summary>The disk report and the dev server list: what the scanner reads, and the two actions.</summary>
    public static IServiceCollection AddDiskAndDevServers(this IServiceCollection services)
    {
        services.AddSingleton<DiskCollector>();
        services.AddSingleton(sp => new DiskService(
            sp.GetRequiredService<DiskCollector>(), sp.GetRequiredService<IOptionsMonitor<AgentOptions>>(), sp.GetRequiredService<ILogger<DiskService>>()));
        services.AddSingleton(sp => ProcessProbe.ForThisMachine(sp.GetRequiredService<IToolRunner>()));
        services.AddSingleton(_ => ProcessSignal.ForThisMachine());
        services.AddSingleton<DevServerCollector>();
        services.AddSingleton<DevServerStopper>();
        services.AddSingleton<DiskCleaner>();
        return services;
    }

    /// <summary>The background measuring: only the long-running service does it.</summary>
    public static IServiceCollection AddDiskWorker(this IServiceCollection services) => services.AddHostedService<DiskWorker>();
}
