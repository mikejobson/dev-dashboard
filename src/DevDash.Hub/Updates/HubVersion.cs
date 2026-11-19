using System.Reflection;
using DevDash.Contracts;

namespace DevDash.Hub.Updates;

/// <summary>This hub's own version, and whether a newer release is out. The hub never updates itself.</summary>
public static class HubVersion
{
    /// <summary>The version the hub was built with (<c>-p:Version</c> from the release tag), without the commit suffix.</summary>
    public static string Current { get; } = Read();

    /// <summary>The newer release, or null when this hub is current, or either version can't be compared.</summary>
    public static LatestRelease? NewerThan(string current, LatestRelease? latest) =>
        latest is not null
        && SemVer.ParseOrNull(current) is { } mine
        && SemVer.ParseOrNull(latest.Version) is { } theirs
        && theirs > mine
            ? latest
            : null;

    private static string Read()
    {
        var info = typeof(HubVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
