using System.Globalization;
using System.Security.Claims;
using DevDash.Contracts;
using DevDash.Hub.Auth;
using DevDash.Hub.Data;
using DevDash.Hub.GitHub;
using DevDash.Hub.Services;
using DevDash.Hub.Updates;

namespace DevDash.Hub.Api;

public static class ApiEndpoints
{
    public const string AgentPolicy = "Agent";
    private const int MaxMachineIdLength = 128;

    public static void MapDevDashApi(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

        var api = app.MapGroup("/api/v1");

        // ---- agents -----------------------------------------------------------------------------
        api.MapPost("/snapshots", IngestAsync).RequireAuthorization(AgentPolicy);

        // Agent actions. An agent only ever sees, and can only report on, actions for the machine its key is bound to.
        api.MapPost("/actions/claim", ClaimActionsAsync).RequireAuthorization(AgentPolicy);
        api.MapPost("/actions/{id}/result", CompleteActionAsync).RequireAuthorization(AgentPolicy);

        // ---- browser / curl (read-only views of what the pages show) -----------------------------
        api.MapGet("/machines", (SnapshotStore store, AgentUpdates updates) => store.Machines.Select(m => new
        {
            m.MachineId,
            m.Machine.Hostname,
            m.Machine.Os,
            m.Machine.AgentVersion,
            // Null from an agent that predates self-update: it cannot be updated from here.
            m.Machine.UpdateMode,
            LatestVersion = updates.For(m).Latest,
            UpdateAvailable = updates.For(m).Behind,
            CanUpdate = updates.For(m).CanUpdate,
            m.Machine.LastUpdate,
            m.Machine.TailscaleName,
            m.FirstSeenAt,
            m.LastSeenAt,
            Online = store.IsOnline(m),
            m.Snapshot.ScannedAt,
            m.Snapshot.ScanDurationMs,
            m.Snapshot.Roots,
            Entries = m.Snapshot.Entries.Count,
        }));

        api.MapGet("/machines/{machineId}/snapshot", (string machineId, SnapshotStore store) =>
            store.Get(machineId) is { } machine ? Results.Json(machine.Snapshot, SnapshotSchema.JsonOptions) : Results.NotFound());

        api.MapGet("/repos", async (RepoCatalog catalog, CancellationToken ct) =>
            (await catalog.GetAsync(ct)).Select(r => new
            {
                r.Key,
                r.Name,
                r.Owner,
                Kind = r.Kind.ToString(),
                Description = r.Description?.Text,
                DescriptionSource = r.Description?.Source,
                GitHub = r.GitHub is null ? null : new { r.GitHub.NameWithOwner, r.GitHub.Url, r.GitHub.IsArchived, r.GitHub.PushedAt, r.GitHub.OpenPullRequests },
                r.RemoteMissing,
                PullRequests = r.PullRequests.Select(p => new { p.Number, p.Title, p.Url, Ci = p.Ci.ToString(), p.IsDraft }),
                Issues = r.Issues.Select(i => new { i.Number, i.Title, i.Url }),
                r.LastActivity,
                Clones = r.Clones.Select(c => new
                {
                    c.Machine.MachineId,
                    c.Entry.Path,
                    c.Git?.Branch,
                    c.Git?.Upstream,
                    c.Git?.Ahead,
                    c.Git?.Behind,
                    Dirty = c.Git?.DirtyCount,
                    c.Git?.Untracked,
                    c.Git?.IsLinkedWorktree,
                }),
            }));

        // The latest agent release the hub has seen on GitHub, and when it last looked.
        api.MapGet("/releases/latest", (ReleaseStore store) => Results.Json(new { store.Status, Latest = store.Current }, SnapshotSchema.JsonOptions));

        api.MapGet("/github", (GitHubStore store) => Results.Json(new { store.Status, Data = store.Current }, SnapshotSchema.JsonOptions));

        api.MapPost("/github/refresh", (GitHubPoller poller) =>
        {
            poller.RequestPoll();
            return Results.Accepted();
        });

        api.MapGet("/findings", async (FindingsService findings, bool? all, CancellationToken ct) =>
            (await findings.GetAsync(ct))
                .Where(v => all == true || v.IsActive)
                .Select(v => new
                {
                    v.Finding.Fingerprint,
                    v.Finding.RuleId,
                    Severity = v.Finding.Severity.ToString(),
                    v.Finding.Title,
                    v.Finding.Explanation,
                    Commands = v.Finding.Commands.Select(c => new
                    {
                        c.Label,
                        c.Command,
                        Kind = c.Kind.ToString(),
                        MachineId = v.Finding.MachineIdFor(c),
                        Action = c.Action?.Kind.ToString(),
                        CanRun = v.CanRun(c),
                    }),
                    v.Finding.MachineId,
                    v.Finding.Path,
                    v.Finding.RepoKey,
                    v.Finding.Url,
                    Status = v.Status?.ToString() ?? "Active",
                    v.SnoozedUntil,
                }));

        // The audit trail of agent actions. Queueing one is only possible from the dashboard, after a confirmation.
        api.MapGet("/actions", async (ActionQueue actions, int? take, CancellationToken ct) =>
            (await actions.RecentAsync(Math.Clamp(take ?? 50, 1, 500), ct)).Select(a => new
            {
                Id = ActionQueue.Wire(a.Id),
                a.MachineId,
                Kind = a.Kind.ToString(),
                a.CommandLine,
                a.RepoPath,
                a.FindingFingerprint,
                a.QueuedBy,
                Status = a.Status.ToString(),
                a.QueuedAt,
                a.ExpiresAt,
                a.ClaimedAt,
                a.CompletedAt,
                a.ExitCode,
                a.Output,
            }));

        api.MapPost("/findings/{fingerprint}/snooze", async (string fingerprint, int? days, FindingsService findings, CancellationToken ct) =>
            await findings.SetByFingerprintAsync(fingerprint, FindingStatus.Snoozed, TimeSpan.FromDays(Math.Clamp(days ?? 7, 1, 365)), ct)
                ? Results.NoContent() : Results.NotFound());

        api.MapPost("/findings/{fingerprint}/dismiss", async (string fingerprint, FindingsService findings, CancellationToken ct) =>
            await findings.SetByFingerprintAsync(fingerprint, FindingStatus.Dismissed, null, ct) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/findings/{fingerprint}/restore", async (string fingerprint, FindingsService findings, CancellationToken ct) =>
        {
            await findings.RestoreAsync(fingerprint, ct);
            return Results.NoContent();
        });
    }

