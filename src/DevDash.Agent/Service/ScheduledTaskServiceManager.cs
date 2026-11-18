using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace DevDash.Agent.Service;

/// <summary>
/// Windows: a Task Scheduler task that starts at the user's logon and runs in their interactive session
/// with their own token. That is what lets git use Git Credential Manager, DPAPI-protected credentials
/// and the user's SSH keys; a Windows Service under LocalSystem would see none of them. The task runs
/// <c>devdash-agentw.exe</c>, the Windows-subsystem build, so no console window appears.
/// </summary>
public sealed class ScheduledTaskServiceManager(ServiceEnvironment environment) : IServiceManager
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>Task names are machine-wide, so each user gets their own.</summary>
    public string TaskName => $"DevDash Agent ({environment.UserName})";

    public string Name => $"scheduled task \"{TaskName}\"";

    public string TaskXmlPath => environment.Join(environment.WindowsDataDirectory, "agent-task.xml");

    /// <summary>Checks for a running agent process; replaced in tests.</summary>
    public Func<string, int?> FindProcess { get; init; } = FindRunningProcess;

    public ServiceFile Definition
    {
        get
        {
            var task = new XElement(Ns + "Task", new XAttribute("version", "1.2"),
                new XElement(Ns + "RegistrationInfo",
                    new XElement(Ns + "Description", "DevDash agent: reports the state of your code folders to the DevDash hub."),
                    new XElement(Ns + "URI", "\\" + TaskName)),
                new XElement(Ns + "Triggers",
                    new XElement(Ns + "LogonTrigger",
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "UserId", environment.QualifiedUserName ?? environment.UserName))),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(Ns + "UserId", string.IsNullOrEmpty(environment.UserId) ? environment.QualifiedUserName ?? environment.UserName : environment.UserId),
                        // The user's own logon session: no stored password, nothing runs unless they are logged in.
                        new XElement(Ns + "LogonType", "InteractiveToken"),
                        new XElement(Ns + "RunLevel", "LeastPrivilege"))),
                new XElement(Ns + "Settings",
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(Ns + "AllowHardTerminate", "true"),
                    new XElement(Ns + "StartWhenAvailable", "true"),
                    new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(Ns + "IdleSettings",
                        new XElement(Ns + "StopOnIdleEnd", "false"),
                        new XElement(Ns + "RestartOnIdle", "false")),
                    new XElement(Ns + "AllowStartOnDemand", "true"),
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(Ns + "Hidden", "false"),
                    new XElement(Ns + "RunOnlyIfIdle", "false"),
                    new XElement(Ns + "WakeToRun", "false"),
                    // PT0S = no time limit; the default would kill the agent after three days.
                    new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(Ns + "Priority", "7"),
                    new XElement(Ns + "RestartOnFailure",
                        new XElement(Ns + "Interval", "PT1M"),
                        new XElement(Ns + "Count", "999"))),
                new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(Ns + "Exec",
                        new XElement(Ns + "Command", environment.ServiceExecutablePath),
                        new XElement(Ns + "Arguments", ServiceManagers.RunArguments),
                        new XElement(Ns + "WorkingDirectory", environment.HomeDirectory))));

            // schtasks reads UTF-16 with a byte-order mark most reliably.
            var xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" + task.ToString().ReplaceLineEndings("\r\n") + "\r\n";
            return new ServiceFile(TaskXmlPath, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        }
    }

    public IReadOnlyList<ServiceCommand> InstallCommands =>
    [
        Schtasks("/End", "/TN", TaskName) with { IgnoreFailure = true },
        Schtasks("/Create", "/TN", TaskName, "/XML", TaskXmlPath, "/F"),
        Schtasks("/Run", "/TN", TaskName),
    ];

    public IReadOnlyList<ServiceCommand> UninstallCommands =>
    [
        Schtasks("/End", "/TN", TaskName) with { IgnoreFailure = true },
        Schtasks("/Delete", "/TN", TaskName, "/F") with { IgnoreFailure = true },
    ];

    public IReadOnlyList<ServiceCommand> StartCommands => [Schtasks("/Run", "/TN", TaskName)];
    public IReadOnlyList<ServiceCommand> StopCommands => [Schtasks("/End", "/TN", TaskName)];

    public IReadOnlyList<string> Notes => environment.ServiceExecutablePath == environment.ExecutablePath
        ? [$"{ServiceEnvironment.WindowlessExecutableName} was not found beside {environment.ExecutablePath}, so the task runs the console build and a console window will be visible."]
        : [];

    public async Task<ServiceState> GetStateAsync(ICommandRunner runner, CancellationToken ct)
    {
        // The exit code says whether the task exists. Its "Status" text is localised, so whether it is
        // running is read from the process list instead.
        var query = await runner.RunAsync(Schtasks("/Query", "/TN", TaskName), ct).ConfigureAwait(false);
        if (!query.Ok)
        {
            return new ServiceState(false, false);
        }

        var pid = FindProcess(environment.ServiceExecutablePath);
        return new ServiceState(true, pid is not null, pid is null ? null : $"pid {pid}");
    }

    private static ServiceCommand Schtasks(params string[] args) => new("schtasks.exe", args);

    private static int? FindRunningProcess(string executablePath)
    {
        var name = Path.GetFileNameWithoutExtension(executablePath);
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                if (process.Id != Environment.ProcessId)
                {
                    return process.Id;
                }
            }
        }

        return null;
    }
}
