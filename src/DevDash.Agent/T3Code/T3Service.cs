using System.Collections.Concurrent;
using DevDash.Agent.Toolchains;
using DevDash.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.T3Code;

/// <summary>
/// T3 Code as the scanner sees it: whether its CLI is there, and which project and threads each repository
/// has. Everything goes through read-only calls (<c>--version</c>, <c>thread list</c>, <c>thread show</c>),
/// each with a time limit, and anything that fails is left out of the snapshot: T3 Code not running, or not
/// installed, never makes a scan fail.
/// <para>
/// <b>Whose project is it?</b> <c>thread list --project &lt;path&gt;</c> answers with the nearest project that
/// encloses the path, so a project rooted at <c>~/Developer</c> would claim every repository in it. A project
/// counts as a repository's own only when no other scanned repository resolves to it; one that several do
/// is left out for all of them, and stays left out until a full scan says otherwise.
/// </para>
/// <para>
/// <b>Cost.</b> One <c>thread list</c> per repository per scan (about 0.2 s, four at a time), and a
/// <c>thread show</c> only for a thread whose <c>updatedAt</c> moved since it was last shown. After three
/// failures with no answer at all, the rest of the scan does not ask.
/// </para>
/// </summary>
public sealed class T3Service
{
    private const int Parallelism = 4;
    private const int GiveUpAfter = 3;
    private const string NoProject = "No T3 Code project matches";

    private readonly IToolRunner _runner;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ILogger<T3Service> _logger;
    private readonly TimeProvider _time;
    private readonly ToolEnvironment _environment;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _infoLock = new();

    private T3Info? _info;
    private string? _cli;
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
    private string? _lookupProblem;

    // The project rule, as of the last full scan (and the single-entry rescans since).
    private Dictionary<string, string> _owners = new(StringComparer.Ordinal);
    private HashSet<string> _ambiguous = new(StringComparer.Ordinal);

    // The last thread show of each thread, and the updatedAt it was shown at.
    private readonly ConcurrentDictionary<string, (DateTimeOffset? UpdatedAt, T3Thread Thread)> _shown = new(StringComparer.Ordinal);

    public T3Service(IToolRunner runner, IOptionsMonitor<AgentOptions> options, ILogger<T3Service> logger, TimeProvider? time = null, ToolEnvironment? environment = null)
    {
        _runner = runner;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _environment = environment ?? ToolEnvironment.Current();
    }

    private T3Options Options => _options.CurrentValue.T3;

    private TimeSpan Timeout => TimeSpan.FromSeconds(Math.Max(1, Options.TimeoutSeconds));

    /// <summary>What goes in <see cref="MachineInfo.T3"/>: null when the integration is off.</summary>
    public T3Info? Current
    {
        get
        {
            lock (_infoLock)
            {
                return _info is null ? null : _lookupProblem is { } problem && _info.Available ? _info with { Problem = problem } : _info;
            }
        }
    }

    /// <summary>Looks for the CLI and asks its version, at most every <see cref="T3Options.CheckIntervalMinutes"/> (five minutes while it is missing).</summary>
    public async Task<T3Info?> InfoAsync(CancellationToken ct)
    {
        var options = Options;
        if (!options.Enabled)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        lock (_infoLock)
        {
            var interval = TimeSpan.FromMinutes(_info is { Available: true } ? Math.Max(1, options.CheckIntervalMinutes) : 5);
            if (_info is not null && now - _checkedAt < interval)
            {
                return _info;
            }
        }

        T3Info info;
        var cli = T3Locator.Find(options.Path, options.ResolveHome(), _environment);
        if (cli is null)
        {
            info = new T3Info { Available = false, Problem = string.IsNullOrWhiteSpace(options.Path) ? "its CLI was not found" : "Agent:T3:Path does not exist" };
        }
        else
        {
            var result = await new T3Cli(_runner, cli).VersionAsync(Timeout, ct).ConfigureAwait(false);
            info = result.Ok
                ? new T3Info { Available = true, Version = T3Parser.ParseVersion(result.StdOut + "\n" + result.StdErr) }
                : new T3Info { Available = false, Problem = Describe(result) };
        }

        lock (_infoLock)
        {
            if (_info?.Available != info.Available)
            {
                _logger.LogInformation("T3 Code {State}", info.Available ? $"found ({info.Version ?? "unknown version"})" : $"not available: {info.Problem}");
            }

            _info = info;
            _cli = info.Available ? cli : null;
            _checkedAt = now;
        }

        return info;
    }

