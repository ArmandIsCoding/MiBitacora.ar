using System.Text;
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

internal static class NotebookChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "mibitacora-notebooks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await VerifyAsync(root, check); }
        finally { Directory.Delete(root, true); }
    }

    private static async Task VerifyAsync(string root, Action<bool, string> check)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"Data Source={root}/test.db;Pooling=False;Foreign Keys=True"));
        services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton(new UploadFileStore(Path.Combine(root, "uploads"), NullLogger<UploadFileStore>.Instance));
        services.AddScoped<IJournalStore, JournalStore>();
        services.AddScoped<AccountDeletionService>();
        var analyzer = new FakeJournalMemory();
        services.AddSingleton<IJournalMemoryAnalyzer>(analyzer);
        services.AddSingleton<IJournalReportAnalyzer>(analyzer);
        services.AddSingleton<JournalMemoryJobs>();
        services.AddSingleton<JournalTopicJobs>();
        services.AddScoped<JournalMemoryService>();
        services.AddSingleton<IAudioTranscriber, RejectAudioTranscriber>();
        services.AddSingleton<JournalConversationJobs>();
        services.AddScoped<JournalConversationService>();
        services.AddSingleton(new AttachmentPreparationService(Path.Combine(root, "staging"), Path.Combine(root, "tools")));
        services.AddScoped<JournalReportService>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var store = scope.ServiceProvider.GetRequiredService<IJournalStore>();
        var a = Guid.NewGuid().ToString();
        var b = Guid.NewGuid().ToString();
        var legacyId = Guid.NewGuid();
        var legacyDate = DateTime.UtcNow.AddMonths(-7);
        var reflection = new JournalReflection("Un paso", "Una respuesta existente.", [], ["¿Qué necesitás?"], [], null, false, 1);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261109212031_AddJournalMemory");
            db.Users.AddRange(new ApplicationUser { Id = a, UserName = "notebook-a" }, new ApplicationUser { Id = b, UserName = "notebook-b" });
            await db.SaveChangesAsync();
            var json = JsonSerializer.Serialize(reflection, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO JournalEntries (Id, UserId, Text, SearchText, CreatedUtc, ForSession, IsDateOnly, ReflectionJson)
                VALUES ({legacyId}, {a}, 'ANTECEDENTE_ANTIGUO: extraño mi casa.', 'casa', {legacyDate}, 1, 1, {json})
                """);
            await db.Database.MigrateAsync();
        }
        var legacy = (await store.GetEntryAsync(a, legacyId))!;
        check(legacy.NotebookId is null && legacy.NotebookName == "Mi diario" && legacy.CreatedUtc == legacyDate &&
            legacy.IsDateOnly && legacy.ForSession && legacy.Reflection?.Overview == reflection.Overview,
            "Diarios: la migración conserva entradas existentes, fechas y respuestas en Mi diario");
        check((await store.GetNotebooksAsync(a)).Single().EntryCount == 1 && (await store.GetNotebooksAsync(b)).Single().EntryCount == 0,
            "Diarios: cada cuenta tiene su diario inicial y sus propios conteos");
        var house = await store.SaveNotebookAsync(a, null, "  Casa   nueva  ");
        var clocks = await store.SaveNotebookAsync(a, null, "Relojes");
        var foreign = await store.SaveNotebookAsync(b, null, "Casa nueva");
        check(house.Name == "Casa nueva" && house.Id != foreign.Id, "Diarios: normaliza nombres y permite el mismo nombre en otra cuenta");
        await Reject(() => store.SaveNotebookAsync(a, null, "casa nueva"), "Diarios: evita nombres duplicados sin distinguir mayúsculas");
        await Reject(() => store.SaveNotebookAsync(a, null, "mi diario"), "Diarios: reserva el nombre del diario inicial");
        await Reject(() => store.SaveNotebookAsync(a, null, "   "), "Diarios: rechaza un nombre vacío");
        await Reject(() => store.SaveNotebookAsync(a, null, new string('x', 81)), "Diarios: valida la longitud del nombre");
        await Reject(() => store.SaveNotebookAsync(a, foreign.Id, "Ajeno"), "Diarios: no renombra diarios ajenos");
        await Reject(() => store.GetNotebookEntriesAsync(a, foreign.Id), "Diarios: no permite abrir diarios ajenos");
        await Reject(() => store.AddEntryAsync(a, "No permitido", [], notebookId: foreign.Id), "Diarios: no guarda entradas en diarios ajenos");
        await Reject(() => store.MoveEntryAsync(a, legacyId, foreign.Id), "Diarios: no mueve originales a diarios ajenos");
        await Reject(() => store.DeleteNotebookAsync(a, foreign.Id!.Value), "Diarios: no elimina diarios ajenos");
        var foreignEntry = await store.AddEntryAsync(b, "SECRETO_OTRA_CUENTA", [], notebookId: foreign.Id);
        await Reject(() => store.MoveEntryAsync(a, foreignEntry.Id, house.Id), "Diarios: no permite apropiarse de una entrada ajena");
        await using (var db = await factory.CreateDbContextAsync())
        {
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE JournalEntries SET NotebookId={foreign.Id} WHERE Id={legacyId}");
                throw new Exception("La base permitió un diario de otra cuenta.");
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            { check(true, "Diarios: la clave foránea también impide asociaciones entre cuentas"); }
        }
        var watch = await store.AddEntryAsync(a, "ANTECEDENTE_ANTIGUO: el reloj de mi abuelo me recuerda su casa.",
            [new("recuerdo.txt", Encoding.UTF8.GetBytes("Documento del diario Relojes."))], notebookId: clocks.Id);
        await store.SaveReflectionAsync(a, watch.Id, reflection);
        await store.SetEntryForSessionAsync(a, watch.Id, true);
        var prior = await store.AddEntryAsync(a, "Estoy pensando cómo acomodarme después de la mudanza.", [], notebookId: house.Id);
        await store.SaveReflectionAsync(a, prior.Id, reflection);
        await store.AddEntryAsync(a, "Una consulta sobre otro reloj.", [], notebookId: clocks.Id);
        var current = await store.AddEntryAsync(a, "Necesito sentir que este lugar es mío.", [], notebookId: house.Id);
        var context = await store.GetContextAsync(a, current.Id, false);
        check(context.History.Count == 1 && context.History.Single().Id == prior.Id && context.History.Single().Reflection?.OpenQuestions.Single() == "¿Qué necesitás?",
            "Diarios: una respuesta continúa exclusivamente los turnos y preguntas de ese diario");
        var enriched = await scope.ServiceProvider.GetRequiredService<JournalMemoryService>().EnrichAsync(a, context);
        check(enriched.MemorySources!.Any(s => s.EntryId == legacyId) && enriched.MemorySources!.Any(s => s.EntryId == watch.Id) &&
            enriched.History.Single().Id == prior.Id && analyzer.Seen.All(s => !s.Text.Contains("SECRETO")),
            "Diarios: la memoria recupera antecedentes de hace meses y de otros diarios, sólo de la misma cuenta");
        var report = await scope.ServiceProvider.GetRequiredService<JournalReportService>().GenerateAsync(a);
        check(report.Report!.Sections.Count == 10 && analyzer.Planned.Any(s => s.EntryId == watch.Id) && analyzer.Planned.Any(s => s.EntryId == current.Id),
            "Diarios: el informe para terapia sigue considerando todos los diarios");
        JournalCorpus before;
        await using (var db = await factory.CreateDbContextAsync()) before = await JournalCorpusReader.ReadAsync(db, a);
        var cachedCalls = analyzer.IndexCalls;
        var originalWatch = (await store.GetEntryAsync(a, watch.Id))!;
        await store.MoveEntryAsync(a, watch.Id, house.Id);
        house = await store.SaveNotebookAsync(a, house.Id, "Mi casa nueva");
        var moved = (await store.GetEntryAsync(a, watch.Id))!;
        check(moved.NotebookName == house.Name && moved.NotebookId == house.Id && moved.Text == originalWatch.Text &&
            moved.CreatedUtc == originalWatch.CreatedUtc && moved.ForSession && moved.Attachments.Single().Id == originalWatch.Attachments.Single().Id &&
            moved.Reflection?.Overview == originalWatch.Reflection?.Overview,
            "Diarios: mover y renombrar conserva texto, fecha, adjuntos, respuesta y marca para terapia");
        await using (var db = await factory.CreateDbContextAsync())
        {
            var after = await JournalCorpusReader.ReadAsync(db, a);
            check(before.Fingerprint == after.Fingerprint, "Diarios: organizar no invalida el informe ni la memoria de textos intactos");
        }
        await scope.ServiceProvider.GetRequiredService<JournalReportService>().GenerateAsync(a);
        check(analyzer.IndexCalls == cachedCalls, "Diarios: mover entradas y renombrar no vuelve a indexar textos con la API");
        var all = await store.GetEntriesAsync(a, int.MaxValue);
        var days = await store.GetEntryDaysAsync(a, TimeZoneInfo.Utc);
        check(all.Count == days.Values.Sum() && (await store.GetSessionEntriesAsync(a)).Count == 2 &&
            (await store.GetEntriesInRangeAsync(a, DateTime.MinValue, DateTime.MaxValue)).Count == all.Count,
            "Diarios: calendario, hoja para terapia y exportación mantienen todas las entradas");
        for (var i = 0; i < 35; i++)
        {
            await store.AddEntryAsync(a, $"Página {i}", [], notebookId: clocks.Id);
            if (i % 10 == 0) await store.AddEntryAsync(a, $"Otro hilo {i}", [], notebookId: house.Id);
        }
        var newest = await store.GetNotebookEntriesAsync(a, clocks.Id, 30);
        var older = await store.GetNotebookEntriesAsync(a, clocks.Id, 30, newest[0].CreatedUtc);
        var pages = older.Concat(newest).ToArray();
        check(pages.Length == 36 && pages.Select(e => e.Id).Distinct().Count() == pages.Length &&
            pages.All(e => e.NotebookId == clocks.Id) && pages.SequenceEqual(pages.OrderBy(e => e.CreatedUtc)),
            "Diarios: la paginación no salta, duplica ni mezcla entradas con otros hilos");
        for (var i = 0; i < 25; i++) await store.SaveNotebookAsync(a, null, $"Diario {i + 1}");
        check((await store.GetNotebooksAsync(a)).Count == 28, "Diarios: permite crear muchos diarios sin un cupo fijo");
        var entryCount = (await store.GetEntriesAsync(a, int.MaxValue)).Count;
        await store.DeleteNotebookAsync(a, house.Id!.Value);
        check((await store.GetEntryAsync(a, watch.Id))!.NotebookId is null && (await store.GetEntryAsync(a, watch.Id))!.Reflection is not null &&
            (await store.GetEntriesAsync(a, int.MaxValue)).Count == entryCount &&
            (await store.GetNotebookEntriesAsync(a, null)).Any(e => e.Id == current.Id),
            "Diarios: quitar un diario pasa sus entradas a Mi diario sin perderlas");
        var ownFile = await store.GetAttachmentAsync(a, watch.Attachments.Single().Id);
        check(ownFile is not null, "Diarios: los archivos siguen disponibles después de mover y quitar el diario");
        if (ownFile is not null) await ownFile.Content.DisposeAsync();
        var user = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(a))!;
        check((await scope.ServiceProvider.GetRequiredService<AccountDeletionService>().DeleteAsync(user)).Succeeded,
            "Diarios: borrar la cuenta elimina también sus diarios y originales sin conflictos de claves foráneas");
        check((await store.GetNotebooksAsync(b)).Any(n => n.Id == foreign.Id) && (await store.GetEntryAsync(b, foreignEntry.Id)) is not null,
            "Diarios: eliminar una cuenta conserva intactos los diarios de las demás");

        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); throw new Exception(label + ": se permitió la operación."); }
            catch (JournalInputException) { check(true, label); }
        }
    }
}
