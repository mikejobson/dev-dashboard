using System.Text.RegularExpressions;

namespace DevDash.Contracts;

/// <summary>The processes listening on a TCP port that look like development servers (§21), as of one scan.</summary>
public sealed record DevServerReport
{
    public DateTimeOffset CollectedAt { get; init; }

    public IReadOnlyList<DevServer> Servers { get; init; } = [];

    /// <summary>Why the list is empty or partial: the listing tool was missing or did not answer.</summary>
    public string? Problem { get; init; }
}

/// <summary>One listening process: inside a scanned repository, or a well-known development tool.</summary>
public sealed record DevServer
{
    public int Pid { get; init; }

    /// <summary>The program's name: <c>node</c>, <c>dotnet</c>, <c>Akin</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The command line, secrets masked and cut to <see cref="DevServerText.MaxCommandChars"/> (<see cref="DevServerText.Command"/>).</summary>
    public string? Command { get; init; }

    /// <summary>When the process started. With <see cref="Command"/>, what tells this process from a later one that got the same pid.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Its working folder. Null on Windows, where it cannot be read from outside the process.</summary>
    public string? Cwd { get; init; }

    public IReadOnlyList<int> Ports { get; init; } = [];

    /// <summary>The scanned working tree it runs in (by working folder or a path on its command line), or null for a tool outside every repository.</summary>
    public string? RepoPath { get; init; }
}

/// <summary>How a command line is reported: the same on the agent that lists it and the agent that is asked to stop it.</summary>
public static partial class DevServerText
{
    public const int MaxCommandChars = 300;

    [GeneratedRegex(@"(?i)(token|secret|password|passwd|pwd|apikey|api-key|api_key|auth|credential|connectionstring)")]
    private static partial Regex SecretName();

    [GeneratedRegex(@"(?i)(\b[a-z][a-z0-9+.-]*://)([^/\s:@]+):([^/\s@]+)@")]
    private static partial Regex UrlCredentials();

    /// <summary>
    /// The arguments joined by spaces, with the value of anything that looks like a secret replaced by <c>***</c>
    /// (<c>--token x</c>, <c>--password=x</c>, <c>API_KEY=x</c>, <c>https://user:pass@host</c>), whitespace runs
    /// folded, and cut to <see cref="MaxCommandChars"/>.
    /// </summary>
    public static string Command(IEnumerable<string> args)
    {
        var parts = new List<string>();
        var maskNext = false;
        foreach (var raw in args)
        {
            var arg = raw.Trim();
            if (arg.Length == 0)
            {
                continue;
            }

            if (maskNext && !arg.StartsWith('-'))
            {
                parts.Add("***");
                maskNext = false;
                continue;
            }

            maskNext = false;
            var eq = arg.IndexOf('=');
            if (eq > 0 && SecretName().IsMatch(arg[..eq]))
            {
                parts.Add(arg[..(eq + 1)] + "***");
                continue;
            }

            if (arg.StartsWith('-') && eq < 0 && SecretName().IsMatch(arg))
            {
                maskNext = true;
            }

            parts.Add(UrlCredentials().Replace(arg, "$1$2:***@"));
        }

        var text = string.Join(' ', parts);
        text = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        return text.Length <= MaxCommandChars ? text : text[..(MaxCommandChars - 1)] + "…";
    }

    /// <summary>As <see cref="Command(IEnumerable{string})"/> for a command line that is only known as one string (Windows, <c>ps</c>).</summary>
    public static string Command(string commandLine) => Command(commandLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Two start times are the same process's when they are within two seconds (<c>ps</c> reports whole seconds).</summary>
    public static bool SameStart(DateTimeOffset a, DateTimeOffset b) => (a - b).Duration() <= TimeSpan.FromSeconds(2);
}
