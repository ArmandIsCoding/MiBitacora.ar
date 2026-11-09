using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiBitacora.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEntryDatePrecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDateOnly",
                table: "JournalEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDateOnly",
                table: "JournalEntries");
        }
    }
}
