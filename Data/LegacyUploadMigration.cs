using Microsoft.EntityFrameworkCore;
using MiBitacora.Services;

namespace MiBitacora.Data;

public static class LegacyUploadMigration
{
    // Run before EF removes the old Content column. A copied row is checkpointed by
    // clearing its BLOB, so an interrupted export can safely resume on next startup.
    public static async Task<bool> ExportAsync(ApplicationDbContext db, UploadFileStore files,
        CancellationToken cancellationToken = default)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            await using var schema = connection.CreateCommand();
            schema.CommandText = "SELECT COUNT(*) FROM pragma_table_info('JournalAttachments') WHERE name = 'Content'";
            if (Convert.ToInt64(await schema.ExecuteScalarAsync(cancellationToken)) == 0) return false;

            await using var query = connection.CreateCommand();
            query.CommandText = """
                SELECT a.Id, e.UserId, a.Size FROM JournalAttachments a
                JOIN JournalEntries e ON e.Id = a.EntryId
                """;
            var rows = new List<(string Id, string UserId, long Size)>();
            await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken))
                    rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));

            foreach (var row in rows)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT Content FROM JournalAttachments WHERE Id = @id";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@id";
                parameter.Value = row.Id;
                command.Parameters.Add(parameter);
                var content = (byte[])(await command.ExecuteScalarAsync(cancellationToken))!;
                var id = Guid.Parse(row.Id);
                await using (var existing = files.OpenRead(row.UserId, id))
                {
                    if (existing is not null)
                    {
                        if (existing.Length != row.Size) throw new IOException("Un upload migrado tiene un tamaño incorrecto.");
                        if (content.Length > 0)
                        {
                            var saved = new byte[content.Length];
                            await existing.ReadExactlyAsync(saved, cancellationToken);
                            if (!saved.AsSpan().SequenceEqual(content))
                                throw new IOException("Un upload migrado no coincide con el original.");
                        }
                    }
                    else
                    {
                        if (content.LongLength != row.Size || content.Length == 0)
                            throw new IOException("Falta un original durante la migración de uploads.");
                        await files.WriteAsync(row.UserId, id, content, cancellationToken);
                    }
                }
                command.CommandText = "UPDATE JournalAttachments SET Content = X'' WHERE Id = @id";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            return true;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
