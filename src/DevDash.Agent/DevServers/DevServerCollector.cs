using DevDash.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.DevServers;

public sealed class DevServerOptions
{
    /// <summary>List listening development servers with every scan, and accept StopDevServer when actions are on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Most servers reported per machine; the ones inside repositories come first.</summary>
    public int MaxServers { get; set; } = 50;

    /// <summary>How long a stop waits for the process to exit before reporting that it is still shutting down.</summary>
    public int StopWaitSeconds { get; set; } = 5;

    /// <summary>
    /// More program names never reported, on top of <see cref="DevServerCollector.IgnoredTools"/>: comma- or
    /// space-separated (a string, because configuration adds to a list's defaults instead of replacing them).
    /// </summary>
    public string Ignore { get; set; } = "";
}

/// <summary>
/// Turns the operating system's listening processes into the snapshot's dev servers: those running inside a
/// scanned repository (by working folder, or a path on the command line), plus well-known development tools
/// wherever they run. Everything else (the desktop's own listeners, sync clients, chat apps) is left out.
/// </summary>
public sealed class DevServerCollector(IProcessProbe probe, IOptionsMonitor<AgentOptions> options, ILogger<DevServerCollector> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Program names that are development servers wherever they run. Compared without case and without <c>.exe</c>.</summary>
    public static readonly IReadOnlySet<string> KnownTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "node", "nodejs", "deno", "bun", "dotnet", "python", "python3", "ruby", "go", "java", "php", "docker-proxy",
        "vite", "next-server", "esbuild", "webpack", "uvicorn", "gunicorn", "puma", "rails", "beam.smp", "hugo", "jekyll", "air", "cargo", "flask",
    };

    /// <summary>
    /// Coding agents, editors and DevDash itself: they often run inside a repository and open a local port, but they are
    /// not servers the user started, and offering to stop one (the Claude session doing the work, say) would be wrong.
    /// </summary>
    public static readonly IReadOnlySet<string> IgnoredTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "claude", "codex", "gemini", "aider", "opencode", "t3", "devdash-agent", "code", "code-insiders", "cursor", "windsurf", "zed",
        "rider", "idea", "webstorm", "copilot-language-server", "omnisharp", "gopls", "rust-analyzer", "typescript-language-server",
    };

    /// <summary>Package paths that mark a coding agent run through node (an npm install of the CLI).</summary>
    private static readonly string[] IgnoredPackages = ["@anthropic-ai/claude-code", "@openai/codex", "@google/gemini-cli"];

    /// <summary>True for a coding agent, editor or language server (<see cref="IgnoredTools"/> plus <paramref name="extra"/>), whatever its folder.</summary>
    public static bool IsIgnored(ListeningProcess process, IReadOnlySet<string>? extra = null)
    {
        bool Named(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            var name = Path.GetFileName(path.Replace('\\', '/').TrimEnd('/'));
            name = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            return IgnoredTools.Contains(name) || extra?.Contains(name) == true
                || name.StartsWith("code-", StringComparison.OrdinalIgnoreCase); // VS Code's versioned server, code-6904…
        }

        return Named(process.Name) || Named(process.Executable) || (process.Args.Count > 0 && Named(process.Args[0]))
            || process.Args.Any(a => IgnoredPackages.Any(p => a.Replace('\\', '/').Contains(p, StringComparison.OrdinalIgnoreCase)))
            || (process.Executable ?? "").Replace('\\', '/').Contains("/claude/versions/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What to call a process: its program's name, unless the system's short name says nothing (Claude Code's
    /// binary is named after its version, <c>2.1.329</c>), then the command's first word.
    /// </summary>
    public static string DisplayName(ListeningProcess process)
    {
        var name = process.Name;
        var looksLikeVersion = name.Length > 0 && name.All(c => char.IsDigit(c) || c == '.');
        return looksLikeVersion && process.Args.Count > 0 ? Path.GetFileName(process.Args[0].Replace('\\', '/')) : name;
    }

    public static bool IsKnownTool(string name)
    {
        var bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        return KnownTools.Contains(bare) || (bare.StartsWith("python3.", StringComparison.OrdinalIgnoreCase) && bare.Length <= 10);
    }

    /// <summary>The report for this scan. Null when the list is off; a report with a problem when the system could not be asked.</summary>
    public async Task<DevServerReport?> CollectAsync(IReadOnlyList<DevEntry> entries, CancellationToken ct)
    {
        var settings = options.CurrentValue.DevServers;
        if (!settings.Enabled)
        {
            return null;
        }

        ListenResult result;
        try
        {
            result = await probe.ListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not list listening processes: {Error}", ex.Message);
            return new DevServerReport { CollectedAt = _time.GetUtcNow(), Problem = "Could not list listening processes: " + ex.Message };
        }

        var repos = RepoPaths(entries);
        var extra = settings.Ignore.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var servers = result.Processes
            .Where(p => p.Pid != Environment.ProcessId && !IsIgnored(p, extra))
            .Select(p => (Process: p, Repo: RepoFor(p, repos, OperatingSystem.IsLinux())))
            .Where(x => x.Repo is not null || IsKnownTool(x.Process.Name) || (x.Process.Args.Count > 0 && IsKnownTool(Path.GetFileName(x.Process.Args[0]))))
            .OrderBy(x => x.Repo is null)
            .ThenBy(x => x.Process.Ports.FirstOrDefault())
            .Take(Math.Clamp(settings.MaxServers, 1, 500))
            .Select(x => ToServer(x.Process, x.Repo))
            .ToList();
        return new DevServerReport { CollectedAt = _time.GetUtcNow(), Servers = servers, Problem = result.Problem };
    }

    public static DevServer ToServer(ListeningProcess process, string? repo) => new()
    {
        Pid = process.Pid,
        Name = DisplayName(process),
        Command = process.Args.Count > 0 ? DevServerText.Command(process.Args) : null,
        StartedAt = process.StartedAt,
        Cwd = process.Cwd,
        Ports = process.Ports,
        RepoPath = repo,
    };

    /// <summary>The working trees in a scan: repositories and linked worktrees, wherever they sit.</summary>
    public static IReadOnlyList<string> RepoPaths(IReadOnlyList<DevEntry> entries) =>
        DevDash.Core.RepoIdentity.Flatten(entries).Where(e => e.Kind == EntryKind.GitRepo).Select(e => e.Path).ToList();

    /// <summary>
    /// The working tree a process runs in: the deepest one holding its working folder, else the deepest one holding
    /// its program or a path on its command line (<c>--project /…/akin</c>, <c>/…/akin/bin/Debug/Akin</c>).
    /// </summary>
    public static string? RepoFor(ListeningProcess process, IReadOnlyList<string> repos, bool caseSensitive)
    {
        if (process.Cwd is { } cwd && Deepest(cwd, repos, caseSensitive) is { } byCwd)
        {
            return byCwd;
        }

        foreach (var candidate in PathsIn(process))
        {
            if (Deepest(candidate, repos, caseSensitive) is { } byArg)
            {
                return byArg;
            }
        }

        return null;
    }

    /// <summary>Absolute paths in the program and its arguments, quotes and <c>--option=</c> taken off.</summary>
    public static IEnumerable<string> PathsIn(ListeningProcess process)
    {
        IEnumerable<string?> raw = [process.Executable, .. process.Args];
        foreach (var item in raw)
        {
            if (item is null)
            {
                continue;
            }

            var text = item.Trim('"', '\'');
            var eq = text.IndexOf('=');
            if (eq > 0 && !LooksAbsolute(text))
            {
                text = text[(eq + 1)..].Trim('"', '\'');
            }

            if (text.Length > 2 && LooksAbsolute(text))
            {
                yield return text;
            }
        }
    }

    /// <summary>An absolute path on any system: <c>/…</c> or <c>C:\…</c>. Not <see cref="Path.IsPathFullyQualified(string)"/>, whose answer depends on the system it runs on.</summary>
    public static bool LooksAbsolute(string text) =>
        text.Length > 0 && (text[0] == '/' || (text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] is '\\' or '/'));

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    public static bool Within(string folder, string path, bool caseSensitive)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var a = folder.TrimEnd('/', '\\');
        var b = path.TrimEnd('/', '\\');
        return b.Equals(a, comparison)
            || (b.StartsWith(a, comparison) && b.Length > a.Length && b[a.Length] is '/' or '\\');
    }

    private static string? Deepest(string path, IReadOnlyList<string> repos, bool caseSensitive) =>
        repos.Where(r => Within(r, path, caseSensitive)).OrderByDescending(r => r.Length).FirstOrDefault();
}