    /// <summary>The CLI, when T3 Code is on and available. Asks again if the last answer is old.</summary>
    public async Task<T3Cli?> CliAsync(CancellationToken ct)
    {
        if (await InfoAsync(ct).ConfigureAwait(false) is not { Available: true })
        {
            return null;
        }

        lock (_infoLock)
        {
            return _cli is null ? null : new T3Cli(_runner, _cli);
        }
    }

    /// <summary>
    /// Adds each repository's own T3 project and threads to the entries. <paramref name="fullScan"/> says the
    /// entries are everything the agent scans, so the project rule is worked out afresh; otherwise they are
    /// one rescanned entry, judged against what the last full scan found.
    /// </summary>
    public async Task<IReadOnlyList<DevEntry>> EnrichAsync(IReadOnlyList<DevEntry> entries, bool fullScan, CancellationToken ct)
    {
        if (await CliAsync(ct).ConfigureAwait(false) is not { } cli)
        {
            return entries;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var targets = DevDash.Core.RepoIdentity.Flatten(entries)
                .Where(e => e.Kind == EntryKind.GitRepo && e.Git is { IsLinkedWorktree: false })
                .Select(e => e.Path)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (targets.Count == 0)
            {
                return entries;
            }

            var lookups = new ConcurrentDictionary<string, Lookup>(StringComparer.Ordinal);
            int failures = 0, answers = 0;
            await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (path, token) =>
            {
                if (Volatile.Read(ref failures) >= GiveUpAfter && Volatile.Read(ref answers) == 0)
                {
                    return;
                }

                var lookup = await LookupAsync(cli, path, token).ConfigureAwait(false);
                Interlocked.Increment(ref lookup.Failed ? ref failures : ref answers);
                lookups[path] = lookup;
            }).ConfigureAwait(false);

            lock (_infoLock)
            {
                _lookupProblem = answers == 0 && failures > 0 ? "T3 Code did not answer; is the app running?" : null;
            }

            var found = lookups.Where(l => l.Value.ProjectId is not null).ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal);
            ApplyProjectRule(found.ToDictionary(f => f.Key, f => f.Value.ProjectId!, StringComparer.Ordinal), fullScan);

            var budget = new Budget(Math.Max(0, Options.MaxShowsPerScan));
            var projects = new Dictionary<string, T3Project>(StringComparer.Ordinal);
            foreach (var (path, lookup) in found.Where(f => !_ambiguous.Contains(f.Value.ProjectId!)).OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                projects[path] = await BuildAsync(cli, lookup, budget, ct).ConfigureAwait(false);
            }

            if (fullScan)
            {
                // Forget the threads no repository has any more.
                var live = found.Values.SelectMany(l => l.Threads).Select(t => t.Thread.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var id in _shown.Keys.Where(id => !live.Contains(id)).ToList())
                {
                    _shown.TryRemove(id, out _);
                }
            }

            return projects.Count == 0 ? entries : Apply(entries, projects);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Which project ids belong to one repository only. After a full scan: those exactly one path resolved
    /// to. After a rescan of one entry: those the last full scan gave it, plus new ones no other path has.
    /// </summary>
    internal void ApplyProjectRule(IReadOnlyDictionary<string, string> projectOfPath, bool fullScan)
    {
        if (fullScan)
        {
            var byProject = projectOfPath.GroupBy(p => p.Value, StringComparer.Ordinal).ToList();
            _ambiguous = byProject.Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            _owners = byProject.Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Key, StringComparer.Ordinal);
            return;
        }

