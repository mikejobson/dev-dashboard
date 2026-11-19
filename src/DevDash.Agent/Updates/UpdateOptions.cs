using DevDash.Contracts;

namespace DevDash.Agent.Updates;

/// <summary>
/// Self-update settings (<c>Agent:Updates</c>). Like every agent setting they come from this machine
/// only: <c>agent.json</c>, <c>DEVDASH_Agent__Updates__*</c> or the command line. Nothing the hub
/// sends can change them, and an update action carries a version number and nothing else.
/// </summary>
public sealed class UpdateOptions
{
    /// <summary>
    /// <c>off</c>: never update, refuse update actions. <c>notify</c> (default): update when asked from
    /// the dashboard or by <c>devdash-agent update</c>, never unprompted. <c>auto</c>: also look for
    /// new releases and install them. An unrecognised value counts as <c>off</c>.
    /// </summary>
    public string Mode { get; set; } = AgentUpdateModes.Notify;

    /// <summary>The GitHub repository whose releases this agent installs.</summary>
    public string Repository { get; set; } = "mikejobson/dev-dashboard";

    /// <summary>
    /// Replaces <c>https://github.com/&lt;Repository&gt;/releases</c>, for a mirror or a test server.
    /// Files are read from <c>&lt;BaseUrl&gt;/latest/download/</c> and <c>&lt;BaseUrl&gt;/download/v&lt;version&gt;/</c>.
    /// Whatever it points at, a release is only installed if it is signed by the key this agent was built with.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary><c>auto</c> mode: how often to look for a new release. A random part is added so machines do not all ask at once.</summary>
    public double CheckIntervalHours { get; set; } = 6;

    /// <summary>A new version that has started this many times without reaching the hub is rolled back on its next start.</summary>
    public int MaxStarts { get; set; } = 3;

    /// <summary>A new version that has been running this long without reaching the hub is rolled back.</summary>
    public double VerifyMinutes { get; set; } = 5;

    /// <summary>Upper bound on downloading one release.</summary>
    public int DownloadTimeoutSeconds { get; set; } = 600;

    /// <summary>The mode in effect: one of <see cref="AgentUpdateModes"/>.</summary>
    public string ResolvedMode => AgentUpdateModes.Normalize(Mode) ?? AgentUpdateModes.Off;

    public bool AcceptsRequests => AgentUpdateModes.AcceptsRequests(ResolvedMode);

    public bool IsAuto => ResolvedMode == AgentUpdateModes.Auto;

    public TimeSpan VerifyWindow => TimeSpan.FromMinutes(double.IsFinite(VerifyMinutes) && VerifyMinutes > 0 ? VerifyMinutes : 5);
}
