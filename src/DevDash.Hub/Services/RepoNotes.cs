using System.Text;
using DevDash.Hub.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.Services;

/// <summary><c>Hub:Tags</c>: what tags mean to the hub.</summary>
public sealed class RepoTagOptions
{
    /// <summary>
    /// Tags that quieten a repo, separated by commas or spaces: no alerts about it, and it is left out of
    /// "Where I left off" unless asked for. One string (as <c>MachineOffline:Ignore</c>), because
    /// configuration binding adds to a list's default rather than replacing it.
    /// </summary>
    public string QuietTags { get; set; } = "archived";

    public IReadOnlyList<string> QuietTagList => RepoTags.Parse(QuietTags);
}

/// <summary>One repo's tags, note and pin. <see cref="IsEmpty"/> for a repo nobody has written anything about.</summary>
public sealed record RepoNote(string Key, IReadOnlyList<string> Tags, string Note, bool Pinned)
{
    public DateTimeOffset? UpdatedAt { get; init; }
    public string UpdatedBy { get; init; } = "";

    public bool IsEmpty => Tags.Count == 0 && Note.Length == 0 && !Pinned;

    public bool HasTag(string? tag) => RepoTags.Normalize(tag) is { } t && Tags.Contains(t, StringComparer.Ordinal);

    public static RepoNote None(string key) => new(key, [], "", false);
}

/// <summary>Every repo's note at one moment. Lookups by key ignore case, like the catalog's.</summary>
public sealed class RepoNoteSet
{
    private readonly Dictionary<string, RepoNote> _byKey;
    private readonly HashSet<string> _quietTags;

    public RepoNoteSet(IEnumerable<RepoNote> notes, IEnumerable<string>? quietTags = null)
    {
        _byKey = notes.Where(n => !n.IsEmpty).GroupBy(n => n.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _quietTags = (quietTags ?? []).Select(RepoTags.Normalize).Where(t => t is not null).Select(t => t!).ToHashSet(StringComparer.Ordinal);
    }

    public static RepoNoteSet Empty { get; } = new([]);

    public IReadOnlyCollection<RepoNote> All => _byKey.Values;

    public IReadOnlySet<string> QuietTags => _quietTags;

    public RepoNote For(string key) => _byKey.TryGetValue(key, out var note) ? note : RepoNote.None(key);

    public bool IsPinned(string key) => _byKey.TryGetValue(key, out var note) && note.Pinned;

    /// <summary>A repo carrying one of the quiet tags (<c>archived</c> by default): no alerts, hidden from "Where I left off".</summary>
    public bool IsQuiet(string key) => _byKey.TryGetValue(key, out var note) && note.Tags.Any(_quietTags.Contains);

    /// <summary>The keys of the quiet repos, case-insensitive.</summary>
    public IReadOnlySet<string> QuietKeys => _byKey.Values.Where(n => n.Tags.Any(_quietTags.Contains)).Select(n => n.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the repo passes a tag filter: no filter passes everything.</summary>
    public bool Matches(string key, string? tag) => RepoTags.Normalize(tag) is not { } t || For(key).Tags.Contains(t, StringComparer.Ordinal);

    /// <summary>Every tag in use with how many repos carry it, most used first.</summary>
    public IReadOnlyList<(string Tag, int Count)> TagCounts() => _byKey.Values
        .SelectMany(n => n.Tags)
        .GroupBy(t => t, StringComparer.Ordinal)
        .Select(g => (Tag: g.Key, Count: g.Count()))
        .OrderByDescending(t => t.Count)
        .ThenBy(t => t.Tag, StringComparer.Ordinal)
        .ToList();
}

/// <summary>The rules for tags and notes. Pure.</summary>
public static class RepoTags
{
    public const int MaxTagLength = 32;
    public const int MaxTags = 12;
    public const int MaxNoteLength = 200;

    /// <summary>
    /// A tag as stored: lower case, a leading <c>#</c> dropped, letters, digits and <c>- _ . / :</c> only
    /// (anything else becomes <c>-</c>), at most <see cref="MaxTagLength"/>. Null when nothing is left.
    /// </summary>
    public static string? Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var text = tag.Trim().TrimStart('#').ToLowerInvariant();
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var keep = char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':';
            var next = keep ? c : '-';
            if (next == '-' && builder.Length > 0 && builder[^1] == '-')
            {
                continue;
            }

            builder.Append(next);
        }

        var result = builder.ToString().Trim('-');
        if (result.Length > MaxTagLength)
        {
            result = result[..MaxTagLength].TrimEnd('-');
        }

        return result.Length == 0 ? null : result;
    }

