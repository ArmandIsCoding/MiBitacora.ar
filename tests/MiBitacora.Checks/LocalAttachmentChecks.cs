using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ImageMagick;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiBitacora.Data;
using MiBitacora.Services;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

internal static class LocalAttachmentChecks
{
    public static async Task RunAsync(Action<bool, string> check, bool codecs, bool live = false, string baseUrl = "http://127.0.0.1:11434", string? onlyCase = null)
    {
        if (live && onlyCase is not (null or "pdf-text" or "pdf-scan" or "audio" or "video")) throw new ArgumentException("Caso de prueba local desconocido.");
        var root = Path.Combine(Path.GetTempPath(), "mibitacora-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = Options.Create(new LocalAttachmentOptions { Enabled = true });
            var processor = new LocalAttachmentProcessor(Path.Combine(root, "staging"), Path.GetFullPath("media-tools"), options);
            var pdf = Pdf(false);
            var prepared = await processor.PrepareAsync(new("prueba.pdf", "application/pdf", new MemoryStream(pdf)));
            check(prepared.Text!.Contains("viernes") && prepared.Text.Contains("verde") && prepared.Images.Count == 2
                && prepared.Images.All(i => i.Content[0] == 0xff) && prepared.Coverage.Contains("1, 3"),
                "Lectura local: PDF con texto y páginas visuales etiquetadas, sin inventar cobertura completa");
            var scanned = await processor.PrepareAsync(new("escaneado.pdf", "application/pdf", new MemoryStream(Pdf(true))));
            check(string.IsNullOrWhiteSpace(scanned.Text) && scanned.Images.Count == 2,
                "Lectura local: un PDF escaneado llega como imágenes reales y no como números de página ficticios");
            try { await processor.PrepareAsync(new("roto.pdf", "application/pdf", new MemoryStream("%PDF-roto"u8.ToArray()))); throw new Exception("PDF inválido aceptado"); }
            catch (JournalInputException) { check(!Directory.EnumerateFileSystemEntries(Path.Combine(root, "staging")).Any(), "Lectura local: PDF inválido conserva el original y limpia todos los temporales"); }
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                try { await processor.PrepareAsync(new("cancelado.pdf", "application/pdf", new MemoryStream(pdf)), ct: cancel.Token); throw new Exception("Cancelación ignorada"); }
                catch (OperationCanceledException) { check(true, "Lectura local: la cancelación no inicia un decodificador ni deja archivos"); }
            }
            var keep = Path.Combine(root, "staging", "keep.txt"); await File.WriteAllTextAsync(keep, "archivo ajeno");
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            {
                try { await processor.PrepareAsync(new("interrumpido.pdf", "application/pdf", new MemoryStream(pdf)), ct: cancel.Token); throw new Exception("La lectura no se canceló"); }
                catch (OperationCanceledException) { check(File.Exists(keep) && !Directory.EnumerateDirectories(Path.Combine(root, "staging")).Any(),
                    "Lectura local: cancelar durante la preparación termina el proceso hijo y conserva archivos ajenos"); }
            }
            File.Delete(keep);
            await StoreAsync(root, processor, check, codecs);
            if (codecs) await CodecsAsync(root, processor, check);
            if (live) await LiveAsync(root, processor, baseUrl, check, onlyCase);
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("mibitacora-local-", StringComparison.Ordinal)) throw new IOException("Directorio de prueba inválido.");
            Directory.Delete(resolved, true);
        }
    }

    internal static byte[] Pdf(bool scanned)
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var image = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "red-square.png"));
        for (var i = 0; i < 3; i++)
        {
            var page = builder.AddPage(400, 400);
            if (scanned) page.AddPng(image, new PdfRectangle(30, 30, 370, 370));
            else page.AddText($"Pagina {i + 1}. Mi cuaderno es verde. La reunion es el viernes.", 14, new PdfPoint(20, 300), font);
        }
        return builder.Build();
    }

    private static async Task StoreAsync(string root, LocalAttachmentProcessor processor, Action<bool, string> check, bool codecs)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"Data Source={root}/test.db;Pooling=False;Foreign Keys=True"));
        services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton(new UploadFileStore(Path.Combine(root, "uploads"), NullLogger<UploadFileStore>.Instance));
        services.AddScoped<IJournalStore, JournalStore>();
        services.AddSingleton(processor);
        var model = new CaptureModel();
        var transcriber = new FakeAudio();
        services.AddSingleton<IAudioTranscriber>(transcriber);
        services.AddSingleton<IJournalReflector>(new TestAi(model));
        services.AddSingleton<IJournalMemoryAnalyzer, FakeMemory>();
        services.AddSingleton<JournalMemoryJobs>(); services.AddSingleton<JournalConversationJobs>();
        services.AddScoped<JournalMemoryService>(); services.AddScoped<LocalAttachmentAnalysis>(); services.AddScoped<JournalConversationService>();
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var user = Guid.NewGuid().ToString(); var other = Guid.NewGuid().ToString();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            db.Users.AddRange(new ApplicationUser { Id = user, UserName = user }, new ApplicationUser { Id = other, UserName = other });
            await db.SaveChangesAsync();
        }
        var store = provider.GetRequiredService<IJournalStore>();
        var flow = provider.GetRequiredService<JournalConversationService>();
        foreach (var extension in new[] { ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonl", ".xml", ".yml", ".yaml", ".log", ".ini" })
        {
            var saved = await store.AddEntryAsync(user, "Documento sintético", [new("prueba" + extension, Encoding.UTF8.GetBytes("cuaderno verde, reunion viernes"))]);
            check(saved.Attachments[0].ExtractedText?.Contains("viernes") == true, "Lectura local de documento: " + extension);
        }
        foreach (var (extension, part, xml) in new[] {
            (".docx", "word/document.xml", "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>cuaderno verde reunion viernes</w:t></w:r></w:p></w:body></w:document>"),
            (".pptx", "ppt/slides/slide1.xml", "<p:sld xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><a:p><a:r><a:t>cuaderno verde reunion viernes</a:t></a:r></a:p></p:sld>"),
            (".odt", "content.xml", "<office:document-content xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0' xmlns:text='urn:oasis:names:tc:opendocument:xmlns:text:1.0'><text:p>cuaderno verde reunion viernes</text:p></office:document-content>") })
        {
            var saved = await store.AddEntryAsync(user, "Documento sintético", [new("prueba" + extension, Zip((part, xml)))]);
            check(saved.Attachments[0].ExtractedText?.Contains("viernes") == true, "Lectura local de documento: " + extension);
        }
        foreach (var extension in new[] { ".xlsx", ".xlsm" })
        {
            var saved = await store.AddEntryAsync(user, "Planilla sintética", [new("prueba" + extension, Workbook())]);
            check(saved.Attachments[0].ExtractedText?.Contains("viernes") == true, "Lectura local de planilla: " + extension);
        }
        foreach (var extension in new[] { ".xls", ".xlsb" })
        {
            var saved = await store.AddEntryAsync(user, "Matriz numérica sintética de 10 por 10", [new("10x10" + extension, await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "10x10" + extension)))]);
            check(saved.Attachments[0].ExtractedText is { Length: > 100 } text && text.Any(char.IsDigit), "Lectura local de planilla binaria: " + extension);
        }
        foreach (var (extension, format) in new[] { ("jpg", MagickFormat.Jpeg), ("png", MagickFormat.Png), ("webp", MagickFormat.WebP), ("gif", MagickFormat.Gif), ("avif", MagickFormat.Avif) })
        {
            using var photo = new MagickImage(MagickColors.Red, 100, 80);
            var saved = await store.AddEntryAsync(user, "Foto de prueba", [new("imagen." + extension, photo.ToByteArray(format))]);
            var context = await store.GetContextAsync(user, saved.Id, includeMedia: true);
            check(context.Media.Any(i => i.AttachmentId == saved.Attachments[0].Id && i.ContentType == "image/jpeg"), "Lectura visual local: " + extension + " se normaliza a JPEG");
        }
        var heic = await store.AddEntryAsync(user, "Foto HEIC de prueba", [new("imagen.heic", await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "oriented-colors.heic")))]);
        check((await store.GetContextAsync(user, heic.Id, includeMedia: true)).Media.Any(m => m.AttachmentId == heic.Attachments[0].Id), "Lectura visual local: HEIC conserva una copia JPEG utilizable");
        var original = Pdf(false);
        var entry = await store.AddEntryAsync(user, "¿Qué día es la reunión y de qué color es el cuaderno?", [new("prueba.pdf", original)]);
        await flow.GenerateAsync(user, entry.Id);
        var read = (await store.GetEntryAsync(user, entry.Id))!;
        using var promptJson = JsonDocument.Parse(model.Prompt[(model.Prompt.LastIndexOf('\n') + 1)..]);
        var documentSource = promptJson.RootElement.GetProperty("fuentes").EnumerateArray().First(s => s.GetProperty("etiqueta").GetString()!.Contains("prueba.pdf"));
        check(read.Attachments[0].ExtractedText!.Contains("viernes") && model.Prompt.Contains("3 de 3") && model.Images.Count >= 2,
            "Lectura local: el flujo real incorpora texto y ambas páginas del PDF al contexto de Brisa");
        check(documentSource.GetProperty("texto").GetString()!.Contains("viernes"), "Lectura local: el documento actual conserva lugar en el contexto aunque exista historial previo");
        await using (var file = (await store.GetAttachmentAsync(user, entry.Attachments[0].Id))!.Content)
        { using var buffer = new MemoryStream(); await file.CopyToAsync(buffer); check(buffer.ToArray().SequenceEqual(original), "Lectura local: conserva exactamente los bytes del PDF original"); }
        var followup = await store.AddEntryAsync(user, "Volvé a mirar las páginas del PDF", []);
        await flow.GenerateAsync(user, followup.Id);
        check(model.Images.Count >= 2 && model.Prompt.Contains("3 de 3"), "Lectura local: una pregunta posterior recupera las páginas del PDF sin volver a subirlo");
        try { await store.SaveLocalExtractionAsync(other, entry.Attachments[0].Id, "otro texto", "otra lectura"); throw new Exception("Se alteró otra cuenta"); }
        catch (JournalInputException) { check(true, "Lectura local: una cuenta no puede leer ni escribir la extracción de otra"); }
        var broken = await store.AddEntryAsync(user, "Guardar sin analizar", [new("roto.pdf", "%PDF-roto"u8.ToArray())]);
        await flow.GenerateAsync(user, broken.Id, useAi: false);
        check((await store.GetEntryAsync(user, broken.Id))!.Attachments[0].ExtractedText is null,
            "Lectura local: Solo guardar no inicia PDF, video ni transcripción");
        var disabled = new LocalAttachmentProcessor(Path.Combine(root, "disabled-staging"), "missing-tools", Options.Create(new LocalAttachmentOptions()));
        var disabledFlow = new JournalConversationService(store, transcriber, provider.GetRequiredService<JournalMemoryService>(),
            new TestAi(model), new JournalConversationJobs(), localAttachments: new LocalAttachmentAnalysis(store, disabled, transcriber));
        await disabledFlow.GenerateAsync(user, broken.Id);
        check(!Directory.Exists(Path.Combine(root, "disabled-staging")), "Lectura local: con la función desactivada no se inicia ningún decodificador");
        model.External = true;
        await flow.GenerateAsync(user, broken.Id);
        check((await store.GetEntryAsync(user, broken.Id))!.Attachments[0].ExtractedText is null,
            "OpenAI: el nuevo procesamiento local permanece fuera del flujo externo, incluso con la función habilitada");
        model.External = false;
        if (codecs)
        {
            var video = await MakeVideoAsync(root, sound: true);
            var videoEntry = await store.AddEntryAsync(user, "Video sintético", [new("prueba.mp4", video)]);
            await flow.GenerateAsync(user, videoEntry.Id);
            var firstCalls = transcriber.Calls;
            await flow.GenerateAsync(user, videoEntry.Id);
            var cached = (await store.GetEntryAsync(user, videoEntry.Id))!.Attachments[0];
            check(firstCalls == 1 && transcriber.Calls == 1 && cached.ExtractedText!.Contains("hablantes no confirmados") && model.Images.Count >= 2,
                "Video local: fotogramas y voz entran al contexto, la transcripción se reutiliza y no se atribuye el hablante");
            var longVideo = await store.AddEntryAsync(user, "Video con transcripción extensa", [new("largo.mp4", video)]);
            var longTranscript = cached.ExtractedText + new string('x', 8000) + " FINAL-CONSERVADO";
            await store.SaveLocalExtractionAsync(user, longVideo.Attachments[0].Id, longTranscript, cached.ExtractionStatus);
            var followVideo = await store.AddEntryAsync(user, "Volvé a mirar el video", []);
            await flow.GenerateAsync(user, followVideo.Id);
            check((await store.GetEntryAsync(user, longVideo.Id))!.Attachments[0].ExtractedText == longTranscript && transcriber.Calls == firstCalls,
                "Video local: una pregunta posterior no trunca la transcripción original ni vuelve a transcribirla");
        }
    }

    private static async Task CodecsAsync(string root, LocalAttachmentProcessor processor, Action<bool, string> check)
    {
        var video = await MakeVideoAsync(root, sound: false);
        var prepared = await processor.PrepareAsync(new("silencioso.mp4", "video/mp4", new MemoryStream(video)));
        check(prepared.Audio is null && prepared.Images.Count == 2 && prepared.Coverage.Contains("Sin pista"), "Video local: un video sin audio se analiza sin inventar una transcripción");
        var source = Path.Combine(root, "source.mp4"); await File.WriteAllBytesAsync(source, video);
        foreach (var (extension, codec, container) in new[] { ("mov", "copy", "mov"), ("m4v", "copy", "mp4"), ("webm", "libvpx-vp9", "webm"),
            ("ogv", "libtheora", "ogg"), ("3gp", "mpeg4", "3gp"), ("mkv", "copy", "matroska"), ("avi", "mpeg4", "avi"), ("wmv", "wmv2", "asf") })
        {
            var target = Path.Combine(root, "converted." + extension);
            await RunAsync("ffmpeg", ["-v", "error", "-i", source, "-an", "-c:v", codec, "-threads", "1", "-f", container, target]);
            var result = await processor.PrepareAsync(new("video." + extension, "video/unknown", new MemoryStream(await File.ReadAllBytesAsync(target))));
            check(result.Images.Count == 2, "Video local: " + extension + " produce fotogramas JPEG etiquetados");
        }
        try { await processor.PrepareAsync(new("lista.mp4", "video/mp4", new MemoryStream("#EXTM3U\nfile:///private-file"u8.ToArray()))); throw new Exception("Lista de reproducción aceptada"); }
        catch (JournalInputException) { check(true, "Lectura local: rechaza listas de reproducción y referencias a otros archivos antes de ejecutar FFmpeg"); }
        var sound = Path.Combine(root, "tone.wav");
        await RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=330:duration=1", "-ac", "2", "-ar", "48000", sound]);
        foreach (var extension in new[] { "mp3", "m4a", "aac", "ogg", "opus", "flac", "weba", "wav" })
        {
            var target = Path.Combine(root, "converted." + extension);
            var args = new List<string> { "-v", "error", "-i", sound };
            if (extension == "weba") args.AddRange(["-c:a", "libopus", "-f", "webm"]);
            args.Add(target); await RunAsync("ffmpeg", args);
            var result = await processor.PrepareAsync(new("audio." + extension, "audio/unknown", new MemoryStream(await File.ReadAllBytesAsync(target))));
            check(result.Audio is { Length: > 44 } wav && BitConverter.ToInt32(wav, 24) == 16000 && BitConverter.ToInt16(wav, 22) == 1,
                "Audio local: " + extension + " se transforma en WAV mono de 16 kHz");
        }
    }

    private static async Task<byte[]> MakeVideoAsync(string root, bool sound)
    {
        var target = Path.Combine(root, Guid.NewGuid().ToString("N") + ".mp4");
        var args = new List<string> { "-v", "error", "-f", "lavfi", "-i", "color=c=red:s=320x240:d=2:r=5" };
        if (sound) args.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:duration=2"]);
        args.AddRange(["-c:v", "libx264", "-threads", "1", "-pix_fmt", "yuv420p"]);
        if (sound) args.AddRange(["-c:a", "aac", "-shortest"]);
        args.Add(target); await RunAsync("ffmpeg", args); return await File.ReadAllBytesAsync(target);
    }

    internal static async Task<string> RunAsync(string tool, IEnumerable<string> arguments)
    {
        var exe = Path.GetFullPath(Path.Combine("media-tools", tool + (OperatingSystem.IsWindows() ? ".exe" : "")));
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); if (process.ExitCode != 0) throw new Exception(await error); return await output; }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }

    private static byte[] Zip(params (string Name, string Content)[] files)
    {
        using var result = new MemoryStream();
        using (var zip = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files) { using var writer = new StreamWriter(zip.CreateEntry(file.Name).Open()); writer.Write(file.Content); }
        return result.ToArray();
    }
    private static byte[] Workbook() => Zip(
        ("[Content_Types].xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='xml' ContentType='application/xml'/><Override PartName='/xl/workbook.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml'/></Types>"),
        ("_rels/.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='xl/workbook.xml'/></Relationships>"),
        ("xl/workbook.xml", "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='Prueba' sheetId='1' r:id='rId1'/></sheets></workbook>"),
        ("xl/_rels/workbook.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet1.xml'/></Relationships>"),
        ("xl/worksheets/sheet1.xml", "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData><row r='1'><c r='A1' t='inlineStr'><is><t>reunion viernes cuaderno verde</t></is></c></row></sheetData></worksheet>"));

    private sealed class CaptureModel : IJournalModelClient
    {
        public bool External; public string Prompt = ""; public IReadOnlyList<JournalImage> Images = [];
        public bool IsConfigured => true; public string ProviderName => "Prueba"; public bool UsesExternalProvider => External;
        public Task<string> GenerateAsync(string instruction, string prompt, JsonElement schema, IReadOnlyList<JournalImage> images, CancellationToken ct)
        { Prompt = prompt; Images = images; return Task.FromResult("""{"title":"Lectura","message":"El cuaderno es verde y la reunión es el viernes.","themes":[],"openQuestions":[],"sessionTopics":["ordenar las preguntas"],"nextStep":""}"""); }
    }
    private sealed class TestAi(IJournalModelClient model) : JournalAiService(model, NullLogger.Instance);
    private sealed class FakeAudio : IAudioTranscriber
    {
        public int Calls; public bool IsConfigured => true;
        public Task<string> TranscribeAsync(AttachmentContent file, CancellationToken ct = default)
        { Calls++; return Task.FromResult("Mi cuaderno es verde y la reunion es el viernes."); }
    }
    private sealed class FakeMemory : IJournalMemoryAnalyzer
    {
        public bool UsesExternalProvider => false;
        public Task<IReadOnlyList<MemoryDigest>> IndexAsync(IReadOnlyList<TopicSource> sources, CancellationToken ct) => Task.FromResult<IReadOnlyList<MemoryDigest>>(sources.Select(s => new MemoryDigest(s.Id, s.Text[..Math.Min(400, s.Text.Length)], "documento", s.Text[..Math.Min(40, s.Text.Length)])).ToArray());
        public Task<IReadOnlyList<string>> RecallAsync(string conversation, IReadOnlyList<MemoryCard> memories, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static async Task LiveAsync(string root, LocalAttachmentProcessor processor, string baseUrl, Action<bool, string> check, string? onlyCase)
    {
        var settings = new OllamaOptions { BaseUrl = baseUrl, Model = "gemma4:12b", ContextLength = 8192, MaxOutputTokens = 700, TimeoutSeconds = 300 };
        if (!settings.IsValid()) throw new ArgumentException("La prueba requiere un Ollama local de loopback.");
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        using var gate = new LocalInferenceGate();
        var model = new OllamaJournalService(http, Options.Create(settings), gate, NullLogger<OllamaJournalService>.Instance);
        var transcriber = new OllamaAudioTranscriber(http, Options.Create(settings), gate, processor);
        var reportDirectory = Path.GetFullPath(".artifacts/local-attachments/live"); Directory.CreateDirectory(reportDirectory);
        if (onlyCase is null or "pdf-text")
        {
            var prepared = await processor.PrepareAsync(new("texto.pdf", "application/pdf", new MemoryStream(Pdf(false))));
            await Reply("pdf-text", "Respondé en una oración: ¿qué día es la reunión y de qué color es el cuaderno del PDF?",
                "texto.pdf", "application/pdf", prepared, text => text.Contains("viernes") && text.Contains("verde"));
        }
        if (onlyCase is null or "pdf-scan")
        {
            var prepared = await processor.PrepareAsync(new("escaneado.pdf", "application/pdf", new MemoryStream(Pdf(true))));
            await Reply("pdf-scan", "Describí únicamente la figura y su color en las páginas escaneadas. No es una metáfora ni un relato personal.",
                "escaneado.pdf", "application/pdf", prepared, text => text.Contains("roj") && (text.Contains("cuadrad") || text.Contains("rectángul")));
        }
        var speech = Path.GetFullPath(".artifacts/local-attachments/speech.wav");
        if (onlyCase is null or "audio")
        {
            if (!File.Exists(speech)) throw new Exception("Falta el WAV sintético: ejecutá scripts/New-LocalAttachmentTestAudio.ps1 con Windows PowerShell.");
            var mp3 = Path.Combine(root, "speech.mp3"); await RunAsync("ffmpeg", ["-v", "error", "-i", speech, mp3]);
            var watch = Stopwatch.StartNew();
            await using var stream = File.OpenRead(mp3);
            var transcript = await transcriber.TranscribeAsync(new("prueba.mp3", "audio/mpeg", stream));
            var success = transcript.Contains("verde", StringComparison.OrdinalIgnoreCase) && transcript.Contains("viernes", StringComparison.OrdinalIgnoreCase);
            await Record("audio", success, transcript, watch.Elapsed);
            check(success, $"Ollama real: transcripción de MP3 en español ({watch.Elapsed.TotalSeconds:0.0} s)");
        }
        if (onlyCase is null or "video")
        {
            if (!File.Exists(speech)) throw new Exception("Falta el WAV sintético para probar el video.");
            var videoPath = Path.Combine(root, "speech-video.mp4");
            await RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "color=c=red:s=320x240:d=8:r=5", "-i", speech,
                "-c:v", "libx264", "-threads", "1", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", videoPath]);
            await using var stream = File.OpenRead(videoPath);
            var prepared = await processor.PrepareAsync(new("video.mp4", "video/mp4", stream));
            await using var wave = new MemoryStream(prepared.Audio!);
            var transcript = await transcriber.TranscribeAsync(new("video.wav", "audio/wav", wave));
            prepared = prepared with { Text = "[Transcripción automática del video; hablantes no confirmados]\n" + transcript };
            await Reply("video", "Respondé brevemente: ¿qué color se ve en el video, de qué color es el cuaderno mencionado en el audio y qué día es la reunión?",
                "video.mp4", "video/mp4", prepared, text => text.Contains("roj") && text.Contains("verde") && text.Contains("viernes"));
        }

        async Task Reply(string name, string question, string fileName, string type, PreparedLocalAttachment prepared, Func<string, bool> expected)
        {
            var entryId = Guid.NewGuid(); var fileId = Guid.NewGuid();
            var attachment = new JournalAttachment(fileId, entryId, fileName, type, 100, prepared.Coverage, prepared.Text);
            var entry = new JournalEntry(entryId, question, DateTime.UtcNow, null, false, [attachment]);
            var context = new JournalContext(new("Prueba sintética", "", false), entry, [], [], [attachment],
                prepared.Images.Select(i => new JournalMediaFile(fileId, fileName, "image/jpeg", i.Content, i.SourcePart)).ToArray());
            var watch = Stopwatch.StartNew();
            var reply = await model.ReflectAsync(context, true);
            var success = expected(reply.Overview.ToLowerInvariant());
            await Record(name, success, reply.Overview, watch.Elapsed);
            check(success, $"Ollama real: {name}, respuesta basada en el contenido ({watch.Elapsed.TotalSeconds:0.0} s)");
        }
        Task Record(string name, bool success, string answer, TimeSpan elapsed) => File.WriteAllTextAsync(Path.Combine(reportDirectory, name + ".json"),
            JsonSerializer.Serialize(new { Model = settings.Model, BaseUrl = baseUrl, Success = success, Seconds = elapsed.TotalSeconds, Answer = answer,
                SyntheticDataOnly = true, VerifiedUtc = DateTime.UtcNow }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
