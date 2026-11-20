using System.Globalization;
using DevDash.Core.Safety;

namespace DevDash.Hub.Alerts;

/// <summary>
/// Alerts: the hub telling you, instead of you having to look. <c>Hub:Alerts</c> in configuration; the
/// Alerts page can override any of it, and those overrides live in the database (see <see cref="AlertSettings"/>).
/// </summary>
public sealed class AlertOptions
{
    /// <summary>Hub-wide switch. Nothing is evaluated or sent while it is off.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the conditions are looked at when nothing else prompts it.</summary>
    public int EvaluateIntervalMinutes { get; set; } = 5;

    /// <summary>After a snapshot, a GitHub poll or an action result, wait this long before looking, so a burst is one look.</summary>
    public int DebounceSeconds { get; set; } = 15;

    /// <summary>Say it again when a condition is still there after this many hours. 0: once, until it clears and comes back.</summary>
    public int RenotifyHours { get; set; }

    /// <summary>More new alerts than this in one look are sent as a single summary.</summary>
    public int MaxPerEvaluation { get; set; } = 5;

    /// <summary>IANA time zone for quiet hours and the digest, e.g. <c>Europe/London</c>. Empty: the hub's own (<c>TZ</c> in a container).</summary>
    public string TimeZone { get; set; } = "";

    public QuietHoursOptions QuietHours { get; set; } = new();

    public AlertTriggerOptions Triggers { get; set; } = new();

    public AlertChannelOptions Channels { get; set; } = new();

    /// <summary>Puts every number in a sane range and splits a Home Assistant URL that carries its webhook id.</summary>
    public AlertOptions Normalize()
    {
        EvaluateIntervalMinutes = Math.Clamp(EvaluateIntervalMinutes, 1, 24 * 60);
        DebounceSeconds = Math.Clamp(DebounceSeconds, 0, 600);
        RenotifyHours = Math.Clamp(RenotifyHours, 0, 24 * 30);
        MaxPerEvaluation = Math.Clamp(MaxPerEvaluation, 1, 50);
        TimeZone = Clean(TimeZone);
        QuietHours.Start = Clean(QuietHours.Start);
        QuietHours.End = Clean(QuietHours.End);
        Triggers.AtRisk.Days = Math.Clamp(Triggers.AtRisk.Days, 0, 3650);
        Triggers.AtRisk.Level = Triggers.AtRisk.MinLevel.ToString();
        Triggers.MachineOffline.Minutes = Math.Clamp(Triggers.MachineOffline.Minutes, 1, 60 * 24 * 365);
        Triggers.MachineOffline.Ignore = Clean(Triggers.MachineOffline.Ignore);
        Triggers.DailyDigest.Time = Clean(Triggers.DailyDigest.Time);
        Channels.Webhook.Url = Clean(Channels.Webhook.Url);
        Channels.Webhook.Token = Clean(Channels.Webhook.Token);
        Channels.Ntfy.Server = Clean(Channels.Ntfy.Server).TrimEnd('/');
        Channels.Ntfy.Topic = Clean(Channels.Ntfy.Topic).Trim('/');
        Channels.Ntfy.Token = Clean(Channels.Ntfy.Token);
        Channels.HomeAssistant.Normalize();
        return this;

        static string Clean(string? value) => (value ?? "").Trim();
    }

    public TimeZoneInfo Zone()
    {
        if (TimeZone.Length > 0)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // An unknown zone falls back to the hub's own; the Alerts page says so.
            }
        }

        return TimeZoneInfo.Local;
    }

    /// <summary>False when <see cref="TimeZone"/> names a zone this machine does not know.</summary>
    public bool ZoneIsKnown => TimeZone.Length == 0 || TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out _);

    /// <summary>A time of day as written in the settings: <c>7:30</c>, <c>07:30</c> or <c>22</c>.</summary>
    public static TimeOnly? ParseTime(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0)
        {
            return null;
        }

        // A bare hour first: on its own, "H" would be read as a standard format, not as "hours".
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var hour))
        {
            return hour is >= 0 and <= 23 ? new TimeOnly(hour, 0) : null;
        }

        string[] formats = ["H:mm", "HH:mm"];
        return TimeOnly.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;
    }
}

/// <summary>Hours in which nothing new is sent. What comes up meanwhile is sent when they end, if it is still true.</summary>
public sealed class QuietHoursOptions
{
    public bool Enabled { get; set; }

    /// <summary>Local time, <c>HH:mm</c>.</summary>
    public string Start { get; set; } = "22:00";

    /// <summary>Local time, <c>HH:mm</c>. Earlier than <see cref="Start"/> means the next morning.</summary>
    public string End { get; set; } = "07:00";

    /// <summary>Whether a local time of day falls in the quiet hours. Times that cannot be read mean no quiet hours.</summary>
    public bool IsQuiet(TimeOnly local)
    {
        if (!Enabled || AlertOptions.ParseTime(Start) is not { } start || AlertOptions.ParseTime(End) is not { } end || start == end)
        {
            return false;
        }

        return start < end ? local >= start && local < end : local >= start || local < end;
    }
}

