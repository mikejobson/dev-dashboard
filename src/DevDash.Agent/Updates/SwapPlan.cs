using DevDash.Agent.Service;

namespace DevDash.Agent.Updates;

public enum SwapOp
{
    /// <summary>Remove a leftover; fine if it is not there.</summary>
    Delete,

    /// <summary>Copy, replacing the destination.</summary>
    Copy,

    /// <summary>Rename within the folder. With <see cref="SwapStep.Overwrite"/> it replaces the destination in one atomic step.</summary>
    Move,
}

/// <param name="Overwrite">Move only: replace an existing destination (an atomic rename on macOS and Linux).</param>
/// <param name="SkipIfMissing">Do nothing when <paramref name="From"/> does not exist.</param>
/// <param name="BestEffort">A failure here does not stop the swap (clearing out leftovers).</param>
public sealed record SwapStep(SwapOp Op, string From, string? To = null, bool Overwrite = false, bool SkipIfMissing = false, bool BestEffort = false)
{
    public override string ToString() => Op switch
    {
        SwapOp.Delete => $"delete {From}",
        SwapOp.Copy => $"copy {From} -> {To}",
        _ => $"{(Overwrite ? "replace" : "rename")} {From} -> {To}",
    };
}

/// <summary>One program file of the installation, and where its previous version is kept.</summary>
/// <param name="BackupPath">Null when there was no previous file to keep.</param>
public sealed record InstalledFile(string Path, string? BackupPath);

/// <summary>What replacing one file takes, in order.</summary>
/// <param name="BackupStep">Index in <see cref="Steps"/> of the step that puts the previous version at <see cref="InstalledFile.BackupPath"/>, or -1.</param>
public sealed record FileSwap(InstalledFile File, IReadOnlyList<SwapStep> Steps, int BackupStep);

public sealed record SwapPlan(IReadOnlyList<FileSwap> Files)
{
    public IEnumerable<SwapStep> Steps => Files.SelectMany(f => f.Steps);
}

/// <summary>
/// Works out the file operations for putting a new version in place, and for putting the previous one
/// back. Planning is separate from doing so both can be tested for either platform on any machine.
/// <list type="bullet">
/// <item><b>macOS and Linux:</b> a running program's file can be replaced. The current file is copied to
/// <c>.old</c>, then the new file is renamed over it. The rename is atomic, so the path always holds a
/// complete program: the old one or the new one.</item>
/// <item><b>Windows:</b> a running <c>.exe</c> cannot be overwritten or deleted, but it can be renamed.
/// The current file is renamed to <c>.old</c> and the new one renamed into place. Rolling back renames
/// the (running) new file to <c>.bad</c> and the <c>.old</c> back.</item>
/// </list>
/// </summary>
public static class SwapPlanner
{
    /// <param name="exists">Whether a file is there now: a file with no previous version gets no backup.</param>
    public static SwapPlan Install(UpdateInstallation installation, string stagedDirectory, Func<string, bool> exists)
    {
        var files = new List<FileSwap>();
        foreach (var name in installation.FileNames)
        {
            var path = installation.PathOf(name);
            var staged = Path.Combine(stagedDirectory, name);
            var backup = UpdateInstallation.BackupPath(path);
            var hadPrevious = exists(path);
            var steps = new List<SwapStep> { new(SwapOp.Delete, backup, SkipIfMissing: true) };
            var backupStep = -1;
            if (installation.Platform == ServicePlatform.Windows)
            {
                steps.Add(new SwapStep(SwapOp.Delete, UpdateInstallation.DiscardPath(path), SkipIfMissing: true, BestEffort: true));
                if (hadPrevious)
                {
                    backupStep = steps.Count;
                    steps.Add(new SwapStep(SwapOp.Move, path, backup));
                }

                steps.Add(new SwapStep(SwapOp.Move, staged, path));
            }
            else
            {
                if (hadPrevious)
                {
                    backupStep = steps.Count;
                    steps.Add(new SwapStep(SwapOp.Copy, path, backup));
                }

                steps.Add(new SwapStep(SwapOp.Move, staged, path, Overwrite: true));
            }

            files.Add(new FileSwap(new InstalledFile(path, hadPrevious ? backup : null), steps, backupStep));
        }

        return new SwapPlan(files);
    }

