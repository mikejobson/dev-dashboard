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
