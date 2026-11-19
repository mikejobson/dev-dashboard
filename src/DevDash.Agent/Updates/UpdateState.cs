using System.Text;
using System.Text.Json;
using DevDash.Agent.Service;
using DevDash.Contracts;

namespace DevDash.Agent.Updates;

/// <summary>An update that has been installed but has not yet proved itself by reaching the hub.</summary>
public sealed record PendingUpdate
{
    public required string FromVersion { get; init; }
    public required string ToVersion { get; init; }
    public DateTimeOffset InstalledAt { get; init; }

    /// <summary>How many times the new version has started as the agent (<c>run</c>) since it was installed.</summary>
    public int Starts { get; init; }

    /// <summary>The files that were replaced and where the previous version of each is kept.</summary>
    public IReadOnlyList<InstalledFile> Files { get; init; } = [];
}

/// <summary>The contents of <c>devdash-agent.update.json</c>, beside the program.</summary>
public sealed record UpdateState
{
    public PendingUpdate? Pending { get; init; }

    /// <summary>How the most recent update ended. Sent to the hub in every snapshot.</summary>
    public AgentUpdateReport? Last { get; init; }

    /// <summary>A version that was rolled back, so <c>auto</c> mode does not install it again straight away.</summary>
    public string? FailedVersion { get; init; }
    public DateTimeOffset? FailedAt { get; init; }
}

/// <summary>Reads and writes the state file. A missing or unreadable file is an empty state.</summary>
public sealed class UpdateStateStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(SnapshotSchema.IndentedJsonOptions);

    public string Path { get; } = path;

    public UpdateState Read()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(Path), Json) ?? new UpdateState() : new UpdateState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UpdateState();
        }
    }

    /// <summary>Writes through a temporary file and a rename, so a crash never leaves half a file.</summary>
    public void Write(UpdateState state)
    {
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json) + "\n", new UTF8Encoding(false));
        File.Move(temp, Path, overwrite: true);
    }
}

public enum GuardStart
{
    /// <summary>No update is waiting to be confirmed.</summary>
    Nothing,

    /// <summary>This is a new version that has not reached the hub yet: it must do so before <see cref="UpdateGuard.Deadline"/>.</summary>
    Verifying,

    /// <summary>The new version used up its chances. The previous files are back in place and this process must exit so they run.</summary>
    RolledBack,
}

/// <summary>
/// Decides whether a freshly installed version stays. The rule:
/// <list type="number">
/// <item>Installing writes a pending record and keeps the previous files as <c>.old</c>.</item>
/// <item>Each time the new version starts as the agent, the start is counted.</item>
/// <item>The first snapshot the hub accepts confirms the update: the record is cleared and <c>.old</c> deleted.</item>
/// <item>If the new version starts more than <see cref="UpdateOptions.MaxStarts"/> times without that
/// (it keeps crashing), or runs for <see cref="UpdateOptions.VerifyWindow"/> without it (it cannot reach
/// or satisfy the hub), <c>.old</c> is put back and the outcome is reported to the hub by the restored version.</item>
/// </list>
/// The decision is made by the new version itself, at the very start of <c>run</c>, before anything
/// else is loaded. A binary too broken to get that far is caught earlier: nothing is swapped in until
/// its <c>--version</c> has run and printed the expected version.
/// </summary>
public sealed class UpdateGuard(UpdateStateStore store, IUpdateFiles files, TimeProvider time, UpdateOptions options, ServicePlatform platform, string currentVersion)
{
    private readonly Lock _lock = new();
    private DateTimeOffset? _deadline;

    public string CurrentVersion => currentVersion;

    /// <summary>True while this process is a new version waiting for its first accepted snapshot.</summary>
    public bool IsVerifying
    {
        get
        {
            lock (_lock)
            {
                return _deadline is not null;
            }
        }
    }

    /// <summary>When this process gives up waiting and rolls back. Null when nothing is being verified.</summary>
    public DateTimeOffset? Deadline
    {
        get
        {
            lock (_lock)
            {
                return _deadline;
            }
        }
    }

    public UpdateState State => store.Read();

