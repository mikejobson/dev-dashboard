using System.Globalization;
using DevDash.Contracts;
using static DevDash.Core.Cleanup.RuleHelpers;

namespace DevDash.Core.Cleanup.Rules;

/// <summary>Stashes that have been forgotten.</summary>
public sealed class OldStashRule : ICleanupRule
{
    public string Id => "old-stash";
    public string Title => TitleFor(new CleanupOptions());

    public string TitleFor(CleanupOptions options) => $"Stashes older than {Span(options.StashDays)}";
    public string Description => "Old stashes are easy to forget and hard to make sense of later. Apply or drop them.";

    public IEnumerable<Finding> Evaluate(CleanupContext context)
    {
        foreach (var repo in context.Repositories())
        {
            var old = repo.Git.Stashes
                .Where(s => DaysSince(context.Now, s.Date) >= context.Options.StashDays)
                .OrderBy(s => s.Date)
                .ToList();
            if (old.Count == 0)
            {
                continue;
            }

            var oldest = old[0];
            var path = MainPath(repo);

            // Dropping a stash renumbers the ones after it, so drops go highest index first: a script
            // that runs them top to bottom then still hits the stashes it meant to.
            var byIndex = old.OrderByDescending(s => s.Index).ToList();
            FindingCommand[] commands =
            [
                Inspect("List stashes", GitCmd(path, "stash list --date=relative")),
                .. byIndex.Select(s => Inspect(
                    $"Show {Selector(s)} ({DaysSince(context.Now, s.Date)} days old)",
                    GitCmd(path, $"stash show -p {Quote(Selector(s))}"))),
                .. byIndex.Select(s => Fix(
                    $"Drop {Selector(s)}",
                    GitCmd(path, $"stash drop {Quote(Selector(s))}"),
                    s.Sha is null ? null : AgentAction.DropStash(path, s.Index, s.Sha))),
            ];

            yield return this.ForClone(repo, FindingSeverity.Low,
                $"{repo.Entry.Name}: {Plural(old.Count, "old stash", "old stashes")}",
                $"Oldest is {DaysSince(context.Now, oldest.Date)} days old: \"{oldest.Message}\". "
                + "Look at each one, then apply or drop it. Dropping renumbers the stashes after it, so drop the highest number first.",
                commands,
                // Stash indexes shift, so identify stashes by their creation time.
                [.. old.Select(s => s.Date.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))])
                .ActiveAt(old[^1].Date);
        }
    }

    private static string Selector(StashInfo stash) => string.Create(CultureInfo.InvariantCulture, $"stash@{{{stash.Index}}}");
}
