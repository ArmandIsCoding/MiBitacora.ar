using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiBitacora.Components.Shared;
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
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var store = scope.ServiceProvider.GetRequiredService<IJournalStore>();
        var topics = scope.ServiceProvider.GetRequiredService<JournalTopicService>();
        var a = Guid.NewGuid().ToString(); var b = Guid.NewGuid().ToString();
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
        var progress = new List<string>();
        var snapshot = await topics.GenerateAsync(a, new ProgressSink(progress.Add));
        check(snapshot.EntryCount == 44 && snapshot.TextEntryCount == 44 && snapshot.DocumentCount == 1,
            "Temas: se recorren todas las entradas, más allá de la primera página, y los documentos");
        check(string.Concat(analyzer.Seen.Where(s => s.EntryId == longEntry.Id).Select(s => s.Text)) == longEntry.Text,
            "Temas: una entrada larga se divide sin perder texto");
        check(analyzer.Seen.All(s => s.EntryId != foreign.Id) && (await topics.GetAsync(b)).Snapshot is null,
            "Temas: aislamiento de fuentes y devoluciones por cuenta");
        check(snapshot.Topics.Count == 1 && snapshot.Topics[0].Evidence.Select(e => e.EntryId).Distinct().Count() == 44 && progress.Count > 1,
            "Temas: se reúnen las fuentes del mismo tema y se informa el avance");
        check(!(await topics.GetAsync(a)).IsOutdated, "Temas: la devolución recién guardada está vigente");
        check(analyzer.SummaryCalls > 0, "Temas: se sintetiza toda la evidencia extensa antes de escribir la devolución final");

        await using (var renderer = new HtmlRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>()))
        {
            var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<JournalTopics>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["UserId"] = a, ["Active"] = true }))).ToHtmlString());
            check(html.Contains("topic-card") && html.Contains("Ver fragmentos") && html.Contains("Actualizar") && !html.Contains("<script>"),
                "Temas: se renderizan tarjetas guardadas, preguntas y evidencia con HTML escapado");
        }

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
        public async Task<IReadOnlyList<TopicObservation>> DetectAsync(IReadOnlyList<TopicSource> sources, IReadOnlyDictionary<string, string> catalog, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Fail) throw new JournalReflectionException("Fallo simulado");
            if (OnDetect is not null) await OnDetect(ct);
            Seen.AddRange(sources);
            return [new(catalog.Count == 0 ? "nuevo" : catalog.Keys.First(), catalog.Count == 0 ? "Lectura" : "", "Aparecen momentos de lectura. " + new string('a', 550),
                sources.Select(s => new TopicCitation(s.Id, s.Text[..Math.Min(100, s.Text.Length)])).ToArray())];
        }
        public Task<TopicReading> ReadAsync(string title, string observations, CancellationToken ct, bool summarizeOnly = false)
        {
            if (summarizeOnly) SummaryCalls++;
            return Task.FromResult(new TopicReading("La lectura aparece en distintos momentos. <script>Texto literal</script>", summarizeOnly ? [] : ["¿Qué te gustaría retomar?"]));
        }
    }
    private sealed class ProgressSink(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
    private sealed class TopicHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
