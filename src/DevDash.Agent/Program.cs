using System.Text.Json;
using DevDash.Agent;
using DevDash.Agent.Actions;
using DevDash.Agent.Git;
using DevDash.Agent.Logging;
using DevDash.Agent.Scanning;
using DevDash.Agent.Service;
using DevDash.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// install, uninstall, start, stop, restart, logs and --version need no host or configuration.
if (await ServiceCli.TryRunAsync(args) is { } serviceExitCode)
{
    return serviceExitCode;
}

// `run --service` (what the installed service runs) logs to a rolling file instead of the console.
var logDirectory = ServiceCli.TakeLogDirectory(ref args);

var cli = AgentCli.Parse(args);
if (cli.Error is not null || cli.Command == "help")
{
    Console.Error.WriteLine(cli.Error ?? "");
    Console.Error.WriteLine(AgentCli.Usage);
    Console.Error.WriteLine();
    Console.Error.WriteLine(ServiceCli.Usage);
    return cli.Error is null ? 0 : 64;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
});

// The defaults are embedded so the published single-file binary needs nothing beside it.
using (var defaults = typeof(AgentOptions).Assembly.GetManifestResourceStream("appsettings.json"))
{
    if (defaults is not null)
    {
        var buffered = new MemoryStream();
        defaults.CopyTo(buffered);
        buffered.Position = 0;
        builder.Configuration.Sources.Insert(0, new Microsoft.Extensions.Configuration.Json.JsonStreamConfigurationSource { Stream = buffered });
    }
}

// Later sources win: appsettings.json < ~/.config/devdash/agent.json < DEVDASH_ env vars < command line.
var userConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "devdash", "agent.json");
builder.Configuration.AddJsonFile(userConfig, optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("DEVDASH_");
builder.Configuration.AddInMemoryCollection(cli.Overrides);

// Keep stdout clean for `scan --dry-run`; logs go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
if (logDirectory is not null)
{
    // As a service nobody reads the console, and launchd would append it to a file that never rolls.
    builder.Logging.ClearProviders();
    builder.Logging.AddProvider(new FileLoggerProvider(logDirectory));
}

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<GitRunner>(_ => new GitRunner());
builder.Services.AddSingleton<FetchService>();
builder.Services.AddSingleton<RepoScanner>();
builder.Services.AddSingleton<SizeCache>(_ => new SizeCache());
builder.Services.AddSingleton<MachineScanner>();
builder.Services.AddHttpClient<HubClient>(c => c.Timeout = TimeSpan.FromSeconds(60));

if (cli.Command == "run")
{
    builder.Services.AddAgentActions(); // does nothing unless Agent:Actions:Enabled
    builder.Services.AddHostedService<AgentWorker>();
    await builder.Build().RunAsync();
    return 0;
}

using var host = builder.Build();
var options = host.Services.GetRequiredService<IOptions<AgentOptions>>().Value;
var scanner = host.Services.GetRequiredService<MachineScanner>();
var hub = host.Services.GetRequiredService<HubClient>();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

switch (cli.Command)
{
    case "scan" when cli.DryRun:
    {
        // A dry run never fetches and never writes the size cache: it only reads.
        var snapshot = await scanner.ScanAsync(new ScanRequest(AllowFetch: false, cli.Sizes ? SizeMode.Force : SizeMode.CachedOnly), cts.Token);
        Console.Out.WriteLine(JsonSerializer.Serialize(snapshot, SnapshotSchema.IndentedJsonOptions));
        return 0;
    }

    case "scan":
    {
        var snapshot = await scanner.ScanAsync(new ScanRequest(AllowFetch: true, cli.Sizes ? SizeMode.Force : SizeMode.WhenDue), cts.Token);
        try
        {
            var response = await hub.PushAsync(snapshot, cts.Token);
            Console.Error.WriteLine($"Pushed {response.Entries} entries for {response.MachineId} to {options.HubUrl} (scan took {snapshot.ScanDurationMs} ms).");
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    case "status":
    {
        var machine = await MachineInfoProvider.GetAsync(options, cts.Token);
        Console.Out.WriteLine($"agent      {MachineInfoProvider.AgentVersion}");
        Console.Out.WriteLine($"machine    {machine.MachineId} ({machine.Os}{(machine.TailscaleName is null ? "" : ", " + machine.TailscaleName)})");
        Console.Out.WriteLine($"roots      {string.Join(", ", options.ResolveRoots())}");
        Console.Out.WriteLine($"scan       every {options.ScanIntervalMinutes} min, sizes every {options.SizeScanIntervalHours} h, watch {(options.Watch ? "on" : "off")}");
        Console.Out.WriteLine($"auto-fetch {(options.AutoFetch.Enabled ? $"every {options.AutoFetch.IntervalHours} h" : "off")}");
        Console.Out.WriteLine($"actions    {(options.Actions.Enabled ? "on (allowlisted fixes queued from the dashboard)" : "off (report only)")}");
        Console.Out.WriteLine($"hub        {options.HubUrl} ({(await hub.PingAsync(cts.Token) ? "reachable" : "unreachable")})");
        string keyState;
        try
        {
            keyState = options.ResolveApiKey() is null ? "not configured" : "configured";
        }
        catch (InvalidOperationException ex)
        {
            keyState = ex.Message;
        }

        Console.Out.WriteLine($"api key    {keyState}");
        Console.Out.WriteLine($"service    {await ServiceCli.DescribeStateAsync(cts.Token)}");
        return 0;
    }

    default:
        Console.Error.WriteLine(AgentCli.Usage);
        return 64;
}
