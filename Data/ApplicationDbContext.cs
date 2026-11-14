using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MiBitacora.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    internal DbSet<JournalProfileRow> JournalProfiles => Set<JournalProfileRow>();
    internal DbSet<JournalMemoryRow> JournalMemories => Set<JournalMemoryRow>();
    internal DbSet<JournalTopicOverviewRow> JournalTopicOverviews => Set<JournalTopicOverviewRow>();
    internal DbSet<JournalEntryRow> JournalEntries => Set<JournalEntryRow>();
    internal DbSet<JournalNotebookRow> JournalNotebooks => Set<JournalNotebookRow>();
    internal DbSet<JournalNoteRow> JournalNotes => Set<JournalNoteRow>();
    internal DbSet<JournalAttachmentRow> JournalAttachments => Set<JournalAttachmentRow>();
    public DbSet<BillingSubscriptionRow> BillingSubscriptions => Set<BillingSubscriptionRow>();
    public DbSet<BillingPaymentRow> BillingPayments => Set<BillingPaymentRow>();
    public DbSet<BillingWebhookRow> BillingWebhooks => Set<BillingWebhookRow>();
    public DbSet<BillingAccessGrantRow> BillingAccessGrants => Set<BillingAccessGrantRow>();
    public DbSet<AccountAiPreferenceRow> AccountAiPreferences => Set<AccountAiPreferenceRow>();
    internal DbSet<JournalVectorStateRow> JournalVectorStates => Set<JournalVectorStateRow>();
    internal DbSet<JournalVectorChunkRow> JournalVectorChunks => Set<JournalVectorChunkRow>();
    internal DbSet<JournalVectorQueryRow> JournalVectorQueries => Set<JournalVectorQueryRow>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<BillingAccessGrantRow>(entity =>
        {
            entity.HasKey(row => row.UserId);
            entity.HasOne<ApplicationUser>().WithOne().HasForeignKey<BillingAccessGrantRow>(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<AccountAiPreferenceRow>(entity =>
        {
            entity.HasKey(row => row.UserId);
            entity.HasOne<ApplicationUser>().WithOne().HasForeignKey<AccountAiPreferenceRow>(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<JournalVectorStateRow>(entity =>
        {
            entity.HasKey(row => row.UserId);
            entity.HasIndex(row => row.NextAttemptTicks);
            entity.HasOne<ApplicationUser>().WithOne().HasForeignKey<JournalVectorStateRow>(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<JournalVectorChunkRow>(entity =>
        {
            entity.HasIndex(row => new { row.UserId, row.SourceId }).IsUnique();
            entity.HasIndex(row => new { row.UserId, row.CreatedUtc });
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<JournalEntryRow>().WithMany().HasForeignKey(row => row.EntryId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<JournalNoteRow>().WithMany().HasForeignKey(row => row.NoteId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<JournalVectorQueryRow>(entity =>
        {
            entity.HasKey(row => new { row.UserId, row.EntryId });
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<JournalEntryRow>().WithMany().HasForeignKey(row => row.EntryId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<BillingSubscriptionRow>(entity =>
        {
            entity.HasIndex(row => row.ExternalReference).IsUnique();
            entity.HasIndex(row => new { row.Provider, row.ProviderId }).IsUnique();
            entity.HasIndex(row => row.UserId);
            entity.HasIndex(row => new { row.UserId, row.Provider }).IsUnique()
                .HasFilter("\"Status\" NOT IN ('cancelled', 'canceled', 'creation_failed')");
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<BillingPaymentRow>(entity =>
        {
            entity.HasIndex(row => new { row.SubscriptionId, row.ProviderPaymentId }).IsUnique();
            entity.HasIndex(row => new { row.SubscriptionId, row.ProviderInvoiceId });
            entity.HasOne<BillingSubscriptionRow>().WithMany().HasForeignKey(row => row.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<BillingWebhookRow>().HasIndex(row => new { row.ProcessedUtc, row.NextAttemptUtc });

        builder.Entity<JournalMemoryRow>(entity =>
        {
            entity.HasKey(row => new { row.UserId, row.SourceId });
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<JournalEntryRow>().WithMany().HasForeignKey(row => row.EntryId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<JournalNoteRow>().WithMany().HasForeignKey(row => row.NoteId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<JournalTopicOverviewRow>(entity =>
        {
            entity.HasKey(row => row.UserId);
            entity.HasOne<ApplicationUser>().WithOne()
                .HasForeignKey<JournalTopicOverviewRow>(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<JournalProfileRow>(entity =>
        {
            entity.HasKey(row => row.UserId);
            entity.HasOne<ApplicationUser>().WithOne()
                .HasForeignKey<JournalProfileRow>(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<JournalNotebookRow>(entity =>
        {
            entity.HasAlternateKey(row => new { row.UserId, row.Id });
            entity.HasIndex(row => new { row.UserId, row.NormalizedName }).IsUnique();
            entity.HasOne<ApplicationUser>().WithMany()
                .HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<JournalEntryRow>(entity =>
        {
            entity.HasIndex(row => new { row.UserId, row.CreatedUtc }).IsUnique();
            entity.HasIndex(row => new { row.UserId, row.NotebookId, row.CreatedUtc });
            entity.HasOne(row => row.Notebook).WithMany()
                .HasForeignKey(row => new { row.UserId, row.NotebookId })
                .HasPrincipalKey(row => new { row.UserId, row.Id }).OnDelete(DeleteBehavior.NoAction);
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
