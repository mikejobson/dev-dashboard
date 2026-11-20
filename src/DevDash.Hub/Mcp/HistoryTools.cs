using System.ComponentModel;
using DevDash.Hub.History;
using DevDash.Hub.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DevDash.Hub.Mcp;

/// <summary>The <c>history</c> read tool: one metric per day from the rollups the History page draws (§22).</summary>
[McpServerToolType]
public sealed class HistoryTools(HistoryStore history, SnapshotStore snapshots, RepoCatalog catalog, TimeProvider time)
{
    private static readonly Dictionary<string, Func<MachineDayEntity, double>> MachineMetrics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["disk"] = d => d.RepoBytes,
        ["build_output"] = d => d.BuildBytes,
        ["safe_to_remove"] = d => d.SafeToRemoveBytes,
        ["repos"] = d => d.RepoCount,
        ["dirty"] = d => d.DirtyCount,
        ["unpushed"] = d => d.UnpushedCommits,
        ["at_risk"] = d => d.AtRisk,
        ["at_risk_high"] = d => d.AtRiskHigh,
    };

    private static readonly Dictionary<string, Func<CloneDayEntity, double?>> ProjectMetrics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["disk"] = d => d.SizeBytes,
        ["build_output"] = d => d.BuildBytes,
        ["dirty"] = d => d.Dirty,
        ["unpushed"] = d => d.Unpushed,
        ["ahead"] = d => d.Ahead,
        ["behind"] = d => d.Behind,
        ["at_risk"] = d => d.RiskLevel,
    };

    [McpServerTool(Name = "history", Title = "History", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One metric per day over the last N days, from the per-day rollups the hub keeps (the latest scan of each day). " +
                 "Without 'project': per machine, metric one of disk (bytes used by working trees), build_output (bytes), safe_to_remove (bytes), " +
                 "repos, dirty (trees with uncommitted changes), unpushed (commits on no remote), at_risk (trees at risk), at_risk_high. " +
                 "With 'project': per working tree of that project, metric one of disk, build_output, dirty, unpushed, ahead, behind, " +
                 "at_risk (0 none, 1 untracked files, 2 uncommitted/stashes/tags, 3 commits on no remote). Days with no scan are missing.")]
    public async Task<string> History(
        [Description("The metric (see the description). Default 'disk'.")] string? metric = null,
        [Description("Only this machine (hostname or machine id).")] string? machine = null,
        [Description("A project: its name, owner/name, key or a clone's path. Switches to per-working-tree values.")] string? project = null,
        [Description("How many days back, today included (default 30, at most the hub's retention).")] int? days = null,
        CancellationToken ct = default)
    {
        var name = string.IsNullOrWhiteSpace(metric) ? "disk" : metric.Trim();
        var span = Math.Clamp(days ?? 30, 1, history.RetentionDays);
        string? machineId = string.IsNullOrWhiteSpace(machine) ? null : McpLookup.Machine(snapshots, machine).MachineId;

        if (!string.IsNullOrWhiteSpace(project))
        {
            if (!ProjectMetrics.TryGetValue(name, out var pick))
            {
                throw new McpException($"Unknown metric '{name}' for a project. Use one of: {string.Join(", ", ProjectMetrics.Keys)}.");
            }

            var row = McpLookup.Project(await catalog.GetAsync(ct), project);
            var rows = (await history.RepoDaysAsync(row.Key, span, ct)).Where(r => machineId is null || r.MachineId == machineId);
            return McpOutput.Json(new
            {
                now = time.GetUtcNow(),
                metric = name,
                days = span,
                project = row.Key,
                enabled = history.Options.Enabled,
                series = rows.GroupBy(r => (r.MachineId, r.Path)).Select(g => new
                {
                    machine = snapshots.Get(g.Key.MachineId)?.Machine.Hostname ?? g.Key.MachineId,
                    path = g.Key.Path,
                    points = g.OrderBy(r => r.Day).Select(r => new { day = r.Day, value = pick(r) }).Where(p => p.value is not null),
                }),
            });
        }

        if (!MachineMetrics.TryGetValue(name, out var get))
        {
            throw new McpException($"Unknown metric '{name}'. Use one of: {string.Join(", ", MachineMetrics.Keys)}.");
        }

        var machineDays = (await history.MachineDaysAsync(span, ct)).Where(d => machineId is null || d.MachineId == machineId);
        return McpOutput.Json(new
        {
            now = time.GetUtcNow(),
            metric = name,
            days = span,
            enabled = history.Options.Enabled,
            series = machineDays.GroupBy(d => d.MachineId).Select(g => new
            {
                machine = g.MaxBy(d => d.Day)!.Hostname,
                machineId = g.Key,
                points = g.OrderBy(d => d.Day).Select(d => new { day = d.Day, value = get(d) }),
            }),
        });
    }
}
