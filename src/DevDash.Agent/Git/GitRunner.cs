using System.Collections.Concurrent;
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
    /// Keeps read-only commands free of side effects: no index refresh. (No fsmonitor daemon either,
    /// which every command gets from <see cref="Hardening"/>: a repo with <c>core.fsmonitor=true</c> would
    /// otherwise have <c>git status</c> start a background daemon and create a socket inside <c>.git</c>.)
    /// </summary>
    private static readonly string[] ReadOnlyPrefix = ["--no-optional-locks"];

    /// <summary>
    /// Put in front of every git the agent starts. A repository whose <c>.git</c> arrived some way other
    /// than a clone (unpacked from an archive, synced, copied from someone else) brings its own config and
    /// hooks, and the agent runs git in it on its own, without anyone asking. Command-line config wins over
    /// the repository's, so: no hooks (a hooks "folder" that cannot hold any), no fsmonitor daemon, no
    /// signature checks (which run <c>gpg.program</c>), no <c>ext::</c> transport (which runs any command as
    /// a "remote"), and the file transport only for the user's own URLs, not ones a submodule asks for.
    /// The hooks path must stay first: <see cref="ExecuteAsync"/> drops it for a push the user asked for.
    /// </summary>
    private static readonly string[] Hardening =
    [
        "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
        "-c", "core.fsmonitor=false",
        "-c", "log.showSignature=false",
        "-c", "protocol.ext.allow=never",
        "-c", "protocol.file.allow=user",
    ];

    private readonly string _gitPath;

    /// <summary>The keys each config file sets, by file, with its last write and length: read again only when it changes.</summary>
    private readonly ConcurrentDictionary<string, (DateTime Written, long Length, IReadOnlyList<string> Keys)> _configKeys = new(StringComparer.Ordinal);

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
        ExecuteAsync(workingDirectory, ["-c", "gc.auto=0", "-c", "maintenance.auto=false", .. FetchArgs], timeout, ct);

    /// <summary>
    /// Runs <c>git ls-remote --tags --refs -- &lt;remote&gt;</c>: lists the tags a remote has, to tell which
    /// local tags exist nowhere else. It talks to the remote, like a fetch, and writes nothing.
    /// </summary>
    public Task<GitResult> ListRemoteTagsAsync(string workingDirectory, string remote, TimeSpan timeout, CancellationToken ct = default) =>
        ExecuteAsync(workingDirectory, ["ls-remote", "--tags", "--refs", "--", remote], timeout, ct);

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
            "merge-base" => args.Count == 3 && args.Skip(1).All(IsObjectName), // prints one commit
            "diff" => IsCommitDiff(args), // compares two commits; no working tree, no --output
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

    /// <summary>Flags a commit-to-commit <c>git diff</c> may take: output shape only, nothing that runs a helper or writes a file.</summary>
    private static readonly HashSet<string> DiffFlags = new(StringComparer.Ordinal)
    {
        "--no-ext-diff", "--no-textconv", "--no-color", "--no-renames", "--name-only", "--unified=0", "--full-index",
    };

    /// <summary><c>git diff [allowed flags] &lt;sha&gt; &lt;sha&gt;</c>: exactly two full commit names, so it never reads the working tree.</summary>
    private static bool IsCommitDiff(IReadOnlyList<string> args) =>
        args.Skip(1).Count(IsObjectName) == 2
        && args.Skip(1).All(a => IsObjectName(a) || DiffFlags.Contains(a))
        && args.Contains("--no-ext-diff") && args.Contains("--no-textconv");

    private static bool IsObjectName(string arg) =>
        arg.Length is 40 or 64 && arg.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>
    /// Starts git with these arguments, behind <see cref="Hardening"/>, unless the repository's own config
    /// could make this command run a program (<see cref="RefusalAsync"/>): then nothing is started, and the
    /// result is a failure that says why. Internal so that the scanner cannot reach it: besides this class,
    /// only <c>Actions/</c> calls it, with the fixed argument lists of allowlisted agent actions.
    /// </summary>
    /// <param name="runHooks">Leave the repository's hooks on: only for a push the user asked for, whose
    /// <c>pre-push</c> hook (Git LFS uploading its objects, say) has to run for the push to be right.</param>
    internal async Task<GitResult> ExecuteAsync(string workingDirectory, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, bool runHooks = false)
    {
        if (await RefusalAsync(workingDirectory, Command(args), timeout, ct).ConfigureAwait(false) is { } refusal)
        {
            return new GitResult(-1, "", refusal, false);
        }

        return await StartAsync(workingDirectory, [.. runHooks ? Hardening[2..] : Hardening, .. args], timeout, ct).ConfigureAwait(false);
    }

    /// <summary>The git command in an argument list: the first argument that is not a global option (or a <c>-c</c> value).</summary>
    internal static string? Command(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "-c")
            {
                i++;
            }
            else if (!args[i].StartsWith('-'))
            {
                return args[i];
            }
        }

        return null;
    }

    /// <summary>Commands that talk to a remote: the transport, credential helpers and ssh are all in play.</summary>
    private static bool IsNetwork(string? command) => command is "fetch" or "pull" or "push" or "ls-remote";

    /// <summary>
    /// Why this command must not run in this repository, or null when it may. Only the repository's own
    /// (local and per-worktree) config is looked at, never the user's global config, and includes are not
    /// followed but refused:
    /// <list type="bullet">
    /// <item>Before a network command: any include, <c>core.sshCommand</c>, <c>core.gitProxy</c>,
    /// <c>core.askPass</c>, <c>core.alternateRefsCommand</c>, any <c>*.program</c>, a credential helper,
    /// <c>*.uploadpack</c>/<c>*.receivepack</c>, <c>remote.*.vcs</c> (a remote helper) or a
    /// <c>protocol.*.allow</c>.</item>
    /// <item>Before <c>status</c>, which runs clean filters on files it reads again and looks into
    /// submodules: any include, a <c>filter.*.clean/smudge/process</c> or a <c>diff.*.textconv/command</c>,
    /// in the repository's config or any submodule's. (<c>core.fsmonitor</c> and <c>core.hooksPath</c> are
    /// overridden by <see cref="Hardening"/>, so a repository may set them; husky sets the hooks path.)</item>
    /// </list>
    /// </summary>
    internal async Task<string?> RefusalAsync(string workingDirectory, string? command, TimeSpan timeout, CancellationToken ct)
    {
        var network = IsNetwork(command);
        if (!network && command != "status")
        {
            return null;
        }

        var dirs = await StartAsync(workingDirectory, [.. Hardening, "rev-parse", "--path-format=absolute", "--git-common-dir", "--git-dir"], timeout, ct).ConfigureAwait(false);
        var lines = dirs.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!dirs.Ok || lines.Length == 0)
        {
            return null; // not a repository: the command itself will say so
        }

        var commonDir = lines[0];
        var gitDir = lines.Length > 1 ? lines[1] : commonDir;
        List<string> files = [Path.Combine(commonDir, "config"), Path.Combine(commonDir, "config.worktree"), Path.Combine(gitDir, "config.worktree")];
        if (!network)
        {
            files.AddRange(SubmoduleConfigs(Path.Combine(commonDir, "modules")));
        }

        foreach (var file in files.Distinct(StringComparer.Ordinal))
        {
            foreach (var key in await ConfigKeysAsync(workingDirectory, file, timeout, ct).ConfigureAwait(false))
            {
                if (network ? RunsProgramOnNetwork(key) : RunsProgramOnStatus(key))
                {
                    return $"git {command} was not run: this repository's own config ({file}) sets {key}, which could run a program.";
                }
            }
        }

        return null;
    }

    internal static bool RunsProgramOnNetwork(string key)
    {
        var (section, name) = Split(key);
        return section is "include" or "includeif"
            || key.ToLowerInvariant() is "core.sshcommand" or "core.gitproxy" or "core.askpass" or "core.alternaterefscommand"
            || name is "program" or "uploadpack" or "receivepack"
            || (section == "credential" && name == "helper")
            || (section == "remote" && name == "vcs")
            || (section == "protocol" && name == "allow");
    }

    internal static bool RunsProgramOnStatus(string key)
    {
        var (section, name) = Split(key);
        return section is "include" or "includeif"
            || (section == "filter" && name is "clean" or "smudge" or "process")
            || (section == "diff" && name is "textconv" or "command");
    }

    /// <summary>
    /// <c>git lfs install --local</c> writes these exact filter commands into a repository's own config. They
    /// run the git-lfs found on PATH, not anything the repository chose, so they are no reason to refuse.
    /// </summary>
    internal static bool IsStandardLfs(string key, string value) =>
        key.Trim().ToLowerInvariant() switch
        {
            "filter.lfs.clean" => value.Trim() == "git-lfs clean -- %f",
            "filter.lfs.smudge" => value.Trim() is "git-lfs smudge -- %f" or "git-lfs smudge --skip -- %f",
            "filter.lfs.process" => value.Trim() is "git-lfs filter-process" or "git-lfs filter-process --skip",
            _ => false,
        };

    /// <summary>A config key's section and variable name, lower-cased (the subsection between them is not needed).</summary>
    private static (string Section, string Name) Split(string key)
    {
        var lower = key.ToLowerInvariant();
        var first = lower.IndexOf('.');
        return first < 0 ? (lower, "") : (lower[..first], lower[(lower.LastIndexOf('.') + 1)..]);
    }

    /// <summary>The config files of a repository's submodules (and theirs), a bounded number.</summary>
    private static List<string> SubmoduleConfigs(string modules)
    {
        try
        {
            return Directory.Exists(modules)
                ? Directory.EnumerateFiles(modules, "config", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true }).Take(100).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The keys one config file sets, as git reads it, includes not followed. None when the file is missing.</summary>
    private async Task<IReadOnlyList<string>> ConfigKeysAsync(string workingDirectory, string file, TimeSpan timeout, CancellationToken ct)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(file);
            if (!info.Exists)
            {
                return [];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }

        if (_configKeys.TryGetValue(file, out var known) && known.Written == info.LastWriteTimeUtc && known.Length == info.Length)
        {
            return known.Keys;
        }

        var listed = await StartAsync(workingDirectory, [.. Hardening, "config", "--file", file, "--no-includes", "--list", "-z"], timeout, ct).ConfigureAwait(false);

        // A config file git cannot read fails the command too; still, no answer is not taken as "nothing set".
        IReadOnlyList<string> keys = listed.Ok
            ? listed.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Split('\n', 2))
                .Where(pair => !IsStandardLfs(pair[0], pair.Length > 1 ? pair[1] : ""))
                .Select(pair => pair[0].Trim())
                .ToList()
            : ["include.unreadable"];
        if (listed.Ok)
        {
            _configKeys[file] = (info.LastWriteTimeUtc, info.Length, keys);
        }

        return keys;
    }

    /// <summary>Starts git with exactly these arguments.</summary>
    private async Task<GitResult> StartAsync(string workingDirectory, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
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
