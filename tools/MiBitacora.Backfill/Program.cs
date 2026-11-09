using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiBitacora.Data;
using MiBitacora.Services;

// Explicitly invoked maintenance command. Never runs at web startup or exposes an HTTP endpoint.
string Required(string key)
{
    var i = Array.IndexOf(args, key);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"Falta {key}");
}
var root = Path.GetFullPath(Required("--root"));
var userId = Required("--user");
if (!Guid.TryParse(userId, out _)) throw new ArgumentException("La cuenta debe ser un GUID.");
var database = Path.Combine(root, "App_Data", "mi-bitacora.db");
if (!File.Exists(database)) throw new ArgumentException("No existe la base indicada; no se crea una nueva.");
using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "App_Data", "ai.settings.json")));
if (config.RootElement.GetProperty("AI").GetProperty("Provider").GetString() != "OpenAI")
    throw new ArgumentException("Este comando requiere la configuración privada de OpenAI de esta instalación.");
var settings = config.RootElement.GetProperty("OpenAI").Deserialize<OpenAiOptions>()!;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
using var gate = new LocalInferenceGate();
var model = new OpenAiJournalService(http, Options.Create(settings), gate, NullLogger<OpenAiJournalService>.Instance);
var services = new ServiceCollection().AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
var connection = new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWrite, ForeignKeys = true, DefaultTimeout = 15 };
services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite(connection.ToString()));
services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
services.AddSingleton(new UploadFileStore(Path.Combine(root, "uploads"), NullLogger<UploadFileStore>.Instance));
services.AddScoped<IJournalStore, JournalStore>();
await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
await using (var db = await factory.CreateDbContextAsync())
    if (!await db.Users.AnyAsync(u => u.Id == userId)) throw new ArgumentException("La cuenta no existe en esta instalación.");
var store = scope.ServiceProvider.GetRequiredService<IJournalStore>();
var topics = new JournalTopicService(factory, model, new JournalTopicJobs());
var entries = await store.GetEntriesAsync(userId, int.MaxValue, cancellationToken: cancellation.Token);
var pending = entries.Where(e => e.Reflection is null || e.Reflection.IsDemo || e.Reflection.ConversationVersion < JournalReflection.CurrentConversationVersion)
    .OrderBy(e => e.CreatedUtc).ThenBy(e => e.Id).ToList();
var backupDirectory = Path.Combine(root, "App_Data", "conversation-backups");
Directory.CreateDirectory(backupDirectory);
var backup = Path.Combine(backupDirectory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
await File.WriteAllTextAsync(backup, JsonSerializer.Serialize(new
{
    userId, createdUtc = DateTime.UtcNow,
    reflections = entries.Select(e => new { e.Id, e.Reflection }),
    topicState = await topics.GetAsync(userId, cancellation.Token)
}, new JsonSerializerOptions(JsonSerializerDefaults.Web)), cancellation.Token);
Console.WriteLine($"Respaldo privado creado. Entradas: {entries.Count}; respuestas pendientes: {pending.Count}.");
var completed = 0; var failed = 0; var skipped = 0;
foreach (var entry in pending)
{
    cancellation.Token.ThrowIfCancellationRequested();
    try
    {
        var context = await store.GetContextAsync(userId, entry.Id, includeMedia: true, cancellation.Token);
        // Current profile text cannot establish what was true when a historical entry was written.
        if (context.Current.CreatedUtc.Date < DateTime.UtcNow.Date)
            context = context with { Profile = context.Profile with { PersonalContext = "" } };
        var answer = await model.ReflectAsync(context, true, cancellation.Token);
        var current = await store.GetEntryAsync(userId, entry.Id, cancellation.Token);
        if (current is null || current.Text != entry.Text ||
            current.Reflection is { IsDemo: false, ConversationVersion: >= JournalReflection.CurrentConversationVersion })
        { skipped++; continue; }
        await store.SaveReflectionAsync(userId, entry.Id, answer, cancellation.Token);
        completed++;
        Console.WriteLine($"Respuesta {completed + failed + skipped}/{pending.Count} guardada ({entry.CreatedUtc:yyyy-MM-dd}).");
    }
    catch (JournalInputException) { skipped++; Console.WriteLine("Una entrada dejó de estar disponible; se omite."); }
    catch (JournalReflectionException ex)
    {
        failed++;
        Console.WriteLine($"Respuesta pendiente ({entry.CreatedUtc:yyyy-MM-dd}): {ex.Message}");
        // Do not automatically retry or keep spending after a provider error. Rerun resumes missing work.
        break;
    }
}
if (failed == 0 && args.Contains("--topics"))
{
    Console.WriteLine("Actualizando temas con contexto pertinente…");
    var snapshot = await topics.GenerateAsync(userId, new Progress<string>(Console.WriteLine), cancellation.Token);
    Console.WriteLine($"Temas actualizados: {snapshot.Topics.Count}.");
}
var finalEntries = await store.GetEntriesAsync(userId, int.MaxValue, cancellationToken: cancellation.Token);
var remaining = finalEntries.Count(e => e.Reflection is null || e.Reflection.IsDemo || e.Reflection.ConversationVersion < JournalReflection.CurrentConversationVersion);
Console.WriteLine($"Resultado: {completed} nuevas respuestas; {skipped} omitidas; {remaining} pendientes de {finalEntries.Count} entradas.");
Environment.ExitCode = remaining == 0 && failed == 0 ? 0 : 2;
