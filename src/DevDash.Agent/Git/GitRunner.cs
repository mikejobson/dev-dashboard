using System.Diagnostics;
using System.Text;

namespace DevDash.Agent.Git;

public sealed record GitResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// The only place the agent starts git. Arguments go to the process as a list (never through a shell),
/// credential prompts are disabled, and every call has a timeout. Scanning can only run an allowlist of
/// read-only commands plus <c>fetch --prune --quiet</c> and <c>ls-remote --tags</c>, which reads the
/// remote and changes nothing locally. The separate, opt-in agent actions
/// (<c>Actions/ActionExecutor</c>) run their own fixed commands through <see cref="ExecuteAsync"/>.
/// </summary>
public sealed class GitRunner
{
    private static readonly string[] FetchArgs = ["fetch", "--prune", "--quiet"];

    /// <summary>
    /// Keeps read-only commands free of side effects: no index refresh, and no fsmonitor daemon.
    /// A repo with <c>core.fsmonitor=true</c> would otherwise have <c>git status</c> start a background
    /// daemon and create a socket inside <c>.git</c>.
    /// </summary>
    private static readonly string[] ReadOnlyPrefix = ["--no-optional-locks", "-c", "core.fsmonitor=false"];

    private readonly string _gitPath;

    public GitRunner(string gitPath = "git")
    {
        _gitPath = gitPath;
    }

    /// <summary>Runs a read-only git command in <paramref name="workingDirectory"/>.</summary>
    public Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        EnsureReadOnly(args);
        return ExecuteAsync(workingDirectory, [.. ReadOnlyPrefix, .. args], timeout, ct);
    }

    /// <summary>
    /// Runs <c>git fetch --prune --quiet</c>: the agent's only write, and it only touches remote-tracking refs.
    /// Automatic gc/maintenance is switched off so nothing else in the repository changes.
    /// </summary>
    public Task<GitResult> FetchAsync(string workingDirectory, TimeSpan timeout, CancellationToken ct = default) =>
        ExecuteAsync(workingDirectory, ["-c", "gc.auto=0", "-c", "maintenance.auto=false", "-c", "core.fsmonitor=false", .. FetchArgs], timeout, ct);

    /// <summary>
    /// Runs <c>git ls-remote --tags --refs -- &lt;remote&gt;</c>: lists the tags a remote has, to tell which
    /// local tags exist nowhere else. It talks to the remote, like a fetch, and writes nothing.
    /// </summary>
    public Task<GitResult> ListRemoteTagsAsync(string workingDirectory, string remote, TimeSpan timeout, CancellationToken ct = default) =>
        ExecuteAsync(workingDirectory, ["-c", "core.fsmonitor=false", "ls-remote", "--tags", "--refs", "--", remote], timeout, ct);

    /// <summary>Throws unless <paramref name="args"/> is one of the read-only commands the scanner uses.</summary>
    public static void EnsureReadOnly(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException("No git command given.", nameof(args));
        }

        var ok = args[0] switch
        {
            "status" => args.Skip(1).All(a => a.StartsWith("--", StringComparison.Ordinal)),
            "for-each-ref" => true,
            "rev-parse" => true,
            "log" => true,
            "rev-list" => args.Count >= 3 && args[1] == "--count", // counts commits, prints one number
            "symbolic-ref" => args.Count == 3 && args[1] == "--short", // read form only
            "remote" => args.Count == 2 && args[1] == "-v",
            "worktree" => args.Count >= 2 && args[1] == "list",
            "stash" => args.Count >= 2 && args[1] == "list",
            "ls-files" => args.Count == 3 && args[1] == "--", // lists what git tracks under one path
            "config" => args.Count == 3 && args[1] == "--get" && args[2] == "user.email", // reads one value; compared locally, never sent
            "submodule" => args.Count == 3 && args[1] == "status" && args[2] == "--recursive", // lists them, nothing else
            _ => false,
        };

        if (!ok)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} is not on the agent's read-only allowlist.");
        }
    }

    /// <summary>
    /// Starts git with exactly these arguments. Internal so that the scanner cannot reach it: besides
    /// this class, only <c>Actions/</c> calls it, with the fixed argument lists of allowlisted agent actions.
    /// </summary>
    internal async Task<GitResult> ExecuteAsync(string workingDirectory, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _gitPath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // Never wait for a human: no terminal prompts, no credential-manager UI, no askpass dialogs.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["GIT_ASKPASS"] = "";
        psi.Environment["SSH_ASKPASS"] = "";
        // Do not refresh the index or take locks for read-only commands.
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        // Stable, parseable output.
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["GIT_PAGER"] = "cat";

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new GitResult(-1, "", $"Could not start git: {ex.Message}", false);
        }

        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

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
            catch (InvalidOperationException)
            {
                // already exited
            }

            ct.ThrowIfCancellationRequested();
            return new GitResult(-1, "", $"git {args[^1]} timed out after {timeout.TotalSeconds:0}s", true);
        }

        return new GitResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), false);
    }
}
