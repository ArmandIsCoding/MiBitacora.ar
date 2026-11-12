using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiBitacora.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVectorMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JournalVectorChunks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceHash = table.Column<string>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    NoteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    ModelKey = table.Column<string>(type: "TEXT", nullable: false),
                    Embedding = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalVectorChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JournalVectorChunks_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JournalVectorChunks_JournalEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JournalVectorChunks_JournalNotes_NoteId",
                        column: x => x.NoteId,
                        principalTable: "JournalNotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JournalVectorQueries",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    QueryHash = table.Column<string>(type: "TEXT", nullable: false),
                    ModelKey = table.Column<string>(type: "TEXT", nullable: false),
                    Embedding = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalVectorQueries", x => new { x.UserId, x.EntryId });
                    table.ForeignKey(
                        name: "FK_JournalVectorQueries_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JournalVectorQueries_JournalEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JournalVectorStates",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CatalogRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    IndexedRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ModelKey = table.Column<string>(type: "TEXT", nullable: false),
                    Failures = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalVectorStates", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_JournalVectorStates_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JournalVectorChunks_EntryId",
                table: "JournalVectorChunks",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalVectorChunks_NoteId",
                table: "JournalVectorChunks",
                column: "NoteId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalVectorChunks_UserId_CreatedUtc",
                table: "JournalVectorChunks",
                columns: new[] { "UserId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalVectorChunks_UserId_SourceId",
                table: "JournalVectorChunks",
                columns: new[] { "UserId", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalVectorQueries_EntryId",
                table: "JournalVectorQueries",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalVectorStates_NextAttemptTicks",
                table: "JournalVectorStates",
                column: "NextAttemptTicks");

            // The outbox lives in SQLite: imports and edits outside JournalStore are covered too.
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE JournalVectorText USING fts5(
                    Text, content='JournalVectorChunks', content_rowid='Id',
                    tokenize='unicode61 remove_diacritics 2');
                CREATE TRIGGER vector_text_insert AFTER INSERT ON JournalVectorChunks BEGIN
                    INSERT INTO JournalVectorText(rowid, Text) VALUES (new.Id, new.Text);
                END;
                CREATE TRIGGER vector_text_delete AFTER DELETE ON JournalVectorChunks BEGIN
                    INSERT INTO JournalVectorText(JournalVectorText, rowid, Text) VALUES ('delete', old.Id, old.Text);
                END;
                CREATE TRIGGER vector_text_update AFTER UPDATE OF Text ON JournalVectorChunks BEGIN
                    INSERT INTO JournalVectorText(JournalVectorText, rowid, Text) VALUES ('delete', old.Id, old.Text);
                    INSERT INTO JournalVectorText(rowid, Text) VALUES (new.Id, new.Text);
                END;
                """);
            migrationBuilder.Sql("""
                INSERT INTO JournalVectorStates (UserId, Revision, CatalogRevision, IndexedRevision, ModelKey, Failures, NextAttemptTicks)
                    SELECT Id, 1, 0, 0, '', 0, 0 FROM AspNetUsers;
                CREATE TRIGGER vector_user_insert AFTER INSERT ON AspNetUsers BEGIN
                    INSERT INTO JournalVectorStates VALUES (new.Id, 1, 0, 0, '', 0, 0);
                END;
                """);
            foreach (var (table, key, invalidate, watched) in new[]
            {
                ("JournalEntries", "UserId", "EntryId", "Text, CreatedUtc"),
                ("JournalNotes", "UserId", "NoteId", "Text, CreatedUtc"),
                ("JournalProfiles", "UserId", "", "PersonalContext")
            })
            {
                var predicate = invalidate.Length == 0 ? "UserId = old.UserId AND Kind = 3" : $"{invalidate} = old.Id";
                var changed = table == "JournalProfiles" ? "old.PersonalContext IS NOT new.PersonalContext"
                    : "old.Text IS NOT new.Text OR old.CreatedUtc IS NOT new.CreatedUtc";
                migrationBuilder.Sql($"""
                    CREATE TRIGGER vector_{table}_insert AFTER INSERT ON {table} BEGIN
                        {Dirty($"new.{key}")}
                    END;
                    CREATE TRIGGER vector_{table}_update AFTER UPDATE OF {watched} ON {table}
                        WHEN {changed} BEGIN
                        DELETE FROM JournalVectorChunks WHERE {predicate};
                        {Dirty($"old.{key}")}
                    END;
                    CREATE TRIGGER vector_{table}_delete AFTER DELETE ON {table} BEGIN
                        DELETE FROM JournalVectorChunks WHERE {predicate};
                        {Dirty($"old.{key}")}
                    END;
                    """);
            }
            migrationBuilder.Sql($"""
                CREATE TRIGGER vector_attachment_insert AFTER INSERT ON JournalAttachments BEGIN
                    DELETE FROM JournalVectorChunks WHERE EntryId = new.EntryId;
                    {Dirty("(SELECT UserId FROM JournalEntries WHERE Id = new.EntryId)")}
                END;
                CREATE TRIGGER vector_attachment_update AFTER UPDATE OF ExtractedText, FileName, ContentType, IsVoiceMessage, EntryId
                    ON JournalAttachments WHEN old.ExtractedText IS NOT new.ExtractedText OR old.FileName IS NOT new.FileName
                        OR old.ContentType IS NOT new.ContentType OR old.IsVoiceMessage IS NOT new.IsVoiceMessage OR old.EntryId IS NOT new.EntryId BEGIN
                    DELETE FROM JournalVectorChunks WHERE EntryId = old.EntryId OR EntryId = new.EntryId;
                    {Dirty("(SELECT UserId FROM JournalEntries WHERE Id = old.EntryId)")}
                    {Dirty("(SELECT UserId FROM JournalEntries WHERE Id = new.EntryId)")}
                END;
                CREATE TRIGGER vector_attachment_delete AFTER DELETE ON JournalAttachments BEGIN
                    DELETE FROM JournalVectorChunks WHERE EntryId = old.EntryId;
                    {Dirty("(SELECT UserId FROM JournalEntries WHERE Id = old.EntryId)")}
                END;
                """);
        }

        private static string Dirty(string owner) => $"""
            DELETE FROM JournalVectorQueries WHERE UserId = {owner};
            INSERT INTO JournalVectorStates (UserId, Revision, CatalogRevision, IndexedRevision, ModelKey, Failures, NextAttemptTicks)
                SELECT Id, 1, 0, 0, '', 0, 0 FROM AspNetUsers WHERE Id = {owner}
                ON CONFLICT(UserId) DO UPDATE SET Revision = Revision + 1, NextAttemptTicks = 0;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var name in new[] { "vector_text_insert", "vector_text_delete", "vector_text_update", "vector_user_insert",
                "vector_attachment_insert", "vector_attachment_update", "vector_attachment_delete" })
                migrationBuilder.Sql($"DROP TRIGGER {name};");
            foreach (var table in new[] { "JournalEntries", "JournalNotes", "JournalProfiles" })
                foreach (var operation in new[] { "insert", "update", "delete" })
                    migrationBuilder.Sql($"DROP TRIGGER vector_{table}_{operation};");
            migrationBuilder.Sql("DROP TABLE JournalVectorText;");
            migrationBuilder.DropTable(
                name: "JournalVectorChunks");

            migrationBuilder.DropTable(
                name: "JournalVectorQueries");

            migrationBuilder.DropTable(
                name: "JournalVectorStates");
        }
    }
}
