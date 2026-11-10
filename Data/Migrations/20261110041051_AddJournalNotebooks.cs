using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiBitacora.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJournalNotebooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "NotebookId",
                table: "JournalEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "JournalNotebooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalNotebooks", x => x.Id);
                    table.UniqueConstraint("AK_JournalNotebooks_UserId_Id", x => new { x.UserId, x.Id });
                    table.ForeignKey(
                        name: "FK_JournalNotebooks_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_UserId_NotebookId_CreatedUtc",
                table: "JournalEntries",
                columns: new[] { "UserId", "NotebookId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalNotebooks_UserId_NormalizedName",
                table: "JournalNotebooks",
                columns: new[] { "UserId", "NormalizedName" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_JournalEntries_JournalNotebooks_UserId_NotebookId",
                table: "JournalEntries",
                columns: new[] { "UserId", "NotebookId" },
                principalTable: "JournalNotebooks",
                principalColumns: new[] { "UserId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JournalEntries_JournalNotebooks_UserId_NotebookId",
                table: "JournalEntries");

            migrationBuilder.DropTable(
                name: "JournalNotebooks");

            migrationBuilder.DropIndex(
                name: "IX_JournalEntries_UserId_NotebookId_CreatedUtc",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "NotebookId",
                table: "JournalEntries");
        }
    }
}
