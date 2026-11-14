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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MiBitacora.Components.Shared;
using MiBitacora.Data;
using MiBitacora.Services;

internal static class ThreadChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "mibitacora-threads-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await VerifyAsync(root, check); }
        finally { Directory.Delete(root, true); }
    }

    private static async Task VerifyAsync(string folder, Action<bool, string> check)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(TimeZoneInfo.Utc);
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"Data Source={folder}/test.db;Pooling=False;Foreign Keys=True"));
        services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton(new UploadFileStore(Path.Combine(folder, "uploads"), NullLogger<UploadFileStore>.Instance));
        services.AddScoped<IJournalStore, JournalStore>();
        services.AddScoped<AccountDeletionService>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var store = scope.ServiceProvider.GetRequiredService<IJournalStore>();
        var a = Guid.NewGuid().ToString(); var b = Guid.NewGuid().ToString();
        var legacyId = Guid.NewGuid();
        var legacyDate = DateTime.UtcNow.AddMonths(-3);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261113045613_AddPremiumAccessAndProvider");
            db.Users.AddRange(new ApplicationUser { Id = a, UserName = "thread-a" }, new ApplicationUser { Id = b, UserName = "thread-b" });
            await db.SaveChangesAsync();
            var json = JsonSerializer.Serialize(Reply("¿Qué querés hacer con tu MacBook?"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO JournalEntries (Id, UserId, Text, SearchText, CreatedUtc, ForSession, IsDateOnly, ReflectionJson)
                VALUES ({legacyId}, {a}, 'Quiero aprovechar mi MacBook.', 'macbook', {legacyDate}, 1, 1, {json})
                """);
            await db.Database.MigrateAsync();
        }
        var legacy = (await store.GetEntryAsync(a, legacyId))!;
        check(legacy.ReplyToEntryId is null && legacy.ReplyToQuestion is null && legacy.CreatedUtc == legacyDate
            && legacy.ForSession && legacy.IsDateOnly && legacy.Reflection!.OpenQuestions.Count == 1,
            "Hilos: la migración aditiva conserva originales y devoluciones anteriores sin crear respuestas ficticias");
        var foreign = await store.AddEntryAsync(b, "SECRETO_AJENO", []);
        var otherNotebook = await store.SaveNotebookAsync(a, null, "Otro diario");
        await Reject(() => store.AddEntryAsync(a, "No permitido", [], replyToEntryId: foreign.Id), "rechaza responder a otra cuenta");
        await Reject(() => store.GetThreadAsync(b, legacy.Id), "no permite leer un hilo ajeno");
        await Reject(() => store.AddEntryAsync(a, "No permitido", [], notebookId: otherNotebook.Id, replyToEntryId: legacy.Id), "evita mezclar diarios");
        await Reject(() => store.AddEntryAsync(a, "No permitido", [], replyToEntryId: Guid.NewGuid()), "rechaza preguntas eliminadas o inexistentes");

        var unrelated = await store.AddEntryAsync(a, "OTRO_HILO: estoy pensando en mudarme.", []);
        await store.SaveReflectionAsync(a, unrelated.Id, Reply("¿Qué barrio te gusta?"));
        var first = await store.AddEntryAsync(a, "Quiero aprender a dibujar.", [new("ideas.txt", Encoding.UTF8.GetBytes("Lista de ideas para dibujar."))], replyToEntryId: legacy.Id);
        first = (await store.GetEntryAsync(a, first.Id))!;
        check(first.ReplyToEntryId == legacy.Id && first.ReplyToQuestion == legacy.Reflection!.OpenQuestions.Single()
            && first.Attachments.Count == 1, "Hilos: guarda el vínculo, la pregunta elegida y los adjuntos al recargar la entrada");
        await store.SaveReflectionAsync(a, first.Id, Reply("¿Qué te gustaría dibujar primero?"));
        var otherReply = await store.AddEntryAsync(a, "OTRA_RESPUESTA: Palermo.", [], replyToEntryId: unrelated.Id);
        await store.SaveReflectionAsync(a, otherReply.Id, Reply("¿Querés conocer el barrio?"));
        var branch = await store.AddEntryAsync(a, "RAMA_PARALELA: también podría componer música.", [], replyToEntryId: legacy.Id);
        var second = await store.AddEntryAsync(a, "Sí, eso.", [], replyToEntryId: first.Id);
        var context = await store.GetContextAsync(a, second.Id, false);
        check(context.History.Select(entry => entry.Id).SequenceEqual(new[] { legacy.Id, first.Id }),
            "Hilos: una respuesta breve retoma sólo sus ancestros y excluye preguntas recientes de otros hilos y ramas");
        var ordinary = await store.AddEntryAsync(a, "Una entrada fuera de los hilos.", []);
        check((await store.GetContextAsync(a, ordinary.Id, false)).History.All(entry => entry.ReplyToEntryId is null),
            "Hilos: escribir fuera de un hilo no continúa accidentalmente una de sus respuestas");
        await store.SaveReflectionAsync(a, first.Id, Reply("¿Querés cambiar de actividad?"));
        await store.SaveReflectionAsync(a, legacy.Id, Reply("¿Preferís vender la MacBook?"));
        context = await store.GetContextAsync(a, second.Id, false);
        check(context.Current.ReplyToQuestion == "¿Qué te gustaría dibujar primero?" &&
            context.History.Last().Reflection!.OpenQuestions.Single() == context.Current.ReplyToQuestion &&
            context.History.First().Reflection!.OpenQuestions.Single() == first.ReplyToQuestion,
            "Hilos: regenerar devoluciones conserva las preguntas que efectivamente se respondieron a lo largo de la rama");
        foreach (var external in new[] { false, true })
        {
            var model = new Model(external);
            await new ReflectionService(model).ReflectAsync(context, true);
            var prompt = JsonDocument.Parse(model.Prompt[model.Prompt.IndexOf('{')..]).RootElement;
            check(prompt.GetProperty("preguntaQueResponde").GetString() == context.Current.ReplyToQuestion
                && !model.Prompt.Contains("OTRO_HILO") && !model.Prompt.Contains("RAMA_PARALELA")
                && prompt.GetProperty("fuentes").EnumerateArray().Any(source => source.GetProperty("devolucionAnterior").GetString()?.Contains("dibujar primero") == true),
                $"Hilos: {(external ? "OpenAI" : "Ollama")} recibe el hilo elegido y su pregunta explícita");
        }
        var threads = await store.GetThreadsAsync(a, null);
        check(threads.Count == 2 && threads.Single(thread => thread.EntryId == legacy.Id).ReplyCount == 3 &&
            threads.All(thread => thread.EntryId != foreign.Id), "Hilos: lista conversaciones independientes con sus propios conteos y sin datos ajenos");
        var threadPage = await store.GetThreadAsync(a, second.Id);
        check(threadPage.Root.Id == legacy.Id && threadPage.Entries.Select(entry => entry.Id).ToHashSet().SetEquals([legacy.Id, first.Id, second.Id, branch.Id]),
            "Hilos: abrir cualquier respuesta recupera la conversación completa y sus ramas");

        await using (var db = await factory.CreateDbContextAsync())
        {
            await RejectSql($"UPDATE JournalEntries SET ReplyToEntryId={foreign.Id} WHERE Id={second.Id}", "la base impide vincular cuentas distintas");
            await RejectSql($"UPDATE JournalEntries SET ReplyToEntryId={second.Id} WHERE Id={legacy.Id}", "la base impide ciclos y padres futuros");
            async Task RejectSql(FormattableString sql, string label)
            {
                try { await db.Database.ExecuteSqlInterpolatedAsync(sql); throw new Exception("Se permitió una relación inválida."); }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { check(true, "Hilos: " + label); }
            }
        }
        var prior = second;
        for (var i = 0; i < 33; i++) prior = await store.AddEntryAsync(a, $"Continuación {i}", [], replyToEntryId: prior.Id);
        var latest = await store.GetThreadAsync(a, legacy.Id, 30);
        var older = await store.GetThreadAsync(a, legacy.Id, 30, latest.Entries[0].CreatedUtc);
        var pages = older.Entries.Concat(latest.Entries).ToArray();
        check(latest.HasEarlier && !older.HasEarlier && pages.Length == 37 && pages.Select(entry => entry.Id).Distinct().Count() == 37
            && pages.SequenceEqual(pages.OrderBy(entry => entry.CreatedUtc)), "Hilos: pagina una conversación larga sin saltos, duplicados ni mensajes de otro hilo");
        check((await store.GetContextAsync(a, prior.Id, false)).History.Count == 8,
            "Hilos: limita los ancestros del contexto sin recorrer todo el historial de la cuenta");
        await store.MoveEntryAsync(a, second.Id, otherNotebook.Id);
        var moved = await store.GetThreadAsync(a, legacy.Id, int.MaxValue);
        check(moved.Entries.Count == 37 && moved.Entries.All(entry => entry.NotebookId == otherNotebook.Id)
            && (await store.GetThreadsAsync(a, null)).Single().EntryId == unrelated.Id,
            "Hilos: mover cualquier mensaje mueve el hilo entero sin separarlo entre diarios");
        var file = await store.GetAttachmentAsync(a, first.Attachments.Single().Id);
        check(file is not null && moved.Entries.Single(entry => entry.Id == first.Id).Reflection is not null,
            "Hilos: mover conserva devoluciones y archivos originales");
        if (file is not null) await file.Content.DisposeAsync();
        await store.DeleteEntryAsync(a, first.Id);
        second = (await store.GetEntryAsync(a, second.Id))!;
        check(second.ReplyToEntryId is null && second.ReplyToQuestion is null && (await store.GetEntryAsync(a, prior.Id)) is not null
            && (await store.GetThreadAsync(a, prior.Id)).Root.Id == second.Id,
            "Hilos: eliminar un mensaje conserva las respuestas posteriores como otro hilo y retira su pregunta citada");
        await store.DeleteEntryAsync(a, legacy.Id);
        check((await store.GetEntryAsync(a, branch.Id))!.ReplyToEntryId is null && (await store.GetEntryAsync(a, otherReply.Id)) is not null,
            "Hilos: borrar el inicio conserva las respuestas y los otros hilos");
        await store.DeleteNotebookAsync(a, otherNotebook.Id!.Value);
        check((await store.GetThreadAsync(a, prior.Id)).Entries.All(entry => entry.NotebookId is null),
            "Hilos: quitar un diario conserva los vínculos de sus conversaciones en Mi diario");
        var exported = JsonSerializer.Serialize(await store.GetEntriesAsync(a, int.MaxValue));
        check(exported.Contains("ReplyToEntryId") && exported.Contains("ReplyToQuestion"), "Hilos: la exportación conserva los vínculos y preguntas de las respuestas");

        await using (var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>()))
        {
            var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<ReflectionCard>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Reflection"] = Reply("¿Qué querés crear?"), ["Respond"] = EventCallback.Factory.Create(new object(), () => { })
                }))).ToHtmlString());
            check(html.Contains("Responder</button>") && html.Contains("conversation-question"),
                "Hilos: la pregunta ofrece un control accesible para responder");
        }
        var user = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(a))!;
        check((await scope.ServiceProvider.GetRequiredService<AccountDeletionService>().DeleteAsync(user)).Succeeded
            && (await store.GetEntriesAsync(a)).Count == 0 && (await store.GetEntryAsync(b, foreign.Id)) is not null,
            "Hilos: borrar una cuenta con conversaciones enlazadas conserva intactas las demás cuentas");

        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); throw new Exception("Se permitió una operación inválida."); }
            catch (JournalInputException) { check(true, "Hilos: " + label); }
        }
    }

    private static JournalReflection Reply(string question) => new("Una idea", "Podés explorar eso que te entusiasma.", [], [question], [], null, false, 1, DateTime.UtcNow);
    private sealed class ReflectionService(IJournalModelClient model) : JournalAiService(model, NullLogger.Instance), IJournalReflector;
    private sealed class Model(bool external) : IJournalModelClient
    {
        public bool IsConfigured => true;
        public string ProviderName => "Prueba";
        public bool UsesExternalProvider => external;
        public string Prompt { get; private set; } = "";
        public Task<string> GenerateAsync(string instruction, string prompt, JsonElement schema, IReadOnlyList<JournalImage> images, CancellationToken cancellationToken)
        {
            Prompt = prompt;
            return Task.FromResult("""{"title":"Dibujar","message":"Podés darle un lugar a ese interés.","themes":[],"openQuestions":["¿Qué querés probar?"],"sessionTopics":[],"nextStep":""}""");
        }
    }
}
