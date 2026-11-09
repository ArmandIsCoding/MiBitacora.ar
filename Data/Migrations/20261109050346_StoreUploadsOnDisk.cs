using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiBitacora.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoreUploadsOnDisk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The app exports originals before migrating. Refuse a direct EF update
            // that would otherwise discard BLOBs that have not reached disk yet.
            migrationBuilder.Sql("""
                CREATE TABLE __UploadExportGuard (Remaining INTEGER CHECK (Remaining = 0));
                INSERT INTO __UploadExportGuard SELECT COUNT(*) FROM JournalAttachments WHERE length(Content) > 0;
                DROP TABLE __UploadExportGuard;
                """);
            // Native SQLite DROP COLUMN keeps this migration transactional instead
            // of rebuilding the table with foreign keys temporarily disabled.
            migrationBuilder.Sql("ALTER TABLE JournalAttachments DROP COLUMN Content;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Para volver al almacenamiento BLOB, restaurá el respaldo previo a la migración.");
        }
    }
}
