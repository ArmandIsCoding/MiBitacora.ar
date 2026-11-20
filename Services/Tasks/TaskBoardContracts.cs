namespace MiBitacora.Services;

public sealed record TaskBoardSummary(Guid Id, string Name, string Description, string Color,
    bool IncludeInContext, bool Archived, int Cards, int Done, int Revision);
public sealed record TaskColumn(Guid Id, string Name, int Position, bool IsDone);
public sealed record TaskCard(Guid Id, Guid ColumnId, string Title, string Description, string Color,
    DateOnly? DueDate, int Position, bool Archived, DateTime CreatedUtc, DateTime UpdatedUtc);
public sealed record TaskBoard(TaskBoardSummary Summary, IReadOnlyList<TaskColumn> Columns, IReadOnlyList<TaskCard> Cards);

public static class TaskContext
{
    public static string Text(string board, string column, bool done, string title, string description, DateOnly? due) =>
        $"Tablero: {board}\nTarjeta: {title}\nEstado elegido: {column}" +
        (done ? " (marcada como hecha por la persona; no verifica la realización)" : "") +
        (due is { } date ? $"\nFecha objetivo: {date:yyyy-MM-dd} (no demuestra incumplimiento ni malestar)" : "") +
        (description.Length > 0 ? "\nDescripción: " + description : "");
    public static string Url(Guid cardId) => $"/pendientes?tarjeta={cardId:D}";
}
