using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevDash.Hub.Alerts;

/// <summary>One alert, ready to send.</summary>
/// <param name="Url">The hub page about it, absolute. Null when <c>Hub:PublicUrl</c> is not set.</param>
/// <param name="Path">The same page relative to the hub, always there.</param>
/// <param name="ExternalUrl">A page outside the hub (the pull request on GitHub), when there is one.</param>
/// <param name="Count">How many alerts this message stands for: more than one for a summary.</param>
public sealed record AlertMessage(
    string Trigger,
    string Title,
    string Body,
    AlertSeverity Severity,
    string Key,
    DateTimeOffset At,
    string? Url,
    string Path,
    string? ExternalUrl = null,
    int Count = 1,
    bool Test = false);

/// <param name="Error">What went wrong, safe to show and to store: it never holds a token, a topic or a webhook address.</param>
public sealed record DeliveryResult(string Channel, bool Ok, string? Error);

/// <summary>The channels an alert can go out on.</summary>
public static class AlertChannels
{
    public const string Webhook = "webhook";
    public const string Ntfy = "ntfy";
    public const string HomeAssistant = "homeassistant";

    public static readonly IReadOnlyList<string> All = [Webhook, Ntfy, HomeAssistant];

    public static string Label(string channel) => channel switch
    {
        Webhook => "Webhook",
        Ntfy => "ntfy",
        HomeAssistant => "Home Assistant",
        _ => channel,
    };

    public static bool IsEnabled(string channel, AlertOptions options) => channel switch
    {
        Webhook => options.Channels.Webhook.Enabled,
        Ntfy => options.Channels.Ntfy.Enabled,
        HomeAssistant => options.Channels.HomeAssistant.Enabled,
        _ => false,
    };

    /// <summary>What is missing before the channel can send anything, or null when it is ready. Says nothing about <c>Enabled</c>.</summary>
    public static string? WhyNotReady(string channel, AlertOptions options)
    {
        switch (channel)
        {
            case Webhook:
                return options.Channels.Webhook.Url.Length == 0 ? "No URL is set."
                    : HttpUrl(options.Channels.Webhook.Url) is null ? "The URL is not an http or https address."
                    : null;
            case Ntfy:
                var ntfy = options.Channels.Ntfy;
                return ntfy.Topic.Length == 0 ? "No topic is set."
                    : HttpUrl(ntfy.Server) is null ? "The server is not an http or https address."
                    : null;
            case HomeAssistant:
                var ha = options.Channels.HomeAssistant;
                return ha.Url.Length == 0 ? "No Home Assistant address is set."
                    : HttpUrl(ha.Url) is null ? "The Home Assistant address is not an http or https address."
                    : ha.WebhookId.Length == 0 ? "No webhook ID is set."
                    : null;
            default:
                return "There is no such channel.";
        }
    }

    /// <summary>The channels that are switched on and have what they need.</summary>
    public static IReadOnlyList<string> Active(AlertOptions options) =>
        All.Where(c => IsEnabled(c, options) && WhyNotReady(c, options) is null).ToList();

    internal static Uri? HttpUrl(string? text) =>
        Uri.TryCreate((text ?? "").Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.Host.Length > 0
            ? uri
            : null;

    /// <summary>Every configured value that must never be shown, logged or stored in a delivery error.</summary>
    internal static IEnumerable<string> Secrets(AlertOptions options)
    {
        string[] values =
        [
            options.Channels.Webhook.Url, options.Channels.Webhook.Token,
            options.Channels.Ntfy.Topic, options.Channels.Ntfy.Token,
            options.Channels.HomeAssistant.WebhookId,
        ];
        return values.Where(v => v.Length >= 3);
    }
}

/// <summary>
/// Sends an alert to one channel: a JSON POST each time. It only ever reports a status code or a
/// short reason; the request's address, headers and body are never logged.
/// </summary>
public sealed class AlertSender(HttpClient http)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<DeliveryResult> SendAsync(string channel, AlertMessage message, AlertOptions options, CancellationToken ct = default)
    {
        if (AlertChannels.WhyNotReady(channel, options) is { } why)
        {
            return new DeliveryResult(channel, false, why);
        }

