using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MiBitacora.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    internal DbSet<JournalProfileRow> JournalProfiles => Set<JournalProfileRow>();
    internal DbSet<JournalEntryRow> JournalEntries => Set<JournalEntryRow>();
    internal DbSet<JournalNoteRow> JournalNotes => Set<JournalNoteRow>();
    internal DbSet<JournalAttachmentRow> JournalAttachments => Set<JournalAttachmentRow>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<JournalProfileRow>(entity =>
        {
            entity.HasKey(row => row.UserId);
            entity.HasOne<ApplicationUser>().WithOne()
                .HasForeignKey<JournalProfileRow>(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<JournalEntryRow>(entity =>
        {
            entity.HasIndex(row => new { row.UserId, row.CreatedUtc }).IsUnique();
            entity.HasOne<ApplicationUser>().WithMany()
                .HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<JournalNoteRow>(entity =>
        {
            entity.HasIndex(row => new { row.UserId, row.CreatedUtc });
            entity.HasOne<ApplicationUser>().WithMany()
                .HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<JournalAttachmentRow>(entity =>
        {
            entity.HasIndex(row => new { row.EntryId, row.Position }).IsUnique();
            entity.HasOne(row => row.Entry).WithMany(row => row.Attachments)
                .HasForeignKey(row => row.EntryId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
