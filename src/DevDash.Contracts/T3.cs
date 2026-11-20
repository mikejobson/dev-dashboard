using System.Text.Json.Serialization;

namespace DevDash.Contracts;

/// <summary>
/// Whether this machine has T3 Code's command-line tool, as far as the agent could tell. Sent in every
/// snapshot by agents from 0.7.0 that have the T3 integration on (<c>Agent:T3:Enabled</c>); null otherwise.
/// The CLI's path is not sent.
/// </summary>
public sealed record T3Info
{
    /// <summary>True when the CLI was found and answered <c>--version</c>.</summary>
    public bool Available { get; init; }

    /// <summary>What <c>t3 --version</c> printed, without the leading <c>t3 v</c>: <c>0.1.9</c>.</summary>
    public string? Version { get; init; }

    /// <summary>Why it is not available, or why the last lookups failed, in a few words: "not installed", "T3 Code is not running".</summary>
    public string? Problem { get; init; }
}

/// <summary>
/// The T3 Code project a repository belongs to, and its most recent threads. Only sent when the project
/// is this repository's own: a project that several scanned repositories resolve to (a project rooted
/// at the folder that holds them all) is nobody's, and is left out for all of them.
/// </summary>
public sealed record T3Project
{
    /// <summary>Most threads sent per repository.</summary>
    public const int MaxThreads = 10;

    public required string ProjectId { get; init; }

    /// <summary>Up to <see cref="MaxThreads"/> threads that are not archived, most recently updated first.</summary>
    public IReadOnlyList<T3Thread> Threads { get; init; } = [];

    /// <summary>How many threads the project has that are not archived, when more than were sent.</summary>
    public int? ThreadCount { get; init; }
}

/// <summary>One T3 Code thread, as <c>t3 thread list</c> and <c>t3 thread show</c> describe it.</summary>
public sealed record T3Thread
{
    public required string Id { get; init; }
    public string Title { get; init; } = "";

    /// <summary>T3's own status word: <c>running</c>, <c>completed</c>, <c>failed</c>, <c>idle</c>…</summary>
    public string Status { get; init; } = "";

    /// <summary>The thread in the T3 Code web app (<c>https://app.t3.codes/threads/…</c>).</summary>
    public string? Link { get; init; }

    /// <summary>The branch the thread works on, and the worktree it works in, when it has its own. From <c>thread show</c>.</summary>
    public string? Branch { get; init; }
    public string? WorktreePath { get; init; }

    /// <summary>Questions or approvals the thread is waiting on. Above zero means it needs you.</summary>
    public int PendingRequests { get; init; }

    public IReadOnlyList<T3PullRequest> PullRequests { get; init; } = [];

    /// <summary>T3 considers the thread done with.</summary>
    public bool Settled { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonIgnore]
    public T3ThreadState State => T3Threads.StateOf(this);
}

/// <summary>A pull request a thread opened or was linked to.</summary>
public sealed record T3PullRequest
{
    public string? Url { get; init; }
    public int? Number { get; init; }
    public string? State { get; init; }
}

/// <summary>What the dashboard makes of a thread's status, for a chip.</summary>
public enum T3ThreadState
{
    /// <summary>Not running and not waiting: finished a run, or never started one.</summary>
    Idle,
    Running,

    /// <summary>Waiting for an answer or an approval.</summary>
    NeedsInput,
    Settled,
    Failed,
}

/// <summary>What the agent reports back after starting a thread.</summary>
public sealed record T3StartedThread
{
    public required string ThreadId { get; init; }
    public string? Link { get; init; }
}

public static class T3Threads
{
    /// <summary>Waiting on you first, then running, then settled; a failed status counts as failed.</summary>
    public static T3ThreadState StateOf(T3Thread thread)
    {
        if (thread.PendingRequests > 0)
        {
            return T3ThreadState.NeedsInput;
        }

        var status = thread.Status.Trim().ToLowerInvariant();
        if (status is "running" or "starting" or "queued" or "in_progress" or "in-progress" or "working")
        {
            return T3ThreadState.Running;
        }

        if (status is "waiting" or "needs_input" or "needs-input" or "awaiting_input" or "pending")
        {
            return T3ThreadState.NeedsInput;
        }

        if (thread.Settled)
        {
            return T3ThreadState.Settled;
        }

        return status is "failed" or "error" or "errored" ? T3ThreadState.Failed : T3ThreadState.Idle;
    }

    public static string Label(T3ThreadState state) => state switch
    {
        T3ThreadState.Running => "running",
        T3ThreadState.NeedsInput => "needs input",
        T3ThreadState.Settled => "settled",
        T3ThreadState.Failed => "failed",
        _ => "idle",
    };

    /// <summary>The chip class the hub uses for a state.</summary>
    public static string ChipClass(T3ThreadState state) => state switch
    {
        T3ThreadState.Running => "info",
        T3ThreadState.NeedsInput => "warn",
        T3ThreadState.Settled => "ok",
        T3ThreadState.Failed => "danger",
        _ => "muted",
    };
}
