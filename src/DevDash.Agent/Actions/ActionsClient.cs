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

    /// <summary>
    /// Takes the actions queued for this machine. The hub hands each one out once, so anything returned
    /// here must be run (or refused) and reported.
    /// </summary>
    public async Task<IReadOnlyList<PendingAgentAction>> ClaimAsync(CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, ClaimPath);
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
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Hub refused the result for action {actionId}: {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
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
