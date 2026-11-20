namespace DevDash.Agent.Actions;

/// <summary>
/// Agent actions: allowlisted fixes queued from the dashboard. Off by default. Nothing the hub sends
/// can turn this on; only this machine's own configuration (<c>Agent:Actions:Enabled</c>,
/// <c>--enable-actions</c>) can.
/// </summary>
public sealed class ActionsOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// How often to ask the hub for work when the hub does not hold the request open (an older hub, or
    /// one that is failing). Also the longest wait between attempts after an error.
    /// </summary>
    public int PollSeconds { get; set; } = 30;

    /// <summary>
    /// How long the agent asks the hub to hold each poll open when nothing is queued, so a request made
    /// in the dashboard arrives at once instead of on the next poll. 0 goes back to plain polling.
    /// Keep it under the idle timeout of any proxy in front of the hub; 25 suits <c>tailscale serve</c>.
    /// </summary>
    public int LongPollSeconds { get; set; } = 25;

    /// <summary>How long a refresh asked for from the dashboard waits for the scan loop (it queues behind a full scan) before reporting that the agent is busy.</summary>
    public int RefreshTimeoutSeconds { get; set; } = 240;

    /// <summary>Timeout for the actions that talk to a remote (fetch, push).</summary>
    public int NetworkTimeoutSeconds { get; set; } = 120;

    /// <summary>Timeout for a clone, which downloads a whole repository.</summary>
    public int CloneTimeoutSeconds { get; set; } = 900;

    /// <summary>
    /// A folder to move removed projects into, instead of the operating system's trash (<c>~/.Trash</c>,
    /// the Recycle Bin, or the freedesktop trash). It must be on the same volume as the scan roots: a
    /// removal is a rename, never a copy. Leave empty to use the system trash.
    /// </summary>
    public string? TrashDirectory { get; set; }
}
