using DevDash.Contracts;
using static DevDash.Core.Cleanup.RuleHelpers;

namespace DevDash.Core.Cleanup.Rules;

/// <summary>Packages with a known high or critical vulnerability, as the package tools reported at the agent's last check.</summary>
public sealed class VulnerableDependenciesRule : ICleanupRule
{
    public string Id => "vulnerable-deps";
    public string Title => "Dependencies with known vulnerabilities";
    public string Description => "Packages that `dotnet list package --vulnerable` or `npm audit` reports as high or critical. Checked about once a day on machines where dependency checks are on.";

    public IEnumerable<Finding> Evaluate(CleanupContext context)
    {
        foreach (var clone in context.Clones())
        {
            if (clone.Entry.Dependencies is not { } health || health.Critical + health.High == 0)
            {
                continue;
            }

            var path = clone.Entry.Path;
            var serious = health.Reports
                .SelectMany(r => r.TopVulnerable)
                .Where(p => p.Severity is "critical" or "high")
                .ToList();
            var names = serious.Select(p => p.Version is null ? p.Name : $"{p.Name} {p.Version}").Distinct(StringComparer.Ordinal).ToList();
            var count = health.Critical + health.High;
            var others = health.VulnerableTotal - count;

            var commands = new List<FindingCommand>();
            foreach (var report in health.Reports.Where(r => r.Vulnerable is { } v && v.Critical + v.High > 0))
            {
                if (report.Ecosystem == DependencyReport.NuGet)
                {
                    var target = report.Target is null ? "" : Quote(report.Target) + " ";
                    commands.Add(Inspect("List the vulnerable NuGet packages", $"cd {Quote(path)} && dotnet list {target}package --vulnerable --include-transitive"));
                }
                else if (report.Ecosystem == DependencyReport.Npm)
                {
                    commands.Add(Inspect("Show the npm advisories", $"cd {Quote(path)} && npm audit"));
                    commands.Add(Fix("Update to patched versions where a compatible one exists", $"cd {Quote(path)} && npm audit fix"));
                }
            }

            yield return this.ForClone(clone, health.Critical > 0 ? FindingSeverity.High : FindingSeverity.Medium,
                $"{clone.Entry.Name}: {Plural(count, health.Critical > 0 && health.High == 0 ? "critical vulnerability" : health.Critical == 0 ? "high-severity vulnerability" : "serious vulnerability", health.Critical > 0 && health.High == 0 ? "critical vulnerabilities" : health.Critical == 0 ? "high-severity vulnerabilities" : "serious vulnerabilities")} in its dependencies",
                $"{(health.Critical > 0 ? $"{health.Critical} critical" : "")}{(health.Critical > 0 && health.High > 0 ? ", " : "")}{(health.High > 0 ? $"{health.High} high" : "")}"
                + (names.Count > 0 ? $": {string.Join(", ", names.Take(5))}{(count > Math.Min(5, names.Count) ? $" and {count - Math.Min(5, names.Count)} more" : "")}" : "")
                + $". Checked {(DaysSince(context.Now, health.CheckedAt) <= 0 ? "today" : Plural(DaysSince(context.Now, health.CheckedAt), "day") + " ago")} on {clone.Machine.Hostname}."
                + (others > 0 ? $" {others} more of lower severity." : ""),
                commands,
                [.. serious.SelectMany(p => p.Advisories.Count > 0 ? p.Advisories : [p.Name]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)])
                with
            { Url = serious.SelectMany(p => p.Advisories).FirstOrDefault(a => a.StartsWith("GHSA-", StringComparison.Ordinal)) is { } id ? "https://github.com/advisories/" + id : null };
        }
    }
}