    /// <summary>Puts the previous version of each file back. A file that had no previous version is removed.</summary>
    public static IReadOnlyList<SwapStep> Rollback(ServicePlatform platform, IEnumerable<InstalledFile> files)
    {
        var steps = new List<SwapStep>();
        foreach (var file in files)
        {
            if (platform == ServicePlatform.Windows)
            {
                // The new file may be the program that is running right now, so it is moved aside, not deleted.
                var discard = UpdateInstallation.DiscardPath(file.Path);
                steps.Add(new SwapStep(SwapOp.Delete, discard, SkipIfMissing: true, BestEffort: true));
                steps.Add(new SwapStep(SwapOp.Move, file.Path, discard, Overwrite: true, SkipIfMissing: true));
                if (file.BackupPath is not null)
                {
                    steps.Add(new SwapStep(SwapOp.Move, file.BackupPath, file.Path));
                }
            }
            else if (file.BackupPath is not null)
            {
                steps.Add(new SwapStep(SwapOp.Move, file.BackupPath, file.Path, Overwrite: true));
            }
            else
            {
                steps.Add(new SwapStep(SwapOp.Delete, file.Path, SkipIfMissing: true));
            }
        }

        return steps;
    }
}

/// <summary>The few file operations an update needs. Replaced in tests to make one fail.</summary>
public interface IUpdateFiles
{
    bool Exists(string path);
    void Delete(string path);
    void Copy(string from, string to);
    void Move(string from, string to, bool overwrite);
}

public sealed class UpdateFiles : IUpdateFiles
{
    public bool Exists(string path) => File.Exists(path);

    public void Delete(string path) => File.Delete(path);

    // File.Copy keeps the mode bits on macOS and Linux, and the copy is a new file, so it is never the one in use.
    public void Copy(string from, string to) => File.Copy(from, to, overwrite: true);

    public void Move(string from, string to, bool overwrite) => File.Move(from, to, overwrite);
}

public static class SwapExecutor
{
    /// <summary>Runs the steps in order. Throws on the first one that fails, unless it is best-effort.</summary>
    public static void Run(IEnumerable<SwapStep> steps, IUpdateFiles files)
    {
        foreach (var step in steps)
        {
            try
            {
                if (step.SkipIfMissing && !files.Exists(step.From))
                {
                    continue;
                }

                switch (step.Op)
                {
                    case SwapOp.Delete:
                        files.Delete(step.From);
                        break;
                    case SwapOp.Copy:
                        files.Copy(step.From, step.To!);
                        break;
                    default:
                        files.Move(step.From, step.To!, step.Overwrite);
                        break;
                }
            }
            catch (Exception ex) when (step.BestEffort && ex is IOException or UnauthorizedAccessException)
            {
                // A leftover that could not be cleared; the steps that matter will fail if it is in the way.
            }
        }
    }

    /// <summary>
    /// Puts the new files in place. If any step fails, every file already touched is put back the way it
    /// was and the error is rethrown, so the installation is never left half old and half new.
    /// </summary>
    public static void Install(SwapPlan plan, ServicePlatform platform, IUpdateFiles files)
    {
        // Only files whose previous version really is at .old are rolled back: a leftover .old from an
        // earlier update must never be mistaken for it.
        var touched = new List<InstalledFile>();
        try
        {
            foreach (var swap in plan.Files)
            {
                for (var index = 0; index < swap.Steps.Count; index++)
                {
                    Run([swap.Steps[index]], files);
                    if (index == swap.BackupStep)
                    {
                        touched.Add(swap.File);
                    }
                    else if (swap.BackupStep < 0 && index == swap.Steps.Count - 1)
                    {
                        touched.Add(swap.File); // a new file with no previous version: undoing means removing it
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                Run(SwapPlanner.Rollback(platform, Enumerable.Reverse(touched)), files);
            }
            catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"{ex.Message} Putting the previous files back also failed: {undo.Message}", ex);
            }

            throw;
        }
    }
}