    /// <summary>The machine an agent's key is bound to. A key binds when it first pushes a snapshot.</summary>
    private static string? MachineOf(ClaimsPrincipal user) => user.FindFirstValue(AgentKeyAuthenticationHandler.MachineClaim);

    /// <summary>
    /// An agent's poll for work. With <c>?wait=N</c> (0.5.0 agents) it is a long poll: when nothing is
    /// queued for the machine the request is held for up to N seconds and answered the moment something
    /// is, so a click in the dashboard reaches the agent at once. Without it (older agents) the answer
    /// is immediate, as it always was. The hub still never opens a connection to an agent.
    /// </summary>
    private static async Task<IResult> ClaimActionsAsync(int? wait, ClaimsPrincipal user, ActionQueue actions, IHostApplicationLifetime lifetime, CancellationToken ct)
    {
        if (MachineOf(user) is not { } machineId)
        {
            return Results.Problem("This API key is not bound to a machine yet. Push a snapshot first.", statusCode: StatusCodes.Status409Conflict);
        }

        // Held polls end when the hub stops, so shutting down does not wait out every agent's poll.
        var hold = TimeSpan.FromSeconds(Math.Clamp(wait ?? 0, 0, AgentActions.MaxClaimWaitSeconds));
        var claimed = hold > TimeSpan.Zero
            ? await actions.ClaimAsync(machineId, hold, ct, lifetime.ApplicationStopping)
            : await actions.ClaimAsync(machineId, ct);
        return Results.Json(new ActionClaimResponse { Actions = claimed }, SnapshotSchema.JsonOptions);
    }

    private static async Task<IResult> CompleteActionAsync(string id, ActionResultRequest? result, ClaimsPrincipal user, ActionQueue actions, CancellationToken ct)
    {
        if (result is null)
        {
            return Results.Problem("No result given.", statusCode: StatusCodes.Status400BadRequest);
        }

        // Not found covers both "no such action" and "another machine's action", without saying which.
        return MachineOf(user) is { } machineId && await actions.CompleteAsync(machineId, id, result, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> IngestAsync(MachineSnapshot? snapshot, ClaimsPrincipal user, SnapshotStore store, ApiKeyService keys, CancellationToken ct)
    {
        if (snapshot?.Machine is null || string.IsNullOrWhiteSpace(snapshot.Machine.MachineId))
        {
            return Results.Problem("The snapshot has no machine id.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (snapshot.Machine.MachineId.Length > MaxMachineIdLength)
        {
            return Results.Problem("The machine id is too long.", statusCode: StatusCodes.Status400BadRequest);
        }

        // The current format and the one before it, so agents can be updated one at a time after the hub.
        if (!SnapshotSchema.IsSupported(snapshot.SchemaVersion))
        {
            return Results.Problem(
                $"Snapshot schema {snapshot.SchemaVersion} is not supported; this hub speaks schema {SnapshotSchema.MinSupportedVersion} to {SnapshotSchema.Version}. Update the agent or the hub.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // A key belongs to one machine: it binds on first use and is refused for any other machine after that.
        var keyId = int.Parse(user.FindFirstValue(AgentKeyAuthenticationHandler.KeyIdClaim)!, CultureInfo.InvariantCulture);
        if (!await keys.BindAndTouchAsync(keyId, snapshot.Machine.MachineId, ct))
        {
            return Results.Problem("This API key belongs to a different machine.", statusCode: StatusCodes.Status403Forbidden);
        }

        var view = await store.IngestAsync(snapshot, ct);
        return Results.Json(
            new IngestResponse { MachineId = view.MachineId, Entries = snapshot.Entries.Count, ReceivedAt = view.LastSeenAt },
            SnapshotSchema.JsonOptions);
    }
}
