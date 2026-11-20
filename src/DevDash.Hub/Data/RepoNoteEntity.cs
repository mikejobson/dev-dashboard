using Microsoft.EntityFrameworkCore;

namespace DevDash.Hub.Data;

/// <summary>
/// What the user wrote about one repo on the hub: tags, a one-line note and whether it is pinned. Keyed by
/// the repo's dashboard key (<c>github.com/owner/repo</c>, or <c>local:&lt;machine&gt;:&lt;path&gt;</c> for a repo
/// with no hosted remote), lower-cased, so it follows the repo across machines and clones. Nothing here
/// reaches an agent. A row with no tags, no note and no pin is deleted rather than kept empty.
/// </summary>
public sealed class RepoNoteEntity
{
    public required string Key { get; set; }

    /// <summary>Normalised tags (see <see cref="Services.RepoTags.Parse"/>), separated by single spaces.</summary>
    public string Tags { get; set; } = "";
    public string Note { get; set; } = "";
    public bool Pinned { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";

    internal static void Configure(ModelBuilder modelBuilder) => modelBuilder.Entity<RepoNoteEntity>(e =>
    {
        e.ToTable("RepoNotes");
        e.HasKey(n => n.Key);
        e.Property(n => n.Key).HasMaxLength(1024);
        e.Property(n => n.Tags).HasMaxLength(512);
        e.Property(n => n.Note).HasMaxLength(512);
    });
}
