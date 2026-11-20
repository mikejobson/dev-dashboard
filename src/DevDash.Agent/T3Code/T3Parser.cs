using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevDash.Contracts;

namespace DevDash.Agent.T3Code;

/// <summary>One thread as <c>t3 thread list --json</c> gives it, with the two facts the snapshot itself does not carry.</summary>
public sealed record T3ListedThread(T3Thread Thread, string? ProjectId, bool Archived);

/// <summary>
/// Reads what T3 Code's CLI prints. Pure. Every field is optional and of whatever type it turns out to be:
/// the CLI is young and its JSON may grow or change, and a field that cannot be read is simply left out.
/// </summary>
public static partial class T3Parser
{
    [GeneratedRegex(@"v?(\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.\-+]+)?)")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Uuid();

    [GeneratedRegex(@"https://\S+")]
    private static partial Regex Url();

    /// <summary><c>t3 v0.1.9</c> → <c>0.1.9</c>.</summary>
    public static string? ParseVersion(string output) =>
        VersionPattern().Match(output ?? "") is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>The array <c>t3 thread list --json</c> prints. Null when it is not JSON or not an array.</summary>
    public static IReadOnlyList<T3ListedThread>? ParseThreadList(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // An array, or an object that wraps one.
            if (root.ValueKind == JsonValueKind.Object)
            {
                root = Prop(root, "threads") is { ValueKind: JsonValueKind.Array } threads ? threads : default;
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return root.EnumerateArray().Select(Listed).Where(t => t is not null).Select(t => t!).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The <c>thread</c> object of <c>t3 thread show &lt;id&gt; --json</c>. Null when it is not there.</summary>
    public static T3ListedThread? ParseThreadShow(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var thread = Prop(root, "thread") is { ValueKind: JsonValueKind.Object } inner ? inner : root;
            return Listed(thread);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What <c>t3 thread start --json</c> printed: the new thread's id and its link. The shape has not been
    /// seen for real, so this takes the id from <c>threadId</c>, <c>id</c>, or a <c>thread</c> object holding
    /// either; failing JSON, the first id-shaped text, and the first https address as the link.
    /// </summary>
    public static T3StartedThread? ParseStarted(string output)
    {
        var text = (output ?? "").Trim();
        if (text.Length == 0)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String && root.GetString() is { Length: > 0 } bare)
            {
                return new T3StartedThread { ThreadId = bare };
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                var thread = Prop(root, "thread") is { ValueKind: JsonValueKind.Object } inner ? inner : root;
                var id = Str(root, "threadId") ?? Str(thread, "threadId") ?? Str(thread, "id") ?? Str(root, "id");
                if (id is { Length: > 0 })
                {
                    return new T3StartedThread { ThreadId = id, Link = SafeLink(Str(root, "link") ?? Str(thread, "link") ?? Str(root, "url") ?? Str(thread, "url")) };
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: look for an id in the text.
        }

        return Uuid().Match(text) is { Success: true } uuid
            ? new T3StartedThread { ThreadId = uuid.Value, Link = SafeLink(Url().Match(text) is { Success: true } url ? url.Value.TrimEnd('.', ',', ')') : null) }
            : null;
    }

    /// <summary>Only an https address is ever shown as a link.</summary>
    public static string? SafeLink(string? link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;

    private static T3ListedThread? Listed(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object || (Str(node, "threadId") ?? Str(node, "id")) is not { Length: > 0 } id)
        {
            return null;
        }

        var archived = Bool(node, "archived") || Str(node, "archivedAt") is not null;
        var thread = new T3Thread
        {
            Id = id,
            Title = Str(node, "title") ?? "",
            Status = Str(node, "status") ?? "",
            Link = SafeLink(Str(node, "link")),
            Branch = Str(node, "branch"),
            WorktreePath = Str(node, "worktreePath"),
            PendingRequests = Int(node, "pendingRequestCount") ?? Int(node, "pendingRequests") ?? 0,
            PullRequests = PullRequests(node),
            Settled = Bool(node, "settled"),
            CreatedAt = Date(node, "createdAt"),
            UpdatedAt = Date(node, "updatedAt"),
        };
        return new T3ListedThread(thread, Str(node, "projectId"), archived);
    }

    /// <summary><c>pullRequests</c> (a list) and <c>linkedPullRequest</c> (one), each an object, a URL or a number.</summary>
    private static IReadOnlyList<T3PullRequest> PullRequests(JsonElement node)
    {
        var result = new List<T3PullRequest>();
        void Add(JsonElement value)
        {
            T3PullRequest? pr = value.ValueKind switch
            {
                JsonValueKind.String => SafeLink(value.GetString()) is { } url ? new T3PullRequest { Url = url, Number = NumberFromUrl(url) } : null,
                JsonValueKind.Number when value.TryGetInt32(out var n) => new T3PullRequest { Number = n },
                JsonValueKind.Object => new T3PullRequest
                {
                    Url = SafeLink(Str(value, "url") ?? Str(value, "htmlUrl") ?? Str(value, "html_url")),
                    Number = Int(value, "number") ?? NumberFromUrl(Str(value, "url") ?? Str(value, "htmlUrl")),
                    State = Str(value, "state") ?? Str(value, "status"),
                },
                _ => null,
            };
            if (pr is { Url: not null } or { Number: not null } && !result.Any(r => (r.Url ?? "") == (pr.Url ?? "") && r.Number == pr.Number))
            {
                result.Add(pr);
            }
        }

        if (Prop(node, "linkedPullRequest") is { } linked)
        {
            Add(linked);
        }

        if (Prop(node, "pullRequests") is { ValueKind: JsonValueKind.Array } list)
        {
            foreach (var item in list.EnumerateArray().Take(10))
            {
                Add(item);
            }
        }

        return result;
    }

    private static int? NumberFromUrl(string? url)
    {
        var tail = url?.TrimEnd('/').Split('/').LastOrDefault();
        return int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static JsonElement? Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;

    private static string? Str(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.True };

    private static int? Int(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var n) ? n : null;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
