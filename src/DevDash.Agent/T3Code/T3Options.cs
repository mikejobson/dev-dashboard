namespace DevDash.Agent.T3Code;

/// <summary>
/// T3 Code on this machine (<c>Agent:T3</c>). On by default: the agent looks for T3's command-line tool,
/// reports each repository's threads, and, when actions are on too, takes the "open in T3" and "start a
/// thread" actions. <c>--no-t3</c> turns all of it off.
/// </summary>
public sealed class T3Options
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The CLI to run, when it is not where T3 Code puts it (<c>~/.t3/bin/t3</c>; on Windows <c>t3.exe</c>,
    /// <c>t3.cmd</c> or <c>t3</c> there) or on <c>PATH</c>. When set, nothing else is tried.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>T3 Code's own folder. Empty: <c>T3CODE_HOME</c>, else <c>~/.t3</c>.</summary>
    public string? Home { get; set; }

    /// <summary>Scan T3 Code's projects folder (<c>&lt;home&gt;/projects</c>) as well as <c>~/Developer</c> when no roots are configured.</summary>
    public bool ScanProjects { get; set; } = true;

    /// <summary>Time limit for each read-only call (<c>--version</c>, <c>thread list</c>, <c>thread show</c>, <c>app</c>).</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Time limit for <c>thread start</c>, which makes a worktree before it answers.</summary>
    public int StartTimeoutSeconds { get; set; } = 120;

    /// <summary>How long the answer to "is the CLI there, and which version" is kept before asking again.</summary>
    public int CheckIntervalMinutes { get; set; } = 30;

    /// <summary>Most <c>thread show</c> calls in one scan. Threads past it are reported from the list alone and filled in on the next scan.</summary>
    public int MaxShowsPerScan { get; set; } = 30;

    /// <summary>T3 Code's folder. <paramref name="userHome"/> is for tests: given it, <c>T3CODE_HOME</c> is not read.</summary>
    public string ResolveHome(string? userHome = null)
    {
        if (!string.IsNullOrWhiteSpace(Home))
        {
            return System.IO.Path.GetFullPath(AgentOptions.ExpandHome(Home.Trim(), userHome));
        }

        if (userHome is null && Environment.GetEnvironmentVariable("T3CODE_HOME") is { Length: > 0 } fromEnvironment)
        {
            return System.IO.Path.GetFullPath(fromEnvironment);
        }

        return System.IO.Path.Combine(userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".t3");
    }

    /// <summary>Where T3 Code keeps the projects it created or cloned itself.</summary>
    public string ProjectsFolder(string? userHome = null) => System.IO.Path.Combine(ResolveHome(userHome), "projects");

    /// <summary>Where T3 Code puts the worktrees it makes for threads.</summary>
    public string WorktreesFolder(string? userHome = null) => System.IO.Path.Combine(ResolveHome(userHome), "worktrees");
}
