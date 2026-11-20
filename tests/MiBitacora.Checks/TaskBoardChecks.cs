using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiBitacora.Data;
using MiBitacora.Services;

internal static class TaskBoardChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "mibitacora-tasks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { await VerifyAsync(root, check); } finally { Directory.Delete(root, true); }
    }
    private static async Task VerifyAsync(string root, Action<bool, string> check)
    {
        var services = new ServiceCollection().AddLogging();
        var clock = new BoardClock(DateTimeOffset.UtcNow.AddMinutes(-10));
        services.AddSingleton<TimeProvider>(clock); services.AddSingleton(TimeZoneInfo.FindSystemTimeZoneById("Argentina Standard Time"));
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"Data Source={root}/test.db;Pooling=False;Foreign Keys=True"));
        services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton(new UploadFileStore(Path.Combine(root, "uploads"), NullLogger<UploadFileStore>.Instance));
        services.AddScoped<IJournalStore, JournalStore>(); services.AddScoped<TaskBoardService>(); services.AddScoped<ThoughtMapService>();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys"))).SetApplicationName("task-tests");
        services.AddScoped<TherapyShareBuilder>(); services.AddScoped<TherapyShareService>();
        await using var provider = services.BuildServiceProvider(); await using var scope = provider.CreateAsyncScope();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var tasks = scope.ServiceProvider.GetRequiredService<TaskBoardService>(); var store = scope.ServiceProvider.GetRequiredService<IJournalStore>();
        var a = Guid.NewGuid().ToString(); var b = Guid.NewGuid().ToString(); var legacy = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261119195428_AddTherapyShares");
            db.Users.AddRange(new ApplicationUser { Id = a, UserName = "board-a" }, new ApplicationUser { Id = b, UserName = "board-b" });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO JournalEntries (Id, UserId, Text, SearchText, CreatedUtc, ForSession, IsDateOnly) VALUES ({legacy}, {a}, 'ORIGINAL_SINTETICO', 'original', {DateTime.UtcNow.AddDays(-30)}, 0, 0)");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO JournalVectorChunks (UserId, SourceId, SourceHash, EntryId, Kind, Label, Text, ModelKey) VALUES ({a}, 'legacy:0', 'legacy-hash', {legacy}, 0, 'Original', 'originalsintetico', 'mock')");
            await db.Database.MigrateAsync();
            check(await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM JournalEntries WHERE Id={legacy} AND Text='ORIGINAL_SINTETICO'").SingleAsync() == 1, "Pendientes: migración aditiva conserva las entradas existentes");
            check(await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM JournalVectorText WHERE JournalVectorText MATCH 'originalsintetico'").SingleAsync() == 1, "Pendientes: la migración conserva el índice de texto previo");
            await db.GetService<IMigrator>().MigrateAsync("20261119195428_AddTherapyShares");
            await db.Database.MigrateAsync();
            check(await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM JournalVectorText WHERE JournalVectorText MATCH 'originalsintetico'").SingleAsync() == 1, "Pendientes: revertir y reaplicar la migración conserva el texto y sus disparadores de búsqueda");
        }
        var board = await tasks.SaveBoardAsync(a, null, "  Proyectos   DIY  ", "Un proyecto ficticio.", "clay", true);
        var other = await tasks.SaveBoardAsync(b, null, "Proyectos DIY", "CONTEXTO_AJENO", "sage", true);
        check(board.Summary.Name == "Proyectos DIY" && board.Columns.Select(c => c.Name).SequenceEqual(new[] { "Por hacer", "En marcha", "Hecho" }) && board.Columns[^1].IsDone,
            "Pendientes: tablero propio con nombre normalizado y tres estados iniciales editables");
        await Reject(() => tasks.SaveBoardAsync(a, null, "proyectos diy", "", "sage", true), "evita nombres duplicados en la misma cuenta");
        check(await tasks.GetAsync(b, board.Summary.Id) is null && (await tasks.ListAsync(a)).All(x => x.Id != other.Summary.Id), "Pendientes: lectura y listado aislados entre cuentas");
        await Reject(() => tasks.SaveBoardAsync(b, board.Summary.Id, "Invadido", "", "sage", true, board.Summary.Revision), "impide editar tableros ajenos");
        board = await tasks.SaveCardAsync(a, board.Summary.Id, null, board.Columns[0].Id, "Diseñar xilofon ficticio", "Medir las piezas antes de cortar.", "honey", new DateOnly(2027, 1, 5), board.Summary.Revision);
        var card = board.Cards.Single();
        check(card.DueDate == new DateOnly(2027, 1, 5) && card.Color == "honey" && card.Description.Contains("Medir"), "Pendientes: persiste título, descripción, fecha opcional y color");
        await Reject(() => tasks.SaveCardAsync(a, board.Summary.Id, null, other.Columns[0].Id, "Ajena", "", "paper", null, board.Summary.Revision), "rechaza estados de otro tablero");
        await Reject(() => tasks.SaveCardAsync(a, board.Summary.Id, null, board.Columns[0].Id, new string('x', 161), "", "paper", null, board.Summary.Revision), "valida longitudes en el servidor");
        await Reject(() => tasks.SaveCardAsync(a, board.Summary.Id, null, board.Columns[0].Id, "Inválida", "", "url(test)", null, board.Summary.Revision), "rechaza colores fuera de la paleta");
        await Reject(() => tasks.MoveCardAsync(b, board.Summary.Id, card.Id, board.Columns[1].Id, null, board.Summary.Revision), "impide mover tarjetas ajenas");
        check(await tasks.FindCardBoardAsync(b, card.Id) is null, "Pendientes: los enlaces a tarjetas no revelan tableros de otra cuenta");
        var staleRevision = board.Summary.Revision;
        board = await tasks.SaveCardAsync(a, board.Summary.Id, null, board.Columns[0].Id, "Elegir la madera", "", "sage", null, board.Summary.Revision);
        var second = board.Cards.Single(c => c.Id != card.Id);
        await Reject(() => tasks.MoveCardAsync(a, board.Summary.Id, card.Id, board.Columns[1].Id, null, staleRevision), "detecta cambios concurrentes sin sobrescribirlos");
        var beforeReorder = (await Corpus()).Fingerprint;
        board = await tasks.MoveCardAsync(a, board.Summary.Id, second.Id, board.Columns[0].Id, card.Id, board.Summary.Revision);
        check(board.Cards.Where(c => c.ColumnId == board.Columns[0].Id).First().Id == second.Id, "Pendientes: reordena tarjetas dentro del estado");
        check((await Corpus()).Fingerprint == beforeReorder, "Pendientes: reordenar tarjetas no cambia su contexto ni vuelve obsoletos los informes");
        board = await tasks.MoveCardAsync(a, board.Summary.Id, card.Id, board.Columns[1].Id, null, board.Summary.Revision);
        check(board.Cards.Single(c => c.Id == card.Id).ColumnId == board.Columns[1].Id, "Pendientes: mueve entre estados con orden persistente");
        await Reject(() => tasks.MoveCardAsync(a, board.Summary.Id, card.Id, board.Columns[1].Id, second.Id, board.Summary.Revision), "rechaza una posición perteneciente a otro estado");
        await Reject(() => tasks.DeleteColumnAsync(a, board.Summary.Id, board.Columns[0].Id, board.Summary.Revision), "no elimina estados que contienen tarjetas");
        board = await tasks.SaveColumnAsync(a, board.Summary.Id, null, "Esperando materiales", false, board.Summary.Revision);
        var addedColumn = board.Columns[^1];
        board = await tasks.ShiftColumnAsync(a, board.Summary.Id, addedColumn.Id, -1, board.Summary.Revision);
        check(board.Columns[2].Id == addedColumn.Id, "Pendientes: crea y reordena estados personalizados");
        board = await tasks.DeleteColumnAsync(a, board.Summary.Id, addedColumn.Id, board.Summary.Revision);
        check(board.Columns.Count == 3 && board.Columns.Select(c => c.Position).SequenceEqual(new[] { 0, 1, 2 }), "Pendientes: retira sólo estados vacíos y normaliza su orden");
        var corpus = await Corpus();
        check(corpus.TaskCount == 2 && corpus.Sources.Where(s => s.Kind == TopicSourceKind.Task).All(s => s.TaskId.HasValue && s.EntryId is null && s.NoteId is null)
            && corpus.Sources.All(s => !s.Text.Contains("CONTEXTO_AJENO")), "Pendientes: originales actuales y fuentes propias se incorporan al contexto sin suplantar entradas");
        await using (var db = await factory.CreateDbContextAsync())
            check((await JournalCorpusReader.ReadAsync(db, a, DateTime.UtcNow.AddDays(-1))).Sources.All(s => s.Kind != TopicSourceKind.Task), "Pendientes: no proyecta estados actuales sobre entradas históricas");
        var fingerprint = corpus.Fingerprint;
        var updated = board.Cards.Single(c => c.Id == card.Id);
        clock.Advance(TimeSpan.FromSeconds(2));
        board = await tasks.SaveCardAsync(a, board.Summary.Id, card.Id, updated.ColumnId, updated.Title, "Revisar el diseño del xilofon ficticio.", updated.Color, updated.DueDate, board.Summary.Revision);
        check((await Corpus()).Fingerprint != fingerprint, "Pendientes: los cambios actualizan la huella del contexto y dejan informes anteriores desactualizados");
        board = await tasks.ArchiveCardAsync(a, board.Summary.Id, card.Id, true, board.Summary.Revision);
        check((await Corpus()).TaskCount == 1 && board.Cards.Any(c => c.Id == card.Id && c.Archived), "Pendientes: archivar conserva la tarjeta y la excluye de nuevas consultas");
        board = await tasks.ArchiveCardAsync(a, board.Summary.Id, card.Id, false, board.Summary.Revision);
        check((await Corpus()).TaskCount == 2, "Pendientes: restaurar recupera la tarjeta y su estado");
        board = await tasks.SaveBoardAsync(a, board.Summary.Id, board.Summary.Name, board.Summary.Description, "clay", false, board.Summary.Revision);
        check((await Corpus()).TaskCount == 0, "Pendientes: excluir un tablero retira todas sus tarjetas del contexto");
        board = await tasks.SaveBoardAsync(a, board.Summary.Id, board.Summary.Name, board.Summary.Description, "clay", true, board.Summary.Revision);
        board = await tasks.ArchiveBoardAsync(a, board.Summary.Id, true, board.Summary.Revision);
        check((await Corpus()).TaskCount == 0 && (await tasks.GetAsync(a, board.Summary.Id))!.Cards.Count == 2, "Pendientes: archivar el tablero conserva sus tarjetas sin usarlas como contexto");
        await Reject(() => tasks.MoveCardAsync(a, board.Summary.Id, card.Id, board.Columns[0].Id, null, board.Summary.Revision), "exige restaurar el tablero antes de editar sus tarjetas");
        board = await tasks.ArchiveBoardAsync(a, board.Summary.Id, false, board.Summary.Revision);
        var vectors = new VectorMemoryService(factory, new OfflineEmbedding(), Options.Create(new EmbeddingOptions()), NullLogger<VectorMemoryService>.Instance);
        await vectors.SynchronizeCatalogAsync(a);
        await using (var db = await factory.CreateDbContextAsync()) check(await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM JournalVectorChunks WHERE UserId={a} AND TaskId={card.Id}").SingleAsync() > 0, "Pendientes: la memoria vectorial conserva la identidad de la tarjeta original");
        var entry = await store.AddEntryAsync(a, "Quiero pensar mi xilofon ficticio.", []);
        var context = await store.GetContextAsync(a, entry.Id, false);
        var enriched = await vectors.EnrichAsync(a, context, true);
        check(enriched.MemorySources!.Any(s => s.Kind == TopicSourceKind.Task && s.TaskId == card.Id), "Pendientes: recupera tarjetas pertinentes por texto aunque Ollama no esté disponible");
        JsonElement? request = null;
        using var gate = new LocalInferenceGate();
        using var http = new HttpClient(new Handler(async (r, ct) =>
        {
            request = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { done = true, done_reason = "stop", message = new { content = JsonSerializer.Serialize(new { title = "Un proyecto", message = "Podemos pensar qué lugar querés darle a este proyecto.", themes = Array.Empty<object>(), openQuestions = new[] { "¿Qué te gustaría explorar?" }, sessionTopics = Array.Empty<string>(), nextStep = "" }) } }) };
        }));
        var response = await new OllamaJournalService(http, Options.Create(new OllamaOptions()), gate, NullLogger<OllamaJournalService>.Instance).ReflectAsync(enriched, true);
        check(request!.Value.ToString().Contains("xilofon ficticio") && response.ContextSources!.Any(s => s.TaskId == card.Id), "Pendientes: Brisa recibe la tarjeta y guarda una fuente navegable en la devolución");
        await store.SaveReflectionAsync(a, entry.Id, response, expectedMemoryRevision: enriched.MemoryRevision);
        var map = await scope.ServiceProvider.GetRequiredService<ThoughtMapService>().GetAsync(a);
        check(map.Nodes.Any(n => n.Url == TaskContext.Url(card.Id)) && map.Links.Any(l => l.Source == "task-" + card.Id.ToString("N")), "Pendientes: las conexiones consultadas aparecen en el mapa privado con su tarjeta");
        var shares = scope.ServiceProvider.GetRequiredService<TherapyShareService>();
        var sharePreview = await shares.PrepareAsync(a, "Recorrido ficticio");
        var sharedJson = JsonSerializer.Serialize(sharePreview.Snapshot);
        check(!sharedJson.Contains(card.Id.ToString(), StringComparison.OrdinalIgnoreCase) && !sharedJson.Contains(card.Id.ToString("N"), StringComparison.OrdinalIgnoreCase)
            && sharePreview.Snapshot.Map.Nodes.All(n => n.Url is null), "Pendientes: las tarjetas y sus enlaces privados no se agregan a la copia pública de terapia");
        var shared = await shares.PublishAsync(a, sharePreview, true);
        var beforeTaskChange = await shares.PrepareAsync(a, "Recorrido ficticio");
        updated = board.Cards.Single(c => c.Id == card.Id); clock.Advance(TimeSpan.FromSeconds(2));
        board = await tasks.SaveCardAsync(a, board.Summary.Id, card.Id, updated.ColumnId, updated.Title, "Nueva aclaración ficticia.", updated.Color, updated.DueDate, board.Summary.Revision);
        await Reject(() => shares.PublishAsync(a, beforeTaskChange, true), "la publicación detecta un cambio de tareas que pudo alterar la vigencia del informe");
        check(await shares.OpenAsync(shared.Token) is not null, "Pendientes: editar tareas no cambia ni revoca una versión fija ya aprobada");
        other = await tasks.SaveCardAsync(b, other.Summary.Id, null, other.Columns[0].Id, "Tarjeta ajena", "CONTEXTO_AJENO", "paper", null, other.Summary.Revision);
        await vectors.SynchronizeCatalogAsync(b);
        var oldRevision = enriched.MemoryRevision;
        board = await tasks.SaveBoardAsync(a, board.Summary.Id, board.Summary.Name, board.Summary.Description, "clay", false, board.Summary.Revision);
        await using (var db = await factory.CreateDbContextAsync()) check(await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM JournalVectorChunks WHERE UserId={a} AND TaskId IS NOT NULL").SingleAsync() == 0, "Pendientes: excluir el tablero purga de inmediato sus fragmentos indexados");
        await using (var db = await factory.CreateDbContextAsync()) check(await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM JournalVectorChunks WHERE UserId={b} AND TaskId IS NOT NULL").SingleAsync() > 0, "Pendientes: invalidar el contexto conserva los fragmentos de otras cuentas");
        await Reject(() => store.SaveReflectionAsync(a, entry.Id, response, expectedMemoryRevision: oldRevision), "una edición de tareas impide guardar una devolución con contexto obsoleto");
        var afterExclusion = await vectors.EnrichAsync(a, await store.GetContextAsync(a, entry.Id, false), true);
        check(afterExclusion.MemorySources!.All(s => s.Kind != TopicSourceKind.Task), "Pendientes: una consulta posterior respeta la exclusión de contexto");
        var exported = await tasks.ExportAsync(a);
        check(exported.Count == 1 && exported[0].Cards.Count == 2 && exported.All(x => x.Summary.Id != other.Summary.Id), "Pendientes: la exportación personal incluye tableros y tarjetas propios");
        var user = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(a))!;
        check((await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().DeleteAsync(user)).Succeeded, "Pendientes: permite borrar la cuenta con sus tableros");
        await using (var db = await factory.CreateDbContextAsync()) check(await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM TaskBoards WHERE UserId={a}").SingleAsync() == 0
            && await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM TaskCards WHERE BoardId={board.Summary.Id}").SingleAsync() == 0
            && await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM TaskBoards WHERE UserId={b}").SingleAsync() == 1, "Pendientes: el borrado elimina tableros, estados y tarjetas en cascada sin afectar otra cuenta");

        async Task<JournalCorpus> Corpus() { await using var db = await factory.CreateDbContextAsync(); return await JournalCorpusReader.ReadAsync(db, a); }
        async Task Reject(Func<Task> action, string label) { var rejected = false; try { await action(); } catch (JournalInputException) { rejected = true; } check(rejected, "Pendientes: " + label); }
    }
    private sealed class BoardClock(DateTimeOffset now) : TimeProvider { private DateTimeOffset _now = now; public override DateTimeOffset GetUtcNow() => _now; public void Advance(TimeSpan span) => _now += span; }
    private sealed class OfflineEmbedding : IJournalEmbeddingClient
    {
        public Task<EmbeddingModel> GetModelAsync(CancellationToken ct) => throw new EmbeddingUnavailableException("Simulado");
        public Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingModel model, IReadOnlyList<string> inputs, bool interactive, CancellationToken ct) => throw new EmbeddingUnavailableException("Simulado");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> call) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => call(r, ct); }
}
