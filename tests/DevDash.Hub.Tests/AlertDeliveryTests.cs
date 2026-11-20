using System.Net;
using System.Text.Json;
using DevDash.Contracts;
using DevDash.Hub.Alerts;
using DevDash.Hub.Data;
using DevDash.Hub.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevDash.Hub.Tests;

/// <summary>
/// Alerts end to end inside the hub: the channels against a pretend server, then evaluation, dedupe,
/// the settings and the page against the real hub with an in-memory database. Nothing here leaves the
/// process: every address is under <c>example.test</c> and answered by a stub.
/// </summary>
public class AlertDeliveryTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private const string Topic = "topic-s3cret";
    private const string NtfyToken = "tk_ntfy-s3cret";
    private const string HookId = "hook-s3cret-id";
    private const string HookUrl = "https://hooks.example.test/in/url-s3cret";
    private const string HookToken = "bearer-s3cret";

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ---- the channels, on their own -------------------------------------------------------------

    private sealed class Recorder(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Auth, string? ContentType, JsonElement Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // A known length, not chunked: plenty of small webhook receivers only read a body that says how long it is.
            Assert.True(request.Content!.Headers.ContentLength > 0);
            Assert.NotEqual(true, request.Headers.TransferEncodingChunked);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), request.Content.Headers.ContentType?.MediaType, JsonDocument.Parse(body).RootElement));
            return respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static AlertOptions AllChannels() => new AlertOptions
    {
        Channels =
        {
            Webhook = { Enabled = true, Url = HookUrl, Token = HookToken },
            Ntfy = { Enabled = true, Server = "https://ntfy.example.test/", Topic = Topic, Token = NtfyToken },
            HomeAssistant = { Enabled = true, Url = "http://ha.example.test:8123", WebhookId = HookId },
        },
    }.Normalize();

    private static AlertMessage Message(AlertSeverity severity = AlertSeverity.High, string? external = "https://github.com/acme/widget/pull/7") =>
        new(AlertTriggers.CiFailed, "CI failing: acme/widget#7", "Größe ändern (feature/ü)", severity, "ci-failed:abc", Start, "https://hub.example.test/github", "github", external);

    [Fact]
    public async Task The_webhook_gets_a_json_post_with_the_alert_and_the_bearer_token()
    {
        var handler = new Recorder();

        var result = await new AlertSender(new HttpClient(handler)).SendAsync(AlertChannels.Webhook, Message(), AllChannels());

        Assert.True(result.Ok, result.Error);
        var request = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, HookUrl, "Bearer " + HookToken, "application/json"), (request.Method, request.Url, request.Auth, request.ContentType));
        var body = request.Body;
        Assert.Equal("devdash", body.GetProperty("source").GetString());
        Assert.Equal("ci-failed", body.GetProperty("trigger").GetString());
        Assert.Equal("high", body.GetProperty("severity").GetString());
        Assert.Equal("CI failing: acme/widget#7", body.GetProperty("title").GetString());
        Assert.Equal("Größe ändern (feature/ü)", body.GetProperty("message").GetString());
        Assert.Equal("https://hub.example.test/github", body.GetProperty("url").GetString());
        Assert.Equal("github", body.GetProperty("path").GetString());
        Assert.Equal("https://github.com/acme/widget/pull/7", body.GetProperty("link").GetString());
        Assert.Equal("ci-failed:abc", body.GetProperty("key").GetString());
        Assert.Equal(1, body.GetProperty("count").GetInt32());
        Assert.Equal(Start, body.GetProperty("time").GetDateTimeOffset());
        Assert.False(body.GetProperty("test").GetBoolean());

        // No token: no header. No public address or outside page: those fields are simply absent.
        var plain = AllChannels();
        plain.Channels.Webhook.Token = "";
        await new AlertSender(new HttpClient(handler)).SendAsync(AlertChannels.Webhook, Message(external: null) with { Url = null }, plain);
        Assert.Null(handler.Requests[1].Auth);
        Assert.False(handler.Requests[1].Body.TryGetProperty("url", out _));
        Assert.False(handler.Requests[1].Body.TryGetProperty("link", out _));
    }

    [Fact]
    public async Task Ntfy_gets_its_json_form_with_the_topic_in_the_body_and_the_token_as_bearer()
    {
        var handler = new Recorder();
        var sender = new AlertSender(new HttpClient(handler));

        var result = await sender.SendAsync(AlertChannels.Ntfy, Message(), AllChannels());

        Assert.True(result.Ok, result.Error);
        var request = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, "https://ntfy.example.test/", "Bearer " + NtfyToken), (request.Method, request.Url, request.Auth)); // the topic is not in the address
        var body = request.Body;
        Assert.Equal(Topic, body.GetProperty("topic").GetString());
        Assert.Equal("CI failing: acme/widget#7", body.GetProperty("title").GetString());
        Assert.Equal("Größe ändern (feature/ü)", body.GetProperty("message").GetString()); // any text: it is not a header
        Assert.Equal(4, body.GetProperty("priority").GetInt32());
        Assert.Equal("https://hub.example.test/github", body.GetProperty("click").GetString());
        var action = Assert.Single(body.GetProperty("actions").EnumerateArray());
        Assert.Equal(("view", "Open on GitHub", "https://github.com/acme/widget/pull/7"), (action.GetProperty("action").GetString(), action.GetProperty("label").GetString(), action.GetProperty("url").GetString()));

        await sender.SendAsync(AlertChannels.Ntfy, Message(AlertSeverity.Info, external: null) with { Body = "" }, AllChannels());
        Assert.Equal(2, handler.Requests[1].Body.GetProperty("priority").GetInt32());
        Assert.Equal("CI failing: acme/widget#7", handler.Requests[1].Body.GetProperty("message").GetString()); // ntfy wants a message
        Assert.False(handler.Requests[1].Body.TryGetProperty("actions", out _));
    }

    [Fact]
    public async Task Home_assistant_gets_the_alert_at_its_webhook_trigger()
    {
        var handler = new Recorder();

        var result = await new AlertSender(new HttpClient(handler)).SendAsync(AlertChannels.HomeAssistant, Message(), AllChannels());

        Assert.True(result.Ok, result.Error);
        var request = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, $"http://ha.example.test:8123/api/webhook/{HookId}", (string?)null, "application/json"), (request.Method, request.Url, request.Auth, request.ContentType));

        // What an automation reads as trigger.json.*
        Assert.Equal("CI failing: acme/widget#7", request.Body.GetProperty("title").GetString());
        Assert.Equal("Größe ändern (feature/ü)", request.Body.GetProperty("message").GetString());
        Assert.Equal("ci-failed", request.Body.GetProperty("trigger").GetString());
        Assert.Equal("high", request.Body.GetProperty("severity").GetString());
        Assert.Equal("https://hub.example.test/github", request.Body.GetProperty("url").GetString());
    }

    [Fact]
    public async Task A_refusal_or_a_dead_server_is_reported_without_anything_secret()
    {
        var options = AllChannels();
        var refused = new AlertSender(new HttpClient(new Recorder(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = $"No webhook {HookId}" })));
        var down = new AlertSender(new HttpClient(new Recorder(request => throw new HttpRequestException($"Connection refused ({request.RequestUri})"))));
        var slow = new AlertSender(new HttpClient(new Recorder(_ => throw new TaskCanceledException("timed out"))));

        foreach (var channel in AlertChannels.All)
        {
            var no = await refused.SendAsync(channel, Message(), options);
            Assert.False(no.Ok);
            Assert.Contains("answered 404", no.Error);

            var dead = await down.SendAsync(channel, Message(), options);
            Assert.False(dead.Ok);
            Assert.StartsWith("Could not reach", dead.Error);
            Assert.Contains("Connection refused", dead.Error);

            var late = await slow.SendAsync(channel, Message(), options);
            Assert.False(late.Ok);
            Assert.Contains("No answer", late.Error);

            foreach (var secret in (string[])[Topic, NtfyToken, HookId, HookUrl, "url-s3cret", HookToken])
            {
                Assert.DoesNotContain(secret, no.Error + dead.Error + late.Error);
            }
        }
    }

    [Fact]
    public async Task A_channel_that_is_not_set_up_says_what_is_missing_and_sends_nothing()
    {
        var handler = new Recorder();
        var sender = new AlertSender(new HttpClient(handler));
        var empty = new AlertOptions().Normalize();

        Assert.Equal("No URL is set.", (await sender.SendAsync(AlertChannels.Webhook, Message(), empty)).Error);
        Assert.Equal("No topic is set.", (await sender.SendAsync(AlertChannels.Ntfy, Message(), empty)).Error);
        Assert.Equal("No Home Assistant address is set.", (await sender.SendAsync(AlertChannels.HomeAssistant, Message(), empty)).Error);
        Assert.Empty(handler.Requests);

        var odd = new AlertOptions { Channels = { Webhook = { Url = "file:///etc/passwd" }, Ntfy = { Server = "ntfy.sh", Topic = "t" }, HomeAssistant = { Url = "http://ha.example.test:8123" } } }.Normalize();
        Assert.Contains("not an http or https address", AlertChannels.WhyNotReady(AlertChannels.Webhook, odd));
        Assert.Contains("not an http or https address", AlertChannels.WhyNotReady(AlertChannels.Ntfy, odd));
        Assert.Equal("No webhook ID is set.", AlertChannels.WhyNotReady(AlertChannels.HomeAssistant, odd));

        // Ready is not the same as switched on: only both make a channel active.
        var ready = AllChannels();
        ready.Channels.Ntfy.Enabled = false;
        Assert.Equal([AlertChannels.Webhook, AlertChannels.HomeAssistant], AlertChannels.Active(ready));
        Assert.Empty(AlertChannels.Active(empty));
    }

    // ---- the hub --------------------------------------------------------------------------------

    /// <summary>A hub with ntfy switched on, a public address, UTC, and only the at-risk trigger unless a test says otherwise.</summary>
    private static (HubFactory Factory, Clock Clock) Hub(params (string Key, string? Value)[] settings)
    {
        var clock = new Clock(Start);
        var factory = new HubFactory { Time = clock };
        var defaults = new Dictionary<string, string?>
        {
            ["Hub:PublicUrl"] = "https://hub.example.test",
            ["Hub:Alerts:TimeZone"] = "UTC",
            ["Hub:Alerts:Channels:Ntfy:Enabled"] = "true",
            ["Hub:Alerts:Channels:Ntfy:Server"] = "https://ntfy.example.test",
            ["Hub:Alerts:Channels:Ntfy:Topic"] = Topic,
            ["Hub:Alerts:Triggers:MachineOffline:Enabled"] = "false",
        };
        foreach (var (key, value) in defaults.Concat(settings.ToDictionary(s => s.Key, s => s.Value)))
        {
            factory.Settings[key] = value;
        }

        return (factory, clock);
    }

    private static AlertService Service(HubFactory factory) => factory.Services.GetRequiredService<AlertService>();

    private static Task IngestAsync(HubFactory factory, string machine, params DevEntry[] entries)
    {
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        return factory.Services.GetRequiredService<SnapshotStore>().IngestAsync(AlertEvaluationTests.Snapshot(machine, now, entries));
    }

    private static DevEntry Stale(string name, DateTimeOffset now) => AlertEvaluationTests.Repo(name, now, unpushedDays: 5);

    private static List<JsonElement> Sent(HubFactory factory)
    {
        lock (factory.AlertRequests)
        {
            return factory.AlertRequests.Select(r => JsonDocument.Parse(r.Body).RootElement).ToList();
        }
    }

    private static async Task<List<AlertDeliveryEntity>> LogAsync(HubFactory factory) => [.. await Service(factory).DeliveriesAsync()];

    [Fact]
    public async Task A_condition_alerts_once_then_again_only_after_it_clears_and_returns()
    {
        var (factory, clock) = Hub();
        using var _ = factory;
        await IngestAsync(factory, "laptop", Stale("widget", clock.Now));

        var first = await Service(factory).EvaluateAsync();

        Assert.Equal((1, 1, 0), (first.Active, first.Sent, first.Failed));
        var message = Assert.Single(Sent(factory));
        Assert.Equal(Topic, message.GetProperty("topic").GetString());
        Assert.Equal("widget on laptop-host: 2 commits on feature/x not on any remote, oldest 5 days", message.GetProperty("title").GetString());
        Assert.Equal("https://hub.example.test/safety", message.GetProperty("click").GetString());
        Assert.Equal("https://ntfy.example.test/", factory.AlertRequests[0].Url);

        // Still true an hour later, and after another snapshot of the same thing: said once.
        clock.Now = Start.AddHours(1);
        await IngestAsync(factory, "laptop", Stale("widget", Start));
        Assert.Equal(0, (await Service(factory).EvaluateAsync()).Sent);
        clock.Now = Start.AddDays(3);
        Assert.Equal(0, (await Service(factory).EvaluateAsync()).Sent);
        Assert.Single(Sent(factory));
        Assert.Single(await Service(factory).OpenAsync());

        // Pushed: it clears, quietly.
        await IngestAsync(factory, "laptop", AlertEvaluationTests.Repo("widget", clock.Now));
        var cleared = await Service(factory).EvaluateAsync();
        Assert.Equal((0, 0, 1), (cleared.Active, cleared.Sent, cleared.Cleared));
        Assert.Empty(await Service(factory).OpenAsync());
        Assert.Single(Sent(factory));

        // The same thing again later is a new alert.
        clock.Now = Start.AddDays(20);
        await IngestAsync(factory, "laptop", Stale("widget", clock.Now));
        Assert.Equal(1, (await Service(factory).EvaluateAsync()).Sent);
        Assert.Equal(2, Sent(factory).Count);

        var log = await LogAsync(factory);
        Assert.Equal(2, log.Count);
        Assert.All(log, d => Assert.True(d.Ok && d.Channel == AlertChannels.Ntfy && d.Trigger == AlertTriggers.AtRisk && !d.IsTest));
    }

    [Fact]
    public async Task With_a_renotify_interval_it_is_said_again_while_it_lasts()
    {
        var (factory, clock) = Hub(("Hub:Alerts:RenotifyHours", "24"));
        using var _ = factory;
        await IngestAsync(factory, "laptop", Stale("widget", clock.Now));

        Assert.Equal(1, (await Service(factory).EvaluateAsync()).Sent);
        clock.Now = Start.AddHours(23);
        Assert.Equal(0, (await Service(factory).EvaluateAsync()).Sent);
        clock.Now = Start.AddHours(24);
        Assert.Equal(1, (await Service(factory).EvaluateAsync()).Sent);
        clock.Now = Start.AddHours(30);
        Assert.Equal(0, (await Service(factory).EvaluateAsync()).Sent);
        Assert.Equal(2, Sent(factory).Count);
    }

    [Fact]
    public async Task Quiet_hours_hold_an_alert_until_they_end_and_drop_it_if_it_cleared_meanwhile()
    {
        var (factory, clock) = Hub(("Hub:Alerts:QuietHours:Enabled", "true"), ("Hub:Alerts:QuietHours:Start", "22:00"), ("Hub:Alerts:QuietHours:End", "07:00"));
        using var _ = factory;
        clock.Now = new DateTimeOffset(2026, 11, 20, 23, 0, 0, TimeSpan.Zero);
        await IngestAsync(factory, "laptop", Stale("widget", clock.Now), Stale("gadget", clock.Now));

        var night = await Service(factory).EvaluateAsync();
        Assert.Equal((2, 0, 2), (night.Active, night.Sent, night.Held));
        Assert.Empty(Sent(factory));
        Assert.All(await Service(factory).OpenAsync(), s => Assert.Null(s.LastNotifiedAt));

        // gadget is pushed at 3am: nobody needs waking, or telling in the morning.
        clock.Now = new DateTimeOffset(2026, 11, 21, 3, 0, 0, TimeSpan.Zero);
        await IngestAsync(factory, "laptop", Stale("widget", clock.Now), AlertEvaluationTests.Repo("gadget", clock.Now));
        Assert.Equal(0, (await Service(factory).EvaluateAsync()).Sent);
        clock.Now = new DateTimeOffset(2026, 11, 21, 6, 59, 0, TimeSpan.Zero);
        Assert.Equal(0, (await Service(factory).EvaluateAsync()).Sent);

        clock.Now = new DateTimeOffset(2026, 11, 21, 7, 0, 0, TimeSpan.Zero);
        Assert.Equal(1, (await Service(factory).EvaluateAsync()).Sent);
        Assert.StartsWith("widget on laptop-host", Assert.Single(Sent(factory)).GetProperty("title").GetString());
        Assert.Equal(0, (await Service(factory).EvaluateAsync()).Sent);
    }

    [Fact]
    public async Task Many_new_alerts_at_once_go_out_as_one_summary_and_none_of_them_again()
    {
        var (factory, clock) = Hub(("Hub:Alerts:MaxPerEvaluation", "3"));
        using var _ = factory;
        await IngestAsync(factory, "laptop", [.. Enumerable.Range(1, 5).Select(i => Stale($"repo{i}", clock.Now))]);

        var run = await Service(factory).EvaluateAsync();

        Assert.Equal((5, 5), (run.Active, run.Sent));
        var summary = Assert.Single(Sent(factory));
        Assert.Equal("5 new alerts", summary.GetProperty("title").GetString());
        Assert.Contains("• repo3 on laptop-host", summary.GetProperty("message").GetString());
        Assert.Equal("https://hub.example.test/alerts", summary.GetProperty("click").GetString());
        Assert.Equal(5, (await Service(factory).OpenAsync()).Count(s => s.LastNotifiedAt is not null));

        // Up to the limit they go one by one.
        await IngestAsync(factory, "desktop", Stale("a", clock.Now), Stale("b", clock.Now), Stale("c", clock.Now));
        Assert.Equal(3, (await Service(factory).EvaluateAsync()).Sent);
        Assert.Equal(4, Sent(factory).Count);
        Assert.Equal(0, (await Service(factory).EvaluateAsync()).Sent);
    }

    [Fact]
    public async Task Every_channel_that_is_on_gets_the_alert_and_one_failing_does_not_stop_the_others()
    {
        var (factory, clock) = Hub(
            ("Hub:Alerts:Channels:Ntfy:Token", NtfyToken),
            ("Hub:Alerts:Channels:Webhook:Enabled", "true"), ("Hub:Alerts:Channels:Webhook:Url", HookUrl), ("Hub:Alerts:Channels:Webhook:Token", HookToken),
            ("Hub:Alerts:Channels:HomeAssistant:Enabled", "true"), ("Hub:Alerts:Channels:HomeAssistant:Url", $"http://ha.example.test:8123/api/webhook/{HookId}"));
        using var _ = factory;
        factory.AlertResponder = (request, _) => new HttpResponseMessage(request.RequestUri!.Host == "hooks.example.test" ? HttpStatusCode.BadGateway : HttpStatusCode.OK);
        await IngestAsync(factory, "laptop", Stale("widget", clock.Now));

        var run = await Service(factory).EvaluateAsync();

        Assert.Equal((1, 0), (run.Sent, run.Failed)); // it got through somewhere, so it is not sent again
        Assert.Equal([HookUrl, "https://ntfy.example.test/", $"http://ha.example.test:8123/api/webhook/{HookId}"], factory.AlertRequests.Select(r => r.Url));
        Assert.Equal(["Bearer " + HookToken, "Bearer " + NtfyToken, null], factory.AlertRequests.Select(r => r.Auth));
        Assert.Equal(0, (await Service(factory).EvaluateAsync()).Sent);
        Assert.Equal(3, factory.AlertRequests.Count);

        var log = await LogAsync(factory);
        Assert.Equal(3, log.Count);
        var failure = Assert.Single(log, d => !d.Ok);
        Assert.Equal((AlertChannels.Webhook, "Webhook answered 502 Bad Gateway"), (failure.Channel, failure.Error));
    }

    [Fact]
    public async Task An_alert_that_reaches_no_channel_is_logged_without_secrets_and_tried_again_later()
    {
        var (factory, clock) = Hub();
        using var _ = factory;
        factory.AlertResponder = (_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden) { ReasonPhrase = $"topic {Topic} is reserved" };
        await IngestAsync(factory, "laptop", Stale("widget", clock.Now));

        var run = await Service(factory).EvaluateAsync();
        Assert.Equal((0, 1), (run.Sent, run.Failed));
        var failure = Assert.Single(await LogAsync(factory));
        Assert.False(failure.Ok);
        Assert.Equal("ntfy answered 403 topic ••• is reserved", failure.Error);
        var open = Assert.Single(await Service(factory).OpenAsync());
        Assert.Equal((null, 1), (open.LastNotifiedAt, open.Attempts));

        // Not hammered: the next look half a minute later leaves it alone.
        clock.Now = Start.AddSeconds(30);
        Assert.Equal((0, 0), ((await Service(factory).EvaluateAsync()).Sent, (await Service(factory).EvaluateAsync()).Failed));
        Assert.Single(factory.AlertRequests);

        // The server is back a minute on: it goes out, once.
        factory.AlertResponder = null;
        clock.Now = Start.AddMinutes(1);
        Assert.Equal(1, (await Service(factory).EvaluateAsync()).Sent);
        Assert.Equal(0, (await Service(factory).EvaluateAsync()).Sent);
        Assert.Equal(2, factory.AlertRequests.Count);
        Assert.Equal([true, false], (await LogAsync(factory)).Select(d => d.Ok)); // newest first

        var html = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/alerts"));
        Assert.Contains("ntfy answered 403 topic ••• is reserved", html);
        Assert.Contains("1 failed", html);
        Assert.DoesNotContain(Topic, html);
    }

    [Fact]
    public async Task With_no_channel_alerts_wait_and_go_out_when_one_is_saved_on_the_page()
    {
        var (factory, clock) = Hub(("Hub:Alerts:Channels:Ntfy:Enabled", "false"), ("Hub:Alerts:Channels:Ntfy:Topic", ""));
        using var _ = factory;
        await IngestAsync(factory, "laptop", Stale("widget", clock.Now));

        var waiting = await Service(factory).EvaluateAsync();
        Assert.Equal((1, 0, 1), (waiting.Active, waiting.Sent, waiting.Held));
        Assert.Empty(factory.AlertRequests);
        Assert.Null(Assert.Single(await Service(factory).OpenAsync()).LastNotifiedAt);
        Assert.Contains("No channel is switched on", WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/alerts")));

        var settings = factory.Services.GetRequiredService<AlertSettings>();
        var edit = await settings.GetAsync();
        edit.Channels.HomeAssistant.Enabled = true;
        edit.Channels.HomeAssistant.Url = "http://ha.example.test:8123";
        await settings.SaveAsync(edit, new Dictionary<string, string?> { [AlertSettings.HomeAssistantWebhookId] = HookId }, "me");

        Assert.Equal(1, (await Service(factory).EvaluateAsync()).Sent);
        Assert.Equal($"http://ha.example.test:8123/api/webhook/{HookId}", Assert.Single(factory.AlertRequests).Url);
    }

    [Fact]
    public async Task Alerts_switched_off_check_and_send_nothing()
    {
        var (factory, clock) = Hub(("Hub:Alerts:Enabled", "false"));
        using var _ = factory;
        await IngestAsync(factory, "laptop", Stale("widget", clock.Now));

        var run = await Service(factory).EvaluateAsync();

        Assert.False(run.Ran);
        Assert.Empty(factory.AlertRequests);
        Assert.Empty(await Service(factory).OpenAsync());
        Assert.Contains("Alerts are switched off", WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/alerts")));
    }

    [Fact]
    public async Task A_machine_that_stops_reporting_alerts_once_and_clears_when_it_is_back()
    {
        var (factory, clock) = Hub(("Hub:Alerts:Triggers:MachineOffline:Enabled", "true"), ("Hub:Alerts:Triggers:MachineOffline:Minutes", "90"));
        using var _ = factory;
        var service = Service(factory); // the hub is "up" from here
        await IngestAsync(factory, "laptop");
        await IngestAsync(factory, "desktop");

        clock.Now = Start.AddMinutes(89);
        await IngestAsync(factory, "desktop");
        Assert.Equal(0, (await service.EvaluateAsync()).Sent);

        clock.Now = Start.AddMinutes(91);
        Assert.Equal(1, (await service.EvaluateAsync()).Sent);
        var message = Assert.Single(Sent(factory));
        Assert.Equal("laptop-host is offline", message.GetProperty("title").GetString());
        Assert.Equal("https://hub.example.test/machines#machine-laptop", message.GetProperty("click").GetString());

        clock.Now = Start.AddHours(10);
        await IngestAsync(factory, "desktop");
        Assert.Equal(0, (await service.EvaluateAsync()).Sent);

        await IngestAsync(factory, "laptop");
        Assert.Equal(1, (await service.EvaluateAsync()).Cleared);
        Assert.Empty(await service.OpenAsync());
    }

    [Fact]
    public async Task A_branch_that_diverges_across_machines_alerts_with_a_link_to_the_repo()
    {
        var (factory, clock) = Hub(("Hub:Alerts:Triggers:AtRisk:Enabled", "false"));
        using var _ = factory;
        DevEntry Ahead(string sha) => AlertEvaluationTests.Repo("widget", clock.Now) is var repo
            ? repo with { Git = repo.Git! with { HeadSha = sha, Ahead = 2 } }
            : throw new InvalidOperationException();
        await IngestAsync(factory, "laptop", Ahead("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        await IngestAsync(factory, "desktop", Ahead("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        Assert.Equal(1, (await Service(factory).EvaluateAsync()).Sent);

        var message = Assert.Single(Sent(factory));
        Assert.Equal("widget: main diverged across 2 machines", message.GetProperty("title").GetString());
        Assert.Equal("https://hub.example.test/repo?key=github.com%2Facme%2Fwidget", message.GetProperty("click").GetString());
        Assert.Equal(4, message.GetProperty("priority").GetInt32());

        // One machine catches up: cleared.
        await IngestAsync(factory, "desktop", Ahead("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        Assert.Equal(1, (await Service(factory).EvaluateAsync()).Cleared);
    }

    [Fact]
    public async Task Ci_turning_red_alerts_and_turning_red_again_after_a_fix_alerts_again()
    {
        var (factory, _) = Hub();
        using var __ = factory;
        var github = factory.Services.GetRequiredService<DevDash.Hub.GitHub.GitHubStore>();
        Task SetAsync(DevDash.Core.GitHub.CiState ci) => github.SetAsync(new DevDash.Core.GitHub.GitHubSnapshot
        {
            ViewerLogin = "me",
            PullRequests = [new() { Repo = "acme/widget", Number = 7, Title = "Add login", HeadRef = "feature/login", Url = "https://github.com/acme/widget/pull/7", Authored = true, Ci = ci }],
        });

        await SetAsync(DevDash.Core.GitHub.CiState.Pending);
        Assert.Equal(0, (await Service(factory).EvaluateAsync()).Sent);

        await SetAsync(DevDash.Core.GitHub.CiState.Failure);
        Assert.Equal(1, (await Service(factory).EvaluateAsync()).Sent);
        Assert.Equal(0, (await Service(factory).EvaluateAsync()).Sent);
        var message = Assert.Single(Sent(factory));
        Assert.Equal("CI failing: acme/widget#7", message.GetProperty("title").GetString());
        Assert.Equal("https://hub.example.test/github", message.GetProperty("click").GetString());
        Assert.Equal("https://github.com/acme/widget/pull/7", message.GetProperty("actions")[0].GetProperty("url").GetString());

        await SetAsync(DevDash.Core.GitHub.CiState.Pending); // a new push
        Assert.Equal(1, (await Service(factory).EvaluateAsync()).Cleared);
        await SetAsync(DevDash.Core.GitHub.CiState.Failure);
        Assert.Equal(1, (await Service(factory).EvaluateAsync()).Sent);
        Assert.Equal(2, Sent(factory).Count);
    }

    [Fact]
    public async Task A_failed_action_alerts_once_and_an_old_failure_not_at_all()
    {
        var (factory, clock) = Hub();
        using var _ = factory;
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<DevDashDbContext>>().CreateDbContextAsync())
        {
            db.AgentActions.Add(new AgentActionEntity { MachineId = "mac", MachineName = "mac-host", Kind = AgentActionKind.Push, CommandLine = "git push origin main", Status = AgentActionStatus.Failed, CompletedAt = clock.Now.AddMinutes(-5), Output = "rejected" });
            db.AgentActions.Add(new AgentActionEntity { MachineId = "mac", MachineName = "mac-host", Kind = AgentActionKind.Fetch, CommandLine = "git fetch --prune", Status = AgentActionStatus.Failed, CompletedAt = clock.Now.AddDays(-2), Output = "from before alerts were on" });
            db.AgentActions.Add(new AgentActionEntity { MachineId = "mac", MachineName = "mac-host", Kind = AgentActionKind.Fetch, CommandLine = "git fetch --prune", Status = AgentActionStatus.Succeeded, CompletedAt = clock.Now });
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, (await Service(factory).EvaluateAsync()).Sent);
        var message = Assert.Single(Sent(factory));
        Assert.Equal("Action failed on mac-host: git push origin main", message.GetProperty("title").GetString());
        Assert.Equal("https://hub.example.test/machines", message.GetProperty("click").GetString());

        // Long after it has left the two-hour window it is still remembered, so it is never sent twice,
        // and it stops being listed as open after a day.
        clock.Now = Start.AddHours(1);
        Assert.Equal(0, (await Service(factory).EvaluateAsync()).Sent);
        Assert.Single(await Service(factory).OpenAsync());
        clock.Now = Start.AddDays(2);
        var later = await Service(factory).EvaluateAsync();
        Assert.Equal((0, 0), (later.Sent, later.Cleared));
        Assert.Empty(await Service(factory).OpenAsync());
        Assert.Single(Sent(factory));
    }

    [Fact]
    public async Task The_daily_digest_goes_out_once_a_day_at_its_time()
    {
        var (factory, clock) = Hub(("Hub:Alerts:Triggers:DailyDigest:Enabled", "true"), ("Hub:Alerts:Triggers:DailyDigest:Time", "08:00"), ("Hub:Alerts:Triggers:AtRisk:Enabled", "false"));
        using var _ = factory;
        clock.Now = new DateTimeOffset(2026, 11, 20, 7, 30, 0, TimeSpan.Zero);
        await IngestAsync(factory, "laptop", Stale("widget", clock.Now), AlertEvaluationTests.Repo("clean", clock.Now) with { Size = new SizeInfo { TotalBytes = 5 * 1024 * 1024, MeasuredAt = clock.Now } });

        await Service(factory).EvaluateAsync();
        Assert.Empty(Sent(factory));

        clock.Now = new DateTimeOffset(2026, 11, 20, 8, 2, 0, TimeSpan.Zero);
        await Service(factory).EvaluateAsync();
        await Service(factory).EvaluateAsync();
        var digest = Assert.Single(Sent(factory));
        Assert.Equal("DevDash daily digest", digest.GetProperty("title").GetString());
        Assert.Equal("1 repo at risk, 1 safe to remove, 5 MB.", digest.GetProperty("message").GetString());
        Assert.Equal(2, digest.GetProperty("priority").GetInt32());
        Assert.Empty(await Service(factory).OpenAsync()); // its bookkeeping is not an open alert

        clock.Now = new DateTimeOffset(2026, 11, 20, 23, 0, 0, TimeSpan.Zero);
        await Service(factory).EvaluateAsync();
        Assert.Single(Sent(factory));

        clock.Now = new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);
        await Service(factory).EvaluateAsync();
        Assert.Equal(2, Sent(factory).Count);
        Assert.All(await LogAsync(factory), d => Assert.Equal(AlertTriggers.Digest, d.Trigger));
    }

    // ---- settings and the page ------------------------------------------------------------------

    [Fact]
    public async Task What_is_saved_on_the_page_overrides_configuration_and_only_what_differs_is_stored()
    {
        var (factory, _) = Hub();
        using var __ = factory;
        var settings = factory.Services.GetRequiredService<AlertSettings>();

        var edit = await settings.GetAsync();
        Assert.Equal((3, 60, "https://ntfy.example.test", true), (edit.Triggers.AtRisk.Days, edit.Triggers.MachineOffline.Minutes, edit.Channels.Ntfy.Server, edit.Channels.Ntfy.Enabled));
        edit.Triggers.AtRisk.Days = 7;
        edit.Triggers.CiFailed.Enabled = false;
        edit.QuietHours.Enabled = true;
        edit.QuietHours.Start = "23:15";
        await settings.SaveAsync(edit, new Dictionary<string, string?>(), "me@example.test");

        var saved = await settings.GetAsync();
        Assert.Equal((7, false, true, "23:15"), (saved.Triggers.AtRisk.Days, saved.Triggers.CiFailed.Enabled, saved.QuietHours.Enabled, saved.QuietHours.Start));
        Assert.Equal(60, saved.Triggers.MachineOffline.Minutes);
        Assert.Equal(4, await settings.OverrideCountAsync());
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<DevDashDbContext>>().CreateDbContextAsync())
        {
            var rows = await db.AlertSettings.OrderBy(r => r.Key).ToListAsync();
            Assert.Equal(["QuietHours:Enabled", "QuietHours:Start", "Triggers:AtRisk:Days", "Triggers:CiFailed:Enabled"], rows.Select(r => r.Key));
            Assert.All(rows, r => Assert.Equal(("me@example.test", false), (r.UpdatedBy, r.IsSecret)));
        }

        // Setting a value back to what configuration says drops the override, so it follows configuration again.
        saved.Triggers.AtRisk.Days = 3;
        await settings.SaveAsync(saved, new Dictionary<string, string?>(), "me@example.test");
        Assert.Equal(3, await settings.OverrideCountAsync());

        await settings.ResetAsync("me@example.test");
        Assert.Equal(0, await settings.OverrideCountAsync());
        Assert.True((await settings.GetAsync()).Triggers.CiFailed.Enabled);
        Assert.Equal(Topic, (await settings.GetAsync()).Channels.Ntfy.Topic); // configuration's own values were never touched
    }

    [Fact]
    public async Task Secrets_are_write_only_stored_encrypted_and_never_reach_the_page()
    {
        var (factory, _) = Hub(("Hub:Alerts:Channels:Ntfy:Topic", ""));
        using var __ = factory;
        var settings = factory.Services.GetRequiredService<AlertSettings>();
        Assert.All((await settings.SecretsAsync()).Values, s => Assert.False(s.Set));

        var edit = await settings.GetAsync();
        edit.Channels.Webhook.Enabled = true;
        await settings.SaveAsync(edit, new Dictionary<string, string?>
        {
            [AlertSettings.NtfyTopic] = Topic,
            [AlertSettings.NtfyToken] = NtfyToken,
            [AlertSettings.WebhookUrl] = HookUrl,
            [AlertSettings.WebhookToken] = HookToken,
            [AlertSettings.HomeAssistantWebhookId] = HookId,
        }, "me");

        // In force…
        var saved = await settings.GetAsync();
        Assert.Equal((Topic, NtfyToken, HookUrl, HookToken, HookId), (saved.Channels.Ntfy.Topic, saved.Channels.Ntfy.Token, saved.Channels.Webhook.Url, saved.Channels.Webhook.Token, saved.Channels.HomeAssistant.WebhookId));
        Assert.All((await settings.SecretsAsync()).Values, s => Assert.Equal((true, true, "set"), (s.Set, s.SavedHere, s.Text)));

        // …but not readable in the database…
        string[] secrets = [Topic, NtfyToken, HookUrl, "url-s3cret", HookToken, HookId];
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<DevDashDbContext>>().CreateDbContextAsync())
        {
            var rows = await db.AlertSettings.Where(r => r.IsSecret).ToListAsync();
            Assert.Equal(5, rows.Count);
            Assert.All(rows, row => Assert.All(secrets, secret => Assert.DoesNotContain(secret, row.Value)));
        }

        // …or anywhere on the page, which only says that they are set.
        var html = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/alerts"));
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, html));
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(html, @"data-secret=""[^""]+"">set<").Count);
        Assert.Contains("type=\"password\"", html);

        // A save that says nothing about a secret keeps it; an empty one removes it.
        await settings.SaveAsync(await settings.GetAsync(), new Dictionary<string, string?>(), "me");
        Assert.Equal(NtfyToken, (await settings.GetAsync()).Channels.Ntfy.Token);
        await settings.SaveAsync(await settings.GetAsync(), new Dictionary<string, string?> { [AlertSettings.NtfyToken] = null }, "me");
        Assert.Equal("", (await settings.GetAsync()).Channels.Ntfy.Token);
        Assert.Equal("not set", (await settings.SecretsAsync())[AlertSettings.NtfyToken].Text);
    }

    [Fact]
    public async Task A_secret_from_configuration_shows_as_set_there_and_a_saved_one_wins_over_it()
    {
        var (factory, _) = Hub(("Hub:Alerts:Channels:HomeAssistant:Url", $"http://ha.example.test:8123/api/webhook/{HookId}"));
        using var __ = factory;
        var settings = factory.Services.GetRequiredService<AlertSettings>();

        // A whole webhook address in configuration is split, so the id is never shown as part of the address.
        var configured = await settings.GetAsync();
        Assert.Equal(("http://ha.example.test:8123", HookId), (configured.Channels.HomeAssistant.Url, configured.Channels.HomeAssistant.WebhookId));
        var states = await settings.SecretsAsync();
        Assert.Equal("set in configuration", states[AlertSettings.NtfyTopic].Text);
        Assert.Equal("set in configuration", states[AlertSettings.HomeAssistantWebhookId].Text);
        Assert.Equal("not set", states[AlertSettings.WebhookUrl].Text);
        var html = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/alerts"));
        Assert.Contains("http://ha.example.test:8123", html);
        Assert.DoesNotContain(HookId, html);
        Assert.DoesNotContain(Topic, html);

        // Saving the form as it is stores nothing: the address still matches configuration.
        await settings.SaveAsync(await settings.GetAsync(), new Dictionary<string, string?>(), "me");
        Assert.Equal(0, await settings.OverrideCountAsync());

        // Pasting a whole webhook address into the address box keeps its id out of the plain settings.
        var edit = await settings.GetAsync();
        edit.Channels.HomeAssistant.Url = "http://other.example.test:8123/api/webhook/another-s3cret-id";
        await settings.SaveAsync(edit, new Dictionary<string, string?>(), "me");
        var saved = await settings.GetAsync();
        Assert.Equal(("http://other.example.test:8123", "another-s3cret-id"), (saved.Channels.HomeAssistant.Url, saved.Channels.HomeAssistant.WebhookId));
        Assert.Equal("set", (await settings.SecretsAsync())[AlertSettings.HomeAssistantWebhookId].Text);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<DevDashDbContext>>().CreateDbContextAsync();
        Assert.All(await db.AlertSettings.ToListAsync(), row => Assert.DoesNotContain("another-s3cret-id", row.Value));
    }

    [Fact]
    public async Task Send_test_posts_one_message_to_that_channel_and_logs_it()
    {
        var (factory, _) = Hub(("Hub:Alerts:Channels:Ntfy:Enabled", "false")); // set up but switched off: a test still goes
        using var __ = factory;

        var ok = await Service(factory).SendTestAsync(AlertChannels.Ntfy);

        Assert.True(ok.Ok, ok.Error);
        var message = Assert.Single(Sent(factory));
        Assert.Equal("DevDash test alert", message.GetProperty("title").GetString());
        Assert.Equal("https://hub.example.test/alerts", message.GetProperty("click").GetString());

        var missing = await Service(factory).SendTestAsync(AlertChannels.HomeAssistant);
        Assert.Equal((false, "No Home Assistant address is set."), (missing.Ok, missing.Error));
        Assert.Single(factory.AlertRequests);

        var log = await LogAsync(factory);
        Assert.Equal([(AlertChannels.HomeAssistant, false), (AlertChannels.Ntfy, true)], log.Select(d => (d.Channel, d.Ok)));
        Assert.All(log, d => Assert.True(d.IsTest));
        Assert.Empty(await Service(factory).OpenAsync());
    }

    [Fact]
    public async Task The_page_lists_what_is_open_the_channels_the_triggers_and_the_log()
    {
        var (factory, clock) = Hub();
        using var _ = factory;
        await IngestAsync(factory, "laptop", Stale("widget", clock.Now));
        await Service(factory).EvaluateAsync();

        var html = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/alerts"));

        Assert.Contains("<h1>Alerts</h1>", html);
        Assert.Matches(@"Open now <span class=""muted small"">1</span>", html);
        Assert.Contains("<a class=\"title-link\" href=\"safety\">widget on laptop-host: 2 commits on feature/x not on any remote, oldest 5 days</a>", html);
        foreach (var text in (string[])["Webhook", "ntfy", "Home Assistant", "Send test", "A machine goes offline", "CI turns red on one of my pull requests", "A daily digest", "Quiet hours", "Delivery log", "Save"])
        {
            Assert.Contains(text, html);
        }

        Assert.DoesNotContain("Alerts carry no link back", html); // Hub:PublicUrl is set
        Assert.DoesNotContain("No channel is switched on", html);

        // And the navigation counts it.
        Assert.Matches(@"href=""alerts""[^>]*>\s*Alerts\s*<span class=""nav-count""[^>]*>1</span>", html);
    }

    [Fact]
    public async Task Without_a_public_address_alerts_carry_no_link_and_the_page_says_so()
    {
        var (factory, clock) = Hub(("Hub:PublicUrl", ""));
        using var _ = factory;
        await IngestAsync(factory, "laptop", Stale("widget", clock.Now));

        await Service(factory).EvaluateAsync();

        Assert.False(Assert.Single(Sent(factory)).TryGetProperty("click", out var _));
        Assert.Contains("Alerts carry no link back", WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/alerts")));
    }

    [Fact]
    public async Task The_delivery_log_keeps_the_latest_attempts_only()
    {
        var (factory, _) = Hub();
        using var __ = factory;
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<DevDashDbContext>>().CreateDbContextAsync())
        {
            db.AlertDeliveries.AddRange(Enumerable.Range(0, AlertService.KeepDeliveries + 30).Select(i => new AlertDeliveryEntity { At = Start, Channel = AlertChannels.Ntfy, Trigger = AlertTriggers.AtRisk, Title = $"old {i}", Ok = true }));
            await db.SaveChangesAsync();
        }

        await Service(factory).SendTestAsync(AlertChannels.Ntfy);

        await using var check = await factory.Services.GetRequiredService<IDbContextFactory<DevDashDbContext>>().CreateDbContextAsync();
        Assert.Equal(AlertService.KeepDeliveries, await check.AlertDeliveries.CountAsync());
        Assert.Equal("DevDash test alert", (await Service(factory).DeliveriesAsync(1))[0].Title);
    }

    [Fact]
    public async Task A_database_from_before_alerts_gains_their_tables()
    {
        var connection = $"Data Source=file:devdash-upgrade-{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var keepAlive = new SqliteConnection(connection);
        await keepAlive.OpenAsync();
        var options = new DbContextOptionsBuilder<DevDashDbContext>().UseSqlite(connection).Options;

        await using (var db = new DevDashDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            foreach (var table in (string[])["AlertSettings", "AlertStates", "AlertDeliveries"])
            {
#pragma warning disable EF1002 // fixed table names
                await db.Database.ExecuteSqlRawAsync($"DROP TABLE \"{table}\""); // what a 0.6.0 hub's database looks like
#pragma warning restore EF1002
            }
        }

        await using (var db = new DevDashDbContext(options))
        {
            var created = await SchemaUpgrade.CreateMissingTablesAsync(db);
            Assert.Equal(["AlertDeliveries", "AlertSettings", "AlertStates"], created.Order());

            db.AlertStates.Add(new AlertStateEntity { Key = "at-risk:abc", Trigger = AlertTriggers.AtRisk, FirstSeenAt = Start, LastSeenAt = Start });
            db.AlertSettings.Add(new AlertSettingEntity { Key = "Enabled", Value = "False" });
            db.AlertDeliveries.Add(new AlertDeliveryEntity { At = Start, Channel = AlertChannels.Ntfy });
            await db.SaveChangesAsync();
            Assert.Empty(await SchemaUpgrade.CreateMissingTablesAsync(db));
        }
    }

    // ---- the background worker ------------------------------------------------------------------

    [Fact]
    public async Task The_worker_looks_at_start_and_again_soon_after_something_changes_but_not_for_its_own_news()
    {
        var (factory, clock) = Hub(("Hub:Alerts:DebounceSeconds", "0"), ("Hub:Alerts:EvaluateIntervalMinutes", "600"));
        using var _ = factory;
        var notifier = factory.Services.GetRequiredService<DashboardNotifier>();
        var worker = new AlertWorker(Service(factory), factory.Services.GetRequiredService<AlertSettings>(), notifier, TimeProvider.System, NullLogger<AlertWorker>.Instance) { StartDelay = TimeSpan.Zero };

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await UntilAsync(() => worker.Runs >= 1);
            Assert.Empty(factory.AlertRequests);

            // A snapshot lands (the store notifies): the worker looks without being asked, and sends.
            await IngestAsync(factory, "laptop", Stale("widget", clock.Now));
            await UntilAsync(() => factory.AlertRequests.Count == 1);
            await UntilAsync(() => worker.Runs >= 2);

            // Sending that alert notified the dashboard; that must not make the worker look again, and again.
            var runs = worker.Runs;
            notifier.Notify(DashboardChange.Alerts);
            await Task.Delay(300);
            Assert.Equal(runs, worker.Runs);

            notifier.Notify(DashboardChange.GitHub);
            await UntilAsync(() => worker.Runs > runs);
            Assert.Single(factory.AlertRequests);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        static async Task UntilAsync(Func<bool> condition)
        {
            for (var i = 0; i < 200 && !condition(); i++)
            {
                await Task.Delay(25);
            }

            Assert.True(condition(), "the worker did not get there in five seconds");
        }
    }
}