public sealed class AlertTriggerOptions
{
    public AtRiskTriggerOptions AtRisk { get; set; } = new();
    public MachineOfflineTriggerOptions MachineOffline { get; set; } = new();
    public SimpleTriggerOptions Diverged { get; set; } = new();
    public CiTriggerOptions CiFailed { get; set; } = new();
    public SimpleTriggerOptions ActionFailed { get; set; } = new();
    public DigestTriggerOptions DailyDigest { get; set; } = new();

    /// <summary>A T3 thread the dashboard started is waiting for an answer or an approval. Off by default.</summary>
    public SimpleTriggerOptions T3NeedsInput { get; set; } = new() { Enabled = false };

    /// <summary>A T3 thread the dashboard started opened a pull request, or settled. Off by default.</summary>
    public SimpleTriggerOptions T3Done { get; set; } = new() { Enabled = false };
}

public sealed class SimpleTriggerOptions
{
    public bool Enabled { get; set; } = true;
}

/// <summary>Work that exists on one machine only (the Safety page's "at risk") and has for a while.</summary>
public sealed class AtRiskTriggerOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>How long the work has to have been sitting there.</summary>
    public int Days { get; set; } = 3;

    /// <summary>
    /// The least serious risk that alerts: <c>High</c> (commits on no remote), <c>Medium</c> (also
    /// uncommitted changes, stashes, local-only tags) or <c>Low</c> (also untracked files).
    /// </summary>
    public string Level { get; set; } = nameof(RiskLevel.High);

    public RiskLevel MinLevel =>
        Enum.TryParse<RiskLevel>(Level, ignoreCase: true, out var level) && level is RiskLevel.Low or RiskLevel.Medium or RiskLevel.High ? level : RiskLevel.High;
}

public sealed class MachineOfflineTriggerOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>A machine that has not reported for this long is offline.</summary>
    public int Minutes { get; set; } = 60;

    /// <summary>Machines to leave out (a laptop that sleeps every night), by host name or machine id, separated by commas.</summary>
    public string Ignore { get; set; } = "";

    public IReadOnlySet<string> IgnoreSet =>
        (Ignore ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public sealed class CiTriggerOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Also alert for draft pull requests.</summary>
    public bool IncludeDrafts { get; set; }
}

public sealed class DigestTriggerOptions
{
    public bool Enabled { get; set; }

    /// <summary>Local time of day, <c>HH:mm</c>.</summary>
    public string Time { get; set; } = "08:00";
}

public sealed class AlertChannelOptions
{
    public WebhookChannelOptions Webhook { get; set; } = new();
    public NtfyChannelOptions Ntfy { get; set; } = new();
    public HomeAssistantChannelOptions HomeAssistant { get; set; } = new();
}

/// <summary>Any endpoint that takes a JSON POST.</summary>
public sealed class WebhookChannelOptions
{
    public bool Enabled { get; set; }

    /// <summary>Secret: webhook addresses usually carry their own key.</summary>
    public string Url { get; set; } = "";

    /// <summary>Secret, optional: sent as <c>Authorization: Bearer</c>.</summary>
    public string Token { get; set; } = "";
}

/// <summary>An ntfy server, the public one or your own.</summary>
public sealed class NtfyChannelOptions
{
    public bool Enabled { get; set; }

    public string Server { get; set; } = "https://ntfy.sh";

    /// <summary>Secret: on a server without access control, knowing the topic is all it takes to read it.</summary>
    public string Topic { get; set; } = "";

    /// <summary>Secret, optional: an ntfy access token.</summary>
    public string Token { get; set; } = "";
}

/// <summary>A Home Assistant webhook trigger: <c>&lt;Url&gt;/api/webhook/&lt;WebhookId&gt;</c>.</summary>
public sealed class HomeAssistantChannelOptions
{
    private const string Marker = "/api/webhook/";

    public bool Enabled { get; set; }

    /// <summary>Where Home Assistant is, e.g. <c>http://homeassistant.local:8123</c>. A full webhook URL is accepted and split.</summary>
    public string Url { get; set; } = "";

    /// <summary>Secret: whoever has the id can fire the automation.</summary>
    public string WebhookId { get; set; } = "";

    /// <summary>Splits a full webhook URL into the address and the id. The id is null when the text holds none.</summary>
    public static (string Url, string? WebhookId) Split(string? url)
    {
        var text = (url ?? "").Trim();
        var at = text.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return (text.TrimEnd('/'), null);
        }

        var id = text[(at + Marker.Length)..].Trim('/');
        var query = id.IndexOfAny(['?', '#']);
        id = query >= 0 ? id[..query] : id;
        return (text[..at].TrimEnd('/'), id.Length > 0 ? id : null);
    }

    /// <summary>After this, <see cref="Url"/> never holds the id.</summary>
    public void Normalize()
    {
        var (url, id) = Split(Url);
        Url = url;
        WebhookId = (WebhookId ?? "").Trim();
        if (WebhookId.Length == 0 && id is not null)
        {
            WebhookId = id;
        }
    }
}
