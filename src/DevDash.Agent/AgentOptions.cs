namespace DevDash.Agent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>
    /// Folders to scan. <c>~</c> expands to the user's home directory. Empty means the defaults:
    /// <c>~/Developer</c>, plus T3 Code's projects folder (<c>~/.t3/projects</c>) when it exists.
    /// </summary>
    public List<string> Roots { get; set; } = [];

    /// <summary>Stable id for this machine. Defaults to the lower-cased host name.</summary>
    public string? MachineId { get; set; }

    public string HubUrl { get; set; } = "http://localhost:5210";
    public string? ApiKey { get; set; }

    /// <summary>File holding the API key, used when <see cref="ApiKey"/> is not set.</summary>
    public string? ApiKeyFile { get; set; }

    public int ScanIntervalMinutes { get; set; } = 15;
    public int SizeScanIntervalHours { get; set; } = 24;

    /// <summary>How deep to look for repos inside worktree folders such as <c>repo.worktrees/</c>.</summary>
    public int WorktreeContainerDepth { get; set; } = 1;

    public int GitTimeoutSeconds { get; set; } = 30;
    public int MaxParallelism { get; set; } = 4;

    /// <summary>Rescan a single entry shortly after files in it change.</summary>
    public bool Watch { get; set; } = true;
    public int WatchDebounceSeconds { get; set; } = 20;

    public AutoFetchOptions AutoFetch { get; set; } = new();

    /// <summary>Send commits per day for the last year (one <c>git log</c> per repository, repeated only when its refs move).</summary>
    public bool Activity { get; set; } = true;

    /// <summary>Which SDKs and runtimes this machine has, and which versions each repo pins.</summary>
    public Toolchains.ToolchainOptions Toolchains { get; set; } = new();

    /// <summary>Slow, once-a-day checks for vulnerable and outdated packages.</summary>
    public Dependencies.DependencyOptions Dependencies { get; set; } = new();

    /// <summary>Allowlisted fixes queued from the dashboard. Off unless this machine turns them on.</summary>
    public Actions.ActionsOptions Actions { get; set; } = new();

    /// <summary>Self-update: off, on request from the dashboard (the default), or automatic.</summary>
    public Updates.UpdateOptions Updates { get; set; } = new();

    /// <summary>T3 Code: its threads in the snapshot, and the actions that open it or start a thread.</summary>
    public T3Code.T3Options T3 { get; set; } = new();

    /// <summary>
    /// The folders to scan. Configured roots are used as they are; with none configured, <c>~/Developer</c>
    /// and, when T3 Code has made one, its projects folder.
    /// </summary>
    /// <param name="home">The user's home folder. Only tests pass it; otherwise it is the real one.</param>
    public IReadOnlyList<string> ResolveRoots(string? home = null)
    {
        List<string> roots;
        if (Roots.Count > 0)
        {
            roots = [.. Roots];
        }
        else
        {
            roots = ["~/Developer"];
            if (T3.Enabled && T3.ScanProjects && T3.ProjectsFolder(home) is { } projects && Directory.Exists(projects))
            {
                roots.Add(projects);
            }
        }

        return roots.Select(r => ExpandHome(r, home)).Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToList();
    }

    public string? ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            return ApiKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(ApiKeyFile))
        {
            var file = Path.GetFullPath(ExpandHome(ApiKeyFile));
            if (!File.Exists(file))
            {
                throw new InvalidOperationException($"API key file {file} does not exist.");
            }

            return File.ReadAllText(file).Trim();
        }

        return null;
    }

    public static string ExpandHome(string path) => ExpandHome(path, null);

    /// <param name="home">The home folder to expand <c>~</c> to; null for the real one.</param>
    public static string ExpandHome(string path, string? home)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path.Length == 1 ? home : Path.Combine(home, path[2..]);
        }

        return path;
    }
}

public sealed class AutoFetchOptions
{
    /// <summary>Run <c>git fetch --prune --quiet</c> so ahead/behind and [gone] stay accurate.</summary>
    public bool Enabled { get; set; } = true;
    public double IntervalHours { get; set; } = 4;
    public int TimeoutSeconds { get; set; } = 60;
}
