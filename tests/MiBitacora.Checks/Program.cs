using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiBitacora.Data;
using MiBitacora.Services;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var image = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "red-square.png"));
var entryId = Guid.NewGuid();
var attachmentId = Guid.NewGuid();
var attachment = new JournalAttachment(attachmentId, entryId, "cuadrado.png", "image/png", image.Length, "", null);
var entry = new JournalEntry(entryId, "Hoy organicé mis apuntes y quiero retomar mis ideas mañana.", DateTime.UtcNow, null, false, [attachment]);
var context = new JournalContext(new("Prueba", "", false), entry, [], [], [attachment],
    [new(attachmentId, attachment.FileName, attachment.ContentType, image)]);

void Check(bool success, string message)
{
    if (!success) throw new Exception(message);
    Console.WriteLine("OK: " + message);
}

OllamaJournalService Service(HttpClient client, LocalInferenceGate gate, OllamaOptions? config = null) =>
    new(client, Options.Create(config ?? new()), gate, NullLogger<OllamaJournalService>.Instance);

string Answer(string quote = "cita inventada") => JsonSerializer.Serialize(new
{
    title = "Tus apuntes", overview = "Ordenaste tus apuntes.",
    themes = new[] { new { title = "Apuntes", observation = "Organizaste tus apuntes.", interpretation = "", quote, sourceId = "actual" } },
    openQuestions = new[] { "¿Qué querés retomar?" }, sessionTopics = Array.Empty<string>(), nextStep = ""
}, json);

HttpResponseMessage Reply(string? answer = null, string reason = "stop") => new(HttpStatusCode.OK)
{
    Content = JsonContent.Create(new { done = true, done_reason = reason, message = new { content = answer ?? Answer() } })
};

using var gate = new LocalInferenceGate();
JsonElement? requestBody = null;
using var client = new HttpClient(new TestHandler(async (request, ct) =>
{
    Check(request.RequestUri == new Uri("http://127.0.0.1:11434/api/chat"), "La inferencia usa exclusivamente loopback");
    requestBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
    return Reply();
}));
var service = Service(client, gate);
var result = await service.ReflectAsync(context, true);
Check(!result.IsDemo, "La IA local funciona sin consentimiento histórico de Gemini");
Check(result.Themes.Single().Quote is null, "Una cita inventada no se presenta como literal");
Check(requestBody!.Value.GetProperty("messages")[1].GetProperty("images")[0].GetString() == Convert.ToBase64String(image), "La imagen se envía en base64");
Check(requestBody.Value.GetProperty("format").GetProperty("type").GetString() == "object", "Se exige salida JSON estructurada");
Check(requestBody.Value.GetProperty("think").ValueKind == JsonValueKind.False, "La devolución no consume el presupuesto en razonamiento interno");

var pdf = attachment with { ContentType = "application/pdf", FileName = "nota.pdf" };
var pdfResult = await service.ReflectAsync(context with
{
    Current = entry with { Attachments = [pdf] }, Attachments = [pdf],
    Media = [new(attachmentId, "nota.pdf", "application/pdf", "%PDF-test"u8.ToArray())]
}, true);
Check(requestBody!.Value.GetProperty("messages")[1].GetProperty("images").GetArrayLength() == 0 &&
    pdfResult.Overview.Contains("quedó fuera"), "PDF no compatible: se omite y se informa");

foreach (var invalid in new[]
{
    new OllamaOptions { BaseUrl = "https://ollama.com" },
    new OllamaOptions { BaseUrl = "http://192.168.1.2:11434" },
    new OllamaOptions { BaseUrl = "http://127.0.0.1:11434/otro" },
    new OllamaOptions { Model = "qwen3-vl:235b-cloud" }
}) Check(!invalid.IsValid(), "Se rechaza una configuración que sale del piloto local");

using var failedClient = new HttpClient(new TestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
try { await Service(failedClient, gate).ReflectAsync(context, true); throw new Exception("Faltó error de modelo"); }
catch (JournalReflectionException ex) { Check(ex.Message.Contains("Falta instalar"), "Un modelo ausente produce un error recuperable, sin fallback"); }
using var truncatedClient = new HttpClient(new TestHandler((_, _) => Task.FromResult(Reply(reason: "length"))));
try { await Service(truncatedClient, gate).ReflectAsync(context, true); throw new Exception("Se aceptó respuesta truncada"); }
catch (JournalReflectionException ex) { Check(ex.Message.Contains("incompleta"), "Se rechaza la generación truncada"); }

var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var calls = 0;
using var queueGate = new LocalInferenceGate();
using var queueClient = new HttpClient(new TestHandler(async (_, ct) =>
{
    if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await release.Task.WaitAsync(ct); }
    return Reply();
}));
var first = Service(queueClient, queueGate).ReflectAsync(context, true);
await entered.Task;
using var cancelled = new CancellationTokenSource();
var second = Service(queueClient, queueGate).ReflectAsync(context, true, cancelled.Token);
cancelled.Cancel();
try { await second; throw new Exception("Faltó cancelación"); } catch (OperationCanceledException) { }
Check(calls == 1, "Dos usuarios comparten la cola y una espera cancelada no llega al modelo");
release.SetResult();
await first;
await Service(queueClient, queueGate).ReflectAsync(context, true);
Check(calls == 2, "La cancelación libera correctamente la cola");

var testDb = Path.Combine(Path.GetTempPath(), $"mibitacora-check-{Guid.NewGuid():N}.db");
var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite($"Data Source={testDb};Pooling=False").Options;
try
{
    await using (var db = new ApplicationDbContext(dbOptions))
    {
        await db.Database.EnsureCreatedAsync();
        db.Users.AddRange(new ApplicationUser { Id = "a", UserName = "a" }, new ApplicationUser { Id = "b", UserName = "b" });
        await db.SaveChangesAsync();
    }
    var store = new JournalStore(new TestDbFactory(dbOptions));
    var a = await store.AddEntryAsync("a", "Apunte de A", [new("cuadrado.png", image)]);
    var b = await store.AddEntryAsync("b", "SECRETO_B", []);
    var own = await store.GetContextAsync("a", a.Id, true);
    Check(own.Media.Count == 1 && !JsonSerializer.Serialize(own).Contains("SECRETO_B"), "El contexto y las imágenes pertenecen al usuario autenticado");
    try { await store.GetContextAsync("a", b.Id, true); throw new Exception("Acceso cruzado"); }
    catch (JournalInputException) { Check(true, "Se rechaza consultar una entrada de otro usuario"); }
}
finally { File.Delete(testDb); }

if (args.Contains("--live"))
{
    using var localClient = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    var live = Service(localClient, gate);
    var timer = Stopwatch.StartNew();
    var text = await live.ReflectAsync(context with { Current = entry with { Attachments = [] }, Attachments = [], Media = [] }, true);
    Check(!text.IsDemo && text.Title.Length > 0, $"Modelo real: texto ({timer.Elapsed.TotalSeconds:F1} s)");
    Console.WriteLine(text.Overview);
    timer.Restart();
    var visual = await live.ReflectAsync(context with { Current = entry with { Text = "Describí la figura y su color en la imagen adjunta." } }, true);
    Check(!visual.IsDemo && JsonSerializer.Serialize(visual).Contains("rojo", StringComparison.OrdinalIgnoreCase), $"Modelo real: reconoce la imagen sintética ({timer.Elapsed.TotalSeconds:F1} s)");
    Console.WriteLine(visual.Overview);
}

sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
sealed class TestDbFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext() => new(options);
}
