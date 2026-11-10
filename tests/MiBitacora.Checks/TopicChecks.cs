using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiBitacora.Components.Shared;
using MiBitacora.Components.Pages;
using Microsoft.JSInterop;
using System.Security.Claims;
using MiBitacora.Data;
using MiBitacora.Services;

internal static class TopicChecks
{
    internal static async Task RunAsync(Action<bool, string> check, bool live)
    {
        using var gate = new LocalInferenceGate();
        var source = new TopicSource("s1", Guid.NewGuid(), DateTime.UtcNow, "Entrada", "Hoy descansé y leí un libro.");
        var citationId = "s1";
        using var http = new HttpClient(new TopicHandler(async (request, ct) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            check(request.RequestUri!.Host == "127.0.0.1" && body.GetProperty("messages")[1].GetProperty("images").GetArrayLength() == 0,
                "Temas: solo texto y servidor local");
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { done = true, done_reason = "stop", message = new { content = JsonSerializer.Serialize(new {
                topics = new[] { new { topicId = "nuevo", newTitle = "Descanso", observation = "Registraste un momento de descanso.",
                    evidence = new[] { new { sourceId = citationId, quote = "Cita inventada" } } } }
            }) } }) };
        }));
        var ollama = new OllamaJournalService(http, Options.Create(new OllamaOptions()), gate, NullLogger<OllamaJournalService>.Instance);
        var extracted = await ollama.DetectAsync([source], new Dictionary<string, string>(), default);
        check(extracted.Single().Evidence.Single().Quote == source.Text, "Temas: se sustituye una cita inventada por un fragmento real");
        citationId = "fuente-ajena";
        try { await ollama.DetectAsync([source], new Dictionary<string, string>(), default); throw new Exception("Se aceptó una fuente inexistente"); }
        catch (JournalReflectionException) { check(true, "Temas: se rechazan fuentes fuera de la solicitud"); }

        var directory = Path.Combine(Path.GetTempPath(), "mibitacora-topics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { await StorageAsync(directory, check, live); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task StorageAsync(string directory, Action<bool, string> check, bool live)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(directory, "topics.db")};Pooling=False;Foreign Keys=True"));
        services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton(new UploadFileStore(Path.Combine(directory, "uploads"), NullLogger<UploadFileStore>.Instance));
        services.AddScoped<IJournalStore, JournalStore>();
        services.AddSingleton<JournalTopicJobs>();
        services.AddSingleton(TimeZoneInfo.Utc);
        var analyzer = new SyntheticAnalyzer();
        services.AddSingleton<IJournalTopicAnalyzer>(analyzer);
        services.AddScoped<JournalTopicService>();
        var memoryAnalyzer = new FakeJournalMemory();
        services.AddSingleton<IJournalMemoryAnalyzer>(memoryAnalyzer);
        services.AddSingleton<IJournalReportAnalyzer>(memoryAnalyzer);
        services.AddSingleton<JournalMemoryJobs>();
        services.AddScoped<JournalMemoryService>();
        services.AddSingleton<IAudioTranscriber, RejectAudioTranscriber>();
        services.AddSingleton<JournalConversationJobs>();
        services.AddScoped<JournalConversationService>();
        services.AddScoped<JournalReportService>();
        var account = new TestAccount();
        var components = new CapturedComponents();
        services.AddSingleton<AuthenticationStateProvider>(account);
        services.AddSingleton<IComponentActivator>(components);
        services.AddSingleton<IJournalReflector, UnusedReflector>();
        services.AddSingleton<IJSRuntime, UnusedJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var store = scope.ServiceProvider.GetRequiredService<IJournalStore>();
        var topics = scope.ServiceProvider.GetRequiredService<JournalTopicService>();
        var a = Guid.NewGuid().ToString(); var b = Guid.NewGuid().ToString();
        account.UserId = a;
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            db.Users.AddRange(new ApplicationUser { Id = a, UserName = "synthetic-a" }, new ApplicationUser { Id = b, UserName = "synthetic-b" });
            await db.SaveChangesAsync();
        }
        var original = await store.AddEntryAsync(a, "Hoy descansé y leí un libro.", [new("apuntes.txt", Encoding.UTF8.GetBytes("Documento de ejemplo: probar otro horario para leer."))]);
        var longText = string.Concat(Enumerable.Repeat("Texto sintético con matices y lectura. ", 170));
        var longEntry = await store.AddEntryAsync(a, longText, []);
        for (var i = 0; i < 42; i++) await store.AddEntryAsync(a, $"Día {i}: un rato de lectura.", []);
        var foreign = await store.AddEntryAsync(b, "SECRETO_DE_OTRA_CUENTA", []);
        var note = await store.SaveNoteAsync(a, null, "Siempre me pasa lo mismo, estoy cansado. <script>Notita literal</script>");
        await store.SaveProfileAsync(a, new("Prueba", "Estoy atravesando un cambio laboral y quiero entender mis emociones.", false));
        var foreignNote = await store.SaveNoteAsync(b, null, "NOTITA_PRIVADA_DE_B");
        await store.SaveProfileAsync(b, new("Otra cuenta", "PERFIL_PRIVADO_DE_B", false));
        var progress = new List<string>();
        var snapshot = await topics.GenerateAsync(a, new ProgressSink(progress.Add));
        check(snapshot.EntryCount == 44 && snapshot.TextEntryCount == 44 && snapshot.DocumentCount == 1,
            "Temas: se recorren todas las entradas, más allá de la primera página, y los documentos");
        check(string.Concat(analyzer.Seen.Where(s => s.EntryId == longEntry.Id).Select(s => s.Text)) == longEntry.Text,
            "Temas: una entrada larga se divide sin perder texto");
        check(analyzer.Seen.All(s => s.EntryId != foreign.Id) && (await topics.GetAsync(b)).Snapshot is null,
            "Temas: aislamiento de fuentes y devoluciones por cuenta");
        check(snapshot.Topics.Count == 1 && snapshot.Topics[0].Evidence.Where(e => e.EntryId.HasValue).Select(e => e.EntryId).Distinct().Count() == 44 && progress.Count > 1,
            "Temas: se reúnen las fuentes del mismo tema y se informa el avance");
        check(snapshot.NoteCount == 1 && snapshot.HasPersonalContext && snapshot.AdditionalContext?.Count == 2 &&
            analyzer.Seen.Any(s => s.Kind == TopicSourceKind.Note && s.NoteId == note.Id && s.EntryId is null) &&
            analyzer.Seen.Any(s => s.Kind == TopicSourceKind.Profile && s.EntryId is null && s.CreatedUtc is null),
            "Temas: notitas y perfil tienen identidad propia, sin entradas ni fechas inventadas");
        check(analyzer.DetectedContexts.All(c => c.Contains("estoy cansado") && c.Contains("cambio laboral")) &&
            analyzer.FinalContexts.All(c => c.Contains("estoy cansado") && c.Contains("cambio laboral")),
            "Temas: el contexto transversal llega a cada lote y a cada devolución final");
        check(!string.Join(" ", analyzer.Seen.Select(s => s.Text).Concat(analyzer.DetectedContexts)).Contains("PRIVAD"),
            "Temas: no se incluyen notitas ni perfil de otra cuenta");
        check(!(await topics.GetAsync(a)).IsOutdated, "Temas: la devolución recién guardada está vigente");
        check(analyzer.SummaryCalls > 0, "Temas: se sintetiza toda la evidencia extensa antes de escribir la devolución final");

        await using (var pageRenderer = new PageRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>()))
        {
            await pageRenderer.Dispatcher.InvokeAsync(async () =>
            {
                var page = pageRenderer.BeginRenderingComponent(typeof(Bitacora), ParameterView.Empty);
                await page.QuiescenceTask;
            });
            check(components.Topics?.UserId == a,
                "Temas: la página entrega al componente el identificador real de la cuenta autenticada");
        }
        await using (var renderer = new PageRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>()))
        {
            Task<string> RenderAsync<T>(ParameterView parameters) where T : IComponent => renderer.Dispatcher.InvokeAsync(async () =>
            {
                var page = renderer.BeginRenderingComponent(typeof(T), parameters);
                await page.QuiescenceTask;
                return page.ToHtmlString();
            });
            var html = await RenderAsync<JournalTopics>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["UserId"] = a, ["Active"] = true }));
            check(html.Contains("topic-card") && html.Contains("Ver fragmentos") && html.Contains("Actualizar") && !html.Contains("<script>"),
                "Temas: se renderizan tarjetas guardadas, preguntas y evidencia con HTML escapado");
            check(html.Contains("Ver contexto adicional considerado") && html.Contains("44 entradas relacionadas") &&
                !html.Contains("45 entradas relacionadas") && !html.Contains("1/1/0001") && !html.Contains("01/01/0001") && !html.Contains("0001-01-01"),
                "Temas: el contexto no infla el contador de entradas ni muestra fechas ficticias");
            check(html.Contains($"/entrada/{original.Id}?tema=t1") && html.Contains("target=\"_blank\"") &&
                !html.Contains($"/entrada/{note.Id}"),
                "Temas: los fragmentos de entradas abren el original, las notitas no inventan enlaces");
            var entryHtml = await RenderAsync<Entrada>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["EntryId"] = longEntry.Id }));
            check(entryHtml.Contains("TU ENTRADA ORIGINAL") && entryHtml.Contains("seccion=temas") && WebUtility.HtmlDecode(entryHtml).Contains(longEntry.Text),
                "Entrada completa: renderiza todo el texto y permite volver al tema sin generar IA");
            var hiddenHtml = await RenderAsync<Entrada>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["EntryId"] = foreign.Id }));
            check(hiddenHtml.Contains("Entrada no disponible") && !hiddenHtml.Contains("SECRETO_DE_OTRA_CUENTA"),
                "Entrada completa: la página no revela contenido de otra cuenta");
        }

        var separated = new SeparatedAnalyzer();
        var separatedSnapshot = await new JournalTopicService(factory, separated, new JournalTopicJobs()).GenerateAsync(a);
        check(separated.FinalContexts["Entradas"] == "" && separated.FinalContexts["Contexto"].Contains("estoy cansado"),
            "Temas: una notita sólo llega a la lectura que la vinculó como fuente, no a todas");
        // Restore the reference snapshot used by concurrency checks below.
        snapshot = await topics.GenerateAsync(a);

        await store.SaveNoteAsync(a, note.Id, "Cambió mi emoción de fondo.");
        check((await topics.GetAsync(a)).IsOutdated, "Temas: editar una notita invalida la huella");
        await store.SaveNoteAsync(a, note.Id, note.Text);
        await store.SetNoteForSessionAsync(a, note.Id, true);
        check(!(await topics.GetAsync(a)).IsOutdated, "Temas: marcar para sesión no cambia el contexto del análisis");
        await store.SaveProfileAsync(a, new("Prueba", "Un contexto diferente", false));
        check((await topics.GetAsync(a)).IsOutdated, "Temas: editar el perfil invalida la huella");
        await store.SaveProfileAsync(a, new("Prueba", "Estoy atravesando un cambio laboral y quiero entender mis emociones.", false));
        await store.DeleteNoteAsync(a, foreignNote.Id);
        check((await topics.GetAsync(a)).Snapshot is not null && (await store.GetNotesAsync(b)).Any(n => n.Id == foreignNote.Id),
            "Temas: eliminar una notita ajena no modifica fuentes ni devoluciones");
        var changedContext = false;
        analyzer.OnDetect = async _ => { if (!changedContext) { changedContext = true; await store.SaveNoteAsync(a, note.Id, "Contexto editado durante el análisis"); } };
        try { await topics.GenerateAsync(a); throw new Exception("Se aceptó contexto obsoleto"); }
        catch (JournalInputException ex) { check(ex.Message.Contains("cambió"), "Temas: editar contexto durante la generación impide publicar el resultado obsoleto"); }
        analyzer.OnDetect = null;
        await store.SaveNoteAsync(a, note.Id, note.Text);
        check((await topics.GetAsync(a)).Snapshot!.GeneratedUtc == snapshot.GeneratedUtc,
            "Temas: el cambio concurrente conserva la última versión completa");

        analyzer.Fail = true;
        try { await topics.GenerateAsync(a); throw new Exception("No falló el analizador"); } catch (JournalReflectionException) { }
        check((await topics.GetAsync(a)).Snapshot!.GeneratedUtc == snapshot.GeneratedUtc,
            "Temas: un fallo conserva íntegra la devolución anterior");
        analyzer.Fail = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        analyzer.OnDetect = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        using (var cancel = new CancellationTokenSource())
        {
            var pending = topics.GenerateAsync(a, ct: cancel.Token);
            await entered.Task;
            try { await topics.GenerateAsync(a); throw new Exception("Se admitieron dos trabajos por cuenta"); }
            catch (JournalInputException) { check(true, "Temas: se impide duplicar la generación de una cuenta"); }
            cancel.Cancel();
            try { await pending; throw new Exception("No se canceló"); } catch (OperationCanceledException) { }
        }
        check((await topics.GetAsync(a)).Snapshot!.GeneratedUtc == snapshot.GeneratedUtc, "Temas: cancelar conserva la versión guardada");
        analyzer.OnDetect = null;
        await store.AddEntryAsync(a, "Otra entrada nueva.", []);
        check((await topics.GetAsync(a)).IsOutdated, "Temas: las entradas nuevas marcan la devolución como desactualizada");
        var changed = false;
        analyzer.OnDetect = async _ => { if (!changed) { changed = true; await store.DeleteEntryAsync(a, original.Id); } };
        try { await topics.GenerateAsync(a); throw new Exception("Se guardó una devolución con fuentes eliminadas"); }
        catch (JournalInputException ex) { check(ex.Message.Contains("cambió"), "Temas: un cambio concurrente impide guardar una devolución obsoleta"); }
        check((await topics.GetAsync(a)).Snapshot is null, "Temas: eliminar una entrada retira también la síntesis que podía citarla");
        analyzer.OnDetect = null;
        await topics.GenerateAsync(b);
        await store.DeleteEntryAsync(a, foreign.Id);
        check((await topics.GetAsync(b)).Snapshot is not null, "Temas: intentar eliminar una entrada ajena no afecta su devolución");
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Users.Where(u => u.Id == b).ExecuteDeleteAsync();
            check((await topics.GetAsync(b)).Snapshot is null, "Temas: eliminar una cuenta elimina sus devoluciones");
        }

        var contextOnly = Guid.NewGuid().ToString();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new ApplicationUser { Id = contextOnly, UserName = "synthetic-context" });
            await db.SaveChangesAsync();
        }
        var longNotes = new List<JournalNote>();
        for (var i = 0; i < 9; i++)
            longNotes.Add(await store.SaveNoteAsync(contextOnly, null, $"NOTITA_{i}_COMPLETA " + new string('x', 1700)));
        var profileText = "PERFIL_COMPLETO " + new string('z', 2900);
        await store.SaveProfileAsync(contextOnly, new("Contexto", profileText, false));
        analyzer.Seen.Clear(); analyzer.DetectedContexts.Clear(); analyzer.FinalContexts.Clear(); analyzer.SummaryInputs.Clear();
        var contextSnapshot = await topics.GenerateAsync(contextOnly);
        check(contextSnapshot.EntryCount == 0 && contextSnapshot.NoteCount == 9 && contextSnapshot.Topics.Count > 0 &&
            contextSnapshot.Topics.All(t => t.Evidence.All(e => e.EntryId is null)),
            "Temas: se puede generar solo con notitas y perfil sin crear entradas ficticias");
        check(longNotes.All(n => string.Concat(analyzer.Seen.Where(s => s.NoteId == n.Id).Select(s => s.Text)) == n.Text) &&
            string.Concat(analyzer.Seen.Where(s => s.Kind == TopicSourceKind.Profile).Select(s => s.Text)) == profileText,
            "Temas: todas las notitas y el perfil largo se procesan completos");
        check(longNotes.All(n => analyzer.SummaryInputs.Any(s => s.Contains(n.Text))) &&
            analyzer.DetectedContexts.All(c => c.Length is > 0 and <= 2400) && analyzer.FinalContexts.All(c => c.Length is > 0 and <= 2400),
            "Temas: el contexto extenso se sintetiza completo antes de compartirse con todos los temas");
        await store.DeleteNoteAsync(contextOnly, longNotes[0].Id);
        check((await topics.GetAsync(contextOnly)).Snapshot is null, "Temas: borrar una notita retira la devolución que podía conservar su contenido");
        await topics.GenerateAsync(contextOnly);
        await store.SaveProfileAsync(contextOnly, new("Contexto", "", false));
        check((await topics.GetAsync(contextOnly)).Snapshot is null, "Temas: vaciar el perfil retira el contexto guardado en la devolución");
        var oldSnapshot = JsonSerializer.Deserialize<JournalTopicSnapshot>("""
            {"generatedUtc":"2026-11-01T00:00:00Z","entryCount":1,"textEntryCount":1,"documentCount":0,"unreadAttachmentCount":0,
             "topics":[{"id":"t1","title":"Anterior","overview":"Texto anterior","questions":[],"evidence":[
             {"entryId":"00000000-0000-0000-0000-000000000001","createdUtc":"2026-11-01T00:00:00Z","label":"Entrada","excerpt":"Original"}]}]}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        check(oldSnapshot.NoteCount == 0 && oldSnapshot.AdditionalContext is null && oldSnapshot.Topics[0].Evidence[0].EntryId.HasValue,
            "Temas: las devoluciones anteriores siguen siendo legibles sin migrar la base");

        if (live)
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            using var gate = new LocalInferenceGate();
            var model = new OllamaJournalService(client, Options.Create(new OllamaOptions()), gate, NullLogger<OllamaJournalService>.Instance);
            var samples = new[] {
                new TopicSource("s1", Guid.NewGuid(), DateTime.UtcNow.AddDays(-2), "Entrada", "Terminé de trabajar muy tarde y dormí poco. Quiero encontrar un horario de descanso."),
                new TopicSource("s2", Guid.NewGuid(), DateTime.UtcNow.AddDays(-1), "Entrada", "Apagué la computadora temprano y pude leer tranquilo antes de dormir."),
                new TopicSource("s3", Guid.NewGuid(), DateTime.UtcNow, "Documento: apuntes.txt", "Ideas para un club de lectura: elegir juntos un libro y conversar al terminarlo.") };
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var detected = await model.DetectAsync(samples, new Dictionary<string, string>(), default);
            check(detected.Count > 0 && detected.All(t => t.Evidence.Count > 0), "Modelo real: detecta temas y fuentes en textos sintéticos");
            var first = detected[0];
            var reading = await model.ReadAsync(first.NewTitle, first.Observation, default);
            check(reading.Overview.Length > 0 && reading.Questions.Count > 0, $"Modelo real: produce una devolución por tema ({timer.Elapsed.TotalSeconds:F1} s)");
            var repeated = await model.DetectAsync(samples, new Dictionary<string, string> { ["t1"] = first.NewTitle }, default);
            check(repeated.Any(t => t.TopicId == "t1"), "Modelo real: reutiliza un tema existente al encontrar más evidencia");
            var summary = await model.ReadAsync(first.NewTitle, first.Observation, default, summarizeOnly: true);
            check(summary.Overview.Length > 0 && summary.Questions.Count == 0, "Modelo real: la síntesis intermedia conserva el formato de resumen");
        }
    }

    private sealed class SyntheticAnalyzer : IJournalTopicAnalyzer
    {
        public bool IsConfigured => true;
        public bool Fail;
        public int SummaryCalls;
        public Func<CancellationToken, Task>? OnDetect;
        public List<TopicSource> Seen { get; } = [];
        public List<string> DetectedContexts { get; } = [];
        public List<string> FinalContexts { get; } = [];
        public List<string> SummaryInputs { get; } = [];
        public async Task<IReadOnlyList<TopicObservation>> DetectAsync(IReadOnlyList<TopicSource> sources, IReadOnlyDictionary<string, string> catalog, CancellationToken ct, string additionalContext = "")
        {
            ct.ThrowIfCancellationRequested();
            if (Fail) throw new JournalReflectionException("Fallo simulado");
            if (OnDetect is not null) await OnDetect(ct);
            Seen.AddRange(sources);
            DetectedContexts.Add(additionalContext);
            return [new(catalog.Count == 0 ? "nuevo" : catalog.Keys.First(), catalog.Count == 0 ? "Lectura" : "", "Aparecen momentos de lectura. " + new string('a', 550),
                sources.Select(s => new TopicCitation(s.Id, s.Text[..Math.Min(100, s.Text.Length)])).ToArray())];
        }
        public Task<TopicReading> ReadAsync(string title, string observations, CancellationToken ct, bool summarizeOnly = false, string additionalContext = "")
        {
            if (summarizeOnly) { SummaryCalls++; SummaryInputs.Add(observations); }
            else FinalContexts.Add(additionalContext);
            return Task.FromResult(new TopicReading("La lectura aparece en distintos momentos. <script>Texto literal</script>", summarizeOnly ? [] : ["¿Qué te gustaría retomar?"]));
        }
    }
    private sealed class ProgressSink(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
    private sealed class SeparatedAnalyzer : IJournalTopicAnalyzer
    {
        public bool IsConfigured => true;
        public Dictionary<string, string> FinalContexts { get; } = [];
        public Task<IReadOnlyList<TopicObservation>> DetectAsync(IReadOnlyList<TopicSource> sources,
            IReadOnlyDictionary<string, string> catalog, CancellationToken ct, string additionalContext = "") =>
            Task.FromResult<IReadOnlyList<TopicObservation>>(sources.GroupBy(s => s.EntryId.HasValue ? "Entradas" : "Contexto")
                .Select(g => new TopicObservation(catalog.FirstOrDefault(p => p.Value == g.Key).Key ?? "nuevo", g.Key,
                    "Observación de estas fuentes.", g.Select(s => new TopicCitation(s.Id, s.Text[..Math.Min(60, s.Text.Length)])).ToArray())).ToArray());
        public Task<TopicReading> ReadAsync(string title, string observations, CancellationToken ct, bool summarizeOnly = false, string additionalContext = "")
        {
            if (!summarizeOnly) FinalContexts[title] = additionalContext;
            return Task.FromResult(new TopicReading("Síntesis breve.", []));
        }
    }
    // Render the actual page's Razor tree, including its interactive child parameters, without a browser circuit.
    private sealed class PageRenderer(IServiceProvider services, ILoggerFactory logger) : StaticHtmlRenderer(services, logger)
    {
        protected override IComponent ResolveComponentForRenderMode(Type componentType, int? parentComponentId,
            IComponentActivator componentActivator, IComponentRenderMode renderMode) =>
            renderMode is InteractiveServerRenderMode ? componentActivator.CreateInstance(componentType)
                : throw new NotSupportedException("Este renderer de prueba solo admite InteractiveServer.");
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/bitacora");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
    }
    private sealed class TestAccount : AuthenticationStateProvider
    {
        public string UserId { get; set; } = "";
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "Test"))));
    }
    private sealed class CapturedComponents : IComponentActivator
    {
        public JournalTopics? Topics { get; private set; }
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is JournalTopics topics) Topics = topics;
            return component;
        }
    }
    private sealed class UnusedReflector : IJournalReflector
    {
        public bool IsConfigured => true;
        public Task<JournalReflection> ReflectAsync(JournalContext context, bool useAi, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Abrir el cuaderno no debe generar una devolución individual.");
    }
    private sealed class UnusedJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
    }
    private sealed class TopicHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
