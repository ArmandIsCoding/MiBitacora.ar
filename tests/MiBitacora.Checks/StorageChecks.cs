using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MiBitacora.Data;
using MiBitacora.Services;

internal static class StorageChecks
{
    public static async Task RunAsync(byte[] image, Action<bool, string> check)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"mibitacora-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try { await VerifyAsync(temporary, image, check); }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static async Task VerifyAsync(string temporary, byte[] image, Action<bool, string> check)
    {
        var aId = Guid.NewGuid().ToString("D");
        var bId = Guid.NewGuid().ToString("D");
        var root = Path.Combine(temporary, "uploads");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(options =>
            options.UseSqlite($"Data Source={Path.Combine(temporary, "test.db")};Pooling=False;Foreign Keys=True"));
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton(new UploadFileStore(root, NullLogger<UploadFileStore>.Instance));
        services.AddScoped<AccountDeletionService>();
        services.AddScoped<IJournalStore, JournalStore>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var uploads = provider.GetRequiredService<UploadFileStore>();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var store = scope.ServiceProvider.GetRequiredService<IJournalStore>();

        // Upgrade an actual legacy schema, with an original BLOB and an interrupted copy.
        var legacyEntry = Guid.NewGuid();
        var legacyFile = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261106203108_AddAccountRegistrationDate");
            db.Users.AddRange(new ApplicationUser { Id = aId, UserName = "a" }, new ApplicationUser { Id = bId, UserName = "b" });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO JournalEntries (Id, UserId, Text, SearchText, CreatedUtc, ForSession)
                VALUES ({legacyEntry}, {aId}, 'Anterior', 'anterior', {DateTime.UtcNow}, 0)
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO JournalAttachments (Id, EntryId, Position, FileName, ContentType, Size, ExtractionStatus, SearchText, Content)
                VALUES ({legacyFile}, {legacyEntry}, 0, 'anterior.png', 'image/png', {image.Length}, '', '', {image})
                """);
            try { await db.Database.MigrateAsync(); throw new Exception("Se descartaron originales sin exportarlos"); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            { check(true, "La migración rechaza eliminar BLOBs que todavía no se exportaron"); }

            await uploads.WriteAsync(aId, legacyFile, image);
            check(await LegacyUploadMigration.ExportAsync(db, uploads), "Se exportan los originales de la base anterior");
            check(await LegacyUploadMigration.ExportAsync(db, uploads), "La exportación se puede reanudar sin duplicar ni perder archivos");
            await db.Database.MigrateAsync();
            check(!await LegacyUploadMigration.ExportAsync(db, uploads), "Una base migrada no vuelve a exportarse");
            await db.Database.OpenConnectionAsync();
            await using var schema = db.Database.GetDbConnection().CreateCommand();
            schema.CommandText = "SELECT COUNT(*) FROM pragma_table_info('JournalAttachments') WHERE name = 'Content' OR type = 'BLOB'";
            check(Convert.ToInt64(await schema.ExecuteScalarAsync()) == 0, "SQLite ya no tiene columna BLOB para los originales");
            await db.Database.CloseConnectionAsync();
        }
        var migrated = await store.GetAttachmentAsync(aId, legacyFile);
        await using (var stream = migrated!.Content)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            check(buffer.ToArray().SequenceEqual(image), "La descarga conserva los bytes del adjunto migrado");
        }
        await store.DeleteEntryAsync(aId, legacyEntry);

        var a = await store.AddEntryAsync(aId, "Apunte de A",
            Enumerable.Range(0, 12).Select(_ => new PendingAttachment("../../cuadrado.png", image)).ToArray());
        var b = await store.AddEntryAsync(bId, "SECRETO_B", [new("cuadrado.png", image)]);
        check(a.Attachments.Count == 12 && Directory.GetFiles(Path.Combine(root, aId)).Length == 12,
            "Se guardan 12 archivos en una entrada, con nombres únicos dentro de uploads/cuenta");
        var own = await store.GetContextAsync(aId, a.Id, true);
        check(own.Media.Count == 3 && own.Media.All(file => file.Content.SequenceEqual(image)) &&
            !JsonSerializer.Serialize(own).Contains("SECRETO_B"), "El contexto visual se lee del disco y pertenece al usuario autenticado");
        try { await store.GetContextAsync(aId, b.Id, true); throw new Exception("Acceso cruzado"); }
        catch (JournalInputException) { check(true, "Se rechaza consultar una entrada de otro usuario"); }
        check(await store.GetAttachmentAsync(aId, b.Attachments[0].Id) is null, "Una cuenta no puede descargar adjuntos ajenos");
        await store.DeleteEntryAsync(aId, b.Id);
        check(File.Exists(Path.Combine(root, bId, $"{b.Attachments[0].Id:D}.bin")), "Una cuenta no puede borrar archivos ajenos");
        try { uploads.OpenRead("../otro", Guid.NewGuid()); throw new Exception("Se permitió salir de uploads"); }
        catch (ArgumentException) { check(true, "Las rutas rechazan códigos de cuenta manipulados"); }

        try { await store.AddEntryAsync(aId, "", [new("grande.txt", new byte[JournalStore.MaxFileBytes + 1])]); throw new Exception("Sin límite individual"); }
        catch (JournalInputException) { check(true, "Se conserva el máximo de 8 MiB por archivo"); }
        try
        {
            await store.AddEntryAsync(aId, "", [new("1.txt", new byte[JournalStore.MaxFileBytes]),
                new("2.txt", new byte[JournalStore.MaxFileBytes]), new("3.txt", [1])]);
            throw new Exception("Sin límite total");
        }
        catch (JournalInputException) { check(true, "Se conserva el máximo de 16 MiB por entrada"); }

        // Force a disk write failure after database insertion. No entry may be committed.
        var blockedRoot = Path.Combine(temporary, "not-a-directory");
        await File.WriteAllTextAsync(blockedRoot, "blocked");
        using (var blocked = new UploadFileStore(blockedRoot, NullLogger<UploadFileStore>.Instance))
        {
            var failedStore = new JournalStore(factory, blocked);
            try { await failedStore.AddEntryAsync(aId, "No guardar", [new("test.png", image)]); throw new Exception("Faltó fallo de disco"); }
            catch (IOException) { }
            check((await store.GetEntriesAsync(aId)).Count == 1, "Un fallo de disco revierte la entrada en SQLite");
        }

        var orphan = Guid.NewGuid();
        await uploads.WriteAsync(aId, orphan, image);
        await File.WriteAllTextAsync(Path.Combine(root, aId, $"{Guid.NewGuid():D}.bin.tmp"), "interrumpido");
        await using (var db = await factory.CreateDbContextAsync()) await uploads.CleanupOrphansAsync(db);
        check(Directory.GetFiles(Path.Combine(root, aId)).Length == 12, "El arranque limpia escrituras huérfanas sin tocar adjuntos válidos");
        await store.DeleteEntryAsync(aId, a.Id);
        check(!Directory.Exists(Path.Combine(root, aId)), "Borrar una entrada elimina sus originales y la carpeta vacía");
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var deletion = await scope.ServiceProvider.GetRequiredService<AccountDeletionService>().DeleteAsync((await users.FindByIdAsync(bId))!);
        check(deletion.Succeeded && !Directory.Exists(Path.Combine(root, bId)) && (await store.GetEntriesAsync(bId)).Count == 0,
            "Borrar una cuenta elimina sus originales y todos sus registros");
        try { await store.AddEntryAsync(bId, "Sesión vieja", [new("x.png", image)]); throw new Exception("Se recreó la cuenta borrada"); }
        catch (DbUpdateException) { check(!Directory.Exists(Path.Combine(root, bId)), "Una sesión anterior no puede recrear archivos de una cuenta borrada"); }
    }
}
