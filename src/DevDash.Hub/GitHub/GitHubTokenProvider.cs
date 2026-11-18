using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.GitHub;

public interface IGitHubTokenProvider
{
    /// <summary>Returns a token, or null when none is configured and <c>gh</c> is not logged in.</summary>
    Task<string?> GetTokenAsync(CancellationToken ct);

    /// <summary>Forget a cached token after GitHub rejects it.</summary>
    void Invalidate();
}

/// <summary>
/// Token lookup order: <c>GitHub:Token</c> setting, the <c>GITHUB_TOKEN</c> environment variable, then
/// <c>gh auth token</c> (local development). The token is only ever used for read-only queries.
/// </summary>
public sealed class GitHubTokenProvider(IOptionsMonitor<GitHubOptions> options, IConfiguration configuration, ILogger<GitHubTokenProvider> logger)
    : IGitHubTokenProvider
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _ghToken;
    private DateTimeOffset _ghTokenAt;

    public async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        var configured = options.CurrentValue.Token;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = configuration["GITHUB_TOKEN"];
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_ghToken is not null && DateTimeOffset.UtcNow - _ghTokenAt < CacheFor)
            {
                return _ghToken;
            }

            _ghToken = await RunGhAsync(ct);
            _ghTokenAt = DateTimeOffset.UtcNow;
            return _ghToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _ghToken = null;

    private async Task<string?> RunGhAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("gh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("token");
            psi.Environment["GH_PROMPT_DISABLED"] = "1";

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            process.StandardInput.Close();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            var token = (await stdout).Trim();
            if (process.ExitCode != 0 || token.Length == 0)
            {
                logger.LogWarning("`gh auth token` failed: {Error}", (await stderr).Trim());
                return null;
            }

            return token;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not get a token from the gh CLI: {Error}", ex.Message);
            return null;
        }
    }
}
