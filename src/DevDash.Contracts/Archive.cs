namespace DevDash.Contracts;

/// <summary>
/// Where this machine's agent archives projects (<c>Agent:Archive:Folder</c>), as of its last full scan.
/// Null in <see cref="MachineInfo.Archive"/> when archiving is not set up, or from an agent before 0.8.0.
/// </summary>
public sealed record ArchiveInfo
{
    /// <summary>The folder archives are written to, as configured (home expanded).</summary>
    public required string Folder { get; init; }

    /// <summary>The folder exists. A NAS that is not mounted reads as false: the agent never creates the folder itself.</summary>
    public bool Available { get; init; }

    /// <summary>A probe file could be created and deleted in it.</summary>
    public bool Writable { get; init; }

    /// <summary>Free space on the volume that holds it, in bytes. Null when it could not be read.</summary>
    public long? FreeBytes { get; init; }

    /// <summary>How much more than the estimate the agent wants free before it starts, in bytes.</summary>
    public long MarginBytes { get; init; }

    /// <summary><see cref="ArchiveFormats.TarGz"/> on macOS and Linux, <see cref="ArchiveFormats.Zip"/> on Windows.</summary>
    public string Format { get; init; } = "";

    /// <summary>Why archiving cannot be used right now, in words. Null when it can.</summary>
    public string? Problem { get; init; }
}

public static class ArchiveFormats
{
    public const string TarGz = "tar.gz";
    public const string Zip = "zip";

    /// <summary>The file name extension, without the dot.</summary>
    public static string Extension(string format) => format == Zip ? "zip" : "tar.gz";
}

/// <summary>
/// The sidecar written beside every archive (<c>&lt;name&gt;-&lt;stamp&gt;.json</c>). It is what a restore checks
/// the archive against, and what the dashboard shows about it. Everything a person needs to know what is
/// in the archive without opening it.
/// </summary>
public sealed record ArchiveManifest
{
    public int ManifestVersion { get; init; } = 1;

    /// <summary>The folder's own name; every entry in the archive is under it.</summary>
    public required string RootName { get; init; }

    /// <summary>The folder that was archived.</summary>
    public required string SourcePath { get; init; }

    public string MachineId { get; init; } = "";
    public string Hostname { get; init; } = "";

    /// <summary>The archive's file name (in the same folder as this manifest).</summary>
    public required string ArchiveFile { get; init; }

    /// <summary><see cref="ArchiveFormats.TarGz"/> or <see cref="ArchiveFormats.Zip"/>.</summary>
    public required string Format { get; init; }

    /// <summary>SHA-256 of the archive file, lower-case hex. A restore refuses an archive that does not match.</summary>
    public required string ArchiveSha256 { get; init; }

    /// <summary>The archive file's size.</summary>
    public long ArchiveBytes { get; init; }

    /// <summary>Entries in the archive by kind, and the bytes of the files (uncompressed).</summary>
    public int FileCount { get; init; }
    public int DirectoryCount { get; init; }
    public int LinkCount { get; init; }
    public long TotalBytes { get; init; }

    /// <summary>
    /// SHA-256 over every entry in order (name, kind, size, link target, SHA-256 of its content). Written
    /// while archiving, checked when the archive is read back, and again after a restore.
    /// </summary>
    public required string ContentSha256 { get; init; }

    /// <summary>Regenerable build output left out (paths relative to the folder), unless asked to keep it.</summary>
    public IReadOnlyList<string> ExcludedBuildOutput { get; init; } = [];

    /// <summary>True when build output was kept.</summary>
    public bool IncludesBuildOutput { get; init; }

    /// <summary>Things that could not go in the archive (a symbolic link on Windows), relative to the folder.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    public IReadOnlyList<RemoteInfo> Remotes { get; init; } = [];
    public IReadOnlyList<ArchivedBranch> Branches { get; init; } = [];

    /// <summary>The checked-out branch, null when detached.</summary>
    public string? HeadBranch { get; init; }

    /// <summary>The commit HEAD was at.</summary>
    public string? HeadSha { get; init; }

    /// <summary>The safety verdict and fingerprint the archive was made under: an archive can hold work that was nowhere else.</summary>
    public string? Verdict { get; init; }
    public string? Fingerprint { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public string DevDashVersion { get; init; } = "";
}

public sealed record ArchivedBranch(string Name, string? Sha);

/// <summary>What an <see cref="AgentActionKind.ArchiveProject"/> reports back besides its output: where the archive is, and its manifest.</summary>
public sealed record ArchiveResult
{
    public required string ArchivePath { get; init; }
    public required string ManifestPath { get; init; }
    public required ArchiveManifest Manifest { get; init; }
}
