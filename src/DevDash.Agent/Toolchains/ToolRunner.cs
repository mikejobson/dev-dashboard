using System.Diagnostics;
using System.Text;

namespace DevDash.Agent.Toolchains;

/// <param name="Started">False when the program could not be started at all (not installed, not executable).</param>
public sealed record ToolResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, bool Started = true)
{
    public bool Ok => Started && !TimedOut && ExitCode == 0;

    public static ToolResult NotStarted(string why) => new(-1, "", why, false, false);
}

/// <summary>One program to start: a full path, an argument list, and nothing a shell would interpret.</summary>
public sealed record ToolCommand(string FileName, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }

    /// <summary>Folders put in front of the child's <c>PATH</c>, so <c>npm</c> finds the <c>node</c> beside it under a service's bare PATH.</summary>
    public IReadOnlyList<string> PathPrefix { get; init; } = [];

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Run below normal priority: for the slow dependency checks, which should never be felt.</summary>
    public bool LowPriority { get; init; }

    /// <summary>Stop reading output after this many characters; the rest is dropped.</summary>
    public int MaxOutputChars { get; init; } = 4 * 1024 * 1024;
}

/// <summary>
/// How the agent starts the toolchain and package tools (<c>dotnet</c>, <c>node</c>, <c>npm</c>…). An
/// interface so the tests never need those tools installed: they hand in a fake.
/// </summary>
public interface IToolRunner
{
    Task<ToolResult> RunAsync(ToolCommand command, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Starts the program directly with an argument list (no shell), no stdin, and a timeout that kills the whole process tree.</summary>
public sealed class ProcessToolRunner : IToolRunner
{
    public async Task<ToolResult> RunAsync(ToolCommand command, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (command.WorkingDirectory is not null)
        {
            psi.WorkingDirectory = command.WorkingDirectory;
        }

        foreach (var arg in command.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in command.Environment)
        {
            psi.Environment[key] = value;
        }

        if (command.PathPrefix.Count > 0)
        {
            var current = psi.Environment.TryGetValue("PATH", out var path) ? path : null;
            psi.Environment["PATH"] = string.Join(Path.PathSeparator, command.PathPrefix.Append(current).Where(p => !string.IsNullOrEmpty(p)));
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return ToolResult.NotStarted(ex.Message);
        }

        if (command.LowPriority)
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Already gone, or not allowed: it runs at normal priority.
            }
        }

        process.StandardInput.Close();
        var stdout = ReadAsync(process.StandardOutput, command.MaxOutputChars);
        var stderr = ReadAsync(process.StandardError, 64 * 1024);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // already exited
            }

            ct.ThrowIfCancellationRequested();
            return new ToolResult(-1, "", $"timed out after {timeout.TotalSeconds:0}s", true);
        }

        return new ToolResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), false);
    }

    /// <summary>Reads to the end so the child never blocks on a full pipe, keeping only the first <paramref name="max"/> characters.</summary>
    private static async Task<string> ReadAsync(StreamReader reader, int max)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                if (text.Length < max)
                {
                    text.Append(buffer, 0, Math.Min(read, max - text.Length));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The process was killed; what was read so far is all there is.
        }

        return text.ToString();
    }
}
