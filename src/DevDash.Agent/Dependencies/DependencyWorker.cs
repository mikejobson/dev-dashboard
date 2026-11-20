using DevDash.Agent.Toolchains;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.Dependencies;

/// <summary>The repositories the last full scan found: what the dependency checks work through.</summary>
public sealed class DependencyTargets
{
    private volatile IReadOnlyList<string> _paths = [];

    public IReadOnlyList<string> Paths => _paths;

    public void Offer(IEnumerable<string> repositories) => _paths = repositories.Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>
/// Works through the scanned repositories in the background, one at a time, checking each about once a
/// day (<see cref="DependencyOptions"/>). It never holds up a scan: the scanner only reads what is
/// already in <see cref="DependencyCache"/>, and a result shows in the snapshot of the next scan.
/// </summary>
public sealed class DependencyWorker(
    DependencyChecker checker,
    DependencyCache cache,
    DependencyTargets targets,
    IOptionsMonitor<AgentOptions> options,
    ILogger<DependencyWorker> logger,
    TimeProvider? time = null) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.CurrentValue.Dependencies.StartDelaySeconds)), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                var settings = options.CurrentValue.Dependencies;
                var worked = false;
                try
                {
                    worked = await CheckNextAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Dependency check failed: {Error}", ex.Message);
                }

                await Task.Delay(worked ? TimeSpan.FromSeconds(Math.Max(1, settings.PauseSeconds)) : Idle, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    /// <summary>The next repository that is due, or null when none is (or the checks are off).</summary>
    public string? NextDue()
    {
        var settings = options.CurrentValue.Dependencies;
        if (!settings.Enabled)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var interval = TimeSpan.FromHours(Math.Max(0.1, settings.IntervalHours));
        var retry = TimeSpan.FromHours(Math.Clamp(settings.RetryHours, 0.1, Math.Max(0.1, settings.IntervalHours)));

        // Never checked first, then the one checked longest ago.
        return targets.Paths
            .Select(path => (Path: path, Entry: cache.Get(path)))
            .Where(x => x.Entry is null || now - x.Entry.AttemptedAt >= (x.Entry.Transient ? retry : interval))
            .OrderBy(x => x.Entry?.AttemptedAt ?? DateTimeOffset.MinValue)
            .Select(x => x.Path)
            .FirstOrDefault();
    }

    /// <summary>Checks the one repository that is most due. False when there was nothing to do.</summary>
    public async Task<bool> CheckNextAsync(CancellationToken ct)
    {
        if (NextDue() is not { } repo)
        {
            return false;
        }

        if (!Directory.Exists(repo))
        {
            return false; // removed since the scan; the next scan drops it from the list
        }

        var settings = options.CurrentValue.Dependencies;
        var previous = cache.Get(repo);
        var entry = await checker.CheckAsync(repo, TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 3600)), ct).ConfigureAwait(false);

        // Offline or timed out, with an earlier answer in hand: keep showing that one, and remember that this was tried.
        if (entry.Transient && previous is { Health.Reports.Count: > 0 } && previous.Health.Reports.Any(r => r.Vulnerable is not null || r.Outdated is not null))
        {
            entry = previous with { AttemptedAt = entry.AttemptedAt, Transient = true };
        }

        cache.Set(repo, entry);
        cache.Save();
        if (entry.Health.Reports.Count > 0)
        {
            logger.LogInformation("Dependencies of {Repo}: {Summary}", Path.GetFileName(repo), string.Join("; ", entry.Health.Reports.Select(r =>
                r.Vulnerable is null && r.Outdated is null ? $"{r.Ecosystem} skipped ({r.Skipped})" : $"{r.Ecosystem} {r.Vulnerable?.Total.ToString() ?? "?"} vulnerable, {r.Outdated?.ToString() ?? "?"} outdated")));
        }

        return true;
    }
}

public static class DependencyServices
{
    /// <summary>What every command needs to put toolchains, pins and the last dependency results into a snapshot.</summary>
    public static IServiceCollection AddToolchains(this IServiceCollection services)
    {
        services.AddSingleton<IToolRunner, ProcessToolRunner>();
        services.AddSingleton(_ => new ToolLocator(ToolEnvironment.Current()));
        services.AddSingleton<ToolchainCollector>();
        services.AddSingleton<ToolchainService>();
        services.AddSingleton(_ => new DependencyCache());
        services.AddSingleton<DependencyTargets>();
        services.AddSingleton<DependencyChecker>();
        return services;
    }

    /// <summary>The background checks themselves: only the long-running service does them.</summary>
    public static IServiceCollection AddDependencyChecks(this IServiceCollection services) => services.AddHostedService<DependencyWorker>();
}
