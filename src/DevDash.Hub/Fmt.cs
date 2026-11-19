using System.Globalization;

namespace DevDash.Hub;

/// <summary>Small display helpers shared by the pages.</summary>
public static class Fmt
{
    public static string Ago(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when is not { } value)
        {
            return "never";
        }

        var span = now - value;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalSeconds < 60 ? "just now"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes}m ago"
            : span.TotalHours < 24 ? $"{(int)span.TotalHours}h ago"
            : span.TotalDays < 60 ? $"{(int)span.TotalDays}d ago"
            : span.TotalDays < 730 ? $"{(int)(span.TotalDays / 30.4)}mo ago"
            : $"{(int)(span.TotalDays / 365.25)}y ago";
    }

    public static string Date(DateTimeOffset? when) =>
        when is { } value ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";

    public static string Size(long? bytes)
    {
        if (bytes is not { } value)
        {
            return "";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{size:0.#} {units[unit]}");
    }

    /// <summary>A duration for people: "850 ms", "17 s", "1 m 5 s", "2 h 3 m".</summary>
    public static string Duration(long milliseconds)
    {
        if (milliseconds < 0)
        {
            milliseconds = 0;
        }

        if (milliseconds < 1000)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{milliseconds} ms");
        }

        var seconds = (long)Math.Round(milliseconds / 1000d, MidpointRounding.AwayFromZero);
        if (seconds < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{seconds} s");
        }

        if (seconds < 3600)
        {
            return seconds % 60 == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 60} m")
                : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60} m {seconds % 60} s");
        }

        var minutes = seconds / 60;
        return minutes % 60 == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h {minutes % 60} m");
    }

    /// <summary>"1 issue", "3 issues".</summary>
    public static string Count(int count, string singular, string? plural = null) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? singular : plural ?? singular + "s")}");

    /// <summary>
    /// The home folder a machine's scan roots sit under (<c>/Users/name</c>, <c>/home/name</c> or
    /// <c>C:\Users\name</c>), or null when they do not share one. The agent does not report its home
    /// folder, so this is read off the roots.
    /// </summary>
    public static string? HomeOf(IEnumerable<string> roots)
    {
        string? home = null;
        foreach (var root in roots)
        {
            var parts = root.Split('/', '\\');
            string? candidate = null;
            if (parts.Length >= 3 && parts[0].Length == 0 && parts[1] is "Users" or "home" && parts[2].Length > 0)
            {
                candidate = $"/{parts[1]}/{parts[2]}";
            }
            else if (parts.Length >= 3 && parts[0].Length == 2 && parts[0][1] == ':' && parts[1].Equals("Users", StringComparison.OrdinalIgnoreCase) && parts[2].Length > 0)
            {
                candidate = $"{parts[0]}\\{parts[1]}\\{parts[2]}";
            }

            if (candidate is null || (home is not null && home != candidate))
            {
                return null;
            }

            home = candidate;
        }

        return home;
    }

    /// <summary>
    /// Shortens every path under <paramref name="home"/> in a path or command line to <c>~/…</c>.
    /// For display only: keep the full text for copying and tooltips.
    /// </summary>
    public static string Tilde(string text, string? home)
    {
        if (string.IsNullOrEmpty(home) || string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (text == home)
        {
            return "~";
        }

        return text.Replace(home + "/", "~/", StringComparison.Ordinal).Replace(home + "\\", "~\\", StringComparison.Ordinal);
    }

    public static string Sha(string? sha) => sha is { Length: >= 7 } ? sha[..7] : sha ?? "";

    public static string RepoUrl(string key) => "repo?key=" + Uri.EscapeDataString(key);

    public static string SeverityClass(Core.Cleanup.FindingSeverity severity) => severity switch
    {
        Core.Cleanup.FindingSeverity.High => "danger",
        Core.Cleanup.FindingSeverity.Medium => "warn",
        Core.Cleanup.FindingSeverity.Low => "info",
        _ => "",
    };
}
