using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using DevDash.Agent.Updates;
using DevDash.Contracts;

namespace DevDash.Agent.Scanning;

public static class MachineInfoProvider
{
    public static string AgentVersion { get; } =
        (typeof(MachineInfoProvider).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    public static async Task<MachineInfo> GetAsync(AgentOptions options, CancellationToken ct)
    {
        var hostname = Environment.MachineName;
        var archive = Actions.ArchiveSetup.Describe(options);
        var (tsName, tsIp) = await TailscaleAsync(ct).ConfigureAwait(false);
        return new MachineInfo
        {
            MachineId = string.IsNullOrWhiteSpace(options.MachineId) ? hostname.ToLowerInvariant() : options.MachineId.Trim(),
            Hostname = hostname,
            Os = OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "Other",
            OsDescription = RuntimeInformation.OSDescription,
            TailscaleName = tsName,
            TailscaleIp = tsIp,
            AgentVersion = AgentVersion,
            ActionsEnabled = options.Actions.Enabled,
            AcceptsRefresh = Actions.ActionWorker.ShouldPoll(options),
            // One switch for all of them today; separate flags so the hub never has to guess from a version number.
            AcceptsRemoveProject = options.Actions.Enabled,
            AcceptsClone = options.Actions.Enabled,
            AcceptsCleanBuildDirs = options.Actions.Enabled,
            AcceptsPull = options.Actions.Enabled,
            AcceptsCleanCache = options.Actions.Enabled && options.Disk.Enabled,
            AcceptsStopDevServer = options.Actions.Enabled && options.DevServers.Enabled,
            Archive = archive,
            AcceptsArchiveProject = options.Actions.Enabled && archive is { Available: true, Writable: true },
            AcceptsRestoreArchive = options.Actions.Enabled && archive is { Available: true },
            UpdateMode = ReportedUpdateMode(options, UpdateInstallation.Current, ReleaseSigning.Embedded),
            DependencyChecks = options.Dependencies.Enabled,
            LastUpdate = UpdateInstallation.Current is { } installation ? new UpdateStateStore(installation.StateFile).Read().Last : null,
        };
    }

    /// <summary>
    /// The update mode the hub is told. An agent that could not update itself whatever it was asked
    /// (a development build, or a build with no release key) says "off", so no Update button is offered for it.
    /// </summary>
    public static string ReportedUpdateMode(AgentOptions options, UpdateInstallation? installation, ReleaseTrust trust) =>
        installation is null || !trust.IsConfigured ? AgentUpdateModes.Off : options.Updates.ResolvedMode;

    /// <summary>Reads this node's tailnet name and IP from <c>tailscale status --json</c>, when the CLI is installed.</summary>
    private static async Task<(string? Name, string? Ip)> TailscaleAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("tailscale")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--json");
            psi.ArgumentList.Add("--peers=false");

            using var process = Process.Start(psi);
            if (process is null)
            {
                return (null, null);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            _ = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return (null, null);
            }

            if (process.ExitCode != 0)
            {
                return (null, null);
            }

            using var doc = JsonDocument.Parse(await stdout.ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("Self", out var self))
            {
                return (null, null);
            }

            var name = self.TryGetProperty("DNSName", out var dns) ? dns.GetString()?.TrimEnd('.') : null;
            string? ip = null;
            if (self.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array && ips.GetArrayLength() > 0)
            {
                ip = ips[0].GetString();
            }

            return (string.IsNullOrEmpty(name) ? null : name, ip);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or JsonException or IOException or OperationCanceledException)
        {
            return (null, null); // tailscale not installed or not running
        }
    }
}
