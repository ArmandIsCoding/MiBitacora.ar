namespace MiBitacora.Data;

internal sealed class TaskBoardRow
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Color { get; set; } = "sage";
    public bool IncludeInContext { get; set; } = true;
    public bool Archived { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public int Revision { get; set; } = 1;
}

internal sealed class TaskColumnRow
{
    public Guid Id { get; set; }
    public Guid BoardId { get; set; }
    public TaskBoardRow Board { get; set; } = null!;
    public string Name { get; set; } = "";
    public int Position { get; set; }
    public bool IsDone { get; set; }
}

internal sealed class TaskCardRow
{
    public Guid Id { get; set; }
    public Guid BoardId { get; set; }
    public TaskBoardRow Board { get; set; } = null!;
    public Guid ColumnId { get; set; }
    public TaskColumnRow Column { get; set; } = null!;
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Color { get; set; } = "paper";
    public DateOnly? DueDate { get; set; }
    public int Position { get; set; }
    public bool Archived { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
