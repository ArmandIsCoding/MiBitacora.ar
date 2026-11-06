using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiBitacora.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountRegistrationDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RegisteredUtc",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RegisteredUtc",
                table: "AspNetUsers");
        }
    }
}
