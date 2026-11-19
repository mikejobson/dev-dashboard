using System.Diagnostics;
using DevDash.Agent.Scanning;
using DevDash.Agent.Service;
using DevDash.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.Updates;

/// <summary>
/// One thing at a time: an agent action and an update never overlap. The action executor holds this
/// while it runs an action (an update queued from the dashboard is itself an action), and auto mode
/// takes it before installing anything.
/// </summary>
public sealed class AgentGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}

/// <summary>
/// How a running agent hands over to the program files that are now on disk (a new version, or the
/// previous one after a roll back): it stops itself and lets the service manager start it again.
/// <list type="bullet">
/// <item><b>macOS:</b> the LaunchAgent has <c>KeepAlive</c>, so launchd starts it again whenever it exits.</item>
/// <item><b>Linux:</b> the unit has <c>Restart=always</c>.</item>
/// <item><b>Windows:</b> a scheduled task is not restarted when it exits, and starting it while it is
/// still running is ignored. So a small detached helper (the console build with <c>relaunch-service</c>)
/// waits for this process to end and then runs the task again. The non-zero exit code also triggers the
/// task's own restart-on-failure a minute later, should the helper not survive.</item>
/// </list>
/// Exiting, rather than asking the service manager to restart the service from inside it, means the
/// agent never has to survive being killed halfway through a command: it shuts down in its own time
/// (logs flushed, any scan cancelled cleanly) and the manager does what it already does for a crash.
/// Not running as a service (a terminal <c>run</c>), the process just exits with the same code.
/// </summary>
public sealed class RestartSignal(IHostApplicationLifetime lifetime, UpdateContext context, ILogger<RestartSignal> logger)
{
    /// <summary>EX_TEMPFAIL: "try again", which is what the service manager then does.</summary>
    public const int ExitCode = 75;

    private int _requested;

    /// <summary>True when the agent was started by its service definition (<c>run --service</c>).</summary>
    public bool RunningAsService { get; init; }

    public bool Requested => Volatile.Read(ref _requested) == 1;

    public void Request(string reason)
    {
        if (Interlocked.Exchange(ref _requested, 1) == 1)
        {
            return;
        }

        logger.LogWarning("Stopping so the service manager restarts the agent: {Reason}", reason);
        if (RunningAsService)
        {
            WindowsRelaunch.Start(context.Installation, logger);
        }

        lifetime.StopApplication();
    }
}

/// <summary>Windows only: starts the helper that runs the scheduled task again once this process has gone.</summary>
public static class WindowsRelaunch
{
    public const string Command = "relaunch-service";

    public static void Start(UpdateInstallation? installation, ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows() || installation is null)
        {
            return;
        }

        try
        {
            var psi = new ProcessStartInfo(installation.ConsoleBinary)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = installation.Directory,
            };
            psi.ArgumentList.Add(Command);
            psi.ArgumentList.Add("--wait-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var helper = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            logger?.LogWarning("Could not start the restart helper ({Error}); the scheduled task's restart-on-failure will start the agent instead.", ex.Message);
        }
    }

    /// <summary><c>devdash-agent relaunch-service --wait-pid N</c>: wait for the old process to exit, then start the service.</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var index = Array.IndexOf(args, "--wait-pid");
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var pid))
        {
            try
            {
                using var old = Process.GetProcessById(pid);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(90));
                await old.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException)
            {
                // Already gone, or taking too long; starting the task is harmless either way (a running task ignores it).
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        var environment = ServiceEnvironment.Current();
        var manager = ServiceManagers.For(environment);
        var runner = new ProcessCommandRunner();
        var ok = true;
        foreach (var command in manager.StartCommands)
        {
            var result = await runner.RunAsync(command, ct).ConfigureAwait(false);
            ok &= result.Ok || command.IgnoreFailure;
        }

        return ok ? 0 : 1;
    }
}

/// <summary>
/// While a new version is waiting to be confirmed: if the hub has not accepted a snapshot from it by
/// the deadline, put the previous version back and restart.
/// </summary>
public sealed class UpdateWatchdog(UpdateGuard guard, RestartSignal restart, TimeProvider time, ILogger<UpdateWatchdog> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (guard.Deadline is not { } deadline)
        {
            return;
        }

        var state = guard.State.Pending;
        logger.LogWarning("This is devdash-agent {Version}, just updated from {Previous} (start {Starts}). It must report to the hub by {Deadline:HH:mm:ss} or the previous version is put back.",
            guard.CurrentVersion, state?.FromVersion, state?.Starts, deadline.ToLocalTime());
        try
        {
            var wait = deadline - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, time, stoppingToken).ConfigureAwait(false);
            }

            if (!guard.IsVerifying)
            {
                return; // confirmed in time
            }

            if (guard.RollBack("it ran without reporting to the hub in time") is { } report)
            {
                logger.LogError("{Detail}", report.Detail);
                restart.Request(report.Detail ?? "rolled back");
            }
            else
            {
                logger.LogError("devdash-agent {Version} did not report to the hub in time, and the previous version could not be restored. Staying on {Version}.", guard.CurrentVersion, guard.CurrentVersion);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }
}

