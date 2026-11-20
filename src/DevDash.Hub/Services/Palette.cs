using DevDash.Contracts;
using DevDash.Core.GitHub;

namespace DevDash.Hub.Services;

public enum PaletteKind
{
    Page,
    Repo,
    Machine,
    PullRequest,
    Action,
}

/// <summary>What choosing an item does, when it is more than following <see cref="PaletteItem.Url"/>.</summary>
public enum PaletteCommand
{
    /// <summary>Go to <see cref="PaletteItem.Url"/>. Everything that needs confirming is this: the page it opens asks.</summary>
    Navigate,

    /// <summary>Ask the agents in <see cref="PaletteItem.Targets"/> to rescan now. Read-only apart from a fetch, so it is not confirmed, as on the pages.</summary>
    Refresh,

    /// <summary>
    /// Open <see cref="PaletteItem.T3Path"/> in the T3 Code app on <see cref="PaletteItem.T3Machine"/>. It only
    /// opens a window, so it is queued (and recorded, with who asked) without a confirmation, like a refresh.
    /// </summary>
    OpenInT3,
}

/// <summary>One row of the command palette.</summary>
public sealed record PaletteItem
{
    /// <summary>Stable across reloads, so "recently used" can be remembered: <c>page:safety</c>, <c>repo:github.com/acme/widget</c>.</summary>
    public required string Id { get; init; }
    public required PaletteKind Kind { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public string? Url { get; init; }
    public bool External { get; init; }

    /// <summary>Other words the item is found by: an owner, a branch, a machine id.</summary>
    public IReadOnlyList<string?> Keywords { get; init; } = [];
    public PaletteCommand Command { get; init; }
    public IReadOnlyList<RefreshTarget> Targets { get; init; } = [];

    /// <summary><see cref="PaletteCommand.OpenInT3"/>: the machine and the working tree.</summary>
    public MachineView? T3Machine { get; init; }
    public string? T3Path { get; init; }

    /// <summary>A repo the user pinned (§20): it ranks a little higher.</summary>
    public bool Pinned { get; init; }
}

/// <summary>
/// What the ⌘K palette lists and in what order. Pure, so the ranking is tested without a browser.
/// <para>
/// Nothing here runs anything destructive. "Remove N safe projects" and "Update all agents" are links
/// to the Safety and Machines pages with <c>?ask=…</c>, which opens that page's own confirmation
/// dialog; the only things done straight from the palette are refreshes, which the pages do not
/// confirm either.
/// </para>
/// </summary>
public static class Palette
{
    public const int DefaultLimit = 12;

    /// <summary>The most refreshes "Fetch all" asks for at once: under the number the hub keeps a record of.</summary>
    public const int FetchAllLimit = 60;

    public const string AskRemoveSafe = "remove-safe";
    public const string AskUpdateAll = "update-all";
    public const string AskT3 = "t3";

    /// <summary>What a pinned repo gains in the ranking: ahead of an equal match, not of a better one.</summary>
    public const int PinnedBoost = 10;

    public static IReadOnlyList<PaletteItem> Pages { get; } =
    [
        Page("overview", "Overview", "", "What needs attention", "home", "today", "activity"),
        Page("left-off", "Where I left off", "left-off", "The projects you touched most recently, on any machine", "recent", "resume", "continue", "touched", "pinned"),
        Page("local", "Local", "local", "Every repo and folder, by machine", "repos", "folders"),
        Page("github", "GitHub", "github", "Pull requests, issues and recent org activity", "prs", "pull requests", "issues", "reviews"),
        Page("cleanup", "Cleanup", "cleanup", "Findings worth tidying up", "findings", "tidy"),
        Page("safety", "Safety", "safety", "What exists on one machine only, and what is safe to remove", "at risk", "remove", "only copy"),
        Page("issues", "Issues", "issues", "Open GitHub issues of your repos, and the T3 threads started for them", "bugs", "t3", "label", "trigger"),
        Page("alerts", "Alerts", "alerts", "Channels, triggers and the delivery log", "notifications", "ntfy", "webhook"),
        Page("history", "History", "history", "Disk, at-risk work and unpushed commits over time", "trends", "charts", "growth", "since yesterday"),
        Page("disk", "Disk", "disk", "Developer caches outside your repos, and what cleaning them frees", "caches", "space", "nuget", "npm", "docker", "xcode", "clean"),
        Page("machines", "Machines", "machines", "Agents, toolchains, actions and API keys", "agents", "keys", "toolchains", "sdks"),
    ];

