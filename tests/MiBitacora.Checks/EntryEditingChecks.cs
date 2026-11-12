using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiBitacora.Components.Shared;
using MiBitacora.Data;
using MiBitacora.Services;

internal static class EntryEditingChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "mibitacora-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await VerifyAsync(root, check); }
        finally { Directory.Delete(root, true); }
    }
    private static async Task VerifyAsync(string root, Action<bool, string> check)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"Data Source={root}/test.db;Foreign Keys=True;Pooling=False"));
        services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton(new UploadFileStore(Path.Combine(root, "uploads"), NullLogger<UploadFileStore>.Instance));
        services.AddScoped<IJournalStore, JournalStore>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var store = scope.ServiceProvider.GetRequiredService<IJournalStore>();
        var a = Guid.NewGuid().ToString(); var b = Guid.NewGuid().ToString();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            db.Users.AddRange(new ApplicationUser { Id = a, UserName = "edit-a" }, new ApplicationUser { Id = b, UserName = "edit-b" });
            await db.SaveChangesAsync();
        }
        var client = new Embeddings();
        var memory = new VectorMemoryService(factory, client, Options.Create(new EmbeddingOptions()), NullLogger<VectorMemoryService>.Instance);
        var notebook = await store.SaveNotebookAsync(a, null, "Recuerdos");
        var file = Encoding.UTF8.GetBytes("DOCUMENTO_FIJO: una lista que se conserva completa.");
        var entry = await store.AddEntryAsync(a, "OBSOLETOXQ: Antes tenía un recuerdo distinto del lago.", [new("apuntes.txt", file)], notebookId: notebook.Id);
        await store.SetEntryForSessionAsync(a, entry.Id, true);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE JournalEntries SET IsDateOnly=1 WHERE Id={entry.Id}");
        await store.SaveReflectionAsync(a, entry.Id, Reply("RESPUESTA_VIEJA_NO_BUSCAR"));
        entry = (await store.GetEntryAsync(a, entry.Id))!;
        var other = await store.AddEntryAsync(a, "Otra entrada intacta.", []);
        await store.SaveReflectionAsync(a, other.Id, Reply("Respuesta intacta"));
        var current = await store.AddEntryAsync(a, "Quiero volver a pensar en ese lago.", []);
        var foreign = await store.AddEntryAsync(b, "Cuaderno ajeno intacto.", []);
        await IndexAll(a); await IndexAll(b);
        await memory.EnrichAsync(a, await store.GetContextAsync(a, current.Id, false), false);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO JournalMemories (UserId,SourceId,SourceHash,DigestJson,EntryId) VALUES ({a},'old-edit-cache','old','{{}}',{entry.Id})");
            var packed = JournalVectors.Pack(Unit());
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO JournalVectorQueries (UserId,EntryId,QueryHash,ModelKey,Embedding) VALUES ({b},{foreign.Id},'other-query',{client.Model.Key},{packed})");
        }
        var revision = await Revision();
        var embedded = client.Documents.Count;
        const string editedText = "Ahora recuerdo el lago con más calma y quiero volver a escribir sobre ese viaje.";
        var updated = await store.UpdateEntryAsync(a, entry.Id, editedText, expectedText: entry.Text);
        check(updated.Text == editedText && updated.Reflection is null, "Edición: guarda el original corregido y quita su devolución anterior");
        check(updated.Id == entry.Id && updated.CreatedUtc == entry.CreatedUtc && updated.IsDateOnly && updated.ForSession
            && updated.NotebookId == notebook.Id && updated.NotebookName == "Recuerdos" && updated.Attachments.SequenceEqual(entry.Attachments),
            "Edición: conserva identidad, fecha importada, diario, selección y metadatos de adjuntos");
        var attachment = await store.GetAttachmentAsync(a, entry.Attachments.Single().Id);
        await using (var stream = attachment!.Content)
        {
            using var copy = new MemoryStream(); await stream.CopyToAsync(copy);
            check(copy.ToArray().SequenceEqual(file), "Edición: conserva el archivo original byte por byte");
        }
        check(await Count("SELECT COUNT(*) FROM JournalVectorChunks WHERE EntryId=$entry", new SqliteParameter("$entry", entry.Id)) == 0
            && await Count("SELECT COUNT(*) FROM JournalVectorText WHERE JournalVectorText MATCH 'OBSOLETOXQ'") == 0
            && await Revision() > revision && client.Documents.Count == embedded,
            "Edición: retira vectores y texto obsoletos de inmediato y agenda su reconstrucción sin bloquear el guardado");
        check(await Count("SELECT COUNT(*) FROM JournalMemories WHERE UserId=$user AND EntryId=$entry", new SqliteParameter("$user", a), new SqliteParameter("$entry", entry.Id)) == 0
            && await Count("SELECT COUNT(*) FROM JournalVectorQueries WHERE UserId=$user", new SqliteParameter("$user", a)) == 0
            && await Count("SELECT COUNT(*) FROM JournalVectorQueries WHERE UserId=$user", new SqliteParameter("$user", b)) == 1,
            "Edición: invalida síntesis y consultas derivadas sin afectar otra cuenta");
        check(await Count("SELECT COUNT(*) FROM JournalEntries WHERE Id=$entry AND SearchText LIKE '%respuesta_vieja%'", new SqliteParameter("$entry", entry.Id)) == 0
            && (await store.GetEntryAsync(a, other.Id))!.Reflection!.Overview == "Respuesta intacta",
            "Edición: limpia la búsqueda de la devolución vieja y preserva respuestas de otras entradas");
        await IndexAll(a);
        check(client.Documents.Skip(embedded).All(s => s.Contains(editedText) || s.Contains("DOCUMENTO_FIJO"))
            && await Count("SELECT COUNT(*) FROM JournalVectorChunks WHERE EntryId=$entry AND Embedding IS NOT NULL", new SqliteParameter("$entry", entry.Id)) == 2,
            "Edición: recalcula sólo fuentes de la entrada modificada con el texto nuevo");
        await store.SaveReflectionAsync(a, entry.Id, Reply("Lectura nueva"));
        revision = await Revision();
        var noChange = await store.UpdateEntryAsync(a, entry.Id, " " + editedText + " ", expectedText: editedText);
        check(noChange.Reflection?.Overview == "Lectura nueva" && await Revision() == revision,
            "Edición: guardar el mismo texto no borra respuestas ni reindexa sin necesidad");
        await Reject(async () => { await store.UpdateEntryAsync(b, entry.Id, "Intrusión"); });
        await Reject(async () => { await store.UpdateEntryAsync(a, Guid.NewGuid(), "Inexistente"); });
        check((await store.GetEntryAsync(a, entry.Id))!.Text == editedText && (await store.GetEntryAsync(b, foreign.Id))!.Text == foreign.Text,
            "Edición: una entrada ajena y una inexistente se rechazan sin modificar datos");
        await Reject(async () => { await store.UpdateEntryAsync(a, entry.Id, "No debe pisar la edición", expectedText: entry.Text); });
        check((await store.GetEntryAsync(a, entry.Id))!.Reflection?.Overview == "Lectura nueva",
            "Edición: un editor desactualizado no sobrescribe cambios de otra pestaña");
        await Reject(async () => { await store.UpdateEntryAsync(a, other.Id, "  "); });
        await Reject(async () => { await store.UpdateEntryAsync(a, other.Id, new string('x', JournalStore.MaxTextLength + 1)); });
        check((await store.GetEntryAsync(a, other.Id))!.Reflection?.Overview == "Respuesta intacta", "Edición: valida vacío y longitud antes de cambiar texto o devolución");
        var withoutCaption = await store.UpdateEntryAsync(a, entry.Id, "", expectedText: editedText);
        check(withoutCaption.Text.Length == 0 && withoutCaption.Attachments.Count == 1 && withoutCaption.Reflection is null,
            "Edición: permite quitar el texto acompañante si conserva un adjunto");

        var indexing = await store.AddEntryAsync(a, "VIEJOANTESDEINDEXAR", []);
        client.Before = async () => { client.Before = null; await store.UpdateEntryAsync(a, indexing.Id, "NUEVODESPUESDEEDITAR", expectedText: indexing.Text); };
        try { await memory.IndexPendingAsync(a, client.Model, 100); throw new Exception("Publicó vectores obsoletos"); } catch (JournalInputException) { }
        await IndexAll(a);
        check(await Count("SELECT COUNT(*) FROM JournalVectorText WHERE JournalVectorText MATCH 'VIEJOANTESDEINDEXAR'") == 0
            && await Count("SELECT COUNT(*) FROM JournalVectorText WHERE JournalVectorText MATCH 'NUEVODESPUESDEEDITAR'") == 1,
            "Edición: una edición concurrente con embeddings impide guardar el texto anterior");
        var racing = await store.AddEntryAsync(a, "Mensaje antes de editar durante la generación.", []);
        await store.SaveReflectionAsync(a, racing.Id, Reply("Respuesta previa"));
        await IndexAll(a);
        var reflector = new Reflector(async () => { await store.UpdateEntryAsync(a, racing.Id, "Mensaje editado mientras llegaba la respuesta.", expectedText: racing.Text); });
        var flow = new JournalConversationService(store, new Audio(),
            new JournalMemoryService(factory, new NoGenerativeSearch(), new JournalMemoryJobs(), vectors: memory), reflector, new JournalConversationJobs());
        await Reject(async () => { await flow.GenerateAsync(a, racing.Id); });
        var afterRace = (await store.GetEntryAsync(a, racing.Id))!;
        check(afterRace.Text.Contains("editado mientras") && afterRace.Reflection is null,
            "Edición: una generación en curso no repone una devolución del texto anterior");

        using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        async Task<string> Waiting(bool offer) => await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<ReplyPending>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["OfferUpgrade"] = offer, ["Message"] = "Esperando una devolución de prueba…" }))).ToHtmlString());
        var freeHtml = await Waiting(true); var paidHtml = await Waiting(false);
        check(freeHtml.Contains("/Account/Manage/Subscription") && freeHtml.Contains("target=\"_blank\"") && freeHtml.Contains("noopener") && freeHtml.Contains("por lo general"),
            "Espera: presenta el plan gratuito y abre la alternativa paga sin abandonar la generación");
        check(!paidHtml.Contains("/Account/Manage/Subscription") && paidHtml.Contains("cuando est") && paidHtml.Contains("role=\"status\""),
            "Espera: una cuenta paga conserva el estado accesible sin publicidad de su propio plan");

        async Task IndexAll(string user) { while (!(await memory.IndexPendingAsync(user, client.Model, 100)).Complete) { } }
        async Task<long> Revision() => await Count("SELECT Revision FROM JournalVectorStates WHERE UserId=$user", new SqliteParameter("$user", a));
        async Task<long> Count(string sql, params SqliteParameter[] parameters)
        {
            await using var db = await factory.CreateDbContextAsync(); await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.Add(parameter);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
    }
    private static async Task Reject(Func<Task> action)
    { try { await action(); throw new Exception("Aceptó una edición inválida"); } catch (JournalInputException) { } }
    private static JournalReflection Reply(string text) => new("Prueba", text, [], [], [], null, false, 1, DateTime.UtcNow);
    private static float[] Unit() { var vector = new float[128]; vector[0] = 1; return vector; }
    private sealed class Embeddings : IJournalEmbeddingClient
    {
        public EmbeddingModel Model { get; } = new("entry-edit-v1", 128);
        public List<string> Documents { get; } = []; public Func<Task>? Before;
        public Task<EmbeddingModel> GetModelAsync(CancellationToken ct) => Task.FromResult(Model);
        public async Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingModel model, IReadOnlyList<string> inputs, bool interactive, CancellationToken ct)
        { if (Before is { } action) await action(); if (!interactive) Documents.AddRange(inputs); return inputs.Select(_ => Unit()).ToArray(); }
    }
    private sealed class Reflector(Func<Task> before) : IJournalReflector
    {
        public bool IsConfigured => true;
        public async Task<JournalReflection> ReflectAsync(JournalContext context, bool useAi, CancellationToken ct) { await before(); return Reply("Respuesta que no debe persistirse"); }
    }
    private sealed class Audio : IAudioTranscriber
    { public bool IsConfigured => true; public Task<string> TranscribeAsync(AttachmentContent audio, CancellationToken ct) => throw new Exception("No hay audio pendiente"); }
    private sealed class NoGenerativeSearch : IJournalMemoryAnalyzer
    {
        public bool UsesExternalProvider => false;
        public Task<IReadOnlyList<MemoryDigest>> IndexAsync(IReadOnlyList<TopicSource> sources, CancellationToken ct) => throw new Exception("No debe resumir");
        public Task<IReadOnlyList<string>> RecallAsync(string query, IReadOnlyList<MemoryCard> memories, int limit, CancellationToken ct) => throw new Exception("No debe buscar con un LLM");
    }
}
