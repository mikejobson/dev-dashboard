using System.Globalization;
using System.Text;

namespace DevDash.Core.Cleanup;

/// <summary>The Fix commands of one rule for one machine, as text to paste into that machine's terminal.</summary>
public sealed record MachineScript(string? MachineId, string MachineName, int FindingCount, int CommandCount, string Text);

/// <summary>
/// Turns findings into one script per machine. A script only ever holds commands meant for its own
/// machine: paths on one machine mean nothing (or something else) on another.
/// </summary>
public static class CleanupScript
{
    /// <summary>Name used for findings that are not tied to a machine (GitHub pull requests).</summary>
    public const string AnyMachine = "any machine";

    public static IReadOnlyList<MachineScript> Build(string ruleTitle, IEnumerable<Finding> findings, DateTimeOffset now)
    {
        // machine id -> (name, finding -> its Fix commands for that machine), in first-seen order.
        var machines = new List<(string? Id, string Name, List<(Finding Finding, List<FindingCommand> Commands)> Items)>();

        foreach (var finding in findings)
        {
            foreach (var command in finding.Commands.Where(c => c.Kind == CommandKind.Fix))
            {
                var id = finding.MachineIdFor(command);
                var index = machines.FindIndex(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    machines.Add((id, id is null ? AnyMachine : finding.MachineNameFor(command) ?? id, []));
                    index = machines.Count - 1;
                }

                var items = machines[index].Items;
                if (items.Count == 0 || !ReferenceEquals(items[^1].Finding, finding))
                {
                    items.Add((finding, []));
                }

                items[^1].Commands.Add(command);
            }
        }

        return machines
            .Select(m =>
            {
                var text = new StringBuilder();
                text.Append(CultureInfo.InvariantCulture,
                    $"# DevDash cleanup: {OneLine(ruleTitle)} on {OneLine(m.Name)}, generated {now.UtcDateTime:yyyy-MM-dd HH:mm} UTC\n");
                text.Append("# Read it before you run it. Each command stands alone; delete the ones you do not want.\n");
                foreach (var (finding, commands) in m.Items)
                {
                    text.Append('\n').Append("# ").Append(OneLine(finding.Title)).Append('\n');
                    foreach (var command in commands)
                    {
                        text.Append(command.Command).Append('\n');
                    }
                }

                return new MachineScript(m.Id, m.Name, m.Items.Count, m.Items.Sum(i => i.Commands.Count), text.ToString());
            })
            .ToList();
    }

    /// <summary>
    /// Comment text must stay on its line: a title is built from branch and folder names, and a line
    /// break in one would turn the rest of the comment into a command.
    /// </summary>
    private static string OneLine(string text) =>
        string.Concat(text.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? ' ' : c));
}
