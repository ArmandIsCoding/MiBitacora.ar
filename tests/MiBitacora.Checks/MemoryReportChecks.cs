using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MiBitacora.Components.Shared;
using MiBitacora.Data;
using MiBitacora.Services;

internal static class MemoryReportChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "mibitacora-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await VerifyAsync(root, check); }
        finally { Directory.Delete(root, true); }
    }
    private static async Task VerifyAsync(string root, Action<bool, string> check)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"Data Source={root}/test.db;Pooling=False;Foreign Keys=True"));
        services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton(new UploadFileStore(Path.Combine(root,"uploads"), NullLogger<UploadFileStore>.Instance));
        services.AddScoped<IJournalStore, JournalStore>();
        services.AddSingleton(TimeZoneInfo.Utc);
        var analyzer = new FakeJournalMemory();
        services.AddSingleton<IJournalMemoryAnalyzer>(analyzer);
        services.AddSingleton<IJournalReportAnalyzer>(analyzer);
        services.AddSingleton<JournalMemoryJobs>();
        services.AddSingleton<JournalTopicJobs>();
        services.AddScoped<JournalMemoryService>();
        services.AddScoped<JournalReportService>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var store = scope.ServiceProvider.GetRequiredService<IJournalStore>();
        var memory = scope.ServiceProvider.GetRequiredService<JournalMemoryService>();
        var reports = scope.ServiceProvider.GetRequiredService<JournalReportService>();
        var a = Guid.NewGuid().ToString(); var b = Guid.NewGuid().ToString();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            db.Users.AddRange(new ApplicationUser { Id=a,UserName="memory-a" },new ApplicationUser{Id=b,UserName="memory-b"});
            await db.SaveChangesAsync();
        }
        var old = await store.AddEntryAsync(a,"ANTECEDENTE_ANTIGUO: Dejé el coro porque el horario coincidía con el cuidado de mi madre.",[]);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE JournalEntries SET CreatedUtc={DateTime.UtcNow.AddMonths(-9)} WHERE Id={old.Id}");
        for(var i=0;i<17;i++) await store.AddEntryAsync(a,$"Registro cotidiano {i}: preparé un desayuno.",[]);
        var prior = await store.AddEntryAsync(a,new string('x',6500)+" FINAL_DEL_TURNO_COMPLETO",[]);
        await store.SaveReflectionAsync(a,prior.Id,new("Anterior","HIPOTESIS_IA_NO_ES_UN_HECHO",[],["¿Qué querés recuperar?"],[],null,false));
        var note = await store.SaveNoteAsync(a,null,"Me preocupa la factura de electricidad.");
        await store.SaveProfileAsync(a,new("Prueba","Contexto personal de prueba",false));
        var current = await store.AddEntryAsync(a,"Quisiera volver a cantar, pero sigo con las mismas obligaciones.",[]);
        await store.AddEntryAsync(a,"FUTURO_NO_RECORDAR",[]);
        await store.AddEntryAsync(b,"SECRETO_DE_B",[]);
        await store.SaveNoteAsync(b,null,"NOTA_SECRETA_DE_B");
        var context = await store.GetContextAsync(a,current.Id,false);
        var enriched = await memory.EnrichAsync(a,context);
        check(enriched.MemorySources?.Any(s=>s.EntryId==old.Id)==true,
            "Memoria: recupera un original de hace meses fuera de los turnos recientes");
        check(enriched.History.Count==6 && enriched.History.Last().Text.EndsWith("FINAL_DEL_TURNO_COMPLETO") &&
            enriched.History.Last().Reflection?.OpenQuestions.Single()=="¿Qué querés recuperar?",
            "Memoria: conserva seis turnos recientes y el final de la entrada y la pregunta anteriores");
        var indexedText=string.Join(" ",analyzer.Seen.Select(s=>s.Text));
        check(!indexedText.Contains("SECRETO") && !indexedText.Contains("FUTURO_NO_RECORDAR") && !indexedText.Contains("HIPOTESIS_IA_NO_ES_UN_HECHO"),
            "Memoria: excluye otras cuentas, entradas futuras y afirmaciones generadas por IA");
        check(string.Concat(analyzer.Seen.Where(s=>s.EntryId==prior.Id).Select(s=>s.Text))==prior.Text,
            "Memoria: indexa una entrada larga completa, incluidos sus últimos fragmentos");
        var calls=analyzer.IndexCalls;
        await memory.EnrichAsync(a,context);
        check(calls==analyzer.IndexCalls,"Memoria: reutiliza el índice sin volver a resumir fuentes intactas");
        await using(var db=await factory.CreateDbContextAsync())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE JournalEntries SET Text={"ANTECEDENTE_ANTIGUO: Ahora pude retomar el coro."} WHERE Id={old.Id}");
        var changed=await memory.EnrichAsync(a,context);
        check(analyzer.IndexCalls==calls+1 && changed.MemorySources!.Single().Text.Contains("retomar"),
            "Memoria: un cambio reemplaza el recuerdo obsoleto y sólo reindexa lo modificado");
        await store.DeleteEntryAsync(a,old.Id);
        check((await memory.EnrichAsync(a,context)).MemorySources!.Count==0,"Memoria: borrar un original impide recuperarlo de nuevo");
        await using(var db=await factory.CreateDbContextAsync())
        {
            await db.Database.OpenConnectionAsync();
            using var command=db.Database.GetDbConnection().CreateCommand();
            command.CommandText="SELECT COUNT(*) FROM JournalMemories WHERE lower(EntryId)=lower($entry)";
            var parameter=command.CreateParameter();parameter.ParameterName="$entry";parameter.Value=old.Id.ToString().ToUpperInvariant();command.Parameters.Add(parameter);
            check(Convert.ToInt64(await command.ExecuteScalarAsync())==0,"Memoria: la eliminación también retira el índice persistido en cascada");
        }
        var snapshot=await reports.GenerateAsync(a);
        check(snapshot.Report?.Sections.Count==10 && snapshot.Topics.Count==0,
            "Informe: guarda un documento integrado de diez apartados, no una tarjeta por entrada");
        check(analyzer.Planned.Any(s=>s.Text=="FUTURO_NO_RECORDAR") && analyzer.Planned.All(s=>!s.Text.Contains("SECRETO")),
            "Informe: recorre todos los textos actuales de la cuenta y mantiene el aislamiento");
        var topics=new JournalTopicService(factory,new UnusedTopicAnalyzer(),new JournalTopicJobs());
        check(!(await topics.GetAsync(a)).IsOutdated,"Informe: la huella del informe completo queda vigente");
        await using(var renderer=new HtmlRenderer(provider,provider.GetRequiredService<ILoggerFactory>()))
        {
            var html=await renderer.Dispatcher.InvokeAsync(async()=> (await renderer.RenderComponentAsync<TherapyReportView>(
                ParameterView.FromDictionary(new Dictionary<string,object?>{{"Snapshot",snapshot},{"PrintMode",true}}))).ToHtmlString());
            check(html.Contains("therapy-report") && html.Contains("/entrada/") && html.Contains("open") && !html.Contains("<script>"),
                "Informe: muestra fuentes enlazadas, evidencia para imprimir y contenido escapado");
        }
        analyzer.FailSection=true;
        try{await reports.GenerateAsync(a);throw new Exception("Publicó informe parcial");}catch(JournalReflectionException){}
        check((await topics.GetAsync(a)).Snapshot!.GeneratedUtc==snapshot.GeneratedUtc,"Informe: un fallo conserva la versión completa anterior");
        analyzer.FailSection=false;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        analyzer.OnSection=async ct=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);};
        using(var cancel=new CancellationTokenSource())
        {
            var pending=reports.GenerateAsync(a,ct:cancel.Token);
            await entered.Task;
            try{await reports.GenerateAsync(a);throw new Exception("Admitió dos informes simultáneos");}
            catch(JournalInputException){check(true,"Informe: evita dos generaciones simultáneas de la misma cuenta");}
            cancel.Cancel();
            try{await pending;throw new Exception("Ignoró cancelación");}catch(OperationCanceledException){}
        }
        check((await topics.GetAsync(a)).Snapshot!.GeneratedUtc==snapshot.GeneratedUtc,"Informe: cancelar conserva el documento anterior y libera el trabajo");
        var edited=false;
        analyzer.OnSection=async _=>{if(!edited){edited=true;await store.SaveNoteAsync(a,note.Id,"Cambió lo que quiero trabajar.");}};
        try{await reports.GenerateAsync(a);throw new Exception("Publicó informe obsoleto");}catch(JournalInputException){}
        check((await topics.GetAsync(a)).IsOutdated && (await topics.GetAsync(a)).Snapshot!.GeneratedUtc==snapshot.GeneratedUtc,
            "Informe: un cambio concurrente impide publicar fuentes obsoletas");
        analyzer.OnSection=null;
        await store.DeleteNoteAsync(a,note.Id);
        check((await topics.GetAsync(a)).Snapshot is null,"Informe: borrar una notita retira el informe que podía citarla");
        var newNote=await store.SaveNoteAsync(a,null,"NOTA_QUE_SE_BORRA_DURANTE_INDEXADO");
        var nextTurn=await store.AddEntryAsync(a,"Un mensaje posterior a la nueva notita.",[]);
        var nextContext=await store.GetContextAsync(a,nextTurn.Id,false);
        analyzer.OnIndex=async()=>await store.DeleteNoteAsync(a,newNote.Id);
        try{await memory.EnrichAsync(a,nextContext);throw new Exception("Resucitó una nota borrada");}
        catch(JournalInputException){check(true,"Memoria: una fuente eliminada durante la indexación no se guarda de nuevo");}

        var model=new ValidationAi(new InvalidReferences());
        try{await model.RecallAsync("Mensaje",[new(new("original",Guid.NewGuid(),DateTime.UtcNow,"Entrada","Texto original"),new("original","Resumen","Conceptos","Texto original"))],3,default);throw new Exception("Aceptó una memoria ajena");}
        catch(JournalReflectionException){check(true,"Memoria: rechaza referencias fuera de las fuentes de la solicitud");}
        try{await model.WriteReportSectionAsync(JournalReportSections.All[0],[new("original",Guid.NewGuid(),DateTime.UtcNow,"Entrada","Texto original")],"Alcance",default);throw new Exception("Aceptó una cita inventada");}
        catch(JournalReflectionException){check(true,"Informe: rechaza una cita inventada antes de publicar");}
        var planClient=new CompletePlanClient();
        var plan=await new ValidationAi(planClient).PlanReportAsync(
            [new(new("original",Guid.NewGuid(),DateTime.UtcNow,"Entrada","Texto original"),new("original","Resumen","Conceptos","Texto original"))],DateTime.UtcNow,default);
        check(planClient.RequiresAllSections && plan.Count==10 && plan.All(p=>p.SourceIds.Single()=="original"),
            "Informe: el esquema exige diez secciones distintas y conserva la procedencia de cada selección");
    }
    private sealed class ValidationAi(IJournalModelClient client):JournalAiService(client,NullLogger.Instance);
    private sealed class CompletePlanClient:IJournalModelClient
    {
        public bool IsConfigured=>true; public string ProviderName=>"Prueba"; public bool UsesExternalProvider=>true;
        public bool RequiresAllSections;
        public Task<string> GenerateAsync(string instruction,string prompt,JsonElement schema,IReadOnlyList<JournalImage> images,CancellationToken ct)
        {
            var sections=schema.GetProperty("properties").GetProperty("sections");
            var required=sections.GetProperty("required").EnumerateArray().Select(v=>v.GetString()).ToHashSet();
            RequiresAllSections=sections.GetProperty("type").GetString()=="object" && required.Count==10 &&
                JournalReportSections.All.All(s=>required.Contains(s.Id) && sections.GetProperty("properties").TryGetProperty(s.Id,out _));
            return Task.FromResult(JsonSerializer.Serialize(new{sections=JournalReportSections.All.ToDictionary(s=>s.Id,_=>new[]{"m0"})}));
        }
    }
    private sealed class InvalidReferences:IJournalModelClient
    {
        public bool IsConfigured=>true; public string ProviderName=>"Prueba"; public bool UsesExternalProvider=>true;
        public Task<string> GenerateAsync(string instruction,string prompt,JsonElement schema,IReadOnlyList<JournalImage> images,CancellationToken ct)=>
            Task.FromResult(schema.GetProperty("properties").TryGetProperty("sources",out _) ? """{"sources":["ajena"]}""" :
                """{"introduction":"Introducción","points":[{"kind":"registro","title":"Un punto","text":"Un texto","question":"","evidence":[{"sourceId":"f0","quote":"Cita inventada"}]}]}""");
    }
    private sealed class UnusedTopicAnalyzer:IJournalTopicAnalyzer
    {
        public bool IsConfigured=>true;
        public Task<IReadOnlyList<TopicObservation>> DetectAsync(IReadOnlyList<TopicSource> sources,IReadOnlyDictionary<string,string> catalog,CancellationToken ct,string additionalContext="")=>throw new NotSupportedException();
        public Task<TopicReading> ReadAsync(string title,string observations,CancellationToken ct,bool summarizeOnly=false,string additionalContext="")=>throw new NotSupportedException();
    }
}

