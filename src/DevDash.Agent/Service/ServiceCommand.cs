using System.Diagnostics;

namespace DevDash.Agent.Service;

/// <summary>One external command the installer runs (<c>launchctl</c>, <c>systemctl</c>, <c>schtasks</c>).</summary>
/// <param name="IgnoreFailure">The step is a best-effort tidy-up, e.g. unloading a service that may not be loaded.</param>
/// <param name="Attempts">Tries before giving up; launchd needs a moment between bootout and bootstrap.</param>
public sealed record ServiceCommand(string FileName, IReadOnlyList<string> Arguments, bool IgnoreFailure = false, int Attempts = 1)
{
    public override string ToString() =>
        string.Join(' ', new[] { FileName }.Concat(Arguments).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
}

public sealed record CommandResult(int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(ServiceCommand command, CancellationToken ct);
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(ServiceCommand command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(command.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in command.Arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return new CommandResult(-1, $"Could not start {command.FileName}.");
            }

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return new CommandResult(process.ExitCode, ((await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false))).Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CommandResult(-1, $"Could not start {command.FileName}: {ex.Message}");
        }
    }
}
