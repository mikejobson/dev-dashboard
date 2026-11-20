namespace DevDash.Contracts;

/// <summary>
/// The developer caches outside the repositories that an agent measures (§21). The snapshot carries the id as
/// text (<see cref="DiskItem.Id"/>), so a hub that meets an id from a newer agent shows it plainly instead of
/// failing to read the snapshot. <see cref="AgentActionKind.CleanCache"/> names one of these, and nothing else.
/// </summary>
public enum DiskCacheId
{
    NuGetGlobalPackages,
    NuGetHttpCache,
    NpmCache,
    PnpmStore,
    YarnCache,
    XcodeDerivedData,
    XcodeArchives,
    XcodeDeviceSupport,
    CoreSimulator,
    DockerImages,
    DockerBuildCache,
    DockerVolumes,
    DotnetSdks,
    HomebrewCache,
    GradleCache,
    MavenRepository,
}

/// <summary>What one machine's developer caches take up, measured in the background every few hours.</summary>
public sealed record DiskReport
{
    /// <summary>When the newest measurement in <see cref="Items"/> was finished.</summary>
    public DateTimeOffset CollectedAt { get; init; }

    public IReadOnlyList<DiskItem> Items { get; init; } = [];
}

/// <summary>One cache, as found and measured on the machine.</summary>
public sealed record DiskItem
{
    /// <summary>A <see cref="DiskCacheId"/> name.</summary>
    public required string Id { get; init; }

    /// <summary>The folder measured. Null for Docker, whose sizes come from Docker itself.</summary>
    public string? Path { get; init; }

    /// <summary>Its size. Null when it could not be measured (see <see cref="Problem"/>).</summary>
    public long? Bytes { get; init; }

    /// <summary>
    /// What cleaning would free, when the tool says (Docker). Null means "about all of it" for a cache that is
    /// cleared, and "unknown" for one that is pruned.
    /// </summary>
    public long? ReclaimableBytes { get; init; }

    /// <summary>The newest file's time: when the cache was last written to.</summary>
    public DateTimeOffset? LastModified { get; init; }

    public DateTimeOffset MeasuredAt { get; init; }

    /// <summary>How many things it holds, where that means something (Docker images, volumes).</summary>
    public int? Count { get; init; }

    /// <summary>True when this agent can clean it here: the recipe has one and the tool it needs was found.</summary>
    public bool Cleanable { get; init; }

    /// <summary>Why the size is missing or partial: timed out, unreadable, the tool did not answer.</summary>
    public string? Problem { get; init; }

    /// <summary>What it is made of, where that helps decide: one per .NET SDK, one per iOS DeviceSupport version. At most 50.</summary>
    public IReadOnlyList<DiskPart> Parts { get; init; } = [];
}

public sealed record DiskPart
{
    public required string Name { get; init; }
    public long Bytes { get; init; }
    public DateTimeOffset? LastModified { get; init; }
}

/// <summary>How a cache is cleaned: a fixed tool and argument list, or moving the folder's contents to the trash.</summary>
/// <param name="Tool">The program, found by the agent itself (<c>dotnet</c>, <c>npm</c>…). Null for <paramref name="TrashContents"/>.</param>
public sealed record CleanRecipe(string? Tool, IReadOnlyList<string> Args, bool TrashContents = false);

/// <summary>What each cache is, and what cleaning it does. Shared, so the dialog shows what the agent runs.</summary>
public static class DiskCaches
{
    public sealed record Info(DiskCacheId Id, string Title, string What, string Cleaning, bool Regenerable, CleanRecipe? Recipe);

