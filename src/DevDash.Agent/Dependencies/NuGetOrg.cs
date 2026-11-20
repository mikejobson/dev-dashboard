using System.Net;
using System.Text.Json;

namespace DevDash.Agent.Dependencies;

/// <summary>One known vulnerability of a package: the versions it affects, how bad it is, and where it is described.</summary>
/// <param name="Severity">NuGet's scale: 0 low, 1 moderate, 2 high, 3 critical.</param>
public sealed record NuGetAdvisory(NuGetVersionRange Versions, int Severity, string? Url);

/// <summary>Thrown when nuget.org could not be reached or answered with something unusable; the check is tried again later.</summary>
public sealed class NuGetUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// What the NuGet check needs from the network, asked of nuget.org and nothing else: the vulnerability
/// data it publishes (the <c>VulnerabilityInfo/6.7.0</c> resource of its service index) and, for
/// "outdated", the version list of each package (the flat container). Never a feed a repository's
/// <c>nuget.config</c> names, so a repository cannot steer the agent, or the user's feed credentials,
/// anywhere. Only <c>https://*.nuget.org</c> is followed, redirects are not, and every answer is
/// size-capped. The vulnerability data is kept for a few hours, so a run through every repository
/// downloads it once.
/// </summary>
public sealed class NuGetOrg : IDisposable
{
    public const string ServiceIndex = "https://api.nuget.org/v3/index.json";

    private const int MaxIndexBytes = 1024 * 1024;
    private const int MaxPageBytes = 64 * 1024 * 1024;
    private const int MaxVersionsBytes = 4 * 1024 * 1024;
    private const int MaxPages = 8;
    private static readonly TimeSpan Keep = TimeSpan.FromHours(6);

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset At, Uri? FlatContainer, IReadOnlyDictionary<string, List<NuGetAdvisory>> Advisories)? _data;
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<string>? Versions)> _versions = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="handler">Tests hand in a fake; otherwise a handler that never follows redirects.</param>
    public NuGetOrg(HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(15) })
        {
            Timeout = Timeout.InfiniteTimeSpan, // each call has its own
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("devdash-agent");
        _time = time ?? TimeProvider.System;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    /// <summary>True for an address the agent may ask: https on nuget.org.</summary>
    public static bool IsNuGetOrg(Uri? uri) =>
        uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("nuget.org", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".nuget.org", StringComparison.OrdinalIgnoreCase));

    /// <summary>Every advisory nuget.org knows, by lower-cased package id.</summary>
    public async Task<IReadOnlyDictionary<string, List<NuGetAdvisory>>> AdvisoriesAsync(TimeSpan timeout, CancellationToken ct) =>
        (await DataAsync(timeout, ct).ConfigureAwait(false)).Advisories;

    /// <summary>The versions nuget.org has of a package, or null when it has none by that id (a private package, say).</summary>
    public async Task<IReadOnlyList<string>?> VersionsAsync(string id, TimeSpan timeout, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        lock (_versions)
        {
            if (_versions.TryGetValue(id, out var known) && now - known.At < Keep)
            {
                return known.Versions;
            }
        }

        var data = await DataAsync(timeout, ct).ConfigureAwait(false);
        if (data.FlatContainer is null || id.Length > 100 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            return null;
        }

        var bytes = await GetAsync(new Uri(data.FlatContainer, $"{id.ToLowerInvariant()}/index.json"), MaxVersionsBytes, timeout, ct, notFoundIsNull: true).ConfigureAwait(false);
        IReadOnlyList<string>? versions = null;
        if (bytes is not null)
        {
            using var doc = Parse(bytes);
            versions = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("versions", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
                : throw new NuGetUnavailableException("nuget.org's version list could not be read");
        }

        lock (_versions)
        {
            if (_versions.Count > 5000)
            {
                _versions.Clear();
            }

            _versions[id] = (now, versions);
        }

        return versions;
    }

    private async Task<(DateTimeOffset At, Uri? FlatContainer, IReadOnlyDictionary<string, List<NuGetAdvisory>> Advisories)> DataAsync(TimeSpan timeout, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (_data is { } cached && now - cached.At < Keep)
            {
                return cached;
            }

            using var index = Parse((await GetAsync(new Uri(ServiceIndex), MaxIndexBytes, timeout, ct).ConfigureAwait(false))!);
            Uri? vulnerabilities = null, flat = null;
            if (index.RootElement.ValueKind == JsonValueKind.Object && index.RootElement.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
            {
                foreach (var resource in resources.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object))
                {
                    var type = Text(resource, "@type");
                    var id = Text(resource, "@id");
                    if (type == "VulnerabilityInfo/6.7.0" && vulnerabilities is null)
                    {
                        vulnerabilities = Allowed(id);
                    }
                    else if (type == "PackageBaseAddress/3.0.0" && flat is null)
                    {
                        flat = Allowed(id) is { } address ? new Uri(address.ToString().TrimEnd('/') + "/") : null;
                    }
                }
            }

            if (vulnerabilities is null)
            {
                throw new NuGetUnavailableException("nuget.org's service index has no vulnerability data");
            }

            // The index lists pages ("base", then "update"s); each maps a lower-cased id to its advisories.
            using var pages = Parse((await GetAsync(vulnerabilities, MaxIndexBytes, timeout, ct).ConfigureAwait(false))!);
            if (pages.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new NuGetUnavailableException("nuget.org's vulnerability index could not be read");
            }

            var advisories = new Dictionary<string, List<NuGetAdvisory>>(StringComparer.OrdinalIgnoreCase);
            foreach (var page in pages.RootElement.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object).Take(MaxPages))
            {
                if (Allowed(Text(page, "@id")) is not { } address)
                {
                    continue;
                }

                using var content = Parse((await GetAsync(address, MaxPageBytes, timeout, ct).ConfigureAwait(false))!);
                if (content.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var package in content.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array))
                {
                    foreach (var item in package.Value.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
                    {
                        if (NuGetVersionRange.TryParse(Text(item, "versions"), out var range))
                        {
                            var severity = item.TryGetProperty("severity", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var n) ? Math.Clamp(n, 0, 3) : 0;
                            if (!advisories.TryGetValue(package.Name, out var list))
                            {
                                advisories[package.Name] = list = [];
                            }

                            list.Add(new NuGetAdvisory(range, severity, Text(item, "url")));
                        }
                    }
                }
            }

            var data = (now, flat, (IReadOnlyDictionary<string, List<NuGetAdvisory>>)advisories);
            _data = data;
            return data;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Uri? Allowed(string? address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) && IsNuGetOrg(uri) ? uri : null;

    /// <summary>The body, capped at <paramref name="max"/> bytes. Null for a 404 when <paramref name="notFoundIsNull"/>.</summary>
    private async Task<byte[]?> GetAsync(Uri uri, int max, TimeSpan timeout, CancellationToken ct, bool notFoundIsNull = false)
    {
        if (!IsNuGetOrg(uri))
        {
            throw new NuGetUnavailableException("only nuget.org is asked");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new NuGetUnavailableException($"nuget.org answered {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength > max)
            {
                throw new NuGetUnavailableException("nuget.org's answer was larger than expected");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cts.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > max)
                {
                    throw new NuGetUnavailableException("nuget.org's answer was larger than expected");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new NuGetUnavailableException("timed out", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new NuGetUnavailableException(DependencyParsers.Unreachable, ex);
        }
    }

    private static JsonDocument Parse(byte[] bytes)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            throw new NuGetUnavailableException("nuget.org's answer could not be read", ex);
        }
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