        using var request = channel switch
        {
            AlertChannels.Ntfy => Ntfy(message, options.Channels.Ntfy),
            AlertChannels.HomeAssistant => HomeAssistant(message, options.Channels.HomeAssistant),
            _ => Webhook(message, options.Channels.Webhook),
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return response.IsSuccessStatusCode
                ? new DeliveryResult(channel, true, null)
                : new DeliveryResult(channel, false, Scrub($"{AlertChannels.Label(channel)} answered {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd(), options));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DeliveryResult(channel, false, $"No answer from {AlertChannels.Label(channel)} in {Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            // The outer message is often just "an error occurred"; the reason (refused, name not found) is inside.
            var reason = ex.InnerException is { Message.Length: > 0 } inner && !ex.Message.Contains(inner.Message, StringComparison.Ordinal) ? $"{ex.Message} {inner.Message}" : ex.Message;
            return new DeliveryResult(channel, false, Scrub($"Could not reach {AlertChannels.Label(channel)}: {reason}", options));
        }
    }

    /// <summary>
    /// JSON with a known length. Streamed JSON goes out chunked, with no <c>Content-Length</c>, and the
    /// small servers a webhook often points at do not all read that.
    /// </summary>
    private static ByteArrayContent Body(object value)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, Json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    /// <summary>The body the generic webhook and Home Assistant both get.</summary>
    internal static object Payload(AlertMessage message) => new
    {
        source = "devdash",
        trigger = message.Trigger,
        severity = message.Severity,
        title = message.Title,
        message = message.Body,
        url = message.Url,
        path = message.Path,
        link = message.ExternalUrl,
        key = message.Key,
        count = message.Count,
        time = message.At.ToUniversalTime(),
        test = message.Test,
    };

    private static HttpRequestMessage Webhook(AlertMessage message, WebhookChannelOptions options)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, AlertChannels.HttpUrl(options.Url)) { Content = Body(Payload(message)) };
        if (options.Token.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        }

        return request;
    }

    private static HttpRequestMessage HomeAssistant(AlertMessage message, HomeAssistantChannelOptions options) =>
        new(HttpMethod.Post, new Uri($"{options.Url.TrimEnd('/')}/api/webhook/{Uri.EscapeDataString(options.WebhookId)}"))
        {
            Content = Body(Payload(message)),
        };

    /// <summary>ntfy's JSON form, posted to the server's root: the topic travels in the body, and the title can be any text.</summary>
    private static HttpRequestMessage Ntfy(AlertMessage message, NtfyChannelOptions options)
    {
        var actions = message.ExternalUrl is { } external
            ? new[] { new { action = "view", label = external.Contains("github.com", StringComparison.OrdinalIgnoreCase) ? "Open on GitHub" : "Open", url = external } }
            : null;
        var body = new
        {
            topic = options.Topic,
            title = message.Title,
            message = message.Body.Length > 0 ? message.Body : message.Title,
            priority = message.Severity switch { AlertSeverity.High => 4, AlertSeverity.Info => 2, _ => 3 },
            tags = new[] { message.Severity switch { AlertSeverity.High => "rotating_light", AlertSeverity.Info => "information_source", _ => "warning" } },
            click = message.Url,
            actions,
        };
        var request = new HttpRequestMessage(HttpMethod.Post, AlertChannels.HttpUrl(options.Server)) { Content = Body(body) };
        if (options.Token.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        }

        return request;
    }

    /// <summary>Takes anything secret out of a message before it is shown or stored.</summary>
    internal static string Scrub(string text, AlertOptions options)
    {
        foreach (var secret in AlertChannels.Secrets(options).OrderByDescending(s => s.Length))
        {
            text = text.Replace(secret, "•••", StringComparison.OrdinalIgnoreCase);
            var escaped = Uri.EscapeDataString(secret);
            if (escaped != secret)
            {
                text = text.Replace(escaped, "•••", StringComparison.OrdinalIgnoreCase);
            }
        }

        return text.Length <= 300 ? text : text[..299] + "…";
    }
}
