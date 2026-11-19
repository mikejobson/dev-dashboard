using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using DevDash.Agent.Scanning;
using DevDash.Agent.Service;
using DevDash.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.Updates;

public enum UpdateStatus
{
    /// <summary>Already on the requested (or latest) version.</summary>
    UpToDate,

    /// <summary><c>--check</c> only: a newer release exists.</summary>
    Available,

    /// <summary>The new version is in place; the agent must be restarted to run it.</summary>
    Installed,

    /// <summary>Declined before anything was downloaded or changed.</summary>
    Refused,

    /// <summary>Something went wrong. The installed version is unchanged.</summary>
    Failed,
}

public sealed record UpdateResult(UpdateStatus Status, string Message, string CurrentVersion, string? TargetVersion = null)
{
    public bool Installed => Status == UpdateStatus.Installed;
}

/// <param name="Version">The release to install; null for the latest one.</param>
/// <param name="CheckOnly">Say what would happen, download nothing but the small signed files.</param>
/// <param name="ReplacePending">
/// Go ahead even though the last update has not been confirmed yet. Only a person at the keyboard
/// (<c>devdash-agent update</c>) may do that; the dashboard and auto mode wait.
/// </param>
public sealed record UpdateRequest(string? Version = null, bool CheckOnly = false, bool ReplacePending = false);

public sealed record ProbeResult(int ExitCode, string Output);

/// <summary>Runs a freshly downloaded program with <c>--version</c>. Replaced in tests.</summary>
public interface IBinaryProbe
{
    Task<ProbeResult> VersionAsync(string path, CancellationToken ct);
}

public sealed class ProcessBinaryProbe : IBinaryProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public async Task<ProbeResult> VersionAsync(string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? "",
        };
        psi.ArgumentList.Add("--version");

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return new ProbeResult(-1, "could not start it");
            }

            process.StandardInput.Close();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return new ProbeResult(-1, $"it did not exit within {Timeout.TotalSeconds:0} seconds");
            }

            var output = (await stdout.ConfigureAwait(false)).Trim();
            return new ProbeResult(process.ExitCode, output.Length > 0 ? output : (await stderr.ConfigureAwait(false)).Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new ProbeResult(-1, ex.Message);
        }
    }
}

/// <summary>What the updater needs to know about the program it is replacing. Null installation: it cannot be updated, and why.</summary>
public sealed record UpdateContext(UpdateInstallation? Installation, string? Problem, string CurrentVersion, ReleaseTrust Trust)
{
    public static UpdateContext ForCurrentProcess() =>
        new(UpdateInstallation.Current, UpdateInstallation.CurrentProblem, MachineInfoProvider.AgentVersion, ReleaseSigning.Embedded);
}

