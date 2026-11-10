using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MiBitacora.Data;

namespace MiBitacora.Services;

public sealed partial class JournalStore
{
    public const int MaxNotebookNameLength = 80;
    public const string DefaultNotebookName = "Mi diario";

    // A null notebook is the permanent default. Existing entries and imports keep their IDs,
    // content, dates and responses; no data backfill or synthetic notebook per account is needed.
    public async Task<IReadOnlyList<JournalNotebook>> GetNotebooksAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var notebooks = await db.JournalNotebooks.AsNoTracking().Where(row => row.UserId == userId)
            .OrderBy(row => row.Name).Select(row => new JournalNotebook(row.Id, row.Name,
                db.JournalEntries.Count(entry => entry.UserId == userId && entry.NotebookId == row.Id)))
            .ToListAsync(cancellationToken);
        notebooks.Insert(0, new(null, DefaultNotebookName,
            await db.JournalEntries.CountAsync(row => row.UserId == userId && row.NotebookId == null, cancellationToken)));
        return notebooks;
    }

    public async Task<JournalNotebook> SaveNotebookAsync(string userId, Guid? notebookId, string name,
        CancellationToken cancellationToken = default)
    {
        name = Regex.Replace(name.Trim().Normalize(NormalizationForm.FormKC), @"\s+", " ");
        if (name.Length == 0) throw new JournalInputException("Dale un nombre a tu diario.");
        RequireLength(name, MaxNotebookNameLength, "El nombre del diario");
        var normalized = name.ToUpperInvariant();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = notebookId is { } id
            ? await db.JournalNotebooks.SingleOrDefaultAsync(row => row.UserId == userId && row.Id == id, cancellationToken)
                ?? throw NotebookUnavailable()
            : new JournalNotebookRow { Id = Guid.NewGuid(), UserId = userId, CreatedUtc = DateTime.UtcNow };
        if (normalized == DefaultNotebookName.ToUpperInvariant() ||
            await db.JournalNotebooks.AnyAsync(item => item.UserId == userId && item.Id != row.Id && item.NormalizedName == normalized, cancellationToken))
            throw new JournalInputException("Ya tenés un diario con ese nombre. Elegí otro para distinguirlos.");
        row.Name = name;
        row.NormalizedName = normalized;
        if (notebookId is null) db.JournalNotebooks.Add(row);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            throw new JournalInputException("Ya tenés un diario con ese nombre. Elegí otro para distinguirlos.");
        }
        return new(row.Id, row.Name,
            await db.JournalEntries.CountAsync(entry => entry.UserId == userId && entry.NotebookId == row.Id, cancellationToken));
    }

    public async Task<IReadOnlyList<JournalEntry>> GetNotebookEntriesAsync(string userId, Guid? notebookId, int take = 40,
        DateTime? beforeUtc = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await RequireNotebookAsync(db, userId, notebookId, cancellationToken);
        var entries = db.JournalEntries.Where(row => row.UserId == userId && row.NotebookId == notebookId);
        if (beforeUtc is { } before) entries = entries.Where(row => row.CreatedUtc < before);
        var limit = take == int.MaxValue ? take : Math.Clamp(take, 1, 100);
        var page = await ReadEntriesAsync(entries.OrderByDescending(row => row.CreatedUtc).Take(limit), cancellationToken);
        page.Reverse();
        return page;
    }

    public async Task MoveEntryAsync(string userId, Guid entryId, Guid? notebookId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entry = await db.JournalEntries.SingleOrDefaultAsync(row => row.UserId == userId && row.Id == entryId, cancellationToken)
            ?? throw EntryUnavailable();
        await RequireNotebookAsync(db, userId, notebookId, cancellationToken);
        entry.NotebookId = notebookId;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteNotebookAsync(string userId, Guid notebookId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await RequireNotebookAsync(db, userId, notebookId, cancellationToken);
        await db.JournalEntries.Where(row => row.UserId == userId && row.NotebookId == notebookId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.NotebookId, (Guid?)null), cancellationToken);
        await db.JournalNotebooks.Where(row => row.UserId == userId && row.Id == notebookId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<string> RequireNotebookAsync(ApplicationDbContext db, string userId, Guid? notebookId,
        CancellationToken cancellationToken)
    {
        if (notebookId is null) return DefaultNotebookName;
        return await db.JournalNotebooks.Where(row => row.UserId == userId && row.Id == notebookId)
            .Select(row => row.Name).SingleOrDefaultAsync(cancellationToken) ?? throw NotebookUnavailable();
    }

    private static JournalInputException NotebookUnavailable() => new("Este diario ya no está disponible. Volvé a elegir uno de tus diarios.");
}
