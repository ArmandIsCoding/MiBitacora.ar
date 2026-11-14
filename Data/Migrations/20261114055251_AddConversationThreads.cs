using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiBitacora.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationThreads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReplyToEntryId",
                table: "JournalEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplyToQuestion",
                table: "JournalEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_UserId_ReplyToEntryId",
                table: "JournalEntries",
                columns: new[] { "UserId", "ReplyToEntryId" });

            // Additive columns and triggers preserve the existing vector/FTS triggers: no SQLite table rebuild.
            foreach (var operation in new[] { "INSERT", "UPDATE OF ReplyToEntryId, UserId, CreatedUtc" })
            {
                var name = operation == "INSERT" ? "insert" : "update";
                migrationBuilder.Sql($"""
                    CREATE TRIGGER thread_parent_{name} BEFORE {operation} ON JournalEntries
                    WHEN new.ReplyToEntryId IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM JournalEntries parent WHERE parent.Id = new.ReplyToEntryId
                            AND parent.UserId = new.UserId AND parent.NotebookId IS new.NotebookId
                            AND parent.CreatedUtc < new.CreatedUtc
                    ) BEGIN
                        SELECT RAISE(ABORT, 'Invalid conversation parent');
                    END;
                    """);
            }
            migrationBuilder.Sql("""
                CREATE TRIGGER thread_parent_delete AFTER DELETE ON JournalEntries BEGIN
                    UPDATE JournalEntries SET ReplyToEntryId = NULL, ReplyToQuestion = NULL
                        WHERE UserId = old.UserId AND ReplyToEntryId = old.Id;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER thread_parent_insert; DROP TRIGGER thread_parent_update; DROP TRIGGER thread_parent_delete;");
            migrationBuilder.DropIndex(
                name: "IX_JournalEntries_UserId_ReplyToEntryId",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "ReplyToEntryId",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "ReplyToQuestion",
                table: "JournalEntries");
        }
    }
}
