using DevDash.Contracts;
using DevDash.Core;
using DevDash.Core.Toolchains;

namespace DevDash.Hub.Services;

/// <summary>One working tree with a pin its machine does not meet.</summary>
public sealed record UnmetPin(MachineView Machine, DevEntry Entry, PinCheck Check)
{
    public string RepoKey => RepoIdentity.KeyFor(Machine.MachineId, Entry);
}

/// <summary>What the Machines page shows for one machine's toolchains.</summary>
/// <param name="Reported">False when the agent sent no inventory: too old, or the feature is off there.</param>
public sealed record MachineToolchains(MachineView Machine, bool Reported, IReadOnlyList<InstalledToolchain> Tools, int Pinned, IReadOnlyList<UnmetPin> Unmet);

/// <summary>Joins each machine's installed toolchains to what its repos pin. Pure over the snapshots.</summary>
public static class ToolchainView
{
    public static IReadOnlyList<MachineToolchains> For(IEnumerable<MachineView> machines) => machines.Select(For).ToList();

    public static MachineToolchains For(MachineView machine)
    {
        var inventory = machine.Machine.Toolchains;
        var pinned = 0;
        var unmet = new List<UnmetPin>();
        foreach (var entry in RepoIdentity.Flatten(machine.Snapshot.Entries))
        {
            if (entry.Pins is not { Count: > 0 })
            {
                continue;
            }

            pinned++;
            unmet.AddRange(ToolchainCheck.For(entry, machine.Machine).Where(c => c.Status == PinStatus.Missing).Select(c => new UnmetPin(machine, entry, c)));
        }

        return new MachineToolchains(machine, inventory is not null, Order(inventory?.Tools ?? []), pinned, unmet);
    }

    /// <summary>Why a machine shows no toolchains, for the page.</summary>
    public static string WhyNotReported(MachineInfo machine)
    {
        var minimum = SemVer.ParseOrNull("0.6.0")!;
        return SemVer.ParseOrNull(machine.AgentVersion) is { } version && version.CompareTo(minimum) >= 0
            ? $"Toolchains are switched off on {machine.Hostname} (Agent:Toolchains:Enabled)."
            : $"Update the agent on {machine.Hostname} to 0.6.0 or newer to see its toolchains.";
    }

    /// <summary>A tool's versions for one line: the newest few, and how many more.</summary>
    public static string Versions(InstalledToolchain tool, int show = 4)
    {
        var versions = tool.Versions.Take(show).ToList();
        return string.Join(", ", versions) + (tool.Versions.Count > show ? $" +{tool.Versions.Count - show}" : "");
    }

    private static readonly string[] ToolOrder = [ToolNames.Dotnet, ToolNames.Node, ToolNames.Python, ToolNames.Go, ToolNames.Rust, ToolNames.Java];

    private static IReadOnlyList<InstalledToolchain> Order(IReadOnlyList<InstalledToolchain> tools) =>
        tools.OrderBy(t => Array.IndexOf(ToolOrder, t.Tool) is var i and >= 0 ? i : ToolOrder.Length).ThenBy(t => t.Tool, StringComparer.Ordinal).ToList();
}

/// <summary>How a repo's dependency check reads on a chip.</summary>
public static class DependencyView
{
    /// <summary>Null when there is nothing worth a chip: no check yet, or nothing vulnerable.</summary>
    public static (string Text, string Class, string Title)? Chip(DependencyHealth? health, DateTimeOffset now)
    {
        if (health is null || health.VulnerableTotal == 0)
        {
            return null;
        }

        var serious = health.Critical + health.High;
        var names = health.Reports.SelectMany(r => r.TopVulnerable).Select(p => $"{p.Name} ({p.Severity})").Take(6).ToList();
        var title = $"{Counts(health)}. {string.Join(", ", names)}{(health.VulnerableTotal > names.Count ? "…" : "")}\nChecked {Fmt.Ago(health.CheckedAt, now)}.";
        return ($"{health.VulnerableTotal} vulnerable", health.Critical > 0 ? "danger" : serious > 0 ? "warn" : "muted", title);
    }

    /// <summary>"1 critical, 2 high, 3 moderate".</summary>
    public static string Counts(DependencyHealth health)
    {
        var parts = new List<string>();
        void Add(int count, string name)
        {
            if (count > 0)
            {
                parts.Add($"{count} {name}");
            }
        }

        Add(health.Critical, "critical");
        Add(health.High, "high");
        Add(health.Reports.Sum(r => r.Vulnerable?.Moderate ?? 0), "moderate");
        Add(health.Reports.Sum(r => r.Vulnerable?.Low ?? 0), "low");
        return parts.Count == 0 ? "no known vulnerabilities" : string.Join(", ", parts);
    }

    public static string Ecosystem(string ecosystem) => ecosystem switch
    {
        DependencyReport.NuGet => "NuGet",
        DependencyReport.Npm => "npm",
        _ => ecosystem,
    };

    public static string SeverityClass(string severity) => severity switch
    {
        "critical" => "danger",
        "high" => "warn",
        "moderate" => "info",
        _ => "muted",
    };
}
