using System.Text.RegularExpressions;

namespace DevDash.Core.Git;

/// <summary>A hosted remote reduced to <c>host/owner/repo</c>.</summary>
public sealed record RemoteIdentity(string Host, string Path)
{
    /// <summary>Lower-cased join key, e.g. <c>github.com/acme-corp/core</c>.</summary>
    public string Key => $"{Host}/{Path}".ToLowerInvariant();

    public override string ToString() => $"{Host}/{Path}";
}

public static partial class RemoteUrlNormalizer
{
    // user@host:path (scp-like). The path must not start with "/" twice, and host has no slash.
    [GeneratedRegex(@"^(?:(?<user>[^@/\s]+)@)?(?<host>[A-Za-z0-9.\-]+):(?!//)(?<path>[^\s]+)$")]
    private static partial Regex ScpLike();

    [GeneratedRegex(@"^(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*)://(?:(?<user>[^@/\s]*)@)?(?<host>[^/:\s]+)(?::(?<port>\d+))?/(?<path>[^\s?#]*)")]
    private static partial Regex UrlLike();

    /// <summary>Returns the identity for a hosted remote, or null for local paths and anything unparseable.</summary>
    public static RemoteIdentity? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        // Credentials first, so a password containing '@' cannot end up in the host.
        url = StripCredentials(url.Trim());
        string host;
        string path;

        var m = UrlLike().Match(url);
        if (m.Success)
        {
            if (m.Groups["scheme"].Value.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            host = m.Groups["host"].Value;
            path = m.Groups["path"].Value;
        }
        else
        {
            // Windows drive paths ("C:\repo", "C:/repo") look scp-like; rule them out first.
            if (url.Length >= 2 && char.IsAsciiLetter(url[0]) && url[1] == ':' && (url.Length == 2 || url[2] is '\\' or '/'))
            {
                return null;
            }

            m = ScpLike().Match(url);
            if (!m.Success)
            {
                return null;
            }

            host = m.Groups["host"].Value;
            path = m.Groups["path"].Value;
        }

        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4].TrimEnd('/');
        }

        host = host.ToLowerInvariant();

        // Azure DevOps: dev.azure.com/org/project/_git/repo and ssh.dev.azure.com:v3/org/project/repo
        if (host == "ssh.dev.azure.com")
        {
            host = "dev.azure.com";
            if (path.StartsWith("v3/", StringComparison.OrdinalIgnoreCase))
            {
                path = path[3..];
            }
        }
        else if (host == "dev.azure.com")
        {
            path = path.Replace("/_git/", "/", StringComparison.OrdinalIgnoreCase);
        }
        else if (host == "ssh.github.com")
        {
            host = "github.com";
        }

        if (path.Length == 0 || !path.Contains('/'))
        {
            return null;
        }

        return new RemoteIdentity(host, path);
    }

    [GeneratedRegex(@"^(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*)://")]
    private static partial Regex SchemePrefix();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.\-]*://[^\s'""<>]+")]
    private static partial Regex UrlInText();

    /// <summary>
    /// Removes <c>user:password@</c>, and any query string or fragment, from URL-style remotes so tokens
    /// never leave the machine. The credentials run to the LAST <c>@</c> of the authority, since a
    /// password may itself contain one, and a URL with no path (<c>https://tok@host</c>) is covered too.
    /// </summary>
    public static string StripCredentials(string url)
    {
        var m = SchemePrefix().Match(url);
        if (!m.Success)
        {
            return url; // scp-like or a local path: nothing that can carry a token in the usual places
        }

        var start = m.Length;
        var end = url.IndexOfAny(['/', '?', '#'], start);
        var authority = end < 0 ? url[start..] : url[start..end];
        var rest = end < 0 ? "" : url[end..];

        // A query or fragment can carry a token (?private_token=…); a remote never needs one to be shown.
        var cut = rest.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            rest = rest[..cut];
        }

        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            var user = authority[..at];
            // Keep the conventional "git@" user for ssh; drop everything for http(s) and the rest.
            var keep = m.Groups["scheme"].Value.Equals("ssh", StringComparison.OrdinalIgnoreCase) && !user.Contains(':') && !user.Contains('@');
            if (!keep)
            {
                authority = authority[(at + 1)..];
            }
        }

        return url[..start] + authority + rest;
    }

    /// <summary>
    /// <see cref="StripCredentials"/> applied to every URL inside free text, such as git's error output,
    /// which repeats the remote's URL (token and all) when a fetch fails.
    /// </summary>
    public static string RedactCredentials(string text) =>
        string.IsNullOrEmpty(text) ? text : UrlInText().Replace(text, match => StripCredentials(match.Value));
}