    /// <summary>Called once, first thing, when the agent starts as <c>run</c>.</summary>
    public GuardStart OnStart()
    {
        lock (_lock)
        {
            UpdateState state;
            try
            {
                state = store.Read();
                DeleteDiscarded();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return GuardStart.Nothing;
            }

            if (state.Pending is not { } pending)
            {
                return GuardStart.Nothing;
            }

            if (pending.ToVersion != currentVersion)
            {
                // The files on disk are not the version that was being installed: the swap was interrupted
                // or someone replaced the program by hand. Either way there is nothing left to verify.
                var report = pending.FromVersion == currentVersion
                    ? new AgentUpdateReport
                    {
                        Outcome = AgentUpdateOutcomes.Failed,
                        FromVersion = pending.FromVersion,
                        ToVersion = pending.ToVersion,
                        At = time.GetUtcNow(),
                        Detail = $"The update to {pending.ToVersion} did not take effect; still on {currentVersion}.",
                    }
                    : state.Last;
                TryWrite(state with { Pending = null, Last = report });
                return GuardStart.Nothing;
            }

            var starts = pending.Starts + 1;
            if (starts > Math.Max(1, options.MaxStarts))
            {
                return RollBackLocked(state, $"it started {pending.Starts} times without reporting to the hub") is null ? GuardStart.Nothing : GuardStart.RolledBack;
            }

            if (!TryWrite(state with { Pending = pending with { Starts = starts } }))
            {
                return GuardStart.Nothing;
            }

            _deadline = time.GetUtcNow() + options.VerifyWindow;
            return GuardStart.Verifying;
        }
    }

    /// <summary>
    /// The hub accepted a snapshot from this version: the update is good. Returns the report when this
    /// call is what confirmed it, null when there was nothing to confirm.
    /// </summary>
    public AgentUpdateReport? Confirm()
    {
        lock (_lock)
        {
            UpdateState state;
            try
            {
                state = store.Read();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (state.Pending is not { } pending || pending.ToVersion != currentVersion)
            {
                _deadline = null;
                return null;
            }

            var report = new AgentUpdateReport
            {
                Outcome = AgentUpdateOutcomes.Succeeded,
                FromVersion = pending.FromVersion,
                ToVersion = pending.ToVersion,
                At = time.GetUtcNow(),
            };
            if (!TryWrite(state with { Pending = null, Last = report, FailedVersion = null, FailedAt = null }))
            {
                return null;
            }

            _deadline = null;
            foreach (var file in pending.Files.Where(f => f.BackupPath is not null))
            {
                try
                {
                    files.Delete(file.BackupPath!);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still in use on Windows; the next update clears it.
                }
            }

            return report;
        }
    }

    /// <summary>
    /// Puts the previous version back and records why. Returns the report, or null when there was nothing
    /// to roll back or the files could not be restored (the update is then left in place and recorded as it stands).
    /// After a successful roll back the caller must exit, so the service manager starts the restored program.
    /// </summary>
    public AgentUpdateReport? RollBack(string reason)
    {
        lock (_lock)
        {
            try
            {
                return RollBackLocked(store.Read(), reason);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private AgentUpdateReport? RollBackLocked(UpdateState state, string reason)
    {
        _deadline = null;
        if (state.Pending is not { } pending)
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (!pending.Files.Any(f => f.BackupPath is not null) || pending.Files.Any(f => f.BackupPath is not null && !files.Exists(f.BackupPath)))
        {
            TryWrite(state with
            {
                Pending = null,
                Last = Report(AgentUpdateOutcomes.Failed, $"{pending.ToVersion} was not confirmed ({reason}), and the previous version is no longer there to go back to."),
            });
            return null;
        }

        try
        {
            SwapExecutor.Run(SwapPlanner.Rollback(platform, pending.Files), files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryWrite(state with
            {
                Pending = null,
                Last = Report(AgentUpdateOutcomes.Failed, $"{pending.ToVersion} was not confirmed ({reason}), and going back to {pending.FromVersion} failed: {ex.Message}"),
            });
            return null;
        }

        var report = Report(AgentUpdateOutcomes.RolledBack, $"Rolled back from {pending.ToVersion} to {pending.FromVersion}: {reason}.");
        TryWrite(state with { Pending = null, Last = report, FailedVersion = pending.ToVersion, FailedAt = now });
        return report;

        AgentUpdateReport Report(string outcome, string detail) => new()
        {
            Outcome = outcome,
            FromVersion = pending.FromVersion,
            ToVersion = pending.ToVersion,
            At = now,
            Detail = detail,
        };
    }

    private bool TryWrite(UpdateState state)
    {
        try
        {
            store.Write(state);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Windows: removes programs a previous roll back had to move aside while they were running.</summary>
    private void DeleteDiscarded()
    {
        var directory = Path.GetDirectoryName(store.Path);
        if (platform != ServicePlatform.Windows || string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var leftover in Directory.EnumerateFiles(directory, "devdash-agent*.bad"))
        {
            try
            {
                files.Delete(leftover);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still running; try again next start.
            }
        }
    }
}
