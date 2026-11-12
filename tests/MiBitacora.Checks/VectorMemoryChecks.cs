using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiBitacora.Data;
using MiBitacora.Components.Shared;
using MiBitacora.Services;

internal static class VectorMemoryChecks
{
    public static async Task RunAsync(Action<bool, string> check, bool live)
    {
        await ClientAsync(check);
        var original = string.Concat(Enumerable.Repeat("á🙂歌 palabra ", 600)) + "COLA_COMPLETA\ud800";
        var parts = JournalVectors.Split(new("original", null, null, "Prueba", original)).ToArray();
        check(string.Concat(parts.Select(s => s.Text)) == original && parts.All(s => Encoding.UTF8.GetByteCount(s.Text) <= JournalVectors.ChunkBytes),
            "Vectores: fragmenta UTF-8 y emojis sin truncar ni perder el final");
        var root = Path.Combine(Path.GetTempPath(), "mibitacora-vectors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await StorageAsync(root, check, live); }
        finally { Directory.Delete(root, true); }
    }

    private static async Task ClientAsync(Action<bool, string> check)
    {
        using var gate = new EmbeddingGate();
        var calls = 0;
        var digest = new string('a', 64);
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            check(request.RequestUri!.Host == "127.0.0.1" && request.Headers.Authorization is null,
                "Embeddings: sólo loopback y sin credenciales de proveedores pagos");
            if (request.Method == HttpMethod.Get) return Json(new { models = new[] { new { name = "embeddinggemma:latest", digest } } });
            calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var json = body.RootElement;
            check(request.RequestUri.AbsolutePath == "/api/embed" && !json.GetProperty("truncate").GetBoolean()
                && json.GetProperty("dimensions").GetInt32() == 768 && json.GetProperty("options").GetProperty("num_gpu").GetInt32() == 0,
                "Embeddings: API por lotes, CPU y error explícito en lugar de truncar contenido");
            return Json(new { embeddings = json.GetProperty("input").EnumerateArray().Select(_ => new[] { 3f, 4f }.Concat(new float[766]).ToArray()).ToArray() });
        }));
        var client = new OllamaEmbeddingClient(http, Options.Create(new EmbeddingOptions()), gate);
        var model = await client.GetModelAsync(default);
        var result = await client.EmbedAsync(model, ["title: Prueba | text: canción", "task: search result | query: canto"], true, default);
        check(calls == 1 && result.Count == 2 && Math.Abs(result[0][0] - .6) < .001 && model.Key.EndsWith(digest),
            "Embeddings: normaliza y vincula el espacio vectorial al digest del modelo");
        digest = new string('b', 64);
        try { await client.EmbedAsync(model, ["consulta"], true, default); throw new Exception("Mezcló modelos"); }
        catch (EmbeddingUnavailableException) { }
        check(calls == 1, "Embeddings: cambiar el modelo impide mezclar vectores incompatibles");
        foreach (var options in new[] { new EmbeddingOptions { BaseUrl = "http://192.168.1.1:11434" },
            new EmbeddingOptions { BaseUrl = "http://127.0.0.1:11434/otro" }, new EmbeddingOptions { Model = "remoto:cloud" } })
            check(!options.IsValid(), "Embeddings: rechaza un servidor o modelo fuera de la instalación local");
        using var malformed = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { embeddings = new[] { new[] { 0f } } }))));
        // A metadata response without an installed model is recoverable and does not expose its body.
        try { await new OllamaEmbeddingClient(malformed, Options.Create(new EmbeddingOptions()), gate).GetModelAsync(default); throw new Exception("Modelo inexistente"); }
        catch (EmbeddingUnavailableException ex) { check(!ex.Message.Contains("embeddings"), "Embeddings: falta de modelo produce un error recuperable sin contenido del servidor"); }
        try { JournalVectors.Normalize([0, 0], 2); throw new Exception("Vector cero"); }
        catch (EmbeddingUnavailableException) { }
        try { JournalVectors.Normalize([float.NaN, 1], 2); throw new Exception("Vector inválido"); }
        catch (EmbeddingUnavailableException) { check(true, "Embeddings: rechaza vectores nulos, no finitos y dimensiones incompatibles"); }
        var wrongDimensions = false;
        using var invalidHttp = new HttpClient(new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? Metadata() : Json(new { embeddings = wrongDimensions ? new[] { new[] { 1f, 0f } } : Array.Empty<float[]>() }))));
        var invalidClient = new OllamaEmbeddingClient(invalidHttp, Options.Create(new EmbeddingOptions()), gate);
        var validModel = await invalidClient.GetModelAsync(default);
        foreach (var incompatible in new[] { false, true })
        {
            wrongDimensions = incompatible;
            try { await invalidClient.EmbedAsync(validModel, ["texto ficticio"], true, default); throw new Exception("Aceptó un lote inválido"); }
            catch (EmbeddingUnavailableException) { }
        }
        check(true, "Embeddings: valida cantidad de resultados y dimensiones antes de persistir");
        var stalled = new StalledStream();
        using var slowHttp = new HttpClient(new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? Metadata() : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalled) }))) { Timeout = Timeout.InfiniteTimeSpan };
        var slowClient = new OllamaEmbeddingClient(slowHttp, Options.Create(new EmbeddingOptions { TimeoutSeconds = 5 }), gate);
        var slowModel = await slowClient.GetModelAsync(default);
        try { await slowClient.EmbedAsync(slowModel, ["texto ficticio"], true, default).WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("No venció el plazo"); }
        catch (EmbeddingUnavailableException) { }
        check(stalled.Cancelled, "Embeddings: el plazo interrumpe también un cuerpo HTTP bloqueado y libera la cola");
        using var lease = await gate.EnterAsync(true, default);

        static HttpResponseMessage Metadata() => Json(new { models = new[] { new { name = "embeddinggemma:latest", digest = new string('a', 64) } } });
    }

    private static async Task StorageAsync(string root, Action<bool, string> check, bool live)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"Data Source={root}/test.db;Pooling=False;Foreign Keys=True"));
        services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton(new UploadFileStore(Path.Combine(root, "uploads"), NullLogger<UploadFileStore>.Instance));
        services.AddScoped<IJournalStore, JournalStore>();
        services.AddSingleton(TimeZoneInfo.Utc);
        var client = new FakeEmbeddings();
        var model = new Reply();
        var legacy = new NoLegacyMemory();
        services.AddSingleton<IJournalEmbeddingClient>(client);
        services.AddSingleton<IJournalReflector>(model);
        services.AddSingleton<IJournalMemoryAnalyzer>(legacy);
        services.AddSingleton<IAudioTranscriber>(new Audio());
        services.AddSingleton(Options.Create(new EmbeddingOptions { BatchSize = 2 }));
        services.AddSingleton<JournalMemoryJobs>();
        services.AddSingleton<JournalConversationJobs>();
        services.AddScoped<VectorMemoryService>();
        services.AddScoped<JournalMemoryService>();
        services.AddScoped<JournalConversationService>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var store = scope.ServiceProvider.GetRequiredService<IJournalStore>();
        var memory = scope.ServiceProvider.GetRequiredService<VectorMemoryService>();
        var flow = scope.ServiceProvider.GetRequiredService<JournalConversationService>();
        var a = Guid.NewGuid().ToString(); var b = Guid.NewGuid().ToString();
        await using (var db = await factory.CreateDbContextAsync())
        {
            // Upgrade an existing account as well as a subsequent signup.
            var previous = db.Database.GetMigrations().Last(m => !m.EndsWith("AddVectorMemory"));
            await db.GetService<IMigrator>().MigrateAsync(previous);
            db.Users.Add(new ApplicationUser { Id = a, UserName = "vector-a" });
            await db.SaveChangesAsync();
            await db.Database.MigrateAsync();
            check(!db.Database.HasPendingModelChanges(), "Vectores: migración y snapshot coinciden con el modelo EF");
            db.Users.Add(new ApplicationUser { Id = b, UserName = "vector-b" });
            await db.SaveChangesAsync();
        }
        check(await Count("SELECT COUNT(*) FROM JournalVectorStates") == 2, "Vectores: la cola persistente incluye cuentas existentes y nuevas");
        var notebook = await store.SaveNotebookAsync(a, null, "Música");
        var old = await store.AddEntryAsync(a, "ANTECEDENTE_CANCION: Dejé el coro de Muñoz porque el ensayo coincidía con cuidar a mi madre.", [], notebookId: notebook.Id);
        await Date(old.Id, DateTime.UtcNow.AddMonths(-9));
        var longEntry = await store.AddEntryAsync(a, string.Concat(Enumerable.Repeat("🙂texto con acentos áéíóú ", 180)) + " FINAL_DEL_ORIGINAL", []);
        for (var i = 0; i < 9; i++) await store.AddEntryAsync(a, $"Desayuno cotidiano número {i}: tostadas.", []);
        var prior = await store.AddEntryAsync(a, "Me gustaría recuperar una actividad que disfruto.", []);
        await store.SaveReflectionAsync(a, prior.Id, new("Prueba", "HIPOTESIS_IA_NO_ES_UN_HECHO", [], ["¿Querés volver a cantar en el coro?"], [], null, false));
        var note = await store.SaveNoteAsync(a, null, "La factura de electricidad se paga el martes.");
        await store.SaveProfileAsync(a, new("Prueba", "PERFIL_ACTUAL: tengo un perro.", false));
        var current = await store.AddEntryAsync(a, "Quisiera volver a cantar, sigo con las mismas obligaciones.", []);
        var future = await store.AddEntryAsync(a, "SECRETO_FUTURO: coro cantar ensayo.", []);
        await Date(future.Id, DateTime.UtcNow.AddDays(2));
        await store.AddEntryAsync(b, "SECRETO_OTRA_CUENTA: coro cantar ensayo.", []);
        await IndexAll(a); await IndexAll(b);
        check(client.Documents.Count > 15 && client.Documents.All(s => !s.Contains("HIPOTESIS_IA_NO_ES_UN_HECHO")),
            "Vectores: indexa originales completos, nunca las afirmaciones generadas por IA");
        check(await Count("SELECT COUNT(*) FROM JournalVectorChunks WHERE Embedding IS NULL") == 0
            && string.Concat((await Texts(longEntry.Id))) == longEntry.Text,
            "Vectores: conserva todos los fragmentos del texto largo y sus embeddings");
        var docs = client.Documents.Count;
        var revision = await Revision();
        var first = await flow.GenerateAsync(a, current.Id);
        check(model.Calls == 1 && legacy.Calls == 0 && model.Context!.MemorySources!.Any(s => s.EntryId == old.Id),
            "Vectores: una devolución recupera un antecedente antiguo de otro diario con una sola generación");
        check(model.Context!.MemorySources!.All(s => !s.Text.Contains("SECRETO")) && model.Context.History.Count == 2,
            "Vectores: excluye otras cuentas y el futuro antes de puntuar; conserva los turnos recientes");
        check((await store.GetEntryAsync(a, prior.Id))!.Reflection!.Overview == "HIPOTESIS_IA_NO_ES_UN_HECHO"
            && (await store.GetEntryAsync(a, future.Id))!.Reflection is null,
            "Vectores: pedir una devolución sólo cambia la respuesta de la entrada elegida");
        var queries = client.Queries.Count;
        await flow.GenerateAsync(a, current.Id);
        check(client.Documents.Count == docs && client.Queries.Count == queries && await Revision() == revision,
            "Vectores: regenerar reutiliza consulta e índice sin volver a procesar el historial");
        model.External = legacy.External = true;
        await flow.GenerateAsync(a, current.Id);
        check(client.Documents.Count == docs && client.Queries.Count == queries && model.Context!.History.Count == 6,
            "Vectores: el plan pago reutiliza los mismos embeddings y amplía el contexto conversacional");
        model.External = legacy.External = false;
        var foreign = await store.GetContextAsync(a, current.Id, false);
        try { await memory.EnrichAsync(b, foreign, false); throw new Exception("Contexto cruzado"); }
        catch (JournalInputException) { check(true, "Vectores: un contexto ajeno no autoriza leer ni guardar consultas de otra cuenta"); }

        // Short replies need the previous question, and that question belongs in the cache fingerprint.
        var shortReply = await store.AddEntryAsync(a, "Sí, eso.", []);
        // AddEntry is monotonic even after an imported future date. Keep this fixture before that future entry.
        await Date(shortReply.Id, DateTime.UtcNow);
        await store.SaveReflectionAsync(a, current.Id, new("Prueba", "Propuesta", [], ["¿Querés volver a cantar?"], [], null, false));
        await flow.GenerateAsync(a, shortReply.Id);
        check(client.Queries.Last().Contains("cantar"), "Vectores: una respuesta breve incorpora la pregunta inmediata al buscar");
        queries = client.Queries.Count;
        await store.SaveReflectionAsync(a, current.Id, new("Prueba", "Otra propuesta", [], ["¿Te preocupa la electricidad?"], [], null, false));
        await flow.GenerateAsync(a, shortReply.Id);
        check(client.Queries.Count == queries + 1, "Vectores: cambiar la pregunta previa invalida la consulta aunque el mensaje sea idéntico");
        await store.DeleteEntryAsync(a, shortReply.Id);

        await Date(current.Id, DateTime.UtcNow.AddMonths(-1));
        await flow.GenerateAsync(a, current.Id);
        check(model.Context!.Profile.PersonalContext == "" && model.Context.Notes.Count == 0
            && model.Context.MemorySources!.All(s => s.CreatedUtc < model.Context.Current.CreatedUtc),
            "Vectores: una entrada histórica no usa perfil actual, notitas posteriores ni mensajes posteriores");
        await Date(current.Id, current.CreatedUtc);
        await IndexAll(a);

        revision = await Revision(); docs = client.Documents.Count;
        await store.SetEntryForSessionAsync(a, prior.Id, true);
        await store.MoveEntryAsync(a, prior.Id, notebook.Id);
        await IndexAll(a);
        check(await Revision() == revision && client.Documents.Count == docs,
            "Vectores: cambiar diario, selección o devolución no reindexa los originales");
        await store.SaveNoteAsync(a, note.Id, "La factura de electricidad ahora vence el jueves.");
        check(await Revision() > revision && await Count("SELECT COUNT(*) FROM JournalVectorChunks WHERE NoteId=$note", new SqliteParameter("$note", note.Id)) == 0,
            "Vectores: editar una notita invalida inmediatamente sus fragmentos y agenda la actualización");
        await IndexAll(a);
        await store.SaveProfileAsync(a, new("Prueba", "", false));
        check(await Count("SELECT COUNT(*) FROM JournalVectorChunks WHERE UserId=$user AND Kind=3", new SqliteParameter("$user", a)) == 0,
            "Vectores: borrar el contexto personal retira también sus vectores");

        var audio = await store.AddEntryAsync(a, "", [new("voz.wav", AttachmentViewerChecks.Wave(), true)]);
        await store.SaveAudioTranscriptAsync(a, audio.Attachments.Single().Id, "Transcripción ficticia: el coro ensaya el martes.");
        await IndexAll(a);
        check(await Count("SELECT COUNT(*) FROM JournalVectorChunks WHERE EntryId=$entry AND Kind=4", new SqliteParameter("$entry", audio.Id)) == 1,
            "Vectores: la transcripción guardada se incorpora como fuente de voz sin reprocesar el audio");

        // A failed second batch must retain the first batch across service restart.
        await store.AddEntryAsync(a, new string('p', 5700), []);
        var beforePartial = await Count("SELECT COUNT(*) FROM JournalVectorChunks WHERE UserId=$user AND Embedding IS NOT NULL", new SqliteParameter("$user", a));
        client.FailAt = client.Calls + 2;
        try { await memory.IndexPendingAsync(a, client.Model, 100); throw new Exception("Faltó fallo de lote"); }
        catch (EmbeddingUnavailableException) { }
        check(await Count("SELECT COUNT(*) FROM JournalVectorChunks WHERE UserId=$user AND Embedding IS NOT NULL", new SqliteParameter("$user", a)) > beforePartial
            && await Count("SELECT COUNT(*) FROM JournalVectorStates WHERE UserId=$user AND IndexedRevision!=Revision", new SqliteParameter("$user", a)) == 1,
            "Vectores: un fallo conserva el avance parcial y deja el trabajo pendiente en SQLite");
        client.FailAt = -1;
        var restarted = new VectorMemoryService(factory, client, Options.Create(new EmbeddingOptions { BatchSize = 2 }), NullLogger<VectorMemoryService>.Instance);
        var resumed = await restarted.IndexPendingAsync(a, client.Model, 100);
        check(resumed.Complete, "Vectores: una instancia nueva continúa la cola persistente sin rehacer los lotes guardados");

        await store.AddEntryAsync(a, "Otra tarea pendiente del índice.", []);
        using (var generationGate = new LocalInferenceGate())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var generationHttp = new HttpClient(new Handler(async (_, ct) =>
            {
                entered.SetResult(); await release.Task.WaitAsync(ct);
                return Json(new { done = true, done_reason = "stop", message = new { content = "{}" } });
            }));
            var generation = new OllamaModelClient(generationHttp, Options.Create(new OllamaOptions()), generationGate, NullLogger.Instance)
                .GenerateAsync("Prueba", "Mensaje ficticio", JsonSerializer.SerializeToElement(new { type = "object" }), [], default);
            await entered.Task;
            var background = new VectorMemoryService(factory, client, Options.Create(new EmbeddingOptions()), NullLogger<VectorMemoryService>.Instance, generationGate);
            var paused = await background.IndexPendingAsync(a, client.Model);
            release.SetResult(); await generation;
            check(!paused.Complete && paused.Embedded == 0, "Vectores: el backfill cede recursos mientras Gemma genera una devolución");
        }
        await IndexAll(a);

        var doomed = await store.AddEntryAsync(a, "BORRADO_DURANTE_EMBEDDING: coro", []);
        client.Before = async () => { client.Before = null; await store.DeleteEntryAsync(a, doomed.Id); };
        try { await memory.IndexPendingAsync(a, client.Model, 100); throw new Exception("Resucitó el original"); }
        catch (JournalInputException) { }
        check(await Count("SELECT COUNT(*) FROM JournalVectorChunks WHERE EntryId=$entry", new SqliteParameter("$entry", doomed.Id)) == 0,
            "Vectores: borrar durante la inferencia impide resucitar fragmentos o embeddings");
        await IndexAll(a);

        var saved = (await store.GetEntryAsync(a, current.Id))!.Reflection!.Overview;
        model.Before = async () => { model.Before = null; await store.SaveNoteAsync(a, note.Id, "Cambio concurrente de contexto."); };
        try { await flow.GenerateAsync(a, current.Id); throw new Exception("Publicó respuesta obsoleta"); }
        catch (JournalInputException) { }
        check((await store.GetEntryAsync(a, current.Id))!.Reflection!.Overview == saved,
            "Vectores: editar durante la generación conserva la devolución anterior mediante validación atómica");
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE JournalEntries SET Text={"Quisiera volver al coro."} WHERE Id={current.Id}");
        client.Unavailable = true;
        var messages = new List<string>();
        var calls = model.Calls;
        await flow.GenerateAsync(a, current.Id, progress: new ProgressCapture(messages));
        check(model.Calls == calls + 1, "Vectores: sin embeddings sigue haciendo una sola generación");
        check(model.Context!.MemorySources!.Any(s => s.EntryId == old.Id),
            "Vectores: la búsqueda lexical prioriza el mensaje actual y recupera el original sin embeddings");
        check(messages.Any(s => s.Contains("coincidencias de texto")), "Vectores: avisa cuando la memoria semántica está pendiente");
        check((await store.GetEntryAsync(a, current.Id))!.Reflection!.LimitedMemory,
            "Vectores: el aviso de memoria limitada permanece guardado junto a la respuesta");
        using (var renderer = new HtmlRenderer(provider, provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()))
        {
            var reflection = (await store.GetEntryAsync(a, current.Id))!.Reflection!;
            var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<ReflectionCard>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Reflection"] = reflection }))).ToHtmlString());
            check(html.Contains("Parte de la memoria") && html.Contains("no estuvo disponible"),
                "Vectores: la tarjeta muestra el alcance limitado después de recargar la página");
        }
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE JournalEntries SET Text={"Quiero retomar con Mun\u0303oz."} WHERE Id={current.Id}");
        await flow.GenerateAsync(a, current.Id);
        check(model.Context!.MemorySources!.Any(s => s.EntryId == old.Id),
            "Vectores: la búsqueda por nombres tolera acentos Unicode descompuestos de textos importados");
        client.Unavailable = false;

        var oldKey = client.Model.Key;
        client.Model = client.Model with { Key = "synthetic-model-v2" };
        await IndexAll(a);
        check(await Count("SELECT COUNT(*) FROM JournalVectorChunks WHERE UserId=$user AND ModelKey=$key", new SqliteParameter("$user", a), new SqliteParameter("$key", oldKey)) == 0,
            "Vectores: un digest nuevo reconstruye el espacio sin reutilizar vectores de otro modelo");
        await store.DeleteEntryAsync(a, old.Id);
        check(await Count("SELECT COUNT(*) FROM JournalVectorText WHERE JournalVectorText MATCH 'ANTECEDENTE_CANCION'") == 0,
            "Vectores: borrar el original lo retira también del índice de texto completo");
        var coldUser = Guid.NewGuid().ToString();
        await using (var db = await factory.CreateDbContextAsync()) { db.Users.Add(new ApplicationUser { Id = coldUser, UserName = "vector-cold" }); await db.SaveChangesAsync(); }
        var coldNotebook = await store.SaveNotebookAsync(coldUser, null, "Coro");
        var coldOriginal = await store.AddEntryAsync(coldUser, "El coro se reunía los martes.", [], notebookId: coldNotebook.Id);
        var coldCurrent = await store.AddEntryAsync(coldUser, "Me gustaría volver al coro.", []);
        docs = client.Documents.Count; queries = client.Queries.Count;
        var coldResponse = await flow.GenerateAsync(coldUser, coldCurrent.Id);
        check(client.Documents.Count == docs && client.Queries.Count == queries && coldResponse.Reflection!.LimitedMemory
            && model.Context!.MemorySources!.Any(s => s.EntryId == coldOriginal.Id),
            "Vectores: una cuenta sin indexar responde con texto sin esperar ni procesar el backlog");
        calls = model.Calls;
        using (var worker = new VectorMemoryWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EmbeddingOptions { BatchSize = 2 }), NullLogger<VectorMemoryWorker>.Instance))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await worker.StartAsync(deadline.Token);
            while (await Count("SELECT COUNT(*) FROM JournalVectorStates WHERE UserId=$user AND IndexedRevision=Revision", new SqliteParameter("$user", coldUser)) == 0)
                await Task.Delay(50, deadline.Token);
            await worker.StopAsync(deadline.Token);
        }
        check(model.Calls == calls && await Count("SELECT COUNT(*) FROM JournalVectorChunks WHERE UserId=$user AND Embedding IS NOT NULL", new SqliteParameter("$user", coldUser)) == 2,
            "Vectores: el worker atiende la cola sin generar respuestas ni depender de una petición abierta");
        await using (var db = await factory.CreateDbContextAsync()) await db.Users.Where(u => u.Id == coldUser).ExecuteDeleteAsync();
        if (live) await LiveAsync(factory, store, check);
        await using (var db = await factory.CreateDbContextAsync()) await db.Users.Where(u => u.Id == a).ExecuteDeleteAsync();
        check(await Count("SELECT COUNT(*) FROM JournalVectorStates WHERE UserId=$user", new SqliteParameter("$user", a)) == 0
            && await Count("SELECT COUNT(*) FROM JournalVectorQueries WHERE UserId=$user", new SqliteParameter("$user", a)) == 0
            && await Count("SELECT COUNT(*) FROM JournalVectorChunks WHERE UserId=$user", new SqliteParameter("$user", a)) == 0
            && await Count("SELECT COUNT(*) FROM JournalVectorStates WHERE UserId=$user", new SqliteParameter("$user", b)) == 1,
            "Vectores: borrar una cuenta elimina cola, consultas e índice y preserva la otra cuenta");
        await using (var db = await factory.CreateDbContextAsync())
        {
            var usersBefore = await db.Users.CountAsync();
            var entriesBefore = await Count("SELECT COUNT(*) FROM JournalEntries");
            var previous = db.Database.GetMigrations().Last(m => !m.EndsWith("AddVectorMemory"));
            await db.GetService<IMigrator>().MigrateAsync(previous);
            await db.Database.MigrateAsync();
            check(await Count("SELECT COUNT(*) FROM JournalVectorStates") == usersBefore && await Count("SELECT COUNT(*) FROM JournalEntries") == entriesBefore,
                "Vectores: la migración se revierte y aplica de nuevo sin perder originales");
        }

        async Task IndexAll(string user) { while (!(await memory.IndexPendingAsync(user, client.Model, 100)).Complete) { } }
        async Task Date(Guid entry, DateTime date)
        { await using var db = await factory.CreateDbContextAsync(); await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE JournalEntries SET CreatedUtc={date} WHERE Id={entry}"); }
        async Task<long> Revision() => await Count("SELECT Revision FROM JournalVectorStates WHERE UserId=$user", new SqliteParameter("$user", a));
        async Task<long> Count(string sql, params SqliteParameter[] args)
        {
            await using var db = await factory.CreateDbContextAsync(); await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = sql;
            foreach (var arg in args) command.Parameters.Add(arg);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        async Task<string[]> Texts(Guid id)
        {
            await using var db = await factory.CreateDbContextAsync(); await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT Text FROM JournalVectorChunks WHERE EntryId=$entry ORDER BY Id"; command.Parameters.Add(new SqliteParameter("$entry", id));
            var result = new List<string>(); await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(reader.GetString(0)); return result.ToArray();
        }
    }

    private static async Task LiveAsync(IDbContextFactory<ApplicationDbContext> factory, IJournalStore store, Action<bool, string> check)
    {
        // This opt-in evaluation uses only a new fictitious account in the temporary test database.
        var user = Guid.NewGuid().ToString();
        await using (var db = await factory.CreateDbContextAsync()) { db.Users.Add(new ApplicationUser { Id = user, UserName = "embedding-live-synthetic" }); await db.SaveChangesAsync(); }
        var music = await store.SaveNotebookAsync(user, null, "Música ficticia");
        var old = await store.AddEntryAsync(user, "Dejé el coro municipal porque los ensayos coincidían con el horario de cuidado de mi madre. Extraño cantar en grupo.", [], notebookId: music.Id);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE JournalEntries SET CreatedUtc={DateTime.UtcNow.AddMonths(-8)} WHERE Id={old.Id}");
        foreach (var text in new[] { "Hice una tarta de manzanas para compartir.", "El colectivo tardó en llegar y llovía.", "Trasplanté las suculentas del balcón.", "Compré tornillos para arreglar la mesa.", "Organicé mis apuntes de matemática.", "Esta semana el desayuno tuvo yogur y fruta." })
            await store.AddEntryAsync(user, text, []);
        var current = await store.AddEntryAsync(user, "Quisiera retomar una actividad musical con otras personas, pero las responsabilidades familiares todavía limitan mis horarios.", []);
        using var gate = new EmbeddingGate();
        using var handler = new CountingHandler();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new OllamaEmbeddingClient(http, Options.Create(new EmbeddingOptions()), gate);
        var memory = new VectorMemoryService(factory, client, Options.Create(new EmbeddingOptions()), NullLogger<VectorMemoryService>.Instance);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var model = await client.GetModelAsync(default);
        var indexed = await memory.IndexPendingAsync(user, model, 100);
        check(indexed.Complete, $"EmbeddingGemma real: indexa {indexed.Embedded} fragmentos ficticios ({timer.Elapsed.TotalSeconds:F2} s)");
        timer.Restart();
        var context = await memory.EnrichAsync(user, await store.GetContextAsync(user, current.Id, false), false);
        check(context.MemorySources!.Any(s => s.EntryId == old.Id), $"EmbeddingGemma real: recupera la conexión musical entre diarios ({timer.Elapsed.TotalSeconds:F2} s)");
        timer.Restart();
        await memory.EnrichAsync(user, await store.GetContextAsync(user, current.Id, false), false);
        check(true, $"EmbeddingGemma real: reutiliza la consulta guardada ({timer.Elapsed.TotalSeconds:F2} s)");
        using var generationGate = new LocalInferenceGate();
        var reflector = new OllamaJournalService(http, Options.Create(new OllamaOptions()), generationGate, NullLogger<OllamaJournalService>.Instance);
        var flow = new JournalConversationService(store, new Audio(),
            new JournalMemoryService(factory, new NoLegacyMemory(), new JournalMemoryJobs(), vectors: memory), reflector, new JournalConversationJobs());
        var queriesBefore = handler.Embeddings;
        timer.Restart();
        var response = await flow.GenerateAsync(user, current.Id);
        check(handler.Generations == 1 && handler.Embeddings == queriesBefore && response.Reflection is { IsDemo: false, LimitedMemory: false }
            && response.Reflection.ContextSources!.Any(s => s.EntryId == old.Id),
            $"Gemma 4 real: una sola generación completa, con antecedentes y sin recalcular embeddings ({timer.Elapsed.TotalSeconds:F2} s; {handler.InputTokens} tokens de entrada, {handler.OutputTokens} de salida; carga {handler.LoadSeconds:F2} s)");
    }

    private sealed class FakeEmbeddings : IJournalEmbeddingClient
    {
        public EmbeddingModel Model { get; set; } = new("synthetic-model-v1", 128);
        public bool Unavailable; public int Calls; public int FailAt = -1;
        public List<string> Documents { get; } = []; public List<string> Queries { get; } = [];
        public Func<Task>? Before;
        public Task<EmbeddingModel> GetModelAsync(CancellationToken ct) => Unavailable ? throw new EmbeddingUnavailableException("No disponible") : Task.FromResult(Model);
        public async Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingModel model, IReadOnlyList<string> inputs, bool interactive, CancellationToken ct)
        {
            Calls++;
            if (Calls == FailAt || Unavailable) throw new EmbeddingUnavailableException("Fallo ficticio");
            if (Before is { } action) await action();
            (interactive ? Queries : Documents).AddRange(inputs);
            return inputs.Select(s => { var vector = new float[128]; vector[s.Contains("coro", StringComparison.OrdinalIgnoreCase) || s.Contains("cantar", StringComparison.OrdinalIgnoreCase) ? 0 : 1] = 1; return vector; }).ToArray();
        }
    }
    private sealed class Reply : IJournalReflector
    {
        public bool IsConfigured => true;
        public bool UsesExternalProvider => External;
        public bool External; public int Calls; public JournalContext? Context; public Func<Task>? Before;
        public async Task<JournalReflection> ReflectAsync(JournalContext context, bool useAi, CancellationToken cancellationToken)
        {
            Context = context; Calls++;
            if (Before is { } action) await action();
            return new("Respuesta ficticia", "Lectura " + Calls, [], [], [], null, false);
        }
    }
    private sealed class NoLegacyMemory : IJournalMemoryAnalyzer
    {
        public bool External; public int Calls;
        public bool UsesExternalProvider => External;
        public Task<IReadOnlyList<MemoryDigest>> IndexAsync(IReadOnlyList<TopicSource> sources, CancellationToken ct) { Calls++; throw new Exception("No debe resumir para responder"); }
        public Task<IReadOnlyList<string>> RecallAsync(string conversation, IReadOnlyList<MemoryCard> memories, int limit, CancellationToken ct) { Calls++; throw new Exception("No debe usar un LLM para buscar"); }
    }
    private sealed class Audio : IAudioTranscriber
    {
        public bool IsConfigured => true;
        public Task<string> TranscribeAsync(AttachmentContent audio, CancellationToken ct) => Task.FromResult("Audio ficticio.");
    }
    private sealed class ProgressCapture(List<string> messages) : IProgress<string> { public void Report(string value) => messages.Add(value); }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private sealed class CountingHandler : DelegatingHandler
    {
        public int Generations; public int Embeddings;
        public int InputTokens; public int OutputTokens; public double LoadSeconds;
        public CountingHandler() : base(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/api/chat") Generations++;
            if (request.RequestUri.AbsolutePath == "/api/embed") Embeddings++;
            var response = await base.SendAsync(request, ct);
            if (request.RequestUri.AbsolutePath == "/api/chat" && response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
                InputTokens = json.RootElement.GetProperty("prompt_eval_count").GetInt32();
                OutputTokens = json.RootElement.GetProperty("eval_count").GetInt32();
                LoadSeconds = json.RootElement.GetProperty("load_duration").GetDouble() / 1_000_000_000;
            }
            return response;
        }
    }
    private sealed class StalledStream : Stream
    {
        public bool Cancelled;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            try { await Task.Delay(Timeout.Infinite, ct); return 0; }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