    private static PaletteItem Page(string id, string title, string url, string detail, params string[] keywords) =>
        new() { Id = "page:" + id, Kind = PaletteKind.Page, Title = title, Url = url, Detail = detail, Keywords = keywords };

    /// <summary>The id an address is remembered under: a page, or a repo's page. Null for anything else.</summary>
    public static string? IdOf(string relativeUrl)
    {
        var path = relativeUrl.TrimStart('/');
        var hash = path.IndexOf('#');
        path = hash >= 0 ? path[..hash] : path;
        var query = "";
        var mark = path.IndexOf('?');
        if (mark >= 0)
        {
            query = path[(mark + 1)..];
            path = path[..mark];
        }

        if (path.Equals("repo", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (pair.StartsWith("key=", StringComparison.OrdinalIgnoreCase))
                {
                    return "repo:" + Uri.UnescapeDataString(pair[4..].Replace('+', ' ')).ToLowerInvariant();
                }
            }

            return null;
        }

        var id = "page:" + (path.Length == 0 ? "overview" : path.ToLowerInvariant());
        return Pages.Any(p => p.Id == id) ? id : null;
    }

    /// <summary>Everything the palette can list right now.</summary>
    /// <param name="canRefresh">Whether a machine's agent can be asked to rescan now.</param>
    /// <param name="safeToRemove">How many projects the Safety page's "Remove N safe projects" would offer.</param>
    /// <param name="updatable">How many agents the Machines page's "Update all" would offer.</param>
    public static IReadOnlyList<PaletteItem> Items(
        IReadOnlyList<RepoRow> repos,
        IReadOnlyList<PullRequestInfo> pullRequests,
        IReadOnlyList<MachineView> machines,
        Func<MachineView, bool> canRefresh,
        int safeToRemove,
        int updatable,
        Func<MachineView, AgentActionKind, bool>? canDo = null,
        RepoNoteSet? notes = null)
    {
        notes ??= RepoNoteSet.Empty;
        var items = new List<PaletteItem>(Pages);

        foreach (var machine in machines)
        {
            var hit = GlobalSearch.MachineHit(machine);
            items.Add(new PaletteItem { Id = "machine:" + machine.MachineId.ToLowerInvariant(), Kind = PaletteKind.Machine, Title = hit.Title, Detail = hit.Detail, Url = hit.Url, Keywords = GlobalSearch.MachineFields(machine)[1..] });
        }

        var fetchAll = new List<(DateTimeOffset? Activity, RefreshTarget Target)>();
        foreach (var row in repos)
        {
            var hit = GlobalSearch.RepoHit(row);
            var note = notes.For(row.Key);
            // Tags are found as "#client"; the note by its words.
            string?[] keywords = [.. GlobalSearch.RepoFields(row)[1..], .. note.Tags.Select(t => "#" + t), note.Note.Length > 0 ? note.Note : null];
            var detail = note.Tags.Count == 0 ? hit.Detail : $"{hit.Detail} · {string.Join(" ", note.Tags.Select(t => "#" + t))}";
            items.Add(new PaletteItem { Id = "repo:" + row.Key.ToLowerInvariant(), Kind = PaletteKind.Repo, Title = hit.Title, Detail = detail, Url = hit.Url, Keywords = keywords, Pinned = note.Pinned });

            foreach (var group in row.Clones.GroupBy(c => c.Machine.MachineId, StringComparer.OrdinalIgnoreCase))
            {
                var machine = group.First().Machine;
                var targets = RefreshTargets.For(group);
                if (targets.Count == 0 || !canRefresh(machine))
                {
                    continue;
                }

                items.Add(new PaletteItem
                {
                    Id = $"refresh:{row.Key.ToLowerInvariant()}@{machine.MachineId.ToLowerInvariant()}",
                    Kind = PaletteKind.Action,
                    Title = $"Refresh {row.Name} on {machine.Machine.Hostname}",
                    Detail = "Rescan now: fetch, sizes and state",
                    Url = hit.Url,
                    Keywords = [row.Owner, "fetch", "rescan"],
                    Command = PaletteCommand.Refresh,
                    Targets = targets,
                });

                // T3 Code: open the clone in the app (queued at once), or open the repo page's "Ask Claude" dialog.
                if (canDo is not null && group.FirstOrDefault(c => c.Entry.Kind == EntryKind.GitRepo && c.Git is { IsLinkedWorktree: false }) is { } t3Clone)
                {
                    if (canDo(machine, AgentActionKind.OpenInT3))
                    {
                        items.Add(new PaletteItem
                        {
                            Id = $"t3-open:{row.Key.ToLowerInvariant()}@{machine.MachineId.ToLowerInvariant()}",
                            Kind = PaletteKind.Action,
                            Title = $"Open {row.Name} in T3 on {machine.Machine.Hostname}",
                            Detail = "Opens the project in the T3 Code app on that machine",
                            Keywords = [row.Owner, "t3", "t3 code", "claude", "app"],
                            Command = PaletteCommand.OpenInT3,
                            T3Machine = machine,
                            T3Path = t3Clone.Entry.Path,
                        });
                    }

                    if (canDo(machine, AgentActionKind.StartT3Thread) && !items.Any(i => i.Id == "t3-ask:" + row.Key.ToLowerInvariant()))
                    {
                        items.Add(new PaletteItem
                        {
                            Id = "t3-ask:" + row.Key.ToLowerInvariant(),
                            Kind = PaletteKind.Action,
                            Title = $"Ask Claude in T3 about {row.Name}…",
                            Detail = "Opens the repo page's dialog; nothing starts until you confirm there",
                            Url = hit.Url + "&ask=" + AskT3,
                            Keywords = [row.Owner, "t3", "claude", "thread", "start"],
                        });
                    }
                }

                // "Fetch all" is for what has somewhere to fetch from.
                if (group.Any(c => c.Git is { Remotes.Count: > 0 }))
                {
                    fetchAll.AddRange(targets.Select(t => (row.LastActivity, t)));
                }
            }
        }

        foreach (var pr in pullRequests)
        {
            var hit = GlobalSearch.PullRequestHit(pr);
            items.Add(new PaletteItem { Id = "pr:" + pr.Url, Kind = PaletteKind.PullRequest, Title = hit.Title, Detail = hit.Detail, Url = hit.Url, External = true, Keywords = GlobalSearch.PullRequestFields(pr)[1..] });
        }

        if (fetchAll.Count > 0)
        {
            // Most recently active first, each entry once, and never more at once than the hub keeps track of.
            var all = fetchAll
                .OrderByDescending(f => f.Activity)
                .Select(f => f.Target)
                .DistinctBy(t => (t.Machine.MachineId.ToLowerInvariant(), t.Path))
                .ToList();
            var targets = all.Take(FetchAllLimit).ToList();
            var machineCount = targets.Select(t => t.Machine.MachineId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            items.Add(new PaletteItem
            {
                Id = "action:fetch-all",
                Kind = PaletteKind.Action,
                Title = "Fetch all",
                Detail = $"Refresh {Fmt.Count(targets.Count, "repo")} on {Fmt.Count(machineCount, "machine")} now"
                    + (all.Count > targets.Count ? $" (the {targets.Count} most recently active of {all.Count})" : ""),
                Keywords = ["refresh", "rescan", "everything"],
                Command = PaletteCommand.Refresh,
                Targets = targets,
            });
        }

        if (safeToRemove > 0)
        {
            items.Add(new PaletteItem
            {
                Id = "action:remove-safe",
                Kind = PaletteKind.Action,
                Title = $"Remove {Fmt.Count(safeToRemove, "safe project")}…",
                Detail = "Opens the Safety page's confirmation; nothing is removed until you confirm there",
                Url = "safety?ask=" + AskRemoveSafe,
                Keywords = ["delete", "trash", "safety", "clean"],
            });
        }

        if (updatable > 0)
        {
            items.Add(new PaletteItem
            {
                Id = "action:update-all",
                Kind = PaletteKind.Action,
                Title = updatable == 1 ? "Update the agent that is behind…" : $"Update all agents ({updatable})…",
                Detail = "Opens the Machines page's confirmation; nothing is queued until you confirm there",
                Url = "machines?ask=" + AskUpdateAll,
                Keywords = ["update all agents", "upgrade", "release", "machines"],
            });
        }

        return items;
    }

    /// <summary>
    /// The items to show for what has been typed, best first. Every word must be found. A word at the
    /// start of the title beats one at the start of a later word, which beats one in the middle; the
    /// title beats the other fields; and something used recently gets a nudge upwards.
    /// With nothing typed: what was used recently, then the pages, then the actions that are not about one repo.
    /// </summary>
    /// <param name="recent">Item ids, most recently used first.</param>
    public static IReadOnlyList<PaletteItem> Search(string? query, IReadOnlyList<PaletteItem> items, IReadOnlyList<string> recent, int limit = DefaultLimit)
    {
        var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            var byId = items.ToLookup(i => i.Id, StringComparer.Ordinal);
            var recents = recent.Select(id => byId[id].FirstOrDefault()).Where(i => i is not null).Select(i => i!).Take(5).ToList();
            return recents
                .Concat(items.Where(i => i.Kind == PaletteKind.Page))
                .Concat(items.Where(i => i.Kind == PaletteKind.Action && i.Id.StartsWith("action:", StringComparison.Ordinal)))
                .DistinctBy(i => i.Id)
                .Take(Math.Max(limit, 16))
                .ToList();
        }

        var scored = new List<(int Score, PaletteItem Item)>();
        foreach (var item in items)
        {
            var score = Score(terms, item);
            if (score > 0)
            {
                scored.Add((score + RecentBoost(recent, item.Id) + (item.Pinned ? PinnedBoost : 0), item));
            }
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Item.Kind)
            .ThenBy(s => s.Item.Title.Length)
            .ThenBy(s => s.Item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(s => s.Item)
            .ToList();
    }

    /// <summary>Up to 25 for the most recently used item, less for older ones, nothing for the rest.</summary>
    public static int RecentBoost(IReadOnlyList<string> recent, string id)
    {
        for (var i = 0; i < recent.Count && i < 8; i++)
        {
            if (recent[i] == id)
            {
                return 25 - i * 3;
            }
        }

        return 0;
    }

    /// <summary>0 when some word is found nowhere. Otherwise the sum of each word's best match.</summary>
    public static int Score(string[] terms, PaletteItem item)
    {
        var total = 0;
        foreach (var term in terms)
        {
            var best = Match(item.Title, term) switch
            {
                MatchKind.Whole => 120,
                MatchKind.Prefix => 100,
                MatchKind.WordStart => 70,
                MatchKind.Inside => 40,
                _ => 0,
            };

            foreach (var keyword in item.Keywords)
            {
                best = Math.Max(best, Match(keyword, term) switch
                {
                    MatchKind.Whole or MatchKind.Prefix => 30,
                    MatchKind.WordStart => 20,
                    MatchKind.Inside => 10,
                    _ => 0,
                });
            }

            if (best == 0)
            {
                return 0;
            }

            total += best;
        }

        return total;
    }

    private enum MatchKind
    {
        None,
        Inside,
        WordStart,
        Prefix,
        Whole,
    }

    private static MatchKind Match(string? field, string term)
    {
        if (string.IsNullOrEmpty(field))
        {
            return MatchKind.None;
        }

        var best = MatchKind.None;
        var at = field.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            if (at == 0)
            {
                return field.Length == term.Length ? MatchKind.Whole : MatchKind.Prefix;
            }

            best = (MatchKind)Math.Max((int)best, (int)(char.IsLetterOrDigit(field[at - 1]) ? MatchKind.Inside : MatchKind.WordStart));
            at = field.IndexOf(term, at + 1, StringComparison.OrdinalIgnoreCase);
        }

        return best;
    }
}
