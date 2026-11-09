using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiBitacora.Data;
using MiBitacora.Services;

string Required(string name) { var index=Array.IndexOf(args,name); return index>=0 && index+1<args.Length ? args[index+1] : throw new ArgumentException("Falta "+name); }
var root=Path.GetFullPath(Required("--root"));
var userId=Required("--user");
if(!Guid.TryParse(userId,out _)) throw new ArgumentException("La cuenta debe ser un GUID.");
var database=Path.Combine(root,"App_Data","mi-bitacora.db");
if(!File.Exists(database)) throw new ArgumentException("La base indicada no existe.");
var backupDirectory=Path.Combine(root,"App_Data","report-backups");
Directory.CreateDirectory(backupDirectory);
var connectionString=new SqliteConnectionStringBuilder{DataSource=database,Mode=SqliteOpenMode.ReadWrite,ForeignKeys=true,DefaultTimeout=15}.ToString();
using(var source=new SqliteConnection(connectionString))
{
    await source.OpenAsync();
    using var user=source.CreateCommand(); user.CommandText="SELECT COUNT(*) FROM AspNetUsers WHERE Id=$user"; user.Parameters.AddWithValue("$user",userId);
    if(Convert.ToInt64(await user.ExecuteScalarAsync())!=1)throw new ArgumentException("La cuenta no existe.");
    if(args.Contains("--backup-only"))
    {
        var backup=Path.Combine(backupDirectory,$"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
        using var target=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=backup}.ToString());
        await target.OpenAsync(); source.BackupDatabase(target);
        Console.WriteLine("Respaldo consistente de SQLite guardado en App_Data/report-backups.");
        return;
    }
}
using var config=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"App_Data","ai.settings.json")));
if(config.RootElement.GetProperty("AI").GetProperty("Provider").GetString()!="OpenAI")throw new ArgumentException("Este comando utiliza la configuración privada de OpenAI de la instalación.");
var settings=config.RootElement.GetProperty("OpenAI").Deserialize<OpenAiOptions>()!;
using var cancellation=new CancellationTokenSource();
Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;cancellation.Cancel();};
using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false}){Timeout=Timeout.InfiniteTimeSpan};
using var gate=new LocalInferenceGate();
var model=new OpenAiJournalService(http,Options.Create(settings),gate,NullLogger<OpenAiJournalService>.Instance);
var services=new ServiceCollection().AddLogging(b=>b.SetMinimumLevel(LogLevel.Warning));
services.AddDbContextFactory<ApplicationDbContext>(o=>o.UseSqlite(connectionString));
services.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
await using var provider=services.BuildServiceProvider();
var factory=provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
var jobs=new JournalTopicJobs();
var topics=new JournalTopicService(factory,model,jobs);
var old=await topics.GetAsync(userId,cancellation.Token);
await File.WriteAllTextAsync(Path.Combine(backupDirectory,$"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"),
    JsonSerializer.Serialize(new{userId,previous=old},new JsonSerializerOptions(JsonSerializerDefaults.Web)),cancellation.Token);
var memory=new JournalMemoryService(factory,model,new JournalMemoryJobs());
var reports=new JournalReportService(factory,memory,model,jobs);
var result=await reports.GenerateAsync(userId,new ConsoleProgress(),cancellation.Token);
Console.WriteLine($"Informe guardado: {result.Report!.Sections.Count} apartados; {result.EntryCount} entradas; {result.NoteCount} notitas. Originales conservados.");
if((await topics.GetAsync(userId,cancellation.Token)).IsOutdated)throw new Exception("El informe quedó desactualizado; revisá cambios simultáneos.");
Console.WriteLine("Verificación completa: informe vigente.");
sealed class ConsoleProgress:IProgress<string>{public void Report(string value)=>Console.WriteLine(value);}
