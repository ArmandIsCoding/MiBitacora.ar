using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiBitacora.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskBoards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TaskId",
                table: "JournalVectorChunks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaskId",
                table: "JournalMemories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TaskBoards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Color = table.Column<string>(type: "TEXT", nullable: false),
                    IncludeInContext = table.Column<bool>(type: "INTEGER", nullable: false),
                    Archived = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskBoards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskBoards_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskColumns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BoardId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDone = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskColumns", x => x.Id);
                    table.UniqueConstraint("AK_TaskColumns_BoardId_Id", x => new { x.BoardId, x.Id });
                    table.ForeignKey(
                        name: "FK_TaskColumns_TaskBoards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "TaskBoards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskCards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BoardId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ColumnId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Color = table.Column<string>(type: "TEXT", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Archived = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskCards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskCards_TaskBoards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "TaskBoards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskCards_TaskColumns_BoardId_ColumnId",
                        columns: x => new { x.BoardId, x.ColumnId },
                        principalTable: "TaskColumns",
                        principalColumns: new[] { "BoardId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JournalVectorChunks_UserId_TaskId",
                table: "JournalVectorChunks",
                columns: new[] { "UserId", "TaskId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskBoards_UserId_NormalizedName",
                table: "TaskBoards",
                columns: new[] { "UserId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskCards_BoardId_ColumnId_Position",
                table: "TaskCards",
                columns: new[] { "BoardId", "ColumnId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskColumns_BoardId_Position",
                table: "TaskColumns",
                columns: new[] { "BoardId", "Position" });

            // Invalidate current context in the same transaction as a task edit. Keep unrelated
            // journal vectors and memories; the lexical catalog repairs only affected sources.
            migrationBuilder.Sql($"""
                CREATE TRIGGER task_board_insert AFTER INSERT ON TaskBoards BEGIN
                    {Dirty("new.UserId", "0")}
                END;
                CREATE TRIGGER task_board_update AFTER UPDATE OF Name, Description, IncludeInContext, Archived, UserId ON TaskBoards
                    WHEN old.Name IS NOT new.Name OR old.Description IS NOT new.Description
                        OR old.IncludeInContext IS NOT new.IncludeInContext OR old.Archived IS NOT new.Archived OR old.UserId IS NOT new.UserId BEGIN
                    {Dirty("old.UserId", "TaskId IN (SELECT Id FROM TaskCards WHERE BoardId = old.Id)")}
                    {Dirty("new.UserId", "TaskId IN (SELECT Id FROM TaskCards WHERE BoardId = new.Id)")}
                END;
                CREATE TRIGGER task_board_delete BEFORE DELETE ON TaskBoards BEGIN
                    {Dirty("old.UserId", "TaskId IN (SELECT Id FROM TaskCards WHERE BoardId = old.Id)")}
                END;
                CREATE TRIGGER task_column_insert AFTER INSERT ON TaskColumns BEGIN
                    {Dirty("(SELECT UserId FROM TaskBoards WHERE Id = new.BoardId)", "0")}
                END;
                CREATE TRIGGER task_column_update AFTER UPDATE OF Name, IsDone ON TaskColumns
                    WHEN old.Name IS NOT new.Name OR old.IsDone IS NOT new.IsDone BEGIN
                    {Dirty("(SELECT UserId FROM TaskBoards WHERE Id = old.BoardId)", "TaskId IN (SELECT Id FROM TaskCards WHERE ColumnId = old.Id)")}
                END;
                CREATE TRIGGER task_column_delete BEFORE DELETE ON TaskColumns BEGIN
                    {Dirty("(SELECT UserId FROM TaskBoards WHERE Id = old.BoardId)", "TaskId IN (SELECT Id FROM TaskCards WHERE ColumnId = old.Id)")}
                END;
                CREATE TRIGGER task_card_insert AFTER INSERT ON TaskCards BEGIN
                    {Dirty("(SELECT UserId FROM TaskBoards WHERE Id = new.BoardId)", "TaskId = new.Id")}
                END;
                CREATE TRIGGER task_card_update AFTER UPDATE OF Title, Description, DueDate, ColumnId, Archived, UpdatedUtc, BoardId ON TaskCards
                    WHEN old.Title IS NOT new.Title OR old.Description IS NOT new.Description OR old.DueDate IS NOT new.DueDate
                        OR old.ColumnId IS NOT new.ColumnId OR old.Archived IS NOT new.Archived OR old.UpdatedUtc IS NOT new.UpdatedUtc OR old.BoardId IS NOT new.BoardId BEGIN
                    {Dirty("(SELECT UserId FROM TaskBoards WHERE Id = old.BoardId)", "TaskId = old.Id")}
                    {Dirty("(SELECT UserId FROM TaskBoards WHERE Id = new.BoardId)", "TaskId = new.Id")}
                END;
                CREATE TRIGGER task_card_delete AFTER DELETE ON TaskCards BEGIN
                    {Dirty("(SELECT UserId FROM TaskBoards WHERE Id = old.BoardId)", "TaskId = old.Id")}
                END;
                """);
        }

        private static string Dirty(string owner, string predicate) => $"""
            DELETE FROM JournalVectorChunks WHERE UserId = {owner} AND ({predicate});
            DELETE FROM JournalMemories WHERE UserId = {owner} AND ({predicate});
            DELETE FROM JournalVectorQueries WHERE UserId = {owner};
            INSERT INTO JournalVectorStates (UserId, Revision, CatalogRevision, IndexedRevision, ModelKey, Failures, NextAttemptTicks)
                SELECT Id, 1, 0, 0, '', 0, 0 FROM AspNetUsers WHERE Id = {owner}
                ON CONFLICT(UserId) DO UPDATE SET Revision = Revision + 1, NextAttemptTicks = 0;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "board", "column", "card" })
                foreach (var operation in new[] { "insert", "update", "delete" })
                    migrationBuilder.Sql($"DROP TRIGGER task_{table}_{operation};");
            migrationBuilder.DropTable(
                name: "TaskCards");

            migrationBuilder.DropTable(
                name: "TaskColumns");

            migrationBuilder.DropTable(
                name: "TaskBoards");

            migrationBuilder.DropIndex(
                name: "IX_JournalVectorChunks_UserId_TaskId",
                table: "JournalVectorChunks");

            // Native SQLite removal preserves the existing FTS table and its triggers.
            migrationBuilder.Sql("ALTER TABLE JournalVectorChunks DROP COLUMN TaskId;");
            migrationBuilder.Sql("ALTER TABLE JournalMemories DROP COLUMN TaskId;");
        }
    }
}
