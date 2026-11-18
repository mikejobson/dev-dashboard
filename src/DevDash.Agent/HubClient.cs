using System.Net.Http.Headers;
using System.Net.Http.Json;
using DevDash.Contracts;
using Microsoft.Extensions.Options;

namespace DevDash.Agent;

/// <summary>Pushes snapshots to the hub's ingest endpoint.</summary>
public sealed class HubClient
{
    public const string SnapshotsPath = "api/v1/snapshots";

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<AgentOptions> _options;

    public HubClient(HttpClient http, IOptionsMonitor<AgentOptions> options)
    {
        _http = http;
        _options = options;
    }

    public async Task<IngestResponse> PushAsync(MachineSnapshot snapshot, CancellationToken ct)
    {
        var options = _options.CurrentValue;
        var key = options.ResolveApiKey()
            ?? throw new InvalidOperationException("No API key configured. Set Agent:ApiKey, Agent:ApiKeyFile or pass --api-key.");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.HubUrl.TrimEnd('/') + "/"), SnapshotsPath))
        {
            Content = JsonContent.Create(snapshot, options: SnapshotSchema.JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"Hub rejected the snapshot: {(int)response.StatusCode} {response.ReasonPhrase}. {body}".Trim());
        }

        return await response.Content.ReadFromJsonAsync<IngestResponse>(SnapshotSchema.JsonOptions, ct).ConfigureAwait(false)
            ?? throw new HttpRequestException("Hub returned an empty response.");
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri(new Uri(_options.CurrentValue.HubUrl.TrimEnd('/') + "/"), "healthz"), ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
