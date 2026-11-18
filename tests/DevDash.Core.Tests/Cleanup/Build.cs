using DevDash.Contracts;
using DevDash.Core.Cleanup;
using DevDash.Core.GitHub;

namespace DevDash.Core.Tests.Cleanup;

/// <summary>Small builders so each rule test reads as "given this repo, expect that finding".</summary>
internal static class Build
{
    public static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset DaysAgo(double days) => Now.AddDays(-days);

    public static MachineSnapshot Machine(string id, params DevEntry[] entries) => new()
    {
        Machine = new MachineInfo { MachineId = id, Hostname = id },
        ScannedAt = Now,
        Roots = ["/dev"],
        Entries = entries,
    };

    public static GitState GitOf(
        string branch = "main",
        string? remote = "https://github.com/acme/widget.git",
        Func<GitState, GitState>? with = null)
    {
        var git = new GitState
        {
            Branch = branch,
            HeadSha = "1111111111111111111111111111111111111111",
            Upstream = remote is null ? null : $"origin/{branch}",
            DefaultBranch = "main",
            LastCommit = new CommitInfo { Sha = "1111111111111111111111111111111111111111", Date = DaysAgo(1), Subject = "work", Author = "me" },
            Remotes = remote is null
                ? []
                : [new RemoteInfo { Name = "origin", Url = remote, Normalized = DevDash.Core.Git.RemoteUrlNormalizer.Normalize(remote)?.Key }],
            Branches = [Branch(branch, upstream: remote is null ? null : $"origin/{branch}", current: true)],
        };
        return with is null ? git : with(git);
    }

    public static BranchInfo Branch(
        string name, string? upstream = null, bool gone = false, int ahead = 0, int behind = 0,
        bool? merged = false, bool current = false, double ageDays = 1) => new()
        {
            Name = name,
            Sha = "2222222222222222222222222222222222222222",
            Upstream = upstream,
            UpstreamGone = gone,
            Ahead = ahead,
            Behind = behind,
            MergedIntoDefault = merged,
            IsCurrent = current,
            LastCommitDate = DaysAgo(ageDays),
        };

    public static DevEntry Repo(string name, GitState? git = null, double modifiedDaysAgo = 1, SizeInfo? size = null) => new()
    {
        Root = "/dev",
        Name = name,
        Path = $"/dev/{name}",
        Kind = EntryKind.GitRepo,
        Git = WithPaths(git ?? GitOf(), $"/dev/{name}"),
        LastModified = DaysAgo(modifiedDaysAgo),
        Size = size,
    };

    public static DevEntry Folder(string name, EntryKind kind = EntryKind.NonGitFolder, params DevEntry[] children) => new()
    {
        Root = "/dev",
        Name = name,
        Path = $"/dev/{name}",
        Kind = kind,
        Children = children.Select(c => c with { Path = $"/dev/{name}/{c.Name}", Git = c.Git is null ? null : WithPaths(c.Git with { CommonDir = null, Worktrees = [] }, $"/dev/{name}/{c.Name}") }).ToList(),
        Folder = new FolderSummary { FileCount = 3, DirectoryCount = 1, SampleNames = ["a.pdf", "b.pdf"] },
        LastModified = DaysAgo(10),
    };

    /// <summary>The finding's Fix command lines, in order.</summary>
    public static List<string> Fixes(Finding finding) => Of(finding, CommandKind.Fix);

    /// <summary>The finding's Inspect command lines, in order.</summary>
    public static List<string> Inspects(Finding finding) => Of(finding, CommandKind.Inspect);

    public static List<string> Of(Finding finding, CommandKind kind) =>
        finding.Commands.Where(c => c.Kind == kind).Select(c => c.Command).ToList();

    /// <summary>The single Fix command, for findings that have exactly one.</summary>
    public static FindingCommand Fix(Finding finding) => Assert.Single(finding.Commands, c => c.Kind == CommandKind.Fix);

    public static CleanupContext Context(params MachineSnapshot[] machines) => new() { Machines = machines, Now = Now };

    public static PullRequestInfo Pr(int number, double idleDays, bool draft = false, CiState ci = CiState.Success, bool authored = true) => new()
    {
        Repo = "acme/widget",
        Number = number,
        Title = $"PR {number}",
        Url = $"https://github.com/acme/widget/pull/{number}",
        IsDraft = draft,
        Ci = ci,
        Authored = authored,
        ReviewRequested = !authored,
        CreatedAt = DaysAgo(idleDays + 5),
        UpdatedAt = DaysAgo(idleDays),
    };

    /// <summary>Fills in the common dir and main worktree the way the agent would for a standalone clone.</summary>
    private static GitState WithPaths(GitState git, string path) => git with
    {
        CommonDir = git.CommonDir ?? $"{path}/.git",
        Worktrees = git.Worktrees.Count > 0 ? git.Worktrees : [new WorktreeInfo { Path = path, Branch = git.Branch, Head = git.HeadSha }],
    };
}