internal sealed class FakeJournalMemory : IJournalMemoryAnalyzer, IJournalReportAnalyzer
{
    public bool UsesExternalProvider=>true;
    public bool IsConfigured=>true;
    public int IndexCalls;
    public bool FailSection;
    public Func<Task>? OnIndex;
    public Func<CancellationToken,Task>? OnSection;
    public List<TopicSource> Seen {get;}=[];
    public List<TopicSource> Planned {get;}=[];
    public async Task<IReadOnlyList<MemoryDigest>> IndexAsync(IReadOnlyList<TopicSource> sources,CancellationToken ct)
    {
        IndexCalls++;Seen.AddRange(sources);
        if(OnIndex is not null)await OnIndex();
        return sources.Select(s=>new MemoryDigest(s.Id,s.Text[..Math.Min(100,s.Text.Length)],"Conceptos",s.Text[..Math.Min(60,s.Text.Length)])).ToArray();
    }
    public Task<IReadOnlyList<string>> RecallAsync(string conversation,IReadOnlyList<MemoryCard> memories,int limit,CancellationToken ct)=>
        Task.FromResult<IReadOnlyList<string>>(memories.Where(m=>m.Source.Text.Contains("ANTECEDENTE_ANTIGUO")).Take(limit).Select(m=>m.Source.Id).ToArray());
    public Task<IReadOnlyList<ReportSectionPlan>> PlanReportAsync(IReadOnlyList<MemoryCard> cards,DateTime nowUtc,CancellationToken ct)
    {
        Planned.AddRange(cards.Select(c=>c.Source));
        return Task.FromResult<IReadOnlyList<ReportSectionPlan>>(JournalReportSections.All.Select(s=>new ReportSectionPlan(s.Id,cards.Take(2).Select(c=>c.Source.Id).ToArray())).ToArray());
    }
    public async Task<TherapyReportSection> WriteReportSectionAsync(ReportSectionDefinition section,IReadOnlyList<TopicSource> sources,string scope,CancellationToken ct)
    {
        if(FailSection)throw new JournalReflectionException("Fallo sintético");
        if(OnSection is not null)await OnSection(ct);
        var source=sources.First();
        return new(section.Id,section.Title,"Una lectura para revisar.",[new("hipotesis","Una posibilidad","Texto literal <script>sin ejecutar</script>","¿Qué me gustaría comprender?",
            [new(source.EntryId,source.CreatedUtc,source.Label,source.Text[..Math.Min(80,source.Text.Length)],source.Kind,source.NoteId)])]);
    }
}
