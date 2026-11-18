namespace DevDash.Agent.Actions;

/// <summary>
/// Agent actions: allowlisted fixes queued from the dashboard. Off by default. Nothing the hub sends
/// can turn this on; only this machine's own configuration (<c>Agent:Actions:Enabled</c>,
/// <c>--enable-actions</c>) can.
/// </summary>
public sealed class ActionsOptions
{
    public bool Enabled { get; set; }

    /// <summary>How often to ask the hub for work while running.</summary>
    public int PollSeconds { get; set; } = 30;

    /// <summary>Timeout for the actions that talk to a remote (fetch, push).</summary>
    public int NetworkTimeoutSeconds { get; set; } = 120;
}
