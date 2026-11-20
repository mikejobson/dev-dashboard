using DevDash.Agent.Toolchains;

namespace DevDash.Agent.T3Code;

/// <summary>
/// The calls the agent makes to T3 Code's CLI, and nothing else. Every one is a fixed argument list
/// started without a shell (<see cref="IToolRunner"/>), with a time limit; the prompt of a new thread
/// goes on standard input, never on the command line.
/// </summary>
public sealed class T3Cli(IToolRunner runner, string fileName)
{
    public string FileName => fileName;

    public Task<ToolResult> VersionAsync(TimeSpan timeout, CancellationToken ct) =>
        RunAsync(["--version"], timeout, ct);

    /// <summary><c>t3 thread list --project &lt;path&gt; --json</c>: the threads of the project that holds the path, newest first.</summary>
    public Task<ToolResult> ListThreadsAsync(string projectPath, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(["thread", "list", "--project", projectPath, "--json"], timeout, ct);

    /// <summary><c>t3 thread show &lt;id&gt; --json --messages 0</c>: one thread's details, without its messages.</summary>
    public Task<ToolResult> ShowThreadAsync(string threadId, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(["thread", "show", threadId, "--json", "--messages", "0"], timeout, ct);

    /// <summary><c>t3 app &lt;path&gt;</c>: open the project in the running desktop app.</summary>
    public Task<ToolResult> OpenAppAsync(string path, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(["app", path], timeout, ct);

    /// <summary>The arguments of <c>t3 thread start</c>, prompt read from standard input. Public so the tests can check them.</summary>
    public static IReadOnlyList<string> StartArguments(string repoPath, string title, string worktreeBase, string newBranch) =>
        ["thread", "start", repoPath, "-", "--title", title, "--worktree", worktreeBase, "--branch", newBranch, "--json"];

    public Task<ToolResult> StartThreadAsync(string repoPath, string prompt, string title, string worktreeBase, string newBranch, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(StartArguments(repoPath, title, worktreeBase, newBranch), timeout, ct, prompt, repoPath);

    private Task<ToolResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct, string? input = null, string? workingDirectory = null) =>
        runner.RunAsync(
            new ToolCommand(fileName, arguments)
            {
                StandardInput = input,
                WorkingDirectory = workingDirectory,
                MaxOutputChars = 2 * 1024 * 1024,
            },
            timeout,
            ct);
}
