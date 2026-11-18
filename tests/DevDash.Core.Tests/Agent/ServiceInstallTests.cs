using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DevDash.Agent.Logging;
using DevDash.Agent.Service;
using Microsoft.Extensions.Logging;

namespace DevDash.Core.Tests.Agent;

/// <summary>
/// The installer, exercised against a temporary home folder and a fake command runner: nothing here
/// registers a real service or runs launchctl, systemctl or schtasks.
/// </summary>
public sealed class ServiceInstallTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("devdash-home-").FullName;
    private readonly FakeRunner _runner = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    private ServiceEnvironment Mac => new()
    {
        Platform = ServicePlatform.MacOS,
        HomeDirectory = _home,
        ExecutablePath = "/Users/sam/.local/bin/devdash-agent",
        UserName = "sam",
        UserId = "501",
        PathVariable = "/opt/homebrew/bin:/usr/bin:relative:/Users/sam/my tools",
    };

    private static ServiceEnvironment Windows(bool windowless = true) => new()
    {
        Platform = ServicePlatform.Windows,
        HomeDirectory = @"C:\Users\Sam Smith",
        ExecutablePath = @"C:\Users\Sam Smith\AppData\Local\DevDash\bin\devdash-agent.exe",
        UserName = "Sam Smith",
        UserId = "S-1-5-21-1-2-3-1001",
        QualifiedUserName = @"DESKTOP\Sam Smith",
        LocalAppData = @"C:\Users\Sam Smith\AppData\Local",
        FileExists = path => windowless && path.EndsWith("devdash-agentw.exe", StringComparison.Ordinal),
    };

    private ServiceEnvironment Linux => new()
    {
        Platform = ServicePlatform.Linux,
        HomeDirectory = _home,
        ExecutablePath = "/home/sam/my bin/devdash-agent",
        UserName = "sam",
        UserId = "1000",
        PathVariable = "/usr/local/bin:/usr/bin",
    };

    /// <summary>A path under the temp home as the macOS and Linux environments spell it, whatever OS the tests run on.</summary>
    private string Home(params string[] parts) => string.Join('/', [_home.TrimEnd('/', '\\'), .. parts]);

    private ServiceCli Cli(ServiceEnvironment environment) => new(environment, ServiceManagers.For(environment), _runner, _out, _err);

    // ---- arguments ---------------------------------------------------------------------------

    [Fact]
    public void Install_arguments_are_parsed()
    {
        var request = InstallRequest.Parse(
            ["--hub-url", "https://devdash.example.ts.net/", "--api-key", " ddk_abc ", "--root", "~/Developer", "--root", "/src", "--no-fetch", "--enable-actions",
                "--set", "ScanIntervalMinutes=30", "--set", "Agent__AutoFetch__IntervalHours=1.5", "--dry-run"]);

        Assert.Null(request.Error);
        Assert.Equal("https://devdash.example.ts.net", request.HubUrl);
        Assert.Equal("ddk_abc", request.ApiKey);
        Assert.Equal(["~/Developer", "/src"], request.Roots);
        Assert.True(request.DryRun);
        Assert.Equal("false", request.Settings["AutoFetch:Enabled"]);
        Assert.Equal("true", request.Settings["Actions:Enabled"]);
        Assert.Equal("30", request.Settings["ScanIntervalMinutes"]);
        Assert.Equal("1.5", request.Settings["AutoFetch:IntervalHours"]);
    }

    [Fact]
    public void The_api_key_can_come_from_a_file_or_the_environment()
    {
        Assert.Equal("from-file", InstallRequest.Parse(["--api-key-file", "k.txt"], readFile: _ => "from-file\n", environmentKey: "from-env").ApiKey);
        Assert.Equal("from-env", InstallRequest.Parse([], environmentKey: "from-env").ApiKey);
        Assert.Null(InstallRequest.Parse([]).ApiKey);
    }

    [Theory]
    [InlineData("--hub-url")]
    [InlineData("--hub-url", "devdash")]
    [InlineData("--hub-url", "ftp://devdash")]
    [InlineData("--set", "NoEquals")]
    [InlineData("--api-key", " ")]
    [InlineData("--frobnicate")]
    public void Bad_install_arguments_are_rejected(params string[] args) => Assert.NotNull(InstallRequest.Parse(args).Error);

    // ---- config ------------------------------------------------------------------------------

    [Fact]
    public void Config_is_merged_into_what_is_already_there()
    {
        const string existing = """
            {
              // hand-written
              "Logging": { "LogLevel": { "Default": "Debug" } },
              "Agent": { "HubUrl": "http://old", "ApiKey": "inline-key", "ScanIntervalMinutes": 5, "AutoFetch": { "TimeoutSeconds": 10 } }
            }
            """;
        var request = InstallRequest.Parse(["--hub-url", "https://hub", "--api-key", "ddk_new", "--root", "/src", "--no-fetch", "--enable-actions", "--set", "MaxParallelism=2", "--set", "MachineId=box"]);

        using var doc = JsonDocument.Parse(AgentConfigWriter.Merge(existing, request, "/home/.config/devdash/agent.key"));
        var agent = doc.RootElement.GetProperty("Agent");

        Assert.Equal("Debug", doc.RootElement.GetProperty("Logging").GetProperty("LogLevel").GetProperty("Default").GetString());
        Assert.Equal("https://hub", agent.GetProperty("HubUrl").GetString());
        Assert.False(agent.TryGetProperty("ApiKey", out _)); // an inline key would shadow the key file
        Assert.Equal("/home/.config/devdash/agent.key", agent.GetProperty("ApiKeyFile").GetString());
        Assert.Equal("/src", agent.GetProperty("Roots")[0].GetString());
        Assert.Equal(5, agent.GetProperty("ScanIntervalMinutes").GetInt32()); // untouched
        Assert.Equal(10, agent.GetProperty("AutoFetch").GetProperty("TimeoutSeconds").GetInt32()); // untouched
        Assert.Equal(JsonValueKind.False, agent.GetProperty("AutoFetch").GetProperty("Enabled").ValueKind);
        Assert.Equal(JsonValueKind.True, agent.GetProperty("Actions").GetProperty("Enabled").ValueKind);
        Assert.Equal(2, agent.GetProperty("MaxParallelism").GetInt32());
        Assert.Equal("box", agent.GetProperty("MachineId").GetString());
    }

    [Fact]
    public void Merging_without_a_new_key_keeps_the_existing_one()
    {
        var first = AgentConfigWriter.Merge(null, InstallRequest.Parse(["--hub-url", "https://hub", "--api-key", "k"]), "/k");
        var second = AgentConfigWriter.Merge(first, InstallRequest.Parse(["--no-watch"]), "/other");

        using var doc = JsonDocument.Parse(second);
        Assert.Equal("/k", doc.RootElement.GetProperty("Agent").GetProperty("ApiKeyFile").GetString());
        Assert.Equal(("https://hub", true), AgentConfigWriter.Summarise(second));
        Assert.Equal((null, false), AgentConfigWriter.Summarise("{}"));
    }

    // ---- install / uninstall against a temp home ---------------------------------------------

    [Fact]
    public async Task Install_writes_config_key_and_plist_then_registers_the_service()
    {
        _runner.Respond("print", 0, "gui/501/x = {\n\tstate = running\n\tpid = 4242\n\tsockets = {\n\t\tstate = not running\n\t}\n}");
        var env = Mac;

        var exit = await Cli(env).InstallAsync(InstallRequest.Parse(["--hub-url", "https://hub", "--api-key", "ddk_secret", "--no-fetch"]), CancellationToken.None);

        Assert.True(exit == 0, _err.ToString());
        Assert.Equal("ddk_secret\n", File.ReadAllText(env.ApiKeyFile));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(env.ApiKeyFile));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(env.ConfigDirectory));
        }

        var config = File.ReadAllText(env.ConfigFile);
        Assert.DoesNotContain("ddk_secret", config);
        Assert.Contains(env.ApiKeyFile, config);
        var plist = Home("Library", "LaunchAgents", "com.github.mikejobson.devdash-agent.plist");
        Assert.True(File.Exists(plist));
        Assert.True(Directory.Exists(Path.Combine(_home, "Library", "Logs", "DevDash")));
        Assert.Equal(
            [
                "launchctl bootout gui/501/com.github.mikejobson.devdash-agent",
                "launchctl enable gui/501/com.github.mikejobson.devdash-agent",
                $"launchctl bootstrap gui/501 {plist}",
                "launchctl print gui/501/com.github.mikejobson.devdash-agent",
            ],
            _runner.Commands);
        Assert.Contains("installed, running (pid 4242)", _out.ToString());
        Assert.DoesNotContain("ddk_secret", _out.ToString());
    }

    [Fact]
    public async Task Dry_run_prints_everything_and_touches_nothing()
    {
        var exit = await Cli(Mac).InstallAsync(InstallRequest.Parse(["--hub-url", "https://hub", "--api-key", "ddk_secret_value", "--dry-run"]), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Empty(_runner.Commands);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_home));
        var text = _out.ToString();
        Assert.Contains("\"HubUrl\": \"https://hub\"", text);
        Assert.Contains("<key>RunAtLoad</key>", text);
        Assert.Contains("launchctl bootstrap gui/501", text);
        Assert.DoesNotContain("ddk_secret_value", text);
    }

    [Fact]
    public async Task No_start_writes_files_but_runs_no_commands()
    {
        var env = Linux;

        var exit = await Cli(env).InstallAsync(InstallRequest.Parse(["--hub-url", "https://hub", "--api-key", "k", "--no-start"]), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Empty(_runner.Commands);
        Assert.True(File.Exists(Path.Combine(_home, ".config", "systemd", "user", "devdash-agent.service")));
    }

    [Fact]
    public async Task Install_needs_a_hub_url_and_a_key_the_first_time_only()
    {
        Assert.Equal(64, await Cli(Mac).InstallAsync(InstallRequest.Parse(["--api-key", "k"]), CancellationToken.None));
        Assert.Equal(64, await Cli(Mac).InstallAsync(InstallRequest.Parse(["--hub-url", "https://hub"]), CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_home));

        Assert.Equal(0, await Cli(Mac).InstallAsync(InstallRequest.Parse(["--hub-url", "https://hub", "--api-key", "k", "--no-start"]), CancellationToken.None));
        // Re-running with no arguments (what an upgrade does) reuses the saved config.
        Assert.Equal(0, await Cli(Mac).InstallAsync(InstallRequest.Parse(["--no-start"]), CancellationToken.None));
        Assert.Equal("k\n", File.ReadAllText(Mac.ApiKeyFile));
    }

    [Fact]
    public async Task A_failed_registration_is_reported_and_keeps_the_config()
    {
        _runner.Respond("bootstrap", 5, "Bootstrap failed: 5: Input/output error");
        var env = Mac with { PathVariable = null };
        var manager = new LaunchdServiceManager(env);
        var cli = new ServiceCli(env, new SingleAttempt(manager), _runner, _out, _err);

        var exit = await cli.InstallAsync(InstallRequest.Parse(["--hub-url", "https://hub", "--api-key", "k"]), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("Input/output error", _err.ToString());
        Assert.True(File.Exists(env.ConfigFile));
    }

    [Fact]
    public async Task Uninstall_keeps_the_config_unless_purged()
    {
        var env = Mac;
        await Cli(env).InstallAsync(InstallRequest.Parse(["--hub-url", "https://hub", "--api-key", "k", "--no-start"]), CancellationToken.None);
        var plist = new LaunchdServiceManager(env).PlistPath;

        Assert.Equal(0, await Cli(env).UninstallAsync(InstallRequest.Parse([]), CancellationToken.None));
        Assert.Equal(["launchctl bootout gui/501/com.github.mikejobson.devdash-agent"], _runner.Commands);
        Assert.False(File.Exists(plist));
        Assert.True(File.Exists(env.ConfigFile));
        Assert.True(File.Exists(env.ApiKeyFile));

        Assert.Equal(0, await Cli(env).UninstallAsync(InstallRequest.Parse(["--purge"]), CancellationToken.None));
        Assert.False(Directory.Exists(env.ConfigDirectory));
        Assert.False(Directory.Exists(env.LogDirectory));
    }

    // ---- service definitions -----------------------------------------------------------------

    [Fact]
    public void Launchd_plist_runs_the_agent_at_login_as_the_user_and_keeps_it_alive()
    {
        var manager = new LaunchdServiceManager(Mac);
        var file = manager.Definition;

        Assert.Equal(Home("Library", "LaunchAgents", "com.github.mikejobson.devdash-agent.plist"), file.Path);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\"", file.Content);
        var dict = XDocument.Parse(file.Content).Root!.Element("dict")!;
        XElement Value(string key) => dict.Elements("key").Single(k => k.Value == key).ElementsAfterSelf().First();

        Assert.Equal("com.github.mikejobson.devdash-agent", Value("Label").Value);
        Assert.Equal(["/Users/sam/.local/bin/devdash-agent", "run", "--service"], Value("ProgramArguments").Elements("string").Select(e => e.Value));
        Assert.Equal("true", Value("RunAtLoad").Name.LocalName);
        Assert.Equal("true", Value("KeepAlive").Name.LocalName);
        Assert.Equal(Home("Library", "Logs", "DevDash", "launchd.err.log"), Value("StandardErrorPath").Value);
        // The installing shell's PATH (absolute entries only) plus the usual tool locations.
        Assert.Equal("/opt/homebrew/bin:/usr/bin:/Users/sam/my tools:/usr/local/bin:/bin:/usr/sbin:/sbin", Value("EnvironmentVariables").Elements("string").Single().Value);
        Assert.DoesNotContain("UserName", file.Content); // a LaunchAgent, not a daemon: it already runs as the user
    }

    [Fact]
    public void Launchd_plist_escapes_xml_in_paths()
    {
        var file = new LaunchdServiceManager(Mac with { ExecutablePath = "/Users/a&b/<bin>/devdash-agent" }).Definition;

        Assert.Contains("/Users/a&amp;b/&lt;bin&gt;/devdash-agent", file.Content);
        Assert.Equal("/Users/a&b/<bin>/devdash-agent", XDocument.Parse(file.Content).Descendants("array").Single().Elements().First().Value);
    }

    [Theory]
    [InlineData("gui/501/x = {\n\tactive count = 1\n\tstate = running\n\tpid = 77\n\tendpoints = {\n\t\tstate = not running\n\t}\n}", true, "77")]
    [InlineData("gui/501/x = {\n\tstate = spawn scheduled\n}", false, null)]
    public void Launchctl_print_output_is_parsed(string output, bool running, string? pid) =>
        Assert.Equal((running, pid), LaunchdServiceManager.ParsePrint(output));

    [Fact]
    public async Task Launchd_state_distinguishes_missing_unloaded_and_running()
    {
        var manager = new LaunchdServiceManager(Mac);
        _runner.Respond("print", 113, "Could not find service");
        Assert.Equal("not installed", (await manager.GetStateAsync(_runner, CancellationToken.None)).ToString());

        Directory.CreateDirectory(Path.GetDirectoryName(manager.PlistPath)!);
        File.WriteAllText(manager.PlistPath, "x");
        Assert.Equal("installed, not running (not loaded)", (await manager.GetStateAsync(_runner, CancellationToken.None)).ToString());

        _runner.Respond("print", 0, "x = {\n\tstate = running\n\tpid = 9\n}");
        Assert.Equal("installed, running (pid 9)", (await manager.GetStateAsync(_runner, CancellationToken.None)).ToString());
    }

    [Fact]
    public void Systemd_unit_is_a_user_unit_that_restarts()
    {
        var manager = new SystemdUserServiceManager(Linux with { ExecutablePath = "/home/sam/my bin/100%/devdash-agent" });
        var file = manager.Definition;

        Assert.Equal(Home(".config", "systemd", "user", "devdash-agent.service"), file.Path);
        var lines = file.Content.Split('\n');
        Assert.Contains("ExecStart=\"/home/sam/my bin/100%%/devdash-agent\" run --service", lines);
        Assert.Contains("Environment=PATH=/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin", lines);
        Assert.Contains("Restart=always", lines);
        Assert.Contains("WantedBy=default.target", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("User=", StringComparison.Ordinal)); // user units cannot and need not set User=
        Assert.Equal("[Unit]", lines[0]);
        Assert.Equal(["systemctl --user daemon-reload", "systemctl --user enable devdash-agent.service", "systemctl --user restart devdash-agent.service"],
            manager.InstallCommands.Select(c => c.ToString()));
    }

    [Fact]
    public void Systemd_unit_honours_xdg_config_home_and_logs_under_xdg_state_home()
    {
        var env = Linux with { XdgConfigHome = "/cfg", XdgStateHome = "/state" };

        Assert.Equal("/cfg/systemd/user/devdash-agent.service", new SystemdUserServiceManager(env).UnitPath);
        Assert.Equal("/state/devdash/logs", env.LogDirectory);
        Assert.Equal(Home(".local", "state", "devdash", "logs"), Linux.LogDirectory);
    }

    [Fact]
    public void Scheduled_task_runs_the_windowless_build_at_logon_as_the_interactive_user()
    {
        var manager = new ScheduledTaskServiceManager(Windows());
        var file = manager.Definition;
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal(@"C:\Users\Sam Smith\AppData\Local\DevDash\agent-task.xml", file.Path);
        Assert.Equal([0xFF, 0xFE], file.Encoding.GetPreamble()); // UTF-16 LE with BOM, as schtasks expects
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", file.Content);
        var task = XDocument.Parse(file.Content[file.Content.IndexOf("<Task", StringComparison.Ordinal)..]).Root!;
        string Text(params string[] path) => path.Aggregate(task, (e, name) => e.Element(ns + name)!).Value;

        Assert.Equal(@"DESKTOP\Sam Smith", Text("Triggers", "LogonTrigger", "UserId"));
        Assert.Equal("S-1-5-21-1-2-3-1001", Text("Principals", "Principal", "UserId"));
        Assert.Equal("InteractiveToken", Text("Principals", "Principal", "LogonType")); // the user's own session, not SYSTEM, no stored password
        Assert.Equal("LeastPrivilege", Text("Principals", "Principal", "RunLevel"));
        Assert.Equal(@"C:\Users\Sam Smith\AppData\Local\DevDash\bin\devdash-agentw.exe", Text("Actions", "Exec", "Command"));
        Assert.Equal("run --service", Text("Actions", "Exec", "Arguments"));
        Assert.Equal("PT0S", Text("Settings", "ExecutionTimeLimit"));
        Assert.Equal("PT1M", Text("Settings", "RestartOnFailure", "Interval"));
        Assert.Equal("false", Text("Settings", "DisallowStartIfOnBatteries"));
        Assert.Equal("false", Text("Settings", "StopIfGoingOnBatteries"));
        Assert.Empty(manager.Notes);
        Assert.Equal(
            [
                "schtasks.exe /End /TN \"DevDash Agent (Sam Smith)\"",
                "schtasks.exe /Create /TN \"DevDash Agent (Sam Smith)\" /XML \"C:\\Users\\Sam Smith\\AppData\\Local\\DevDash\\agent-task.xml\" /F",
                "schtasks.exe /Run /TN \"DevDash Agent (Sam Smith)\"",
            ],
            manager.InstallCommands.Select(c => c.ToString()));
    }

    [Fact]
    public void Scheduled_task_falls_back_to_the_console_build_and_says_so()
    {
        var manager = new ScheduledTaskServiceManager(Windows(windowless: false));

        Assert.Contains(@"\bin\devdash-agent.exe</Command>", manager.Definition.Content);
        Assert.Contains("console window", Assert.Single(manager.Notes));
    }

    [Fact]
    public async Task Scheduled_task_state_comes_from_the_exit_code_and_the_process_list()
    {
        int? pid = null;
        var manager = new ScheduledTaskServiceManager(Windows()) { FindProcess = _ => pid };

        _runner.Respond("/Query", 1, "ERROR: The system cannot find the file specified.");
        Assert.Equal("not installed", (await manager.GetStateAsync(_runner, CancellationToken.None)).ToString());

        _runner.Respond("/Query", 0, "localised text");
        Assert.Equal("installed, not running", (await manager.GetStateAsync(_runner, CancellationToken.None)).ToString());

        pid = 321;
        Assert.Equal("installed, running (pid 321)", (await manager.GetStateAsync(_runner, CancellationToken.None)).ToString());
    }

    [Fact]
    public void Log_folders_follow_each_platforms_convention()
    {
        Assert.Equal(Home("Library", "Logs", "DevDash"), Mac.LogDirectory);
        Assert.Equal(@"C:\Users\Sam Smith\AppData\Local\DevDash\logs", Windows().LogDirectory);
        Assert.Equal(@"C:\Users\Sam Smith\.config\devdash\agent.key", Windows().ApiKeyFile);
    }

    [Fact]
    public void Service_and_log_dir_flags_are_removed_before_normal_parsing()
    {
        string[] args = ["run", "--log-dir", _home, "--no-fetch"];
        Assert.Equal(_home, ServiceCli.TakeLogDirectory(ref args));
        Assert.Equal(["run", "--no-fetch"], args);

        string[] plain = ["scan", "--dry-run"];
        Assert.Null(ServiceCli.TakeLogDirectory(ref plain));
        Assert.Equal(["scan", "--dry-run"], plain);
    }

    // ---- file logger -------------------------------------------------------------------------

    [Fact]
    public void File_logger_writes_one_file_per_day_and_prunes_old_ones()
    {
        var logs = Path.Combine(_home, "logs");
        var time = new TestClock();
        using (var provider = new FileLoggerProvider(logs, time, retainedFiles: 2))
        {
            var logger = provider.CreateLogger("DevDash.Test");
            logger.LogInformation("first {Number}", 1);
            logger.LogError(new InvalidOperationException("boom"), "failed");
            time.Advance(TimeSpan.FromDays(1));
            logger.LogWarning("second day");
            time.Advance(TimeSpan.FromDays(1));
            logger.LogInformation("third day");
        }

        Assert.Equal(["agent-20261119.log", "agent-20261120.log"], Directory.EnumerateFiles(logs).Select(Path.GetFileName).Order());
        Assert.Equal("2026-11-19 12:00:00.000 +00:00 [WRN] DevDash.Test: second day", File.ReadAllText(Path.Combine(logs, "agent-20261119.log")).TrimEnd());
        Assert.Equal(Path.Combine(logs, "agent-20261120.log"), FileLoggerProvider.LatestFile(logs));
    }

    [Fact]
    public void File_logger_starts_a_new_part_when_the_days_file_is_full_and_includes_exceptions()
    {
        var logs = Path.Combine(_home, "logs");
        var time = new TestClock();
        using (var provider = new FileLoggerProvider(logs, time, maxBytes: 200))
        {
            var logger = provider.CreateLogger("C");
            logger.LogError(new InvalidOperationException("boom"), "{Padding}", new string('x', 250));
            logger.LogInformation("next");
        }

        var first = File.ReadAllText(Path.Combine(logs, "agent-20261118.log"));
        Assert.Contains("[ERR] C: xxxx", first);
        Assert.Contains("System.InvalidOperationException: boom", first);
        Assert.Contains("[INF] C: next", File.ReadAllText(Path.Combine(logs, "agent-20261118.1.log")));
    }

    [Fact]
    public async Task Logs_command_prints_the_tail_of_the_newest_file()
    {
        var env = Mac;
        Assert.Equal(1, Cli(env).Logs([]));

        Directory.CreateDirectory(env.LogDirectory);
        await File.WriteAllLinesAsync(Path.Combine(env.LogDirectory, "agent-20261118.log"), ["one", "two", "three"]);

        Assert.Equal(0, Cli(env).Logs(["-n", "2"]));
        Assert.Equal(["two", "three"], _out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeRunner : ICommandRunner
    {
        private readonly Dictionary<string, CommandResult> _responses = [];

        public List<string> Commands { get; } = [];

        /// <summary>Answers any command with <paramref name="verb"/> among its arguments; everything else succeeds silently.</summary>
        public void Respond(string verb, int exitCode, string output) => _responses[verb] = new CommandResult(exitCode, output);

        public Task<CommandResult> RunAsync(ServiceCommand command, CancellationToken ct)
        {
            Commands.Add(command.ToString());
            var match = command.Arguments.FirstOrDefault(_responses.ContainsKey);
            return Task.FromResult(match is null ? new CommandResult(0, "") : _responses[match]);
        }
    }

    /// <summary>Drops the retry delay so the failure test is quick.</summary>
    private sealed class SingleAttempt(IServiceManager inner) : IServiceManager
    {
        public string Name => inner.Name;
        public ServiceFile Definition => inner.Definition;
        public IReadOnlyList<ServiceCommand> InstallCommands => [.. inner.InstallCommands.Select(c => c with { Attempts = 1 })];
        public IReadOnlyList<ServiceCommand> UninstallCommands => inner.UninstallCommands;
        public IReadOnlyList<ServiceCommand> StartCommands => inner.StartCommands;
        public IReadOnlyList<ServiceCommand> StopCommands => inner.StopCommands;
        public IReadOnlyList<string> Notes => inner.Notes;
        public Task<ServiceState> GetStateAsync(ICommandRunner runner, CancellationToken ct) => inner.GetStateAsync(runner, ct);
    }
}
