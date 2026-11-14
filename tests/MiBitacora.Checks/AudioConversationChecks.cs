using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiBitacora.Data;
using MiBitacora.Services;

internal static class AudioConversationChecks
{
    public static async Task RunAsync(Action<bool,string> check)
    {
        await ClientAsync(check);
        var root=Path.Combine(Path.GetTempPath(),"mibitacora-audio-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await StorageAsync(root,check); } finally { Directory.Delete(root,true); }
    }
    private static async Task ClientAsync(Action<bool,string> check)
    {
        using var gate=new LocalInferenceGate();
        var calls=0;
        using var http=new HttpClient(new AudioHandler(async (request,ct)=>
        {
            calls++;
            var body=await request.Content!.ReadAsStringAsync(ct);
            check(request.RequestUri==new Uri("https://api.openai.com/v1/audio/transcriptions") &&
                request.Headers.GetValues("OpenAI-Project").Single()=="proj_audio" &&
                body.Contains("gpt-transcribe") && body.Contains("audio.wav") && !body.Contains("prompt"),
                "Audio: usa el endpoint de transcripción, proyecto y archivo con formato, sin contexto que sugiera recuerdos");
            return new(HttpStatusCode.OK){Content=JsonContent.Create(new{text="Hoy disfruté de caminar junto al río."})};
        }));
        OpenAiAudioTranscriber Client(HttpClient client,string provider="OpenAI")=>new(client,
            Options.Create(new OpenAiOptions{ApiKey="synthetic-key",ProjectId="proj_audio"}),Options.Create(new JournalAiOptions{Provider=provider}),
            Options.Create(new AudioTranscriptionOptions()),gate,NullLogger<OpenAiAudioTranscriber>.Instance);
        var result=await Client(http).TranscribeAsync(new("voz.wav","audio/wav",new MemoryStream(AttachmentViewerChecks.Wave())));
        check(result=="Hoy disfruté de caminar junto al río." && calls==1,"Audio: conserva una transcripción literal sin llamadas repetidas");
        try { await Client(http,"Ollama").TranscribeAsync(new("voz.wav","audio/wav",new MemoryStream(AttachmentViewerChecks.Wave()))); throw new Exception("Faltó bloqueo local"); }
        catch(JournalReflectionException) {check(calls==1,"Audio: una instalación local no envía grabaciones a OpenAI");}
        var failures=0;
        using var denied=new HttpClient(new AudioHandler((request,ct)=>
        {failures++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden){Content=new StringContent("private-provider-body")});}));
        try {await Client(denied).TranscribeAsync(new("voz.wav","audio/wav",new MemoryStream(AttachmentViewerChecks.Wave())));throw new Exception("Faltó error API");}
        catch(JournalReflectionException ex){check(failures==1&&!ex.Message.Contains("private-provider-body"),"Audio: informa errores sin exponer respuestas privadas ni reintentar cargos automáticamente");}
    }
    private static async Task StorageAsync(string root,Action<bool,string> check)
    {
        var services=new ServiceCollection().AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(o=>o.UseSqlite($"Data Source={root}/test.db;Pooling=False;Foreign Keys=True"));
        services.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton(new UploadFileStore(Path.Combine(root,"uploads"),NullLogger<UploadFileStore>.Instance));
        services.AddScoped<IJournalStore,JournalStore>();
        var audio=new FakeAudio();var model=new FakeReply();var memories=new AudioMemory();
        services.AddSingleton<IAudioTranscriber>(audio);services.AddSingleton<IJournalReflector>(model);services.AddSingleton<IJournalMemoryAnalyzer>(memories);
        services.AddSingleton<JournalMemoryJobs>();services.AddSingleton<JournalConversationJobs>();services.AddScoped<JournalMemoryService>();services.AddScoped<JournalConversationService>();
        await using var provider=services.BuildServiceProvider();
        var store=provider.GetRequiredService<IJournalStore>();var flow=provider.GetRequiredService<JournalConversationService>();
        var factory=provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var a=Guid.NewGuid().ToString();var b=Guid.NewGuid().ToString();
        await using(var db=await factory.CreateDbContextAsync()) {await db.Database.MigrateAsync();db.Users.AddRange(new ApplicationUser{Id=a,UserName="audio-a"},new ApplicationUser{Id=b,UserName="audio-b"});await db.SaveChangesAsync();}
        var notebook=await store.SaveNotebookAsync(a,null,"Relojes");
        var wave=AttachmentViewerChecks.Wave();
        var old=await store.AddEntryAsync(a,"",[new("voz.wav",wave,true)],notebookId:notebook.Id);
        await using(var db=await factory.CreateDbContextAsync()) {await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE JournalEntries SET CreatedUtc={DateTime.UtcNow.AddMonths(-6)} WHERE Id={old.Id}");}
        check(audio.Calls==0 && old.Attachments.Single().ExtractedText is null,"Audio: guardar conserva el archivo sin transcribir ni consumir API");
        var activities = new List<BrisaActivity>();
        var operations = new List<(string Operation, BrisaActivity Phase)>();
        audio.Before = () => operations.Add(("audio", activities.Last()));
        model.Before = () => operations.Add(("response", activities.Last()));
        var updated=await flow.GenerateAsync(a,old.Id,activity:new ActivityProgress(activities.Add));
        check(activities.SequenceEqual(new[] { BrisaActivity.Reading, BrisaActivity.Thinking }) &&
            operations.SequenceEqual(new[] { ("audio", BrisaActivity.Reading), ("response", BrisaActivity.Thinking) }),
            "Brisa: lectura y pensamiento siguen la transcripción y la generación reales, sin esperas artificiales");
        check(audio.Calls == 1 && model.Calls == 1, "Brisa: los gestos no agregan llamadas de IA ni repiten la transcripción");
        audio.Before = null; model.Before = null;
        check(updated.Attachments.Single().IsVoiceMessage&&updated.Attachments.Single().ExtractedText==FakeAudio.Words&&model.Context!.Current.Text=="",
            "Audio: guarda el texto transcrito junto al original, sin sustituir el mensaje escrito");
        var original=await store.GetAttachmentAsync(a,old.Attachments.Single().Id);
        await using(var stream=original!.Content){using var buffer=new MemoryStream();await stream.CopyToAsync(buffer);check(buffer.ToArray().SequenceEqual(wave),"Audio: el original se conserva byte por byte");}
        var first=updated.Reflection!;
        await store.SetEntryForSessionAsync(a,old.Id,true);
        var regenerated=await flow.GenerateAsync(a,old.Id);
        check(audio.Calls==1&&regenerated.Id==old.Id&&regenerated.CreatedUtc==updated.CreatedUtc&&regenerated.ForSession&&regenerated.Reflection!.Overview!=first.Overview,
            "Regenerar: reutiliza la transcripción y reemplaza sólo la respuesta, conservando fecha, diario y selección");
        model.Fail=true;
        try{await flow.GenerateAsync(a,old.Id);throw new Exception("Faltó fallo de respuesta");}catch(JournalReflectionException){}
        check((await store.GetEntryAsync(a,old.Id))!.Reflection!.Overview==regenerated.Reflection!.Overview,"Regenerar: un fallo conserva la respuesta anterior");
        model.Fail=false;
        try{await flow.GenerateAsync(b,old.Id);throw new Exception("Se leyó otra cuenta");}catch(JournalInputException){}
        try{await store.SaveAudioTranscriptAsync(b,old.Attachments.Single().Id,"otra voz");throw new Exception("Se escribió otra cuenta");}catch(JournalInputException){}
        check(audio.Calls==1,"Audio: transcribir o regenerar exige que la entrada pertenezca a la cuenta");
        var current=await store.AddEntryAsync(a,"",[new("actual.wav",wave,true)]);
        await flow.GenerateAsync(a,current.Id);
        using var recall = JsonDocument.Parse(memories.LastQuery);
        check(recall.RootElement.GetProperty("actual").GetString()!.Contains(FakeAudio.Words)&&model.Context!.MemorySources!.Any(s=>s.EntryId==old.Id&&s.Kind==TopicSourceKind.Voice&&s.Text==FakeAudio.Words),
            "Memoria: una respuesta de voz busca por su transcripción y recupera audio antiguo de otro diario");
        await using(var db=await factory.CreateDbContextAsync())
        {
            var corpus=await JournalCorpusReader.ReadAsync(db,a);
            check(corpus.TextEntryCount==2&&corpus.Sources.Count(s=>s.Kind==TopicSourceKind.Voice)==2,
                "Informe: los mensajes de voz transcritos forman parte del corpus completo");
        }
        var followup=await store.AddEntryAsync(a,"Sí, es eso.",[]);
        var context=await store.GetContextAsync(a,followup.Id,false);
        check(context.History.Any(e=>e.Id==current.Id&&JournalAudio.ConversationText(e).Contains(FakeAudio.Words)),
            "Conversación: el hilo reciente conserva lo dicho en los audios anteriores");
        var other=await store.AddEntryAsync(a,"Un audio que me compartieron.",[new("adjunto.wav",wave)]);
        await flow.GenerateAsync(a,other.Id);
        await using(var db=await factory.CreateDbContextAsync())
        {
            var corpus=await JournalCorpusReader.ReadAsync(db,a);
            check(corpus.Sources.Any(s=>s.EntryId==other.Id&&s.Kind==TopicSourceKind.Document&&s.Label.Contains("hablante no confirmado")),
                "Audio adjunto: no se convierte automáticamente una voz ajena en autobiografía");
        }
        model.Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);model.Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var running=flow.GenerateAsync(a,current.Id);await model.Entered.Task;
        try{await flow.GenerateAsync(a,current.Id);throw new Exception("Se duplicó la generación");}catch(JournalReflectionException){}
        model.Release.SetResult();await running;
        check(audio.Calls==3,"Regenerar: dos solicitudes simultáneas no duplican trabajo sobre la misma entrada");
        byte[] webm = [0x1a,0x45,0xdf,0xa3,0x87,0x42,0x82,0x84,0x77,0x65,0x62,0x6d,
            0x18,0x53,0x80,0x67,0xff,0x16,0x54,0xae,0x6b,0x85,0xae,0x83,0x83,0x81,0x02];
        var webmEntry = await store.AddEntryAsync(a,"",[new("voz.webm",webm)]);
        var webmPreview = await store.GetAttachmentPreviewAsync(a,webmEntry.Attachments.Single().Id);
        await using (webmPreview!.Content)
            check(webmEntry.Attachments.Single().ContentType=="audio/webm" && webmPreview.ContentType=="audio/webm",
                "Audio: identifica WebM de audio por sus pistas al guardar y reproducir, aunque use la extensión general");
        webm[^1] = 1;
        var video = await store.AddEntryAsync(a,"",[new("video.webm",webm),new("cortado.webm",webm[..^1])]);
        check(video.Attachments.All(f=>f.ContentType=="video/webm"),"Audio: un WebM de video o truncado no se confunde con un mensaje de voz");
        var twoAudios=await store.AddEntryAsync(a,"Dos grabaciones.",[new("primero.wav",wave,true),new("segundo.wav",wave,true)]);
        audio.FailAt=audio.Calls+2;
        try{await flow.GenerateAsync(a,twoAudios.Id);throw new Exception("Faltó el fallo del segundo audio");}catch(JournalReflectionException){}
        var partial=(await store.GetEntryAsync(a,twoAudios.Id))!;
        check(partial.Attachments.Count(JournalAudio.HasTranscript)==1&&partial.Reflection is null,
            "Audio: un fallo posterior conserva las transcripciones ya completadas y todos los originales");
        var callsAfterFailure=audio.Calls;audio.FailAt=0;
        var retried=await flow.GenerateAsync(a,twoAudios.Id);
        check(audio.Calls==callsAfterFailure+1&&retried.Attachments.All(JournalAudio.HasTranscript)&&retried.Reflection is not null,
            "Audio: al reintentar sólo se transcribe el archivo pendiente");
        using var cancel=new CancellationTokenSource();
        model.Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);model.Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled=flow.GenerateAsync(a,twoAudios.Id,ct:cancel.Token);await model.Entered.Task;cancel.Cancel();
        try{await cancelled;throw new Exception("Faltó cancelación");}catch(OperationCanceledException){}
        check((await store.GetEntryAsync(a,twoAudios.Id))!.Reflection!.Overview==retried.Reflection!.Overview,
            "Regenerar: cancelar antes de terminar conserva la última respuesta completa");
    }
    private sealed class FakeAudio : IAudioTranscriber
    {
        public const string Words="Hoy reparé el reloj azul que me regaló mi abuelo. Me sentí acompañado al recordarlo.";
        public bool IsConfigured=>true;public int Calls,FailAt; public Action? Before;
        public Task<string> TranscribeAsync(AttachmentContent file,CancellationToken ct=default)
        {Before?.Invoke();Calls++;if(Calls==FailAt)throw new JournalReflectionException("Fallo sintético de audio");return Task.FromResult(Words);}
    }
    private sealed class FakeReply : IJournalReflector
    {
        public bool IsConfigured=>true;public bool UsesExternalProvider=>true;public bool Fail;public JournalContext? Context;private int _calls;
        public int Calls => _calls; public Action? Before;
        public TaskCompletionSource? Entered,Release;
        public async Task<JournalReflection> ReflectAsync(JournalContext context,bool useAi,CancellationToken cancellationToken=default)
        {
            Before?.Invoke();
            Context=context;if(Fail)throw new JournalReflectionException("Fallo sintético");
            if(Entered is not null){Entered.TrySetResult();await Release!.Task.WaitAsync(cancellationToken);}
            return new("Una lectura",$"Respuesta de prueba {++_calls}",[],[],[],null,false,1,DateTime.UtcNow);
        }
    }
    private sealed class AudioMemory:IJournalMemoryAnalyzer
    {
        public bool UsesExternalProvider=>true; public string LastQuery="";
        public Task<IReadOnlyList<MemoryDigest>> IndexAsync(IReadOnlyList<TopicSource> sources,CancellationToken ct)=>
            Task.FromResult<IReadOnlyList<MemoryDigest>>(sources.Select(s=>new MemoryDigest(s.Id,s.Text,"audio",s.Text[..Math.Min(50,s.Text.Length)])).ToArray());
        public Task<IReadOnlyList<string>> RecallAsync(string conversation,IReadOnlyList<MemoryCard> memories,int limit,CancellationToken ct)
        {LastQuery=conversation;return Task.FromResult<IReadOnlyList<string>>(memories.Take(limit).Select(m=>m.Source.Id).ToArray());}
    }
    private sealed class AudioHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> handler):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>handler(request,ct);}
    private sealed class ActivityProgress(Action<BrisaActivity> report) : IProgress<BrisaActivity>
    { public void Report(BrisaActivity activity) => report(activity); }
}

internal sealed class RejectAudioTranscriber:IAudioTranscriber
{
    public bool IsConfigured=>false;
    public Task<string> TranscribeAsync(AttachmentContent file,CancellationToken ct=default)=>throw new InvalidOperationException("La prueba no debe invocar transcripción.");
}