/// <summary>
/// Downloads, verifies and installs a release. The one code path behind <c>devdash-agent update</c>,
/// the dashboard's Update button and auto mode.
/// <para>
/// The only input from outside this machine is a version number. The files always come from the
/// release URL this agent is configured with, and none is used until <c>checksums.txt</c> has a valid
/// signature from the key compiled into this agent and the file matches its checksum there.
/// </para>
/// </summary>
public sealed class AgentUpdater(
    HttpClient http,
    IOptionsMonitor<AgentOptions> options,
    UpdateContext context,
    IBinaryProbe probe,
    IUpdateFiles files,
    TimeProvider time,
    ILogger<AgentUpdater> logger)
{
    public const string ChecksumsFile = "checksums.txt";
    public const string SignatureFile = "checksums.txt.sig";
    public const string VersionFile = "version.txt";

    private const int MaxSmallFileBytes = 256 * 1024;
    private const long MaxAssetBytes = 512L * 1024 * 1024;

    private UpdateOptions Settings => options.CurrentValue.Updates;

    /// <summary>The release folder for a version, or for the latest release when <paramref name="version"/> is null.</summary>
    public static Uri ReleaseUrl(UpdateOptions settings, SemVer? version, string file)
    {
        var root = string.IsNullOrWhiteSpace(settings.BaseUrl)
            ? $"https://github.com/{settings.Repository.Trim().Trim('/')}/releases"
            : settings.BaseUrl.Trim().TrimEnd('/');
        return new Uri(version is null ? $"{root}/latest/download/{file}" : $"{root}/download/v{version}/{file}");
    }

    /// <summary>Releases are fetched over HTTPS. Plain HTTP is only accepted from this machine itself, for tests.</summary>
    public static string? UrlProblem(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)
            ? null
            : $"{url.GetLeftPart(UriPartial.Authority)} is not an https URL.";

    public async Task<UpdateResult> RunAsync(UpdateRequest request, CancellationToken ct)
    {
        var current = context.CurrentVersion;
        UpdateResult Refuse(string why) => new(UpdateStatus.Refused, why, current, request.Version);

        if (!context.Trust.IsConfigured)
        {
            return Refuse("this build has no release signing key, so it cannot verify a download and will not update itself.");
        }

        if (!SemVer.TryParse(current, out var currentVersion))
        {
            return Refuse($"this agent's own version ({current}) is not a release version.");
        }

        SemVer? target = null;
        if (request.Version is not null && !SemVer.TryParse(request.Version, out target))
        {
            return Refuse($"'{request.Version}' is not a MAJOR.MINOR.PATCH[-prerelease] version.");
        }

        if (target is not null && Compare(currentVersion, target, current) is { } early)
        {
            return early;
        }

        var installation = context.Installation;
        if (installation is null && !request.CheckOnly)
        {
            return Refuse(context.Problem ?? "this agent is not an installed release.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, Settings.DownloadTimeoutSeconds)));
        try
        {
            if (target is null)
            {
                // The latest release's version.txt says which version it is; it is trusted because the signed checksums cover it.
                var latest = await FetchManifestAsync(null, timeout.Token).ConfigureAwait(false);
                target = latest.Version;
                if (target.IsPrerelease)
                {
                    return Refuse($"the latest release ({target}) is a pre-release; ask for it by version to install it.");
                }

                if (Compare(currentVersion, target, current) is { } result)
                {
                    return result;
                }
            }

            if (request.CheckOnly)
            {
                await FetchManifestAsync(target, timeout.Token).ConfigureAwait(false);
                return new UpdateResult(UpdateStatus.Available, $"{target} is available (this is {current}). Its signature is valid.", current, target.ToString());
            }

            return await InstallAsync(installation!, currentVersion, target, request, timeout.Token).ConfigureAwait(false);
        }
        catch (UpdateException ex)
        {
            logger.LogWarning("Update to {Target} failed: {Reason}", target?.ToString() ?? "the latest release", ex.Message);
            return new UpdateResult(UpdateStatus.Failed, ex.Message, current, target?.ToString());
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            var message = ex is OperationCanceledException ? "the download timed out." : ex.Message;
            logger.LogWarning("Update to {Target} failed: {Reason}", target?.ToString() ?? "the latest release", message);
            return new UpdateResult(UpdateStatus.Failed, message, current, target?.ToString());
        }
    }

    private static UpdateResult? Compare(SemVer current, SemVer target, string currentText)
    {
        var order = target.CompareTo(current);
        return order == 0 ? new UpdateResult(UpdateStatus.UpToDate, $"Already on {current}.", currentText, target.ToString())
            : order < 0 ? new UpdateResult(UpdateStatus.Refused, $"{target} is older than the installed {current}; the agent does not downgrade.", currentText, target.ToString())
            : null;
    }

    private sealed class UpdateException(string message) : Exception(message);

    private sealed record Manifest(SemVer Version, IReadOnlyDictionary<string, string> Checksums);

    /// <summary>
    /// Fetches and verifies the signed part of a release: <c>checksums.txt</c>, its signature, and
    /// <c>version.txt</c>. Returns only when the signature is valid and the version is the one the
    /// checksums vouch for (and the one asked for, when a version was given).
    /// </summary>
    private async Task<Manifest> FetchManifestAsync(SemVer? version, CancellationToken ct)
    {
        var checksumBytes = await GetSmallAsync(version, ChecksumsFile, required: true, ct).ConfigureAwait(false);
        var signature = await GetSmallAsync(version, SignatureFile, required: false, ct).ConfigureAwait(false);
        if (ReleaseVerifier.SignatureProblem(checksumBytes, signature, context.Trust) is { } signatureProblem)
        {
            throw new UpdateException($"Refusing {Name(version)}: {signatureProblem}");
        }

        var checksums = ReleaseVerifier.ParseChecksums(Encoding.UTF8.GetString(checksumBytes));
        var versionBytes = await GetSmallAsync(version, VersionFile, required: true, ct).ConfigureAwait(false);
        if (ReleaseVerifier.ChecksumProblem(checksums, VersionFile, ReleaseVerifier.Sha256Hex(versionBytes)) is { } versionProblem)
        {
            throw new UpdateException($"Refusing {Name(version)}: {versionProblem}");
        }

        if (!SemVer.TryParse(Encoding.ASCII.GetString(versionBytes).Trim(), out var stated))
        {
            throw new UpdateException($"Refusing {Name(version)}: its version.txt is not a version.");
        }

        if (version is not null && stated != version)
        {
            throw new UpdateException($"Refusing {version}: the signed files at that address are for {stated}.");
        }

        return new Manifest(stated, checksums);

        static string Name(SemVer? v) => v?.ToString() ?? "the latest release";
    }

    private async Task<UpdateResult> InstallAsync(UpdateInstallation installation, SemVer current, SemVer target, UpdateRequest request, CancellationToken ct)
    {
        var store = new UpdateStateStore(installation.StateFile);
        var state = store.Read();
        if (state.Pending is { } unconfirmed && unconfirmed.ToVersion == current.ToString() && !request.ReplacePending)
        {
            return new UpdateResult(UpdateStatus.Refused,
                $"the update to {unconfirmed.ToVersion} has not been confirmed yet (the agent has not reported to the hub since). Try again once it has.", current.ToString(), target.ToString());
        }

        var manifest = await FetchManifestAsync(target, ct).ConfigureAwait(false);
        var staging = installation.StagingDirectory;
        Directory.CreateDirectory(staging);

        // One update at a time per installation, across processes (the service and someone at a terminal).
        FileStream gate;
        try
        {
            gate = new FileStream(Path.Combine(staging, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return new UpdateResult(UpdateStatus.Refused, "another update of this agent is already running.", current.ToString(), target.ToString());
        }

        var work = Path.Combine(staging, target.ToString());
        try
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }

            Directory.CreateDirectory(work);
            var archive = Path.Combine(work, installation.AssetName);
            var url = ReleaseUrl(Settings, target, installation.AssetName);
            logger.LogInformation("Downloading {Url}", url);
            await DownloadAsync(url, archive, ct).ConfigureAwait(false);

            var actual = await ReleaseVerifier.Sha256HexAsync(archive, ct).ConfigureAwait(false);
            if (ReleaseVerifier.ChecksumProblem(manifest.Checksums, installation.AssetName, actual) is { } checksumProblem)
            {
                throw new UpdateException($"Refusing {target}: {checksumProblem}");
            }

            var unpacked = Directory.CreateDirectory(Path.Combine(work, "new")).FullName;
            Extract(archive, unpacked, installation);

            // Run the new program before it replaces anything: it must start here and be the version it claims.
            var expected = $"devdash-agent {target}";
            for (var index = 0; index < installation.FileNames.Count; index++)
            {
                var name = installation.FileNames[index];
                var result = await probe.VersionAsync(Path.Combine(unpacked, name), ct).ConfigureAwait(false);
                // The windowless Windows build may have nowhere to print; it must still run and exit cleanly.
                var acceptable = result.ExitCode == 0 && (result.Output == expected || (index > 0 && result.Output.Length == 0));
                if (!acceptable)
                {
                    throw new UpdateException(
                        $"Refusing {target}: the downloaded {name} did not run correctly here (`--version` exited {result.ExitCode} and printed '{AgentActions.Truncate(result.Output)}', expected '{expected}'). Nothing was changed.");
                }
            }

            var plan = SwapPlanner.Install(installation, unpacked, files.Exists);
            store.Write(state with
            {
                Pending = new PendingUpdate
                {
                    FromVersion = current.ToString(),
                    ToVersion = target.ToString(),
                    InstalledAt = time.GetUtcNow(),
                    Files = [.. plan.Files.Select(f => f.File)],
                },
            });

            try
            {
                SwapExecutor.Install(plan, installation.Platform, files);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                store.Write(state);
                throw new UpdateException($"Could not put {target} in place in {installation.Directory}: {ex.Message} The installed version is unchanged.");
            }

            logger.LogWarning("Installed devdash-agent {Target} over {Current} in {Directory}; the previous version is kept as .old until the new one reports to the hub",
                target, current, installation.Directory);
            return new UpdateResult(UpdateStatus.Installed,
                $"Installed {target} (was {current}). The previous version is kept until {target} has reported to the hub, and is put back if it does not.",
                current.ToString(), target.ToString());
        }
        finally
        {
            gate.Dispose();
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug("Could not clear {Staging}: {Error}", staging, ex.Message);
            }
        }
    }

    private async Task<byte[]> GetSmallAsync(SemVer? version, string file, bool required, CancellationToken ct)
    {
        var url = ReleaseUrl(Settings, version, file);
        using var response = await SendAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (!required && response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return [];
            }

            throw new UpdateException($"Could not download {url}: {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await CopyLimitedAsync(stream, buffer, MaxSmallFileBytes, url, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private async Task DownloadAsync(Uri url, string path, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException($"Could not download {url}: {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await CopyLimitedAsync(stream, file, MaxAssetBytes, url, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, CancellationToken ct)
    {
        if (UrlProblem(url) is { } problem)
        {
            throw new UpdateException($"Refusing to download a release: {problem}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("devdash-agent");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        // GitHub redirects release downloads to its file store. Wherever a redirect ends, it must still be https.
        if (response.RequestMessage?.RequestUri is { } final && UrlProblem(final) is { } redirected)
        {
            response.Dispose();
            throw new UpdateException($"Refusing to download a release: {redirected}");
        }

        return response;
    }

    private static async Task CopyLimitedAsync(Stream from, Stream to, long limit, Uri url, CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        long total = 0;
        int read;
        while ((read = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new UpdateException($"{url} is larger than the {limit / 1024 / 1024} MB a release file may be.");
            }

            await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Unpacks exactly the program files named by the installation and nothing else, each written once
    /// with its final permissions. The bytes are never touched again, so the code signature a macOS
    /// build carries stays valid.
    /// </summary>
    private static void Extract(string archive, string destination, UpdateInstallation installation)
    {
        var wanted = installation.FileNames.ToHashSet(StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);

        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                if (wanted.Contains(entry.FullName) && found.Add(entry.FullName))
                {
                    if (entry.Length > MaxAssetBytes)
                    {
                        throw new UpdateException($"{entry.FullName} in the release is implausibly large.");
                    }

                    using var source = entry.Open();
                    Write(source, entry.FullName);
                }
            }
        }
        else
        {
            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            while (tar.GetNextEntry() is { } entry)
            {
                var name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.DataStream is { } data && wanted.Contains(name) && found.Add(name))
                {
                    if (entry.Length > MaxAssetBytes)
                    {
                        throw new UpdateException($"{name} in the release is implausibly large.");
                    }

                    Write(data, name);
                }
            }
        }

        if (wanted.Except(found).FirstOrDefault() is { } missing)
        {
            throw new UpdateException($"The release archive {Path.GetFileName(archive)} does not contain {missing}.");
        }

        void Write(Stream source, string name)
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            }

            using var target = new FileStream(Path.Combine(destination, name), options);
            source.CopyTo(target);
        }
    }
}