    /// <summary>Tags typed as one line: separated by spaces or commas, normalised, each once, in the order typed, at most <see cref="MaxTags"/>.</summary>
    public static IReadOnlyList<string> Parse(string? text) =>
        Clean((text ?? "").Split([' ', ',', ';', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Tags given one by one: each normalised (spaces inside one become <c>-</c>), each once, at most <see cref="MaxTags"/>.</summary>
    public static IReadOnlyList<string> Clean(IEnumerable<string?> tags) =>
        tags.Select(Normalize)
            .Where(t => t is not null)
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxTags)
            .ToList();

    /// <summary>A note as stored: one line, control characters dropped, runs of spaces collapsed, at most <see cref="MaxNoteLength"/> characters.</summary>
    public static string NormalizeNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return "";
        }

        var builder = new StringBuilder(note.Length);
        foreach (var c in note)
        {
            var ch = char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c;
            if (ch == ' ' && (builder.Length == 0 || builder[^1] == ' '))
            {
                continue;
            }

            builder.Append(ch);
        }

        var text = builder.ToString().TrimEnd();
        return text.Length <= MaxNoteLength ? text : text[..(MaxNoteLength - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// The tag a search asks for when one of its words is <c>#tag</c>, and the rest of the search. A lone
    /// <c>#</c> or a word like <c>#12</c> (a pull request number) is not a tag.
    /// </summary>
    public static (string? Tag, string Words) SplitSearch(string? query)
    {
        string? tag = null;
        var rest = new List<string>();
        foreach (var word in (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (tag is null && word.Length > 1 && word[0] == '#' && !word[1..].All(char.IsDigit) && Normalize(word) is { } t)
            {
                tag = t;
            }
            else
            {
                rest.Add(word);
            }
        }

        return (tag, string.Join(' ', rest));
    }

    /// <summary>Rows with pinned ones first, each part keeping the order it was given in.</summary>
    public static List<T> PinnedFirst<T>(IEnumerable<T> rows, Func<T, bool> pinned)
    {
        var list = rows.ToList();
        return [.. list.Where(pinned), .. list.Where(r => !pinned(r))];
    }
}

/// <summary>
/// Reads and saves repo notes (table <c>RepoNotes</c>). The whole table is small, so it is kept in memory
/// and read again only after a save; a save tells every page through <see cref="DashboardChange.Notes"/>.
/// </summary>
public sealed class RepoNotes(IDbContextFactory<DevDashDbContext> dbFactory, IOptionsMonitor<HubOptions> options, DashboardNotifier notifier, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<RepoNote>? _cache;

    public async Task<RepoNoteSet> GetAsync(CancellationToken ct = default)
    {
        var notes = _cache;
        if (notes is null)
        {
            await _gate.WaitAsync(ct);
            try
            {
                notes = _cache ??= await LoadAsync(ct);
            }
            finally
            {
                _gate.Release();
            }
        }

        return new RepoNoteSet(notes, options.CurrentValue.Tags.QuietTagList);
    }

    /// <summary>Replaces the repo's tags, note and pin. Everything empty removes the row.</summary>
    public async Task<RepoNote> SaveAsync(string key, IEnumerable<string> tags, string? note, bool pinned, string by, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var stored = Normalized(key);
        var saved = new RepoNote(stored, RepoTags.Clean(tags), RepoTags.NormalizeNote(note), pinned) { UpdatedAt = time.GetUtcNow(), UpdatedBy = by };

        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.RepoNotes.FirstOrDefaultAsync(n => n.Key == stored, ct);
            if (saved.IsEmpty)
            {
                if (row is not null)
                {
                    db.RepoNotes.Remove(row);
                }
            }
            else
            {
                if (row is null)
                {
                    row = new RepoNoteEntity { Key = stored };
                    db.RepoNotes.Add(row);
                }

                row.Tags = string.Join(' ', saved.Tags);
                row.Note = saved.Note;
                row.Pinned = saved.Pinned;
                row.UpdatedAt = saved.UpdatedAt!.Value;
                row.UpdatedBy = by;
            }

            await db.SaveChangesAsync(ct);
            _cache = null;
        }
        finally
        {
            _gate.Release();
        }

        notifier.Notify(DashboardChange.Notes);
        return saved;
    }

    public Task<RepoNote> SetPinnedAsync(RepoNote current, bool pinned, string by, CancellationToken ct = default) =>
        SaveAsync(current.Key, current.Tags, current.Note, pinned, by, ct);

    /// <summary>Keys are compared without case everywhere; stored lower-cased so one repo is one row.</summary>
    private static string Normalized(string key) => key.Trim().ToLowerInvariant();

    private async Task<IReadOnlyList<RepoNote>> LoadAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.RepoNotes.AsNoTracking().ToListAsync(ct))
            .Select(r => new RepoNote(r.Key, RepoTags.Parse(r.Tags), r.Note, r.Pinned) { UpdatedAt = r.UpdatedAt, UpdatedBy = r.UpdatedBy })
            .ToList();
    }
}
