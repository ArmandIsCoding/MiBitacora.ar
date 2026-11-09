using System.Text.Json;
using ImageMagick;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiBitacora.Data;
using MiBitacora.Services;

internal static class StorageChecks
{
    public static async Task<JournalContext> RunAsync(byte[] image, Action<bool, string> check)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"mibitacora-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try { return await VerifyAsync(temporary, image, check); }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static async Task<JournalContext> VerifyAsync(string temporary, byte[] image, Action<bool, string> check)
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
        var fullEntry = await store.GetEntryAsync(aId, a.Id);
        check(fullEntry?.Text == a.Text && fullEntry.Attachments.Count == 12,
            "Entrada completa: devuelve el original y todos sus adjuntos");
        check(await store.GetEntryAsync(aId, b.Id) is null && await store.GetEntryAsync(aId, Guid.NewGuid()) is null,
            "Entrada completa: una entrada ajena y una inexistente son igualmente inaccesibles");
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE JournalEntries SET IsDateOnly = 1 WHERE Id = {a.Id}");
        check((await store.GetEntriesAsync(aId)).Single().IsDateOnly,
            "Las entradas importadas conservan que su fecha original no incluía hora");
        var limited = new JournalStore(factory, uploads, Options.Create(new JournalStorageOptions
            { MaxAccountAttachmentBytes = 1, MaxAccountFiles = 2000 }));
        try { await limited.AddEntryAsync(aId, "", [new("x.png", image)]); throw new Exception("No se aplicó la cuota configurada"); }
        catch (JournalInputException) { check(true, "La cuota de adjuntos se toma de la configuración"); }
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

        var heic = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "oriented-colors.heic"));
        var photoEntry = await store.AddEntryAsync(bId, "Describí la figura y sus colores en la imagen adjunta.", [new("colores.heic", heic)]);
        var photo = photoEntry.Attachments.Single();
        var jpegPath = Path.Combine(root, bId, $"{photo.Id:D}.jpg");
        check(await store.GetAttachmentPreviewAsync(aId, photo.Id) is null && !File.Exists(jpegPath),
            "La vista HEIC verifica al propietario antes de generar el JPG");
        var previews = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => store.GetAttachmentPreviewAsync(bId, photo.Id)));
        foreach (var preview in previews)
        {
            check(preview?.ContentType == "image/jpeg", "La vista previa devuelve JPEG");
            await preview!.Content.DisposeAsync();
        }
        using (var converted = new MagickImage(jpegPath))
        {
            check(converted.Width == 240 && converted.Height == 320 && converted.GetExifProfile() is null,
                "La conversión respeta la orientación y no copia EXIF al JPG");
            using var pixels = converted.GetPixels();
            var top = pixels.GetPixel(20, 20).ToColor()!;
            var bottom = pixels.GetPixel(20, 300).ToColor()!;
            check(top.G > 200 && top.R < 30 && bottom.R > 200 && bottom.G < 30,
                "Los colores y su posición se conservan en el JPG");
        }
        var original = await store.GetAttachmentAsync(bId, photo.Id);
        await using (var stream = original!.Content)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            check(original.ContentType == "image/heic" && buffer.ToArray().SequenceEqual(heic),
                "La descarga mantiene el HEIC original byte por byte");
        }
        var heicContext = await store.GetContextAsync(bId, photoEntry.Id, true);
        var analysisImage = heicContext.Media.Single(file => file.AttachmentId == photo.Id);
        var savedJpeg = await File.ReadAllBytesAsync(jpegPath);
        check(analysisImage.ContentType == "image/jpeg" && analysisImage.Content.SequenceEqual(savedJpeg),
            "Ollama recibe la copia JPG del mismo adjunto HEIC");
        var orphanPreview = Path.Combine(root, bId, $"{Guid.NewGuid():D}.jpg");
        await File.WriteAllBytesAsync(orphanPreview, analysisImage.Content);
        await using (var db = await factory.CreateDbContextAsync()) await uploads.CleanupOrphansAsync(db);
        check(File.Exists(jpegPath) && !File.Exists(orphanPreview), "La limpieza conserva vistas válidas y elimina JPG huérfanos");
        await store.DeleteEntryAsync(bId, photoEntry.Id);
        check(!File.Exists(jpegPath) && !File.Exists(Path.Combine(root, bId, $"{photo.Id:D}.bin")),
            "Borrar una entrada elimina el original y su copia JPG");
        var broken = await store.AddEntryAsync(bId, "Archivo inválido", [new("roto.heic", heic[..32])]);
        check(await store.GetAttachmentPreviewAsync(bId, broken.Attachments.Single().Id) is null,
            "Un HEIC inválido conserva el original y no publica un JPG incompleto");
        var brokenContext = await store.GetContextAsync(bId, broken.Id, true);
        check(brokenContext.Media.All(file => file.AttachmentId != broken.Attachments.Single().Id),
            "Una imagen que no se puede convertir queda fuera del análisis");
        // Include a regenerated JPEG in the account-deletion check.
        var another = await store.AddEntryAsync(bId, "Otra foto", [new("otra.heic", heic)]);
        var anotherPreview = await store.GetAttachmentPreviewAsync(bId, another.Attachments.Single().Id);
        await anotherPreview!.Content.DisposeAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var deletion = await scope.ServiceProvider.GetRequiredService<AccountDeletionService>().DeleteAsync((await users.FindByIdAsync(bId))!);
        check(deletion.Succeeded && !Directory.Exists(Path.Combine(root, bId)) && (await store.GetEntriesAsync(bId)).Count == 0,
            "Borrar una cuenta elimina sus originales y todos sus registros");
        try { await store.AddEntryAsync(bId, "Sesión vieja", [new("x.png", image)]); throw new Exception("Se recreó la cuenta borrada"); }
        catch (DbUpdateException) { check(!Directory.Exists(Path.Combine(root, bId)), "Una sesión anterior no puede recrear archivos de una cuenta borrada"); }
        return heicContext with { History = [], Attachments = photoEntry.Attachments, Media = [analysisImage] };
    }
}
