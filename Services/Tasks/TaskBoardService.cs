using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MiBitacora.Data;

namespace MiBitacora.Services;

public sealed class TaskBoardService(IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock)
{
    public const int MaxBoards = 100, MaxColumns = 12, MaxCards = 2000;
    public const int MaxName = 80, MaxTitle = 160, MaxDescription = 4000;
    public static readonly string[] BoardColors = ["sage", "clay", "sky", "lavender"];
    public static readonly string[] CardColors = ["paper", "sage", "honey", "rose", "sky", "lavender"];
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<TaskBoard>> ExportAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ids = await db.TaskBoards.Where(b => b.UserId == userId).OrderBy(b => b.CreatedUtc).Select(b => b.Id).ToArrayAsync(ct);
        var result = new List<TaskBoard>();
        foreach (var id in ids) if (await ReadAsync(db, userId, id, ct) is { } board) result.Add(board);
        return result;
    }

    public async Task<IReadOnlyList<TaskBoardSummary>> ListAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.TaskBoards.AsNoTracking().Where(b => b.UserId == userId).OrderBy(b => b.Archived).ThenBy(b => b.CreatedUtc)
            .Select(b => new TaskBoardSummary(b.Id, b.Name, b.Description, b.Color, b.IncludeInContext, b.Archived,
                db.TaskCards.Count(c => c.BoardId == b.Id && !c.Archived),
                db.TaskCards.Count(c => c.BoardId == b.Id && !c.Archived && c.Column.IsDone), b.Revision)).ToArrayAsync(ct);
    }
    public async Task<TaskBoard?> GetAsync(string userId, Guid boardId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        return await ReadAsync(db, userId, boardId, ct);
    }
    public async Task<Guid?> FindCardBoardAsync(string userId, Guid cardId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.TaskCards.Where(c => c.Id == cardId && c.Board.UserId == userId).Select(c => (Guid?)c.BoardId).SingleOrDefaultAsync(ct);
    }
    public async Task<TaskBoard> SaveBoardAsync(string userId, Guid? boardId, string name, string description,
        string color, bool includeInContext, int? expectedRevision = null, CancellationToken ct = default)
    {
        name = Name(name); description = Text(description, 500, "La descripción del tablero"); Color(color, BoardColors);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = boardId is { } id ? await OwnedAsync(db, userId, id, expectedRevision, ct, true)
            : new TaskBoardRow { Id = Guid.NewGuid(), UserId = userId, CreatedUtc = Now };
        if (boardId is null && await db.TaskBoards.CountAsync(b => b.UserId == userId, ct) >= MaxBoards)
            throw new JournalInputException($"Podés tener hasta {MaxBoards} tableros, incluidos los archivados.");
        var normalized = name.ToUpperInvariant();
        if (await db.TaskBoards.AnyAsync(b => b.UserId == userId && b.Id != row.Id && b.NormalizedName == normalized, ct))
            throw new JournalInputException("Ya tenés un tablero con ese nombre, quizá entre los archivados. Elegí otro nombre.");
        row.Name = name; row.NormalizedName = normalized; row.Description = description; row.Color = color;
        row.IncludeInContext = includeInContext; row.UpdatedUtc = Now;
        if (boardId is null)
        {
            db.TaskBoards.Add(row);
            var names = new[] { "Por hacer", "En marcha", "Hecho" };
            for (var i = 0; i < names.Length; i++) db.TaskColumns.Add(new() { Id = Guid.NewGuid(), BoardId = row.Id, Name = names[i], Position = i, IsDone = i == 2 });
        }
        else row.Revision++;
        await SaveAsync(db, ct); await tx.CommitAsync(ct);
        return (await GetAsync(userId, row.Id, ct))!;
    }
    public Task<TaskBoard> ArchiveBoardAsync(string userId, Guid boardId, bool archived, int revision, CancellationToken ct = default) =>
        ChangeAsync(userId, boardId, revision, async (db, b) => { b.Archived = archived; await Task.CompletedTask; }, ct, true);

    public Task<TaskBoard> SaveColumnAsync(string userId, Guid boardId, Guid? columnId, string name, bool isDone,
        int revision, CancellationToken ct = default) => ChangeAsync(userId, boardId, revision, async (db, b) =>
    {
        name = Name(name);
        var columns = await db.TaskColumns.Where(c => c.BoardId == b.Id).ToListAsync(ct);
        if (columns.Any(c => c.Id != columnId && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new JournalInputException("Usá un nombre distinto para cada estado.");
        if (columnId is { } id)
        {
            var column = columns.SingleOrDefault(c => c.Id == id) ?? throw Unavailable(); column.Name = name; column.IsDone = isDone;
        }
        else
        {
            if (columns.Count >= MaxColumns) throw new JournalInputException($"Podés agregar hasta {MaxColumns} estados por tablero.");
            db.TaskColumns.Add(new() { Id = Guid.NewGuid(), BoardId = b.Id, Name = name, IsDone = isDone, Position = columns.Count });
        }
    }, ct);
    public Task<TaskBoard> DeleteColumnAsync(string userId, Guid boardId, Guid columnId, int revision, CancellationToken ct = default) =>
        ChangeAsync(userId, boardId, revision, async (db, b) =>
        {
            var columns = await db.TaskColumns.Where(c => c.BoardId == b.Id).OrderBy(c => c.Position).ToListAsync(ct);
            var column = columns.SingleOrDefault(c => c.Id == columnId) ?? throw Unavailable();
            if (columns.Count <= 1) throw new JournalInputException("Conservá al menos un estado en el tablero.");
            if (await db.TaskCards.AnyAsync(c => c.ColumnId == columnId, ct)) throw new JournalInputException("Mové las tarjetas de este estado antes de quitarlo, incluidas las archivadas.");
            db.TaskColumns.Remove(column); columns.Remove(column);
            for (var i = 0; i < columns.Count; i++) columns[i].Position = i;
        }, ct);
    public Task<TaskBoard> ShiftColumnAsync(string userId, Guid boardId, Guid columnId, int direction, int revision, CancellationToken ct = default) =>
        ChangeAsync(userId, boardId, revision, async (db, b) =>
        {
            if (direction is not (-1 or 1)) throw Unavailable();
            var columns = await db.TaskColumns.Where(c => c.BoardId == b.Id).OrderBy(c => c.Position).ToListAsync(ct);
            var i = columns.FindIndex(c => c.Id == columnId); if (i < 0) throw Unavailable();
            var next = i + direction; if (next < 0 || next >= columns.Count) return;
            (columns[i], columns[next]) = (columns[next], columns[i]);
            for (var p = 0; p < columns.Count; p++) columns[p].Position = p;
        }, ct);
    public Task<TaskBoard> SaveCardAsync(string userId, Guid boardId, Guid? cardId, Guid columnId, string title,
        string description, string color, DateOnly? dueDate, int revision, CancellationToken ct = default) =>
        ChangeAsync(userId, boardId, revision, async (db, b) =>
        {
            title = Text(title, MaxTitle, "El título"); if (title.Length == 0) throw new JournalInputException("Escribí un título para la tarjeta.");
            description = Text(description, MaxDescription, "La descripción"); Color(color, CardColors);
            if (dueDate is { Year: < 1900 or > 2200 }) throw new JournalInputException("Elegí una fecha entre 1900 y 2200.");
            if (!await db.TaskColumns.AnyAsync(c => c.Id == columnId && c.BoardId == b.Id, ct)) throw Unavailable();
            var card = cardId is { } id ? await db.TaskCards.SingleOrDefaultAsync(c => c.Id == id && c.BoardId == b.Id, ct) ?? throw Unavailable()
                : new TaskCardRow { Id = Guid.NewGuid(), BoardId = b.Id, CreatedUtc = Now };
            if (cardId is null && await db.TaskCards.CountAsync(c => c.BoardId == b.Id, ct) >= MaxCards)
                throw new JournalInputException($"Este tablero alcanzó sus {MaxCards} tarjetas, incluidas las archivadas.");
            if (cardId is null || card.ColumnId != columnId) card.Position = await db.TaskCards.Where(c => c.BoardId == b.Id && c.ColumnId == columnId && !c.Archived).Select(c => (int?)c.Position).MaxAsync(ct) + 1 ?? 0;
            card.ColumnId = columnId; card.Title = title; card.Description = description; card.Color = color; card.DueDate = dueDate; card.UpdatedUtc = Now;
            if (cardId is null) db.TaskCards.Add(card);
        }, ct);
    public Task<TaskBoard> MoveCardAsync(string userId, Guid boardId, Guid cardId, Guid columnId, Guid? beforeCardId,
        int revision, CancellationToken ct = default) => ChangeAsync(userId, boardId, revision, async (db, b) =>
    {
        var cards = await db.TaskCards.Where(c => c.BoardId == b.Id && !c.Archived).OrderBy(c => c.Position).ThenBy(c => c.CreatedUtc).ThenBy(c => c.Id).ToListAsync(ct);
        var card = cards.SingleOrDefault(c => c.Id == cardId) ?? throw Unavailable();
        if (!await db.TaskColumns.AnyAsync(c => c.Id == columnId && c.BoardId == b.Id, ct)) throw Unavailable();
        if (beforeCardId == cardId) return;
        var destination = cards.Where(c => c.ColumnId == columnId && c.Id != cardId).ToList();
        var index = beforeCardId is { } before ? destination.FindIndex(c => c.Id == before) : destination.Count;
        if (index < 0) throw Unavailable();
        if (card.ColumnId != columnId) { card.ColumnId = columnId; card.UpdatedUtc = Now; }
        destination.Insert(index, card);
        for (var i = 0; i < destination.Count; i++) destination[i].Position = i;
    }, ct);
    public Task<TaskBoard> ArchiveCardAsync(string userId, Guid boardId, Guid cardId, bool archived, int revision, CancellationToken ct = default) =>
        ChangeAsync(userId, boardId, revision, async (db, b) =>
        {
            var card = await db.TaskCards.SingleOrDefaultAsync(c => c.Id == cardId && c.BoardId == b.Id, ct) ?? throw Unavailable();
            card.Archived = archived; card.UpdatedUtc = Now;
            if (!archived) card.Position = (await db.TaskCards.Where(c => c.ColumnId == card.ColumnId && !c.Archived).Select(c => (int?)c.Position).MaxAsync(ct) ?? -1) + 1;
        }, ct);
    private async Task<TaskBoard> ChangeAsync(string userId, Guid id, int revision, Func<ApplicationDbContext, TaskBoardRow, Task> change,
        CancellationToken ct, bool allowArchived = false)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var board = await OwnedAsync(db, userId, id, revision, ct, allowArchived);
        await change(db, board); board.Revision++; board.UpdatedUtc = Now;
        await SaveAsync(db, ct); await tx.CommitAsync(ct);
        return (await GetAsync(userId, id, ct))!;
    }
    private static async Task<TaskBoardRow> OwnedAsync(ApplicationDbContext db, string userId, Guid id, int? revision, CancellationToken ct, bool archived = false)
    {
        var row = await db.TaskBoards.SingleOrDefaultAsync(b => b.Id == id && b.UserId == userId, ct) ?? throw Unavailable();
        if (revision != row.Revision) throw new JournalInputException("El tablero cambió en otra pestaña. Recargá el tablero y revisá los cambios antes de guardar.");
        if (!archived && row.Archived) throw new JournalInputException("Restaurá el tablero para volver a editar sus tarjetas.");
        return row;
    }
    private static async Task<TaskBoard?> ReadAsync(ApplicationDbContext db, string userId, Guid id, CancellationToken ct)
    {
        var b = await db.TaskBoards.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id && b.UserId == userId, ct); if (b is null) return null;
        var columns = await db.TaskColumns.AsNoTracking().Where(c => c.BoardId == id).OrderBy(c => c.Position).ThenBy(c => c.Id).Select(c => new TaskColumn(c.Id, c.Name, c.Position, c.IsDone)).ToArrayAsync(ct);
        var cards = await db.TaskCards.AsNoTracking().Where(c => c.BoardId == id).OrderBy(c => c.Position).ThenBy(c => c.CreatedUtc).ThenBy(c => c.Id)
            .Select(c => new TaskCard(c.Id, c.ColumnId, c.Title, c.Description, c.Color, c.DueDate, c.Position, c.Archived, c.CreatedUtc, c.UpdatedUtc)).ToArrayAsync(ct);
        return new(new(b.Id, b.Name, b.Description, b.Color, b.IncludeInContext, b.Archived, cards.Count(c => !c.Archived), cards.Count(c => !c.Archived && columns.Any(col => col.Id == c.ColumnId && col.IsDone)), b.Revision), columns, cards);
    }
    private static async Task SaveAsync(ApplicationDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new JournalInputException("El tablero cambió en otra pestaña. Recargalo antes de guardar."); }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 }) { throw new JournalInputException("Ese nombre ya está en uso. Elegí otro para distinguir tu tablero."); }
    }
    private static string Name(string name)
    {
        name = Regex.Replace(name.Trim().Normalize(NormalizationForm.FormKC), @"\s+", " ");
        if (name.Length == 0) throw new JournalInputException("Elegí un nombre."); return Text(name, MaxName, "El nombre");
    }
    private static string Text(string value, int max, string label) { value = value.Trim(); if (value.Length > max) throw new JournalInputException($"{label} admite hasta {max} caracteres."); return value; }
    private static void Color(string value, string[] allowed) { if (!allowed.Contains(value)) throw new JournalInputException("Elegí uno de los colores disponibles."); }
    private static JournalInputException Unavailable() => new("Ese tablero, estado o tarjeta ya no está disponible en tu cuenta.");
}
