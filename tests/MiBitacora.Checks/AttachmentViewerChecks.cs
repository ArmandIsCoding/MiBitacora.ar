using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MiBitacora.Components.Shared;
using MiBitacora.Data;
using MiBitacora.Services;

internal static class AttachmentViewerChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "mibitacora-viewer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await VerifyAsync(root, check); }
        finally { Directory.Delete(root, true); }
    }

    private static async Task VerifyAsync(string root, Action<bool, string> check)
    {
        var services = new ServiceCollection().AddLogging().AddOptions();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"Data Source={root}/test.db;Pooling=False;Foreign Keys=True"));
        services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton(new UploadFileStore(Path.Combine(root, "uploads"), NullLogger<UploadFileStore>.Instance));
        services.AddScoped<IJournalStore, JournalStore>();
        services.AddSingleton<IJSRuntime, UnusedJavaScript>();
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var store = provider.GetRequiredService<IJournalStore>();
        var a = Guid.NewGuid().ToString(); var b = Guid.NewGuid().ToString();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            db.Users.AddRange(new ApplicationUser { Id = a, UserName = "viewer-a" }, new ApplicationUser { Id = b, UserName = "viewer-b" });
            await db.SaveChangesAsync();
        }
        var wave = Wave();
        var photos = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "red-square.png"));
        var entry = await store.AddEntryAsync(a, "Adjuntos sintéticos", [new("foto.png", photos), new("audio.wav", wave),
            new("pelicula.mp4", new byte[] { 0, 0, 0, 24 }.Concat("ftypisom0000isommp42"u8.ToArray()).ToArray()),
            new("documento.pdf", "%PDF-1.4\n% fixture"u8.ToArray()), new("apunte.txt", Encoding.UTF8.GetBytes("Texto <script>alert('no ejecutar')</script>")),
            new("otro.zip", "PK unknown"u8.ToArray())]);
        var expected = new[] { AttachmentViewKind.Image, AttachmentViewKind.Audio, AttachmentViewKind.Video, AttachmentViewKind.Pdf, AttachmentViewKind.Text, AttachmentViewKind.File };
        check(entry.Attachments.Select(AttachmentPresentation.Kind).SequenceEqual(expected), "Visor: distingue fotos, audio, video, PDF, documentos y otros archivos");
        var audio = entry.Attachments[1];
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE JournalAttachments SET ContentType='application/octet-stream' WHERE Id={audio.Id}");
        var legacy = (await store.GetEntryAsync(a, entry.Id))!.Attachments[1];
        check(AttachmentPresentation.Kind(legacy) == AttachmentViewKind.Audio,
            "Visor: reconoce audios anteriores guardados con metadatos genéricos");
        var preview = await store.GetAttachmentPreviewAsync(a, audio.Id);
        check(preview?.ContentType == "audio/wav" && preview.Content.CanSeek && preview.Content.Position == 0,
            "Visor: verifica el formato real y deja el stream preparado para reproducir y buscar");
        if (preview is not null) await preview.Content.DisposeAsync();
        check(await store.GetAttachmentPreviewAsync(b, audio.Id) is null, "Visor: no permite reproducir medios de otra cuenta");
        var forged = await store.AddEntryAsync(a, "Archivo mal rotulado", [new("falso.mp3", "<html><script>alert(1)</script></html>"u8.ToArray())]);
        check(await store.GetAttachmentPreviewAsync(a, forged.Attachments[0].Id) is null, "Visor: una extensión de audio no permite servir HTML activo");

        var own = User(a); var foreign = User(b);
        var response = await Execute(own, audio.Id, "GET", "bytes=0-1");
        check(response.Response.StatusCode == 206 && response.Response.ContentType == "audio/wav" &&
            response.Response.Headers.ContentRange.ToString() == $"bytes 0-1/{wave.Length}" && ((MemoryStream)response.Response.Body).ToArray().SequenceEqual(wave[..2]),
            "Visor HTTP: entrega rangos parciales para Safari y avance del reproductor");
        check(response.Response.Headers.ContentDisposition == "inline" && response.Response.Headers.XContentTypeOptions == "nosniff" &&
            response.Response.Headers.CacheControl == "private, no-store" && response.Response.Headers.ContentSecurityPolicy.ToString().Contains("sandbox"),
            "Visor HTTP: muestra en línea con privacidad y aislamiento de contenido");
        var head = await Execute(own, audio.Id, "HEAD");
        check(head.Response.StatusCode == 200 && head.Response.ContentLength == wave.Length && head.Response.Body.Length == 0,
            "Visor HTTP: HEAD entrega metadatos sin descargar el cuerpo");
        check((await Execute(own, audio.Id, "GET", "bytes=9999999-")).Response.StatusCode == 416,
            "Visor HTTP: rechaza rangos fuera del archivo");
        check((await Execute(foreign, audio.Id, "GET")).Response.StatusCode == 404 &&
            (await Execute(own, Guid.NewGuid(), "GET")).Response.StatusCode == 404,
            "Visor HTTP: adjuntos ajenos e inexistentes son igualmente inaccesibles");
        check((await Execute(new ClaimsPrincipal(), audio.Id, "GET")).Response.StatusCode == 401,
            "Visor HTTP: exige una sesión autenticada");
        var download = Http(own, "GET");
        await (await AttachmentEndpoints.DownloadAsync(audio.Id, own, store, download, default)).ExecuteAsync(download);
        check(download.Response.Headers.ContentDisposition.ToString().StartsWith("attachment;") &&
            ((MemoryStream)download.Response.Body).ToArray().SequenceEqual(wave), "Visor: descargar sigue siendo una acción explícita que conserva el original");

        await using (var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>()))
        {
            var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<AttachmentGallery>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Files"] = entry.Attachments }))).ToHtmlString());
            check(html.Contains("Ver foto:") && html.Contains("Escuchar audio:") && html.Contains("Ver video:") &&
                html.Contains("aria-haspopup=\"dialog\"") && !html.Contains(" download") && !html.Contains("<script>"),
                "Visor: las tarjetas abren un diálogo accesible y no fuerzan descargas");
        }

        DefaultHttpContext Http(ClaimsPrincipal user, string method)
        {
            var http = new DefaultHttpContext { RequestServices = provider, User = user };
            http.Request.Method = method;
            http.Response.Body = new MemoryStream();
            return http;
        }
        async Task<DefaultHttpContext> Execute(ClaimsPrincipal user, Guid id, string method, string? range = null)
        {
            var http = Http(user, method);
            if (range is not null) http.Request.Headers.Range = range;
            await (await AttachmentEndpoints.PreviewAsync(id, user, store, http, default)).ExecuteAsync(http);
            return http;
        }
    }

    internal static byte[] Wave()
    {
        const int rate = 8000, samples = rate * 2;
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++) writer.Write((short)(1000 * Math.Sin(2 * Math.PI * 440 * i / rate)));
        return buffer.ToArray();
    }
    private static ClaimsPrincipal User(string id) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));
    private sealed class UnusedJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
    }
}
