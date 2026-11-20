using DevDash.Contracts;
using DevDash.Core;
using DevDash.Hub.Data;
using DevDash.Hub.GitHub;
using DevDash.Hub.Services;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.T3;

/// <summary>
/// Polls the open issues of the repositories the hub tracks (the GitHub repos agents have clones of),
/// with conditional requests so an unchanged list costs nothing, then runs the label trigger. Runs on the
/// GitHub poll's interval, after the first GitHub poll has said which repos those are.
/// </summary>
public sealed class IssueWorker(
    GitHubIssuesClient client,
    IssueStore store,
    IssueSettingsStore settingsStore,
    GitHubStore github,
    SnapshotStore snapshots,
    ActionQueue queue,
    T3Starts starts,
    DashboardNotifier notifier,
    IOptionsMonitor<GitHubOptions> options,
    TimeProvider time,
    ILogger<IssueWorker> logger) : BackgroundService
{
    /// <summary>Most repositories whose issues are polled, most recently pushed first.</summary>
    public const int MaxRepos = 40;

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly Dictionary<(string Repo, int Number), (DateTimeOffset UpdatedAt, string? Labeler)> _labelers = [];

    public void RequestPoll()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.Enabled)
        {
            return;
        }

        notifier.Changed += OnChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (github.HasData)
                {
                    try
                    {
                        await RunOnceAsync(stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Whatever went wrong, the hub keeps running and the next poll tries again.
                        logger.LogError(ex, "Issue poll or label trigger failed");
                    }
                }

                await _wake.WaitAsync(TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.PollIntervalMinutes)), stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            notifier.Changed -= OnChanged;
        }
    }

    // The first GitHub poll says which repos are tracked; poll their issues straight after it.
    private void OnChanged(DashboardChange change)
    {
        if (change == DashboardChange.GitHub && store.Current.PolledAt is null && github.HasData)
        {
            RequestPoll();
        }
    }

    /// <summary>One poll and one pass of the trigger. Never throws for GitHub trouble.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await _running.WaitAsync(ct);
        try
        {
            await PollAsync(ct);
            await AutomateAsync(ct);
        }
        catch (Exception ex) when (ex is GitHubAuthException or HttpRequestException or System.Text.Json.JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Issue poll failed: {Error}", ex.Message);
            await store.SetAsync((await store.GetAsync(ct)) with { PolledAt = time.GetUtcNow(), Error = ex.Message }, ct);
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>The GitHub repos some machine has a clone of, that GitHub knows, and that are not archived.</summary>
    public IReadOnlyList<(string Key, bool? IsPrivate, bool IsInternal)> TrackedRepos()
    {
        var local = snapshots.Machines
            .SelectMany(m => RepoIdentity.Flatten(m.Snapshot.Entries))
            .Select(e => RepoIdentity.PrimaryRemote(e.Git)?.Normalized)
            .Where(k => k is not null && Core.GitHub.GitHubKeys.IsGitHubKey(k))
            .Select(k => k!.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return github.Current.LocalRemotes.Values
            .Where(l => l.Found && l.Repo is { IsArchived: false } && local.Contains(l.Key))
            .OrderByDescending(l => l.Repo!.PushedAt)
            .Take(MaxRepos)
            .Select(l => (l.Key.ToLowerInvariant(), (bool?)l.Repo!.IsPrivate, l.Repo.IsPrivate && !l.Repo.IsCollaboratorsOnly))
            .ToList();
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var previous = await store.GetAsync(ct);
        var repos = new Dictionary<string, RepoIssues>(StringComparer.OrdinalIgnoreCase);
        var now = time.GetUtcNow();
        foreach (var (key, isPrivate, isInternal) in TrackedRepos())
        {
            var before = previous.Repos.GetValueOrDefault(key);
            var result = await client.ListOpenAsync(key, before?.Error is null ? before?.ETag : null, ct);
            var name = GitHubIssuesClient.TrySplit(key, out var owner, out var repo) ? $"{owner}/{repo}" : key;
            repos[key] = result.Status switch
            {
                IssueListStatus.NotModified when before is not null => before with { FetchedAt = now, IsPrivate = isPrivate, IsInternal = isInternal },
                IssueListStatus.Changed => new RepoIssues
                {
                    RepoKey = key,
                    Repo = result.Issues.FirstOrDefault()?.Repo ?? before?.Repo ?? name,
                    ETag = result.ETag,
                    FetchedAt = now,
                    IsPrivate = isPrivate,
                    IsInternal = isInternal,
                    Issues = result.Issues,
                    Truncated = result.Truncated,
                },
                _ => (before ?? new RepoIssues { RepoKey = key, Repo = name }) with { Error = result.Error ?? "Could not list the issues.", IsPrivate = isPrivate, IsInternal = isInternal },
            };
        }

        await store.SetAsync(new IssueSnapshot { PolledAt = now, Repos = repos }, ct);
        logger.LogInformation("Issue poll: {Issues} open issues in {Repos} repos", repos.Values.Sum(r => r.Issues.Count), repos.Count);
    }

    /// <summary>The machines with a clone of a repo (main working trees), and whether each can start a thread now.</summary>
    public IReadOnlyList<IssueMachineOption> MachinesFor(string repoKey) =>
        snapshots.Machines
            .SelectMany(m => RepoIdentity.Flatten(m.Snapshot.Entries)
                .Where(e => e.Kind == EntryKind.GitRepo && e.Git is { IsLinkedWorktree: false }
                    && string.Equals(RepoIdentity.PrimaryRemote(e.Git)?.Normalized, repoKey, StringComparison.OrdinalIgnoreCase))
                .Select(e =>
                {
                    var availability = queue.Availability(m, AgentActionKind.StartT3Thread);
                    return new IssueMachineOption(m.MachineId, m.Machine.Hostname, e.Path, availability.Available, availability.WhyNot, m.LastSeenAt);
                }))
            .ToList();

    /// <summary>The label trigger, then the write-back for threads it started that have since started.</summary>
    public async Task AutomateAsync(CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        var issues = await store.GetAsync(ct);
        var rows = await starts.IssueThreadsAsync(ct);
        var started = rows.Where(r => r.ActionId is not null).Select(r => (r.Repo, r.Number)).ToHashSet();
        var decided = rows.Where(r => r.ActionId is null).Select(r => (r.Repo, r.Number)).ToHashSet();

        foreach (var repo in issues.Repos.Values)
        {
            foreach (var issue in repo.Issues)
            {
                var key = (issue.RepoKey, issue.Number);
                if (!IssueTrigger.Watches(settings, issue))
                {
                    if (decided.Contains(key))
                    {
                        await starts.ClearDecisionAsync(new IssueRef(issue.RepoKey, issue.Number), ct);
                    }

                    continue;
                }

                if (started.Contains(key))
                {
                    continue;
                }

                var decision = IssueTrigger.Decide(new IssueTriggerInput
                {
                    Issue = issue,
                    Settings = settings,
                    RepoIsPrivate = repo.IsPrivate,
                    RepoIsInternal = repo.IsInternal,
                    ViewerLogin = github.Current.ViewerLogin,
                    Labeler = await LabelerAsync(issue, settings.Label, ct),
                    AlreadyStarted = false,
                    Machines = MachinesFor(issue.RepoKey),
                });
                await ApplyAsync(issue, settings, decision, ct);
            }
        }

        await WriteBackAsync(settings, rows, ct);
    }

    private async Task<string?> LabelerAsync(TrackedIssue issue, string label, CancellationToken ct)
    {
        var key = (issue.RepoKey, issue.Number);
        if (_labelers.TryGetValue(key, out var cached) && cached.UpdatedAt == issue.UpdatedAt)
        {
            return cached.Labeler;
        }

        var labeler = await client.LatestLabelerAsync(issue.RepoKey, issue.Number, label, ct);
        _labelers[key] = (issue.UpdatedAt, labeler);
        return labeler;
    }

    private async Task ApplyAsync(TrackedIssue issue, IssueSettings settings, IssueTriggerDecision decision, CancellationToken ct)
    {
        var reference = new IssueRef(issue.RepoKey, issue.Number);
        switch (decision.Verdict)
        {
            case IssueTriggerVerdict.NeedsManualStart:
                await starts.RecordDecisionAsync(reference, IssueThreadStatus.NeedsManualStart, decision.Reason, null, ct);
                return;
            case IssueTriggerVerdict.Pending:
                await starts.RecordDecisionAsync(reference, IssueThreadStatus.Pending, decision.Reason, null, ct);
                return;
            case IssueTriggerVerdict.Start when decision.Machine is { } machine:
                var request = await BuildRequestAsync(issue, settings.Template, machine.MachineId, machine.RepoPath, automatic: true, ct);
                if (request is null)
                {
                    await starts.RecordDecisionAsync(reference, IssueThreadStatus.Pending, $"{machine.Hostname} no longer reports the clone", null, ct);
                    return;
                }

                var outcome = await starts.StartAsync(request, $"{IssueThreadEntity.Auto} ({settings.Label} label)", ct);
                if (!outcome.Ok)
                {
                    await starts.RecordDecisionAsync(reference, IssueThreadStatus.Pending, outcome.Error ?? "could not be queued", machine.MachineId, ct);
                }
                else
                {
                    logger.LogInformation("Started a T3 thread for {Repo}#{Number} on {Machine} (label {Label})", issue.Repo, issue.Number, machine.Hostname, settings.Label);
                }

                return;
        }
    }

    /// <summary>Most comments read for an automatic start, before the ones by people not allowed are left out (one page).</summary>
    private const int CommentsRead = 100;

    /// <summary>
    /// The start request for an issue on one clone: the prompt from the template with the latest comments, a free
    /// branch, the default base. On a repository anyone can comment on (<see cref="IssueTrigger.CollaboratorsOnly"/>
    /// false), an automatic start leaves out every comment by a login that is not allowed and says how many it left
    /// out; a manual start keeps them (a person reads the prompt first) and its <see cref="T3StartRequest.Warning"/>
    /// names their authors.
    /// </summary>
    public async Task<T3StartRequest?> BuildRequestAsync(TrackedIssue issue, string? template, string machineId, string repoPath, bool automatic, CancellationToken ct, bool again = false)
    {
        if (snapshots.Get(machineId) is not { } machine
            || RepoIdentity.Flatten(machine.Snapshot.Entries).FirstOrDefault(e => e.Path == repoPath && e.Kind == EntryKind.GitRepo)?.Git is not { } git)
        {
            return null;
        }

        var branch = T3Prompts.FreeBranch(T3Prompts.IssueBranch(issue.Number, issue.Title), git.Branches.Select(b => b.Name));
        var repoIssues = (await store.GetAsync(ct)).Repos.GetValueOrDefault(issue.RepoKey);
        var strangersWrite = !IssueTrigger.CollaboratorsOnly(repoIssues?.IsPrivate, repoIssues?.IsInternal ?? false);
        var allowed = (await settingsStore.GetAsync(ct)).AllowedSet(github.Current.ViewerLogin);
        bool Allowed(IssueComment c) => c.Author is { } login && allowed.Contains(login);

        IReadOnlyList<IssueComment> comments = [];
        try
        {
            comments = await client.LatestCommentsAsync(issue.RepoKey, issue.Number, issue.Comments, automatic && strangersWrite ? CommentsRead : T3Prompts.MaxComments, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or GitHubAuthException)
        {
            logger.LogDebug("Could not read the comments of {Repo}#{Number}: {Error}", issue.Repo, issue.Number, ex.Message);
        }

        var omitted = 0;
        string? warning = null;
        if (strangersWrite && automatic)
        {
            omitted = comments.Count(c => !Allowed(c));
            comments = comments.Where(Allowed).TakeLast(T3Prompts.MaxComments).ToList();
        }
        else if (strangersWrite)
        {
            var strangers = comments.TakeLast(T3Prompts.MaxComments).Where(c => !Allowed(c)).Select(c => c.Author ?? "an unknown user").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (strangers.Count > 0)
            {
                warning = $"The prompt includes comments by {string.Join(", ", strangers)}, who {(strangers.Count == 1 ? "is" : "are")} not on the allowed list: read them in the prompt before starting, or take them out.";
            }
        }

        var prompt = T3Prompts.RenderIssue(template, new IssuePromptInput
        {
            Repo = issue.Repo,
            Number = issue.Number,
            Url = issue.Url,
            Title = issue.Title,
            Body = issue.Body,
            Author = issue.Author,
            Labels = issue.Labels,
            Comments = comments,
            OmittedComments = omitted,
            Branch = branch,
        });
        return new T3StartRequest
        {
            MachineId = machineId,
            RepoPath = repoPath,
            RepoKey = issue.RepoKey,
            Prompt = prompt,
            Title = T3Prompts.IssueTitle(issue.Number, issue.Title),
            WorktreeBase = T3Prompts.DefaultBase(git),
            NewBranch = branch,
            Origin = "issue",
            Issue = new IssueRef(issue.RepoKey, issue.Number),
            Automatic = automatic,
            Again = again,
            Warning = warning,
        };
    }

    /// <summary>Optional, off by default: once a thread the trigger started has started, say so on the issue. No links, no machine names.</summary>
    private async Task WriteBackAsync(IssueSettings settings, IReadOnlyList<IssueThreadEntity> rows, CancellationToken ct)
    {
        if (!settings.CommentOnStart && !settings.SwapLabel)
        {
            return;
        }

        foreach (var row in rows.Where(r => r is { StartedBy: IssueThreadEntity.Auto, Status: IssueThreadStatus.Started, WriteBack: null, ActionId: not null }))
        {
            var notes = new List<string>();
            if (settings.CommentOnStart)
            {
                var comment = await client.CommentAsync(row.Repo, row.Number, IssueSettings.StartedComment, ct);
                notes.Add(comment.Ok ? "commented" : "could not comment: " + comment.Error);
            }

            if (settings.SwapLabel)
            {
                var swap = await client.SwapLabelAsync(row.Repo, row.Number, settings.Label, settings.StartedLabel, ct);
                notes.Add(swap.Ok ? $"label changed to {settings.StartedLabel}" : "could not change the label: " + swap.Error);
            }

            await starts.RecordWriteBackAsync(row.ActionId!.Value, string.Join("; ", notes), ct);
        }
    }
}
