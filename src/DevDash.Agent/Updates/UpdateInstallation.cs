using System.Runtime.InteropServices;
using DevDash.Agent.Service;

namespace DevDash.Agent.Updates;

/// <summary>
/// The installed agent that an update replaces: the folder, the program files in it, and where the
/// update keeps its own files. Everything lives in that one folder, so a second install elsewhere (or
/// a test in a temporary folder) is independent, and every rename stays on one volume.
/// </summary>
public sealed record UpdateInstallation
{
    public const string UnixBinary = "devdash-agent";
    public const string WindowsBinary = "devdash-agent.exe";

    public required ServicePlatform Platform { get; init; }

    /// <summary>The folder the agent is installed in, e.g. <c>~/.local/bin</c>.</summary>
    public required string Directory { get; init; }

    /// <summary>Release runtime id, e.g. <c>osx-arm64</c>.</summary>
    public required string Rid { get; init; }

    /// <summary>
    /// The program files a release replaces, console build first. Windows has two: the scheduled task runs
    /// <c>devdash-agentw.exe</c> and people run <c>devdash-agent.exe</c>, and they must always be the same version.
    /// </summary>
    public IReadOnlyList<string> FileNames => Platform == ServicePlatform.Windows
        ? [WindowsBinary, ServiceEnvironment.WindowlessExecutableName]
        : [UnixBinary];

    public string AssetName => $"devdash-agent-{Rid}{(Platform == ServicePlatform.Windows ? ".zip" : ".tar.gz")}";

    public string PathOf(string fileName) => Path.Combine(Directory, fileName);

    /// <summary>The console build, which is what gets test-run and what restarts the service on Windows.</summary>
    public string ConsoleBinary => PathOf(FileNames[0]);

    /// <summary>Records an update that is waiting to be confirmed, and how the last one went.</summary>
    public string StateFile => Path.Combine(Directory, "devdash-agent.update.json");

    /// <summary>Downloads are unpacked here, beside the program, so moving them into place is a rename.</summary>
    public string StagingDirectory => Path.Combine(Directory, ".devdash-agent-update");

    public static string BackupPath(string path) => path + ".old";

    /// <summary>Where Windows parks a running program that is being rolled back; deleted on a later start.</summary>
    public static string DiscardPath(string path) => path + ".bad";

    private static readonly Lazy<(UpdateInstallation? Installation, string? Problem)> CurrentProcess = new(Detect);

    /// <summary>The installation this process is running from, or null when it is not one that can be updated.</summary>
    public static UpdateInstallation? Current => CurrentProcess.Value.Installation;

    /// <summary>Why <see cref="Current"/> is null.</summary>
    public static string? CurrentProblem => CurrentProcess.Value.Problem;

    private static (UpdateInstallation?, string?) Detect()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            return (null, "the agent could not work out where it is installed.");
        }

        try
        {
            // ~/.local/bin/devdash-agent may be a link to the real file; the real file is what gets replaced.
            path = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
        }
        catch (IOException)
        {
            path = Path.GetFullPath(path);
        }

        return For(path, CurrentPlatform, CurrentRid);
    }

    /// <summary>Describes the installation that <paramref name="executablePath"/> belongs to, or says why it cannot be updated.</summary>
    public static (UpdateInstallation? Installation, string? Problem) For(string executablePath, ServicePlatform platform, string? rid)
    {
        var directory = Path.GetDirectoryName(executablePath);
        var name = Path.GetFileName(executablePath);
        if (string.IsNullOrEmpty(directory))
        {
            return (null, "the agent could not work out where it is installed.");
        }

        if (rid is null)
        {
            return (null, $"there are no release builds for this platform ({RuntimeInformation.RuntimeIdentifier}).");
        }

        var installation = new UpdateInstallation { Platform = platform, Directory = directory, Rid = rid };
        var comparison = platform == ServicePlatform.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!installation.FileNames.Any(f => f.Equals(name, comparison)))
        {
            return (null, Path.GetFileNameWithoutExtension(name).Equals("dotnet", StringComparison.OrdinalIgnoreCase) || name.StartsWith("testhost", StringComparison.OrdinalIgnoreCase)
                ? "this agent is running from a development build (`dotnet run`), not from an installed release."
                : $"the running program is called {name}, not {installation.FileNames[0]}, so it is not a standard install.");
        }

        return (installation, null);
    }

    public static ServicePlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? ServicePlatform.Windows : OperatingSystem.IsMacOS() ? ServicePlatform.MacOS : ServicePlatform.Linux;

    /// <summary>The release build that matches this process, or null when there is none.</summary>
    public static string? CurrentRid
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : null;
            var cpu = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => null,
            };
            return os is null || cpu is null ? null : $"{os}-{cpu}";
        }
    }
}