    private static readonly Dictionary<DiskCacheId, Info> All = new Info[]
    {
        new(DiskCacheId.NuGetGlobalPackages, "NuGet packages", "Every package version any project has restored.",
            "Deletes them; the next restore downloads what each project needs again.", true,
            new("dotnet", ["nuget", "locals", "global-packages", "--clear"])),
        new(DiskCacheId.NuGetHttpCache, "NuGet HTTP cache", "Downloaded package listings and files, kept for 30 minutes of offline restores.",
            "Deletes it; restores fetch from the feed again.", true,
            new("dotnet", ["nuget", "locals", "http-cache", "--clear"])),
        new(DiskCacheId.NpmCache, "npm cache", "Package tarballs npm has downloaded.",
            "Deletes it; installs download again.", true,
            new("npm", ["cache", "clean", "--force"])),
        new(DiskCacheId.PnpmStore, "pnpm store", "The content-addressed store every pnpm project links from.",
            "Removes only packages no project on this machine references any more.", true,
            new("pnpm", ["store", "prune"])),
        new(DiskCacheId.YarnCache, "Yarn cache", "Package archives Yarn has downloaded.", "Report only.", true, null),
        new(DiskCacheId.XcodeDerivedData, "Xcode DerivedData", "Build products and indexes, one folder per project.",
            "Moves the folders inside it to the Trash; Xcode rebuilds and reindexes on next open.", true,
            new(null, [], TrashContents: true)),
        new(DiskCacheId.XcodeArchives, "Xcode Archives", "Archived builds, with the symbols needed to read crash reports.",
            "Report only: archives cannot be made again.", false, null),
        new(DiskCacheId.XcodeDeviceSupport, "iOS DeviceSupport", "Debug symbols copied from each iOS version a device was connected with.",
            "Report only. Xcode copies them again when a device on that version connects.", true, null),
        new(DiskCacheId.CoreSimulator, "Simulators", "Simulator devices and their caches.",
            "Deletes simulators whose runtime is no longer installed (xcrun simctl delete unavailable).", true,
            new("xcrun", ["simctl", "delete", "unavailable"])),
        new(DiskCacheId.DockerImages, "Docker images", "Every image, used or not.",
            "Removes dangling images only (untagged layers left by rebuilds). Tagged images are kept, even unused ones.", true,
            new("docker", ["image", "prune", "-f"])),
        new(DiskCacheId.DockerBuildCache, "Docker build cache", "Layers BuildKit keeps to speed up builds.",
            "Removes dangling build cache only; the next build may be slower.", true,
            new("docker", ["builder", "prune", "-f"])),
        new(DiskCacheId.DockerVolumes, "Docker volumes", "Data containers keep: databases, uploads.",
            "Report only: volumes hold data, never cleaned from here.", false, null),
        new(DiskCacheId.DotnetSdks, ".NET SDKs", "Installed SDK versions.",
            "Report only: uninstall unused SDKs with the installer or dotnet-core-uninstall.", false, null),
        new(DiskCacheId.HomebrewCache, "Homebrew cache", "Downloaded bottles and source archives.",
            "brew cleanup -s: removes old versions of installed formulae and every cached download.", true,
            new("brew", ["cleanup", "-s"])),
        new(DiskCacheId.GradleCache, "Gradle caches", "Dependencies and build caches Gradle has downloaded.", "Report only.", true, null),
        new(DiskCacheId.MavenRepository, "Maven repository", "Dependencies Maven has downloaded (~/.m2/repository).", "Report only.", true, null),
    }.ToDictionary(i => i.Id);

    public static Info Get(DiskCacheId id) => All[id];

    /// <summary>The cache an id names, or null for an id this build does not know.</summary>
    public static Info? Find(string? id) =>
        id is not null && Enum.TryParse<DiskCacheId>(id, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) && parsed.ToString() == id ? All[parsed] : null;

    /// <summary>True when the agent knows how to clean it at all. Whether this machine can is <see cref="DiskItem.Cleanable"/>.</summary>
    public static bool HasRecipe(DiskCacheId id) => All[id].Recipe is not null;

    /// <summary>The recipe as a person would type it, for the dialog and the audit trail.</summary>
    public static string CommandLine(DiskCacheId id, string? path = null) => All[id].Recipe switch
    {
        null => "",
        { TrashContents: true } => $"move the contents of {AgentActions.ShellQuote(path ?? "~/Library/Developer/Xcode/DerivedData")} to the Trash",
        { } recipe => string.Join(' ', [recipe.Tool!, .. recipe.Args]),
    };
}
