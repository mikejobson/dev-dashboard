using System.Collections.Concurrent;
using DevDash.Agent.Scanning;
using DevDash.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent;

/// <summary>
/// The long-running service: a full scan on a timer, plus a debounced rescan of a single entry when
/// files under it change, and a refresh of a single entry when the dashboard asks for one. Every scan is
/// pushed to the hub. All of it happens on this one loop, so there is only ever one scan at a time and
/// one writer of the last snapshot.
/// </summary>
public sealed class AgentWorker : BackgroundService
{
    private static readonly HashSet<string> IgnoredSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".venv", "target", "dist", ".next", ".angular", ".vs", ".idea",
    };

    private readonly MachineScanner _scanner;
    private readonly HubClient _hub;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ILogger<AgentWorker> _logger;
    private readonly ConcurrentDictionary<(string Root, string Name), byte> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);

    // Refreshes asked for from the dashboard, by entry. Kept apart from _pending: a full scan clears that,
    // and a refresh still has to happen (it forces sizes and a fetch) and be answered.
    private readonly Lock _refreshLock = new();
    private Dictionary<(string Root, string Name), List<TaskCompletionSource<Actions.RefreshOutcome>>> _refreshes = [];
    private readonly Updates.UpdateGuard? _guard;
    private readonly Actions.KnownRemotes? _knownRemotes;
    private MachineSnapshot? _last;
    private int _rescanNow;

    /// <summary>While a new version waits to be confirmed, it keeps trying to reach the hub this often.</summary>
    private static readonly TimeSpan VerifyRetry = TimeSpan.FromSeconds(30);

    public AgentWorker(MachineScanner scanner, HubClient hub, IOptionsMonitor<AgentOptions> options, ILogger<AgentWorker> logger, Actions.RescanRequests? rescans = null, Updates.UpdateGuard? guard = null, Actions.KnownRemotes? knownRemotes = null)
    {
        _guard = guard;
        _knownRemotes = knownRemotes;
        _scanner = scanner;
        _hub = hub;
        _options = options;
        _logger = logger;

        // An agent action just changed a repo: rescan its entry now, without the file-change debounce.
        if (rescans is not null)
        {
            rescans.Requested += (root, name) =>
            {
                _pending.TryAdd((root, name), 0);
                Volatile.Write(ref _rescanNow, 1);
                _signal.Release();
            };

            // A refresh from the dashboard: same loop, no debounce. Requests for one entry share one rescan.
            rescans.RefreshRequested += requests =>
            {
                lock (_refreshLock)
                {
                    foreach (var request in requests)
                    {
                        if (!_refreshes.TryGetValue((request.Root, request.Name), out var waiting))
                        {
                            _refreshes[(request.Root, request.Name)] = waiting = [];
                        }

                        waiting.Add(request.Completion);
                    }
                }

                Volatile.Write(ref _rescanNow, 1);
                _signal.Release();
            };
        }
    }

    /// <summary>
    /// Runs the entry rescans that are waiting: file changes, and refreshes asked for from the dashboard.
    /// The service loop calls this between full scans; it must never run alongside one.
    /// </summary>
    public Task RescanPendingNowAsync(CancellationToken ct) => RescanPendingAsync(ct);

    /// <summary>One full scan and push, as the service loop does on its timer.</summary>
    public Task FullScanNowAsync(CancellationToken ct) => FullScanAsync(quick: false, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;
        _logger.LogInformation("devdash-agent {Version} scanning {Roots} every {Minutes} min, pushing to {Hub} (auto-fetch {Fetch})",
            MachineInfoProvider.AgentVersion, string.Join(", ", options.ResolveRoots()), options.ScanIntervalMinutes, options.HubUrl,
            options.AutoFetch.Enabled ? $"every {options.AutoFetch.IntervalHours}h" : "off");

        var watchers = options.Watch ? StartWatchers(options.ResolveRoots()) : [];
        try
        {
            var nextFull = DateTimeOffset.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                if (DateTimeOffset.UtcNow >= nextFull)
                {
                    // Just updated: the hub accepting a snapshot is what confirms the new version, so get one
                    // there quickly (no fetching, no size scan) and keep trying until it lands. The normal
                    // scan follows straight after.
                    var verifying = _guard?.IsVerifying == true;
                    await FullScanAsync(verifying, stoppingToken).ConfigureAwait(false);
                    nextFull = verifying
                        ? DateTimeOffset.UtcNow + (_guard!.IsVerifying ? VerifyRetry : TimeSpan.Zero)
                        : DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, _options.CurrentValue.ScanIntervalMinutes));
                    _pending.Clear();
                }

                var wait = nextFull - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero && await _signal.WaitAsync(wait, stoppingToken).ConfigureAwait(false))
                {
                    // Something changed: let the edits settle, then rescan just the entries involved.
                    if (Interlocked.Exchange(ref _rescanNow, 0) == 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.WatchDebounceSeconds)), stoppingToken).ConfigureAwait(false);
                        Volatile.Write(ref _rescanNow, 0);
                    }

                    while (_signal.CurrentCount > 0)
                    {
                        await _signal.WaitAsync(stoppingToken).ConfigureAwait(false);
                    }

                    await RescanPendingAsync(stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        finally
        {
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }
        }
    }

    private async Task FullScanAsync(bool quick, CancellationToken ct)
    {
        try
        {
            var request = quick ? new ScanRequest(AllowFetch: false, SizeMode.CachedOnly) : new ScanRequest(AllowFetch: true, SizeMode.WhenDue);
            var snapshot = await _scanner.ScanAsync(request, ct).ConfigureAwait(false);
            _last = snapshot;
            _knownRemotes?.Update(snapshot);
            await PushAsync(snapshot, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Scan failed");
        }
    }

    private async Task RescanPendingAsync(CancellationToken ct)
    {
        Dictionary<(string Root, string Name), List<TaskCompletionSource<Actions.RefreshOutcome>>> refreshes;
        lock (_refreshLock)
        {
            refreshes = _refreshes;
            _refreshes = [];
        }

        // Whatever happens below, everyone waiting on a refresh gets an answer.
        void Answer((string Root, string Name) key, Actions.RefreshOutcome outcome)
        {
            foreach (var waiting in refreshes[key])
            {
                waiting.TrySetResult(outcome);
            }
        }

        try
        {
            if (_last is null)
            {
                foreach (var key in refreshes.Keys)
                {
                    Answer(key, new Actions.RefreshOutcome(false, "The agent has not completed its first scan yet. Try again in a moment."));
                }

                return;
            }

            // An entry that is being refreshed needs no second, lighter rescan for its file changes.
            var changed = _pending.Keys.ToList();
            foreach (var key in changed)
            {
                _pending.TryRemove(key, out _);
            }

            changed.RemoveAll(refreshes.ContainsKey);
            if (changed.Count == 0 && refreshes.Count == 0)
            {
                return;
            }

            var entries = _last.Entries.ToList();
            void Replace((string Root, string Name) key, DevEntry? entry)
            {
                entries.RemoveAll(e => e.Root == key.Root && e.Name == key.Name);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }

            foreach (var key in changed)
            {
                Replace(key, await _scanner.ScanEntryAsync(key.Root, key.Name, new ScanRequest(AllowFetch: false, SizeMode.CachedOnly), ct).ConfigureAwait(false));
            }

            // A refresh fetches now instead of when due, but only on a machine that fetches at all:
            // one with Agent:AutoFetch off stays a machine whose repositories the agent never writes to.
            var fetches = _options.CurrentValue.AutoFetch.Enabled;
            var since = DateTimeOffset.UtcNow;
            var notes = new Dictionary<(string Root, string Name), Actions.RefreshOutcome>();
            foreach (var key in refreshes.Keys)
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var forced = fetches ? new Git.ForcedFetch(since) : null;
                var entry = await _scanner.ScanEntryAsync(key.Root, key.Name, new ScanRequest(AllowFetch: fetches, SizeMode.Force, forced), ct).ConfigureAwait(false);
                Replace(key, entry);
                notes[key] = Describe(key.Name, entry, forced, stopwatch.ElapsedMilliseconds);
            }

            _last = _last with
            {
                ScannedAt = DateTimeOffset.UtcNow,
                Entries = MachineScanner.WithOneGraphPerRepository(entries.OrderBy(e => e.Root, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList()),
            };
            _knownRemotes?.Update(_last);
            if (changed.Count > 0)
            {
                _logger.LogInformation("Rescanned {Names} after file changes", string.Join(", ", changed.Select(c => c.Name)));
            }

            if (refreshes.Count > 0)
            {
                _logger.LogInformation("Refreshed {Names} as asked from the dashboard", string.Join(", ", refreshes.Keys.Select(c => c.Name)));
            }

            var pushError = await PushAsync(_last, ct).ConfigureAwait(false);
            foreach (var (key, note) in notes)
            {
                Answer(key, pushError is null ? note : new Actions.RefreshOutcome(false, $"Rescanned {key.Name}, but the snapshot could not be pushed to the hub: {pushError}"));
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var waiting in refreshes.Values.SelectMany(w => w))
            {
                waiting.TrySetCanceled(CancellationToken.None);
            }

            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rescan failed");
            foreach (var key in refreshes.Keys)
            {
                Answer(key, new Actions.RefreshOutcome(false, "The rescan failed: " + ex.Message));
            }
        }
    }

    /// <summary>What a refresh of one entry is reported as. A fetch that failed is a failure: the remote-tracking branches are not current.</summary>
    private static Actions.RefreshOutcome Describe(string name, DevEntry? entry, Git.ForcedFetch? forced, long milliseconds)
    {
        if (entry is null)
        {
            return new Actions.RefreshOutcome(true, $"{name} no longer exists on this machine, so it was removed from the snapshot.");
        }

        if (entry.Error is { } error)
        {
            return new Actions.RefreshOutcome(false, $"Rescanned {name}, but it could not be read as a git repository: {error}");
        }

        if (forced is not null && !forced.Failures.IsEmpty)
        {
            return new Actions.RefreshOutcome(false,
                $"Rescanned {name} and measured its size, but git fetch failed, so its remote-tracking branches may be out of date.\n{string.Join('\n', forced.Failures)}");
        }

        var fetched = forced is null ? "auto-fetch is off on this machine, so nothing was fetched"
            : forced.Fetched == 0 ? "nothing to fetch"
            : forced.Fetched == 1 ? "fetched"
            : $"fetched {forced.Fetched} repositories";
        return new Actions.RefreshOutcome(true, $"Rescanned {name} in {milliseconds} ms: {fetched}, size measured, snapshot pushed.");
    }

    /// <summary>Pushes the snapshot. Returns null when the hub accepted it, otherwise why it did not.</summary>
    private async Task<string?> PushAsync(MachineSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            var response = await _hub.PushAsync(snapshot, ct).ConfigureAwait(false);
            _logger.LogInformation("Pushed {Count} entries in {Ms} ms scan to hub as {Machine}", response.Entries, snapshot.ScanDurationMs, response.MachineId);
            if (_guard?.Confirm() is { } confirmed)
            {
                _logger.LogWarning("Update confirmed: devdash-agent {Version} has reported to the hub (was {Previous}). The previous version has been removed.",
                    confirmed.ToVersion, confirmed.FromVersion);
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // The hub may be asleep or unreachable; the next scan tries again.
            _logger.LogWarning("Could not push snapshot: {Error}", ex.Message);
            return ex.Message;
        }
    }

    private List<FileSystemWatcher> StartWatchers(IReadOnlyList<string> roots)
    {
        var watchers = new List<FileSystemWatcher>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                };
                void OnChange(object _, FileSystemEventArgs e) => Changed(root, e.FullPath);
                watcher.Changed += OnChange;
                watcher.Created += OnChange;
                watcher.Deleted += OnChange;
                watcher.Renamed += (_, e) => Changed(root, e.FullPath);
                watcher.Error += (_, e) => _logger.LogDebug(e.GetException(), "File watcher error in {Root}", root);
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
            {
                _logger.LogWarning("Could not watch {Root}: {Error}", root, ex.Message);
            }
        }

        return watchers;
    }

    private void Changed(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Length == 0 || segments[0] is "" or "." or ".." || segments[0].StartsWith('.'))
        {
            return;
        }

        // Git's own bookkeeping and build output are noise, and the agent's fetch writes under .git.
        if (segments.Any(IgnoredSegments.Contains) || segments[^1] == ".DS_Store")
        {
            return;
        }

        if (_pending.TryAdd((root, segments[0]), 0))
        {
            _signal.Release();
        }
    }
}
