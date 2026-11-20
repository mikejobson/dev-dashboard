using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DevDash.Contracts;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.Actions;

/// <summary>Asks the hub for this machine's queued actions and reports what happened to them.</summary>
public sealed class ActionsClient(HttpClient http, IOptionsMonitor<AgentOptions> options)
{
    public const string ClaimPath = "api/v1/actions/claim";

    /// <summary>How much longer than the wait it asked for the agent gives the hub to answer a poll.</summary>
    private static readonly TimeSpan ClaimSlack = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan ReportTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Takes the actions queued for this machine. The hub hands each one out once, so anything returned
    /// here must be run (or refused) and reported.
    /// </summary>
    /// <param name="waitSeconds">
    /// Above 0, asks the hub to hold the request open that long when nothing is queued, and answer the
    /// moment something is (a long poll). A hub from before 0.5.0 ignores it and answers at once.
    /// </param>
    public async Task<IReadOnlyList<PendingAgentAction>> ClaimAsync(CancellationToken ct, int waitSeconds = 0)
    {
        waitSeconds = Math.Clamp(waitSeconds, 0, AgentActions.MaxClaimWaitSeconds);
        using var request = Request(HttpMethod.Post, waitSeconds > 0 ? $"{ClaimPath}?wait={waitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : ClaimPath);

        // The client itself has no timeout (a long poll outlasts any sensible fixed one): each call sets its own.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(waitSeconds) + ClaimSlack);
        ct = deadline.Token;
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

        // 409: the key has not pushed a snapshot yet, so it is not bound to a machine. 404: a hub from before actions.
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Hub refused the poll for actions: {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var body = await response.Content.ReadFromJsonAsync<ActionClaimResponse>(SnapshotSchema.JsonOptions, ct).ConfigureAwait(false);
        return body?.Actions ?? [];
    }

    public async Task ReportAsync(string actionId, ActionResultRequest result, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, $"api/v1/actions/{Uri.EscapeDataString(actionId)}/result");
        request.Content = JsonContent.Create(result, options: SnapshotSchema.JsonOptions);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ReportTimeout);
        using var response = await http.SendAsync(request, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Hub refused the result for action {actionId}: {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }

    /// <summary>How a long action is getting on. A hub before 0.8.0 has no such endpoint (404), which is not an error.</summary>
    public async Task ProgressAsync(string actionId, string output, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, $"api/v1/actions/{Uri.EscapeDataString(actionId)}/progress");
        request.Content = JsonContent.Create(new ActionProgressRequest { Output = AgentActions.Truncate(output) }, options: SnapshotSchema.JsonOptions);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ReportTimeout);
        using var response = await http.SendAsync(request, deadline.Token).ConfigureAwait(false);
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var current = options.CurrentValue;
        var key = current.ResolveApiKey()
            ?? throw new InvalidOperationException("No API key configured. Set Agent:ApiKey, Agent:ApiKeyFile or pass --api-key.");
        var request = new HttpRequestMessage(method, new Uri(new Uri(current.HubUrl.TrimEnd('/') + "/"), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }
}
