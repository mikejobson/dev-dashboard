using System.Diagnostics;
using System.Net.Http.Json;
using DevDash.Agent;
using DevDash.Agent.Actions;
using DevDash.Agent.Git;
using DevDash.Agent.Scanning;
using DevDash.Contracts;
using DevDash.Hub.Data;
using DevDash.Hub.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.Tests;

/// <summary>
/// The whole loop with the real agent code talking to the real hub: scan a throwaway repository, queue the
/// fix its finding suggests, let the agent poll, run and report, and see the finding go after the rescan.
/// </summary>
public sealed class AgentLoopTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("devdash-loop-").FullName;
    private readonly string _dev;

    public AgentLoopTests()
    {
        _dev = Directory.CreateDirectory(Path.Combine(_root, "Developer")).FullName;
        var widget = Directory.CreateDirectory(Path.Combine(_dev, "widget")).FullName;
        Git(widget, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(widget, "README.md"), "# Widget\n");
        Git(widget, "add", ".");
        Git(widget, "commit", "-q", "-m", "initial");
        Git(widget, "branch", "old-fix");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Queue_poll_run_report_rescan(bool agentAllowsActions)
    {
        using var factory = new HubFactory();
        var key = await factory.IssueKeyAsync("loop");
        var options = new StaticOptions(new AgentOptions
        {
            Roots = [_dev],
            MachineId = "loop-box",
            HubUrl = "http://localhost/",
            ApiKey = key,
            AutoFetch = new AutoFetchOptions { Enabled = false },
            Actions = new ActionsOptions { Enabled = true },
        });
        var git = new GitRunner();
        var scanner = new MachineScanner(
            new RepoScanner(git, new FetchService(git, options, NullLogger<FetchService>.Instance), options),
            new SizeCache(Path.Combine(_root, "sizes.json")), options, NullLogger<MachineScanner>.Instance);
        var agentHttp = factory.CreateClient();

        async Task PushSnapshotAsync()
        {
            var snapshot = await scanner.ScanAsync(new ScanRequest(AllowFetch: false, SizeMode.CachedOnly), CancellationToken.None);
            var push = new HubClient(agentHttp, options);
            await push.PushAsync(snapshot, CancellationToken.None);
        }

        await PushSnapshotAsync();

        // The dashboard side: the merged-branch finding offers a typed action for this machine.
        var findings = factory.Services.GetRequiredService<FindingsService>();
        var view = (await findings.GetAsync()).Single(v => v.Finding.RuleId == "merged-branch");
        var command = view.Finding.Commands.Single(c => c.Action is not null);
        Assert.True(view.CanRun(command));
        var queued = await factory.Services.GetRequiredService<ActionQueue>().QueueAsync("loop-box", command.Action!, "me@example.com", view.Finding);
        Assert.True(queued.Ok, queued.Error);

        // The agent side. In one variant the machine's owner has since switched actions off locally:
        // the hub still believes they are on, and the agent must refuse on its own.
        options.CurrentValue.Actions.Enabled = agentAllowsActions;
        var rescans = new RescanRequests(options);
        var rescanned = new List<string>();
        rescans.Requested += (_, name) => rescanned.Add(name);
        var worker = new ActionWorker(
            new ActionsClient(agentHttp, options),
            new ActionExecutor(git, new ActionValidator(git, options), options, NullLogger<ActionExecutor>.Instance),
            rescans, options, NullLogger<ActionWorker>.Instance);

        Assert.Equal(1, await worker.PollOnceAsync(CancellationToken.None));
        Assert.Equal(0, await worker.PollOnceAsync(CancellationToken.None)); // nothing is handed out twice

        var row = Assert.Single(await factory.Services.GetRequiredService<ActionQueue>().RecentAsync());
        var branches = Git(Path.Combine(_dev, "widget"), "for-each-ref", "--format=%(refname:short)", "refs/heads").Split('\n');
        if (agentAllowsActions)
        {
            Assert.Equal(AgentActionStatus.Succeeded, row.Status);
            Assert.Equal(0, row.ExitCode);
            Assert.Contains("Deleted branch old-fix", row.Output);
            Assert.DoesNotContain("old-fix", branches);
            Assert.Equal(["widget"], rescanned);

            await PushSnapshotAsync(); // what the scan loop does when asked to rescan
            Assert.DoesNotContain(await findings.GetAsync(), v => v.Finding.RuleId == "merged-branch");
        }
        else
        {
            Assert.Equal(AgentActionStatus.Failed, row.Status);
            Assert.Null(row.ExitCode);
            Assert.Contains("not enabled on this machine", row.Output);
            Assert.Contains("old-fix", branches);
        }
    }

    /// <summary>
    /// Remove and re-clone end to end: the hub's verdict and fingerprint come from the snapshot, the agent's
    /// from its own fetch and scan, and they have to agree for anything to move. Then the tombstone's
    /// clone action brings the project back, and the tombstone goes.
    /// </summary>
    [Fact]
    public async Task Remove_to_the_trash_then_clone_back()
    {
        var origin = Path.Combine(_root, "origin", "gadget.git");
        Directory.CreateDirectory(Path.GetDirectoryName(origin)!);
        Git(_root, "clone", "-q", "--bare", Path.Combine(_dev, "widget"), origin);
        Git(_dev, "clone", "-q", origin, "gadget");
        var gadget = Path.Combine(_dev, "gadget");
        var trash = Path.Combine(_root, "trash");

        using var factory = new HubFactory();
        var options = new StaticOptions(new AgentOptions
        {
            Roots = [_dev],
            MachineId = "loop-box",
            HubUrl = "http://localhost/",
            ApiKey = await factory.IssueKeyAsync("loop"),
            Actions = new ActionsOptions { Enabled = true },
        });
        var git = new GitRunner();
        var scanner = new MachineScanner(
            new RepoScanner(git, new FetchService(git, options, NullLogger<FetchService>.Instance), options),
            new SizeCache(Path.Combine(_root, "sizes.json")), options, NullLogger<MachineScanner>.Instance);
        var agentHttp = factory.CreateClient();
        var known = new KnownRemotes();
        var removals = new RemovalLog(Path.Combine(_root, "removed.json"));

        async Task PushSnapshotAsync()
        {
            var snapshot = await scanner.ScanAsync(new ScanRequest(AllowFetch: true, SizeMode.Force), CancellationToken.None);
            known.Update(snapshot);
            await new HubClient(agentHttp, options).PushAsync(snapshot, CancellationToken.None);
        }

        var validator = new ActionValidator(git, options, removals, known);
        var rescans = new RescanRequests(options);
        var worker = new ActionWorker(
            new ActionsClient(agentHttp, options),
            new ActionExecutor(git, validator, options, NullLogger<ActionExecutor>.Instance,
                projects: new ProjectActions(git, validator, options, NullLogger<ProjectActions>.Instance, scanner, new FolderTrash(trash), removals)),
            rescans, options, NullLogger<ActionWorker>.Instance);

        await PushSnapshotAsync();
        var queue = factory.Services.GetRequiredService<ActionQueue>();
        var tombstones = factory.Services.GetRequiredService<TombstoneStore>();

        // The dashboard: gadget is plainly safe (cloned, nothing added); widget has no remote and is not.
        var rows = factory.Services.GetRequiredService<SafetyService>().Get();
        var row = rows.Single(r => r.Entry.Name == "gadget");
        Assert.Equal(DevDash.Core.Safety.SafetyVerdict.Safe, row.Safety.Verdict);
        Assert.True(rows.Single(r => r.Entry.Name == "widget").Safety.IsAtRisk);
        Assert.True(queue.Availability(row.Machine, AgentActionKind.RemoveProject).Available);

        var queued = await queue.QueueAsync("loop-box", AgentAction.RemoveProject(row.Entry.Path, row.Safety.Fingerprint), "me@example.com");
        Assert.True(queued.Ok, queued.Error);
        Assert.Equal(1, await worker.PollOnceAsync(CancellationToken.None));

        var removal = (await queue.RecentAsync()).Single(a => a.Kind == AgentActionKind.RemoveProject);
        Assert.True(removal.Status == AgentActionStatus.Succeeded, removal.Output);
        Assert.False(Directory.Exists(gadget));
        Assert.True(File.Exists(Path.Combine(trash, "gadget", "README.md")));

        await PushSnapshotAsync(); // what the scan loop does when asked to rescan
        var tombstone = Assert.Single(await tombstones.ListAsync());
        Assert.Equal(row.Entry.Path, tombstone.Tombstone.Path);
        Assert.True(tombstone.Tombstone.SizeBytes > 0);
        Assert.DoesNotContain(factory.Services.GetRequiredService<SafetyService>().Get(), r => r.Entry.Name == "gadget");

        // Back again, from the tombstone's own clone action.
        var clone = await queue.QueueAsync("loop-box", tombstone.CloneAction!, "me@example.com");
        Assert.True(clone.Ok, clone.Error);
        Assert.Equal(1, await worker.PollOnceAsync(CancellationToken.None));

        var cloned = (await queue.RecentAsync()).Single(a => a.Kind == AgentActionKind.Clone);
        Assert.True(cloned.Status == AgentActionStatus.Succeeded, cloned.Output);
        Assert.True(File.Exists(Path.Combine(gadget, "README.md")));
        Assert.Empty(await tombstones.ListAsync());
        Assert.True(Directory.Exists(Path.Combine(trash, "gadget"))); // the trashed copy is left alone

        // The agent would not have cloned anything else into that folder, whatever the hub said.
        var arbitrary = await new ActionExecutor(git, validator, options, NullLogger<ActionExecutor>.Instance)
            .ExecuteAsync(AgentAction.CloneInto(Path.Combine(_dev, "evil"), "https://example.invalid/evil.git"), CancellationToken.None);
        Assert.True(arbitrary.Refused);
        Assert.False(Directory.Exists(Path.Combine(_dev, "evil")));
    }

    private static string Git(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in (string[])["-c", "user.name=DevDash Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", .. args])
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        psi.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private sealed class StaticOptions(AgentOptions value) : IOptionsMonitor<AgentOptions>
    {
        public AgentOptions CurrentValue => value;

        public AgentOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<AgentOptions, string?> listener) => null;
    }
}