        foreach (var (path, project) in projectOfPath)
        {
            if (_ambiguous.Contains(project))
            {
                continue;
            }

            if (_owners.TryGetValue(project, out var owner) && !string.Equals(owner, path, StringComparison.Ordinal))
            {
                _ambiguous.Add(project);
                _owners.Remove(project);
            }
            else
            {
                _owners[project] = path;
            }
        }
    }

    /// <summary>Whether a project id is shared by several repositories, as far as the scans so far tell.</summary>
    internal bool IsShared(string projectId) => _ambiguous.Contains(projectId);

    private sealed record Lookup(string? ProjectId, IReadOnlyList<T3ListedThread> Threads, bool Failed);

    private sealed class Budget(int shows)
    {
        public bool Take() => Interlocked.Decrement(ref shows) >= 0;
    }

    private async Task<Lookup> LookupAsync(T3Cli cli, string path, CancellationToken ct)
    {
        try
        {
            var result = await cli.ListThreadsAsync(path, Timeout, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                // "No T3 Code project matches '<path>'" is an answer: this repository is in no project.
                var none = (result.StdErr + result.StdOut).Contains(NoProject, StringComparison.OrdinalIgnoreCase);
                if (!none)
                {
                    _logger.LogDebug("t3 thread list for {Path} failed: {Problem}", path, Describe(result));
                }

                return new Lookup(null, [], Failed: !none);
            }

            if (T3Parser.ParseThreadList(result.StdOut) is not { } threads)
            {
                _logger.LogDebug("t3 thread list for {Path} printed something that is not a list of threads", path);
                return new Lookup(null, [], Failed: true);
            }

            // A project with no threads names no project id, and has nothing to show anyway.
            var project = threads.Select(t => t.ProjectId).FirstOrDefault(p => !string.IsNullOrEmpty(p));
            return new Lookup(project, threads.Where(t => t.ProjectId is null || t.ProjectId == project).ToList(), Failed: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug("t3 thread list for {Path} failed: {Error}", path, ex.Message);
            return new Lookup(null, [], Failed: true);
        }
    }

    private async Task<T3Project> BuildAsync(T3Cli cli, Lookup lookup, Budget budget, CancellationToken ct)
    {
        var open = lookup.Threads.Where(t => !t.Archived).OrderByDescending(t => t.Thread.UpdatedAt ?? t.Thread.CreatedAt).ToList();
        var threads = new List<T3Thread>();
        foreach (var listed in open)
        {
            if (threads.Count >= T3Project.MaxThreads)
            {
                break;
            }

            var thread = listed.Thread;
            if (_shown.TryGetValue(thread.Id, out var cached) && cached.UpdatedAt == thread.UpdatedAt)
            {
                threads.Add(Merge(thread, cached.Thread));
                continue;
            }

            if (budget.Take() && await ShowAsync(cli, thread.Id, ct).ConfigureAwait(false) is { } shown)
            {
                if (shown.Archived)
                {
                    continue;
                }

                _shown[thread.Id] = (thread.UpdatedAt, shown.Thread);
                threads.Add(Merge(thread, shown.Thread));
                continue;
            }

            // No details this time (none left in this scan's budget, or show failed): the list's own fields.
            threads.Add(cached.Thread is null ? thread : Merge(thread, cached.Thread));
        }

        return new T3Project
        {
            ProjectId = lookup.ProjectId!,
            Threads = threads,
            ThreadCount = open.Count > threads.Count ? open.Count : null,
        };
    }

    private async Task<T3ListedThread?> ShowAsync(T3Cli cli, string id, CancellationToken ct)
    {
        try
        {
            var result = await cli.ShowThreadAsync(id, Timeout, ct).ConfigureAwait(false);
            return result.Ok ? T3Parser.ParseThreadShow(result.StdOut) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug("t3 thread show {Id} failed: {Error}", id, ex.Message);
            return null;
        }
    }

    /// <summary>The list is newer for what it carries; the details (branch, worktree, pending requests) come from show.</summary>
    private static T3Thread Merge(T3Thread listed, T3Thread shown) => listed with
    {
        Branch = shown.Branch ?? listed.Branch,
        WorktreePath = shown.WorktreePath ?? listed.WorktreePath,
        PendingRequests = Math.Max(listed.PendingRequests, shown.PendingRequests),
        PullRequests = listed.PullRequests.Count > 0 ? listed.PullRequests : shown.PullRequests,
        Link = listed.Link ?? shown.Link,
    };

    private static IReadOnlyList<DevEntry> Apply(IReadOnlyList<DevEntry> entries, IReadOnlyDictionary<string, T3Project> projects) =>
        entries.Select(e =>
        {
            var children = e.Children.Count == 0 ? e.Children : Apply(e.Children, projects);
            var project = projects.GetValueOrDefault(e.Path);
            return project is null && ReferenceEquals(children, e.Children) ? e : e with { T3 = project ?? e.T3, Children = children };
        }).ToList();

    private static string Describe(ToolResult result)
    {
        if (!result.Started)
        {
            return "its CLI could not be started";
        }

        if (result.TimedOut)
        {
            return "its CLI did not answer in time";
        }

        var line = (result.StdErr.Length > 0 ? result.StdErr : result.StdOut).Trim().Split('\n')[0].Trim();
        return line.Length == 0 ? $"its CLI exited with {result.ExitCode}" : line.Length > 160 ? line[..160] + "…" : line;
    }
}
