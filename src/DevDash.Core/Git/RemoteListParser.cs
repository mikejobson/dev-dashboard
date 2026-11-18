using DevDash.Contracts;

namespace DevDash.Core.Git;

/// <summary>Parses <c>git remote -v</c>, keeping the fetch URL of each remote.</summary>
public static class RemoteListParser
{
    public static IReadOnlyList<RemoteInfo> Parse(string output)
    {
        var result = new List<RemoteInfo>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var tab = line.IndexOf('\t');
            if (tab <= 0 || !line.EndsWith(" (fetch)", StringComparison.Ordinal))
            {
                continue;
            }

            var name = line[..tab];
            var url = line[(tab + 1)..^" (fetch)".Length].Trim();
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
}
