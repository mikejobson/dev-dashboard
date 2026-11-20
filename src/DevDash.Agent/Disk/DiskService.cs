using System.Text.Json;
using DevDash.Agent.Actions;
using DevDash.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.Disk;

/// <summary>
/// The last disk report, kept in the agent's state folder (<c>agent-disk.json</c>, beside the size cache) so a
/// restart does not measure everything again. The scanner only reads it; <see cref="DiskWorker"/> measures in
/// the background, never on the scan's path.
/// </summary>
public sealed class DiskService
{
    private readonly DiskCollector _collector;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ILogger<DiskService> _logger;
    private readonly TimeProvider _time;
    private readonly string _file;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DiskReport? _report;

    public DiskService(DiskCollector collector, IOptionsMonitor<AgentOptions> options, ILogger<DiskService> logger, string? file = null, TimeProvider? time = null)
    {
        _collector = collector;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _file = file ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "devdash", "agent-disk.json");
        _report = Load(_file);
    }

    private DiskOptions Settings => _options.CurrentValue.Disk;

    private TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(Settings.TimeoutSeconds, 5, 3600));

    /// <summary>What goes into the snapshot: the last report, less anything since skipped. Null when the report is off.</summary>
    public DiskReport? Current
    {
        get
        {
            var settings = Settings;
            if (!settings.Enabled || Volatile.Read(ref _report) is not { } report)
            {
                return null;
            }

            return settings.Skip.Count == 0 ? report : report with { Items = report.Items.Where(i => !settings.Skip.Contains(i.Id)).ToList() };
        }
    }

    /// <summary>True when there is no report yet, or the last one is older than <see cref="DiskOptions.IntervalHours"/>.</summary>
    public bool IsDue() =>
        Settings.Enabled && (Volatile.Read(ref _report) is not { } report || _time.GetUtcNow() - report.CollectedAt >= TimeSpan.FromHours(Math.Max(0.05, Settings.IntervalHours)));

    /// <summary>Measures every cache now and keeps the result.</summary>
    public async Task<DiskReport?> CollectAsync(CancellationToken ct)
    {
        if (!Settings.Enabled)
        {
            return null;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var report = await _collector.CollectAsync(Settings.Skip, Timeout, ct).ConfigureAwait(false);
            Volatile.Write(ref _report, report);
            Save(report);
            _logger.LogInformation("Disk report: {Items}", report.Items.Count == 0 ? "no developer caches found" : string.Join("; ", report.Items.Select(i => $"{i.Id} {(i.Bytes is { } b ? $"{b / (1024 * 1024)} MB" : i.Problem)}")));
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Measures one cache again (after cleaning it) and puts it into the kept report.</summary>
    public async Task RefreshItemAsync(DiskCacheId id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var item = await _collector.CollectItemAsync(id, Timeout, ct).ConfigureAwait(false);
            var current = Volatile.Read(ref _report) ?? new DiskReport { CollectedAt = _time.GetUtcNow() };
            var items = current.Items.Where(i => i.Id != id.ToString()).ToList();
            if (item is not null)
            {
                items.Add(item);
            }

            var updated = current with
            {
                Items = items.OrderBy(i => DiskCaches.Find(i.Id) is { } info ? (int)info.Id : int.MaxValue).ToList(),
            };
            Volatile.Write(ref _report, updated);
            Save(updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Save(DiskReport report)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(report, SnapshotSchema.JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file only saves measuring again after a restart.
        }
    }

    private static DiskReport? Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<DiskReport>(File.ReadAllText(file), SnapshotSchema.JsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Measures the developer caches in the background: shortly after the agent starts when the kept report is due,
/// then every <see cref="DiskOptions.IntervalHours"/>. When it has a new report it asks the scan loop to push.
/// </summary>
public sealed class DiskWorker(DiskService disk, IOptionsMonitor<AgentOptions> options, ILogger<DiskWorker> logger, RescanRequests? rescans = null) : BackgroundService
{
    private static readonly TimeSpan Check = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.CurrentValue.Disk.StartDelaySeconds)), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (disk.IsDue())
                {
                    try
                    {
                        await disk.CollectAsync(stoppingToken).ConfigureAwait(false);
                        rescans?.RequestMachine();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning("Could not measure the developer caches: {Error}", ex.Message);
                    }
                }

                await Task.Delay(Check, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }
}
