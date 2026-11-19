using System.Collections.Concurrent;
using DevDash.Agent.Scanning;
using DevDash.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent;

/// <summary>
/// The long-running service: a full scan on a timer, plus a debounced rescan of a single entry when
/// files under it change. Every scan is pushed to the hub.
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
    private readonly Updates.UpdateGuard? _guard;
    private MachineSnapshot? _last;
    private int _rescanNow;

    /// <summary>While a new version waits to be confirmed, it keeps trying to reach the hub this often.</summary>
    private static readonly TimeSpan VerifyRetry = TimeSpan.FromSeconds(30);

    public AgentWorker(MachineScanner scanner, HubClient hub, IOptionsMonitor<AgentOptions> options, ILogger<AgentWorker> logger, Actions.RescanRequests? rescans = null, Updates.UpdateGuard? guard = null)
    {
        _guard = guard;
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
        }
    }

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
            await PushAsync(snapshot, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Scan failed");
        }
    }

    private async Task RescanPendingAsync(CancellationToken ct)
    {
        if (_last is null)
        {
            return;
        }

        var changed = _pending.Keys.ToList();
        foreach (var key in changed)
        {
            _pending.TryRemove(key, out _);
        }

        if (changed.Count == 0)
        {
            return;
        }

        try
        {
            var entries = _last.Entries.ToList();
            foreach (var (root, name) in changed)
            {
                var entry = await _scanner.ScanEntryAsync(root, name, new ScanRequest(AllowFetch: false, SizeMode.CachedOnly), ct).ConfigureAwait(false);
                entries.RemoveAll(e => e.Root == root && e.Name == name);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }

            _last = _last with
            {
                ScannedAt = DateTimeOffset.UtcNow,
                Entries = entries.OrderBy(e => e.Root, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            };
            _logger.LogInformation("Rescanned {Names} after file changes", string.Join(", ", changed.Select(c => c.Name)));
            await PushAsync(_last, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Rescan failed");
        }
    }

    private async Task PushAsync(MachineSnapshot snapshot, CancellationToken ct)
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
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // The hub may be asleep or unreachable; the next scan tries again.
            _logger.LogWarning("Could not push snapshot: {Error}", ex.Message);
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
