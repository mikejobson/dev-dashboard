using DevDash.Hub.Data;

namespace DevDash.Hub.Alerts;

/// <summary>What one evaluation should do about the alerts that are true now, given the ones already open.</summary>
/// <param name="Fresh">Conditions seen for the first time: a row is opened for each.</param>
/// <param name="Due">To send now. Includes the fresh ones unless they are held.</param>
/// <param name="Held">Would be sent, but it is quiet hours: they stay waiting.</param>
/// <param name="Cleared">Keys of open alerts whose condition has gone: their rows are deleted, so a return alerts again.</param>
public sealed record AlertPlan(
    IReadOnlyList<AlertCandidate> Fresh,
    IReadOnlyList<AlertCandidate> Due,
    IReadOnlyList<AlertCandidate> Held,
    IReadOnlyList<string> Cleared);

/// <summary>
/// The dedupe rules, pure:
/// <list type="bullet">
/// <item>a condition alerts when it first appears, and not again while it lasts;</item>
/// <item>unless <see cref="AlertOptions.RenotifyHours"/> is set, in which case again every that many hours;</item>
/// <item>when it clears its row goes, so the same condition coming back is new;</item>
/// <item>an event (a failed action) never clears: it is sent once and its row kept for a week;</item>
/// <item>in quiet hours nothing is sent; what is still true when they end is sent then;</item>
/// <item>an alert that reached no channel is tried again, waiting longer each time, and given up on after <see cref="MaxAttempts"/>.</item>
/// </list>
/// </summary>
public static class AlertPlanner
{
    public const int MaxAttempts = 8;

    /// <summary>How long the row of an event is kept after it was last seen.</summary>
    public static readonly TimeSpan EventKeep = TimeSpan.FromDays(7);

    public static AlertPlan Plan(IReadOnlyList<AlertCandidate> active, IReadOnlyList<AlertStateEntity> open, AlertOptions options, DateTimeOffset now, bool quiet)
    {
        var states = open.Where(s => s.Trigger != AlertTriggers.Digest).ToDictionary(s => s.Key, StringComparer.Ordinal);
        var activeKeys = active.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);

        var cleared = states.Values
            .Where(s => !activeKeys.Contains(s.Key) && (!s.IsEvent || now - s.LastSeenAt > EventKeep))
            .Select(s => s.Key)
            .ToList();

        var fresh = new List<AlertCandidate>();
        var due = new List<AlertCandidate>();
        var held = new List<AlertCandidate>();
        foreach (var candidate in active)
        {
            bool send;
            if (!states.TryGetValue(candidate.Key, out var state))
            {
                fresh.Add(candidate);
                send = true;
            }
            else
            {
                send = IsDue(state, candidate.IsEvent, options.RenotifyHours, now);
            }

            if (send)
            {
                (quiet ? held : due).Add(candidate);
            }
        }

        return new AlertPlan(fresh, due, held, cleared);
    }

    /// <summary>Whether an open alert should be sent (again) now.</summary>
    public static bool IsDue(AlertStateEntity state, bool isEvent, int renotifyHours, DateTimeOffset now)
    {
        var wanted = state.LastNotifiedAt is not { } last
            || (!isEvent && renotifyHours > 0 && now - last >= TimeSpan.FromHours(renotifyHours));
        return wanted && CanAttempt(state, now);
    }

    /// <summary>False while waiting out a failed attempt, and for good after <see cref="MaxAttempts"/> of them.</summary>
    public static bool CanAttempt(AlertStateEntity state, DateTimeOffset now) =>
        state.Attempts == 0
        || (state.Attempts < MaxAttempts && (state.LastAttemptAt is not { } at || now - at >= Backoff(state.Attempts)));

    /// <summary>1, 2, 4, 8 … minutes after each failure, an hour at most.</summary>
    public static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Clamp(attempts, 1, 10) - 1)));

    /// <summary>
    /// The digest goes out once a day, at the first evaluation at or after its time of day. Enabling it
    /// after that time sends today's straight away.
    /// </summary>
    public static bool DigestDue(DigestTriggerOptions options, AlertStateEntity? state, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!options.Enabled || AlertOptions.ParseTime(options.Time) is not { } at)
        {
            return false;
        }

        var local = TimeZoneInfo.ConvertTime(now, zone);
        if (TimeOnly.FromTimeSpan(local.TimeOfDay) < at)
        {
            return false;
        }

        if (state?.LastNotifiedAt is { } sent && TimeZoneInfo.ConvertTime(sent, zone).Date >= local.Date)
        {
            return false;
        }

        return state is null || CanAttempt(state, now);
    }
}
