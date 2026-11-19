using DevDash.Contracts;

namespace DevDash.Core.Git;

/// <summary>
/// Parses <c>git remote -v</c>, keeping the fetch URL of each remote. A partial clone's fetch line
/// carries its filter after the marker: <c>origin\thttps://… (fetch) [blob:none]</c>.
/// </summary>
public static class RemoteListParser
{
    public static IReadOnlyList<RemoteInfo> Parse(string output)
    {
        var result = new List<RemoteInfo>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var tab = line.IndexOf('\t');
            var marker = line.LastIndexOf(FetchMarker, StringComparison.Ordinal);
            if (tab <= 0 || marker <= tab || !IsFilterSuffix(line.AsSpan(marker + FetchMarker.Length)))
            {
                continue;
            }

            var name = line[..tab];
            var url = line[(tab + 1)..marker].Trim();
            if (url.Length == 0 || result.Any(r => r.Name == name))
            {
                continue;
            }

            result.Add(new RemoteInfo
            {
                Name = name,
                Url = RemoteUrlNormalizer.StripCredentials(url),
                Normalized = RemoteUrlNormalizer.Normalize(url)?.Key,
            });
        }

        return result;
    }

    private const string FetchMarker = " (fetch)";

    /// <summary>Nothing, or a partial-clone filter such as <c> [blob:none]</c>.</summary>
    private static bool IsFilterSuffix(ReadOnlySpan<char> rest) =>
        rest.IsEmpty || (rest.Length > 3 && rest[0] == ' ' && rest[1] == '[' && rest[^1] == ']');
}