/// <summary>
/// <c>auto</c> mode only: every few hours (with a random part, so a fleet does not ask at once) see
/// whether there is a newer release, and install it. Never while an action is running, and never
/// when the hub cannot be reached, because the new version could then not prove itself.
/// </summary>
public sealed class AutoUpdateWorker(
    AgentUpdater updater,
    AgentGate gate,
    RestartSignal restart,
    UpdateContext context,
    HubClient hub,
    IOptionsMonitor<AgentOptions> options,
    TimeProvider time,
    ILogger<AutoUpdateWorker> logger) : BackgroundService
{
    /// <summary>A version that was rolled back is not tried again by auto mode for this long.</summary>
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.CurrentValue.Updates;
        if (AgentUpdateModes.Normalize(settings.Mode) is null)
        {
            logger.LogWarning("Agent:Updates:Mode '{Mode}' is not off, notify or auto; treating it as off.", settings.Mode);
        }

        if (!settings.IsAuto)
        {
            return;
        }

        if (context.Installation is null || !context.Trust.IsConfigured)
        {
            logger.LogWarning("Updates are set to auto, but this agent cannot update itself: {Reason}",
                context.Installation is null ? context.Problem : "this build has no release signing key.");
            return;
        }

        logger.LogInformation("Updates are set to auto: checking for a new release about every {Hours} h.", settings.CheckIntervalHours);
        try
        {
            // Not at once: a machine that has just started (or just updated) has better things to do first.
            await Task.Delay(TimeSpan.FromMinutes(5 + Random.Shared.NextDouble() * 25), time, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (await CheckOnceAsync(stoppingToken).ConfigureAwait(false))
                {
                    return;
                }

                await Task.Delay(NextDelay(options.CurrentValue.Updates.CheckIntervalHours, Random.Shared.NextDouble()), time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    /// <summary>The interval plus up to 25% more, never under half an hour.</summary>
    public static TimeSpan NextDelay(double intervalHours, double random)
    {
        var hours = double.IsFinite(intervalHours) ? Math.Max(0.5, intervalHours) : 6;
        return TimeSpan.FromHours(hours * (1 + 0.25 * Math.Clamp(random, 0, 1)));
    }

    /// <summary>True when <paramref name="version"/> was rolled back recently enough that auto mode leaves it alone.</summary>
    public static bool RecentlyFailed(UpdateState state, string version, DateTimeOffset now) =>
        state.FailedVersion == version && state.FailedAt is { } at && now - at < RetryFailedAfter;

    /// <summary>One check. Returns true when a new version was installed and the agent is restarting.</summary>
    public async Task<bool> CheckOnceAsync(CancellationToken ct)
    {
        if (!options.CurrentValue.Updates.IsAuto || context.Installation is not { } installation)
        {
            return false;
        }

        var check = await updater.RunAsync(new UpdateRequest(CheckOnly: true), ct).ConfigureAwait(false);
        if (check.Status != UpdateStatus.Available || check.TargetVersion is not { } target)
        {
            if (check.Status is UpdateStatus.Failed or UpdateStatus.Refused)
            {
                logger.LogInformation("Update check: {Message}", check.Message);
            }

            return false;
        }

        if (RecentlyFailed(new UpdateStateStore(installation.StateFile).Read(), target, time.GetUtcNow()))
        {
            logger.LogInformation("Release {Target} is available but was rolled back here recently; not trying it again yet.", target);
            return false;
        }

        if (!await hub.PingAsync(ct).ConfigureAwait(false))
        {
            logger.LogInformation("Release {Target} is available, but the hub is not reachable right now, so the update waits.", target);
            return false;
        }

        UpdateResult result;
        using (await gate.EnterAsync(ct).ConfigureAwait(false))
        {
            result = await updater.RunAsync(new UpdateRequest(target), ct).ConfigureAwait(false);
        }

        logger.LogInformation("Automatic update to {Target}: {Message}", target, result.Message);
        if (!result.Installed)
        {
            return false;
        }

        restart.Request($"updated to {target}");
        return true;
    }
}

public static class UpdateServices
{
    /// <summary>Registers self-update for the long-running agent (<c>run</c>).</summary>
    public static IServiceCollection AddAgentUpdates(this IServiceCollection services, UpdateContext context, UpdateGuard? guard, bool runningAsService)
    {
        services.AddUpdater(context);
        services.AddSingleton(sp => new RestartSignal(
            sp.GetRequiredService<IHostApplicationLifetime>(), context, sp.GetRequiredService<ILogger<RestartSignal>>()) { RunningAsService = runningAsService });
        services.AddHostedService<AutoUpdateWorker>();
        if (guard is not null)
        {
            services.AddSingleton(guard);
            services.AddHostedService<UpdateWatchdog>();
        }

        return services;
    }

    /// <summary>Just the updater, for <c>devdash-agent update</c>.</summary>
    public static IServiceCollection AddUpdater(this IServiceCollection services, UpdateContext context)
    {
        services.AddSingleton(context);
        services.AddSingleton<AgentGate>();
        services.AddSingleton<IBinaryProbe, ProcessBinaryProbe>();
        services.AddSingleton<IUpdateFiles, UpdateFiles>();
        // No overall timeout: a release is tens of megabytes. Each update carries its own deadline.
        services.AddHttpClient<AgentUpdater>(c => c.Timeout = Timeout.InfiniteTimeSpan);
        return services;
    }

    /// <summary>The guard for this process's own installation, or null when it is not an installed release.</summary>
    public static UpdateGuard? GuardFor(UpdateContext context, UpdateOptions options) =>
        context.Installation is { } installation
            ? new UpdateGuard(new UpdateStateStore(installation.StateFile), new UpdateFiles(), TimeProvider.System, options, installation.Platform, context.CurrentVersion)
            : null;
}
