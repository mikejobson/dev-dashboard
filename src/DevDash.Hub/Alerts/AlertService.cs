using DevDash.Contracts;
using DevDash.Hub.Data;
using DevDash.Hub.GitHub;
using DevDash.Hub.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.Alerts;

/// <summary>What one evaluation did.</summary>
/// <param name="Active">Conditions that are true now.</param>
/// <param name="Sent">Alerts that reached at least one channel (a summary counts each alert in it).</param>
/// <param name="Held">Waiting for quiet hours to end, or for a channel to be set up.</param>
/// <param name="Failed">Alerts that reached no channel this time.</param>
/// <param name="Cleared">Open alerts whose condition has gone.</param>
public sealed record AlertRun(bool Ran, int Active = 0, int Sent = 0, int Held = 0, int Failed = 0, int Cleared = 0);

/// <summary>
/// Works out what should be alerting (<see cref="AlertEvaluator"/>), what of that is new or due
/// (<see cref="AlertPlanner"/>, against the open alerts in <c>AlertStates</c>), sends it on every channel
/// that is switched on, and logs each attempt in <c>AlertDeliveries</c>. One evaluation runs at a time.
/// </summary>
public sealed class AlertService(
    AlertSettings settings,
    AlertSender sender,
    SnapshotStore snapshots,
    SafetyService safety,
    FindingsService findings,
    GitHubStore github,
    IDbContextFactory<DevDashDbContext> dbFactory,
    IOptionsMonitor<HubOptions> hub,
    DashboardNotifier notifier,
    TimeProvider time,
    ILogger<AlertService> logger)
{
    /// <summary>How many delivery attempts the log keeps.</summary>
    public const int KeepDeliveries = 200;

    /// <summary>A failed action alerts if it failed this recently. Older ones were before alerts were watching.</summary>
    public static readonly TimeSpan FailedActionWindow = TimeSpan.FromHours(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DateTimeOffset _upSince = time.GetUtcNow();

    public DateTimeOffset? LastEvaluatedAt { get; private set; }

    /// <summary>The hub page an alert is about, as an absolute address. Null when <c>Hub:PublicUrl</c> is not set or not a web address.</summary>
    public static string? Link(string? publicUrl, string path) =>
        AlertChannels.HttpUrl(publicUrl) is { } root ? $"{root.GetLeftPart(UriPartial.Path).TrimEnd('/')}/{path.TrimStart('/')}" : null;

    public async Task<AlertRun> EvaluateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var options = await settings.GetAsync(ct);
            var now = time.GetUtcNow();
            LastEvaluatedAt = now;
            if (!options.Enabled)
            {
                return new AlertRun(false);
            }

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var states = await db.AlertStates.ToListAsync(ct);
            var byKey = states.ToDictionary(s => s.Key, StringComparer.Ordinal);

            var inputs = await GatherAsync(db, now, byKey.Keys.ToHashSet(StringComparer.Ordinal), ct);
            var active = AlertEvaluator.Evaluate(inputs, options.Triggers, now);
            var zone = options.Zone();
            var quiet = options.QuietHours.IsQuiet(TimeOnly.FromTimeSpan(TimeZoneInfo.ConvertTime(now, zone).TimeOfDay));
            var plan = AlertPlanner.Plan(active, states, options, now, quiet);

            foreach (var key in plan.Cleared)
            {
                db.AlertStates.Remove(byKey[key]);
                byKey.Remove(key);
            }

            foreach (var candidate in plan.Fresh)
            {
                var state = new AlertStateEntity { Key = candidate.Key, Trigger = candidate.Trigger, IsEvent = candidate.IsEvent, FirstSeenAt = now };
                byKey[candidate.Key] = state;
                db.AlertStates.Add(state);
            }

            foreach (var candidate in active)
            {
                var state = byKey[candidate.Key];
                state.Title = Limit(candidate.Title, 300);
                state.Path = candidate.Path;
                state.LastSeenAt = now;
            }

            var channels = AlertChannels.Active(options);
            int sent = 0, failed = 0;
            if (plan.Due.Count > 0 && channels.Count > 0)
            {
                // A pile of new alerts at once (the first run, a machine coming back) goes out as one message.
                List<(AlertCandidate Message, IReadOnlyList<AlertCandidate> Covers)> batches = plan.Due.Count > options.MaxPerEvaluation
                    ? [(AlertEvaluator.Summary(plan.Due), plan.Due)]
                    : [.. plan.Due.Select(c => (c, (IReadOnlyList<AlertCandidate>)[c]))];

                foreach (var (candidate, covers) in batches)
                {
                    var ok = await DeliverAsync(db, candidate, covers.Count, channels, options, now, ct);
                    foreach (var state in covers.Select(c => byKey[c.Key]))
                    {
                        Record(state, ok, now);
                    }

                    sent += ok ? covers.Count : 0;
                    failed += ok ? 0 : covers.Count;
                }
            }

            var digest = states.FirstOrDefault(s => s.Key == AlertTriggers.Digest);
            if (channels.Count > 0 && AlertPlanner.DigestDue(options.Triggers.DailyDigest, digest, now, zone))
            {
                if (digest is null)
                {
                    digest = new AlertStateEntity { Key = AlertTriggers.Digest, Trigger = AlertTriggers.Digest, Title = "Daily digest", Path = "safety", FirstSeenAt = now };
                    db.AlertStates.Add(digest);
                }

                digest.LastSeenAt = now;
                Record(digest, await DeliverAsync(db, AlertEvaluator.Digest(inputs, options.Triggers, now), 1, channels, options, now, ct), now);
            }

            var changed = plan.Fresh.Count > 0 || plan.Cleared.Count > 0 || db.ChangeTracker.Entries<AlertDeliveryEntity>().Any();
            await db.SaveChangesAsync(ct);
            await TrimDeliveriesAsync(db, ct);
            if (changed)
            {
                notifier.Notify(DashboardChange.Alerts);
            }

            var waiting = plan.Held.Count + (channels.Count == 0 ? plan.Due.Count : 0);
            if (plan.Fresh.Count > 0 || plan.Cleared.Count > 0 || sent + failed > 0)
            {
                logger.LogInformation("Alerts: {Active} active, {New} new, {Sent} sent, {Failed} not delivered, {Held} waiting, {Cleared} cleared",
                    active.Count, plan.Fresh.Count, sent, failed, waiting, plan.Cleared.Count);
            }

            return new AlertRun(true, active.Count, sent, waiting, failed, plan.Cleared.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sends a test message to one channel, whether or not it is switched on, and logs the attempt.</summary>
    public async Task<DeliveryResult> SendTestAsync(string channel, CancellationToken ct = default)
    {
        var options = await settings.GetAsync(ct);
        var now = time.GetUtcNow();
        var message = new AlertMessage(
            AlertTriggers.Test,
            "DevDash test alert",
            $"If you can read this, {AlertChannels.Label(channel)} is set up.",
            AlertSeverity.Info,
            AlertTriggers.Test,
            now,
            Link(hub.CurrentValue.PublicUrl, "alerts"),
            "alerts",
            Test: true);
        var result = await sender.SendAsync(channel, message, options, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.AlertDeliveries.Add(new AlertDeliveryEntity { At = now, Channel = channel, Trigger = AlertTriggers.Test, Title = message.Title, Ok = result.Ok, Error = result.Error, IsTest = true });
        await db.SaveChangesAsync(ct);
        await TrimDeliveriesAsync(db, ct);
        notifier.Notify(DashboardChange.Alerts);
        return result;
    }

    /// <summary>The alerts that are open now, newest first. The digest's bookkeeping row is not one of them.</summary>
    public async Task<IReadOnlyList<AlertStateEntity>> OpenAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        return (await db.AlertStates.AsNoTracking().Where(s => s.Trigger != AlertTriggers.Digest).ToListAsync(ct))
            // An event stays in the table for a week so it is not sent twice; it is only "open" for a day.
            .Where(s => !s.IsEvent || now - s.FirstSeenAt < TimeSpan.FromDays(1))
            .OrderByDescending(s => s.FirstSeenAt)
            .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<AlertDeliveryEntity>> DeliveriesAsync(int take = 50, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AlertDeliveries.AsNoTracking().OrderByDescending(d => d.Id).Take(take).ToListAsync(ct);
    }

    private async Task<AlertInputs> GatherAsync(DevDashDbContext db, DateTimeOffset now, IReadOnlySet<string> openKeys, CancellationToken ct)
    {
        var failed = (await db.AgentActions.AsNoTracking()
                .Where(a => a.Status == AgentActionStatus.Failed && a.Kind != AgentActionKind.Refresh)
                .OrderByDescending(a => a.Id)
                .Take(50)
                .ToListAsync(ct))
            .Where(a => a.CompletedAt is { } at && now - at <= FailedActionWindow)
            .ToList();

        return new AlertInputs
        {
            Safety = safety.Get(),
            Machines = snapshots.Machines,
            Findings = await findings.GetAsync(ct),
            PullRequests = github.Current.PullRequests,
            FailedActions = failed,
            StartedThreads = await db.T3Threads.AsNoTracking().Where(t => t.ThreadId != null).OrderByDescending(t => t.Id).Take(200).ToListAsync(ct),
            HubUpSince = _upSince,
            OpenKeys = openKeys,
        };
    }

    /// <summary>Sends one message on every active channel and logs each attempt. True when at least one got through.</summary>
    private async Task<bool> DeliverAsync(DevDashDbContext db, AlertCandidate candidate, int count, IReadOnlyList<string> channels, AlertOptions options, DateTimeOffset now, CancellationToken ct)
    {
        var message = new AlertMessage(candidate.Trigger, candidate.Title, candidate.Body, candidate.Severity, candidate.Key, now,
            Link(hub.CurrentValue.PublicUrl, candidate.Path), candidate.Path, candidate.ExternalUrl, count);
        var any = false;
        foreach (var channel in channels)
        {
            var result = await sender.SendAsync(channel, message, options, ct);
            any |= result.Ok;
            db.AlertDeliveries.Add(new AlertDeliveryEntity { At = now, Channel = channel, Trigger = candidate.Trigger, Title = Limit(candidate.Title, 300), Ok = result.Ok, Error = result.Error });
            if (!result.Ok)
            {
                // The reason has had every secret taken out; the address is never part of it.
                logger.LogWarning("Alert not delivered on {Channel}: {Error}", channel, result.Error);
            }
        }

        return any;
    }

    private static void Record(AlertStateEntity state, bool delivered, DateTimeOffset now)
    {
        if (delivered)
        {
            state.LastNotifiedAt = now;
            state.Attempts = 0;
            state.LastAttemptAt = null;
        }
        else
        {
            state.Attempts++;
            state.LastAttemptAt = now;
        }
    }

    private static async Task TrimDeliveriesAsync(DevDashDbContext db, CancellationToken ct)
    {
        var cutoff = await db.AlertDeliveries.OrderByDescending(d => d.Id).Skip(KeepDeliveries).Select(d => (int?)d.Id).FirstOrDefaultAsync(ct);
        if (cutoff is { } id)
        {
            await db.AlertDeliveries.Where(d => d.Id <= id).ExecuteDeleteAsync(ct);
        }
    }

    private static string Limit(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary>
/// Runs the evaluation in the background: shortly after anything changes (a snapshot, a GitHub poll, an
/// action result, a snooze) and on a timer besides, because "offline" and "older than 3 days" become
/// true without anything arriving.
/// </summary>
public sealed class AlertWorker(AlertService alerts, AlertSettings settings, DashboardNotifier notifier, TimeProvider time, ILogger<AlertWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>How long after start-up the first look happens: agents get a moment to report first.</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Evaluations run so far, for tests.</summary>
    public int Runs { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        notifier.Changed += OnChanged;
        try
        {
            await Task.Delay(StartDelay, time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var interval = TimeSpan.FromMinutes(5);
                var debounce = TimeSpan.FromSeconds(15);
                try
                {
                    var options = await settings.GetAsync(stoppingToken);
                    interval = TimeSpan.FromMinutes(options.EvaluateIntervalMinutes);
                    debounce = TimeSpan.FromSeconds(options.DebounceSeconds);

                    // Whatever arrived while waiting is covered by this look.
                    _wake.Wait(0, CancellationToken.None);
                    await alerts.EvaluateAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not evaluate alerts");
                }

                Runs++;
                if (await WaitAsync(interval, stoppingToken))
                {
                    await Task.Delay(debounce, time, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        finally
        {
            notifier.Changed -= OnChanged;
        }
    }

    /// <summary>True when woken by a change, false when the interval ran out. The interval follows the injected clock.</summary>
    private async Task<bool> WaitAsync(TimeSpan interval, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var woken = _wake.WaitAsync(linked.Token);
        var timer = Task.Delay(interval, time, linked.Token);
        var first = await Task.WhenAny(woken, timer);
        if (first == woken && woken.IsCompletedSuccessfully)
        {
            await linked.CancelAsync();
            return true;
        }

        // Stop waiting on the semaphore. If a wake slipped in at the same instant it has been taken, which is fine: the next look is due anyway.
        await linked.CancelAsync();
        try
        {
            await woken;
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        ct.ThrowIfCancellationRequested();
        return false;
    }

    private void OnChanged(DashboardChange change)
    {
        // Its own notifications must not wake it, or one evaluation would schedule the next.
        if (change == DashboardChange.Alerts)
        {
            return;
        }

        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // already due
        }
    }
}
