using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MiBitacora.Data;
using MiBitacora.Services;

internal static class TherapyShareChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "mibitacora-sharing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { await VerifyAsync(folder, check); } finally { Directory.Delete(folder, true); }
    }
    private static async Task VerifyAsync(string folder, Action<bool, string> check)
    {
        var clock = new ShareClock(DateTimeOffset.UtcNow);
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(TimeZoneInfo.FindSystemTimeZoneById("Argentina Standard Time"));
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"Data Source={folder}/test.db;Pooling=False;Foreign Keys=True"));
        services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(folder, "keys"))).SetApplicationName("sharing-tests");
        services.AddSingleton(new UploadFileStore(Path.Combine(folder, "uploads"), NullLogger<UploadFileStore>.Instance));
        services.AddScoped<IJournalStore, JournalStore>(); services.AddScoped<ThoughtMapService>(); services.AddScoped<TherapyShareBuilder>(); services.AddScoped<TherapyShareService>();
        await using var provider = services.BuildServiceProvider(); await using var scope = provider.CreateAsyncScope();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var store = scope.ServiceProvider.GetRequiredService<IJournalStore>(); var shares = scope.ServiceProvider.GetRequiredService<TherapyShareService>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var a = Guid.NewGuid().ToString(); var b = Guid.NewGuid().ToString(); var empty = Guid.NewGuid().ToString();
        var legacy = Guid.NewGuid(); var originalText = "REGISTRO_A: hoy retomé mi cuaderno.";
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261117041858_PreserveBillingAfterAccountDeletion");
            foreach (var id in new[] { a, b, empty }) db.Users.Add(new() { Id = id, UserName = id, SecurityStamp = Guid.NewGuid().ToString() });
            await db.SaveChangesAsync(); var date = clock.GetUtcNow().UtcDateTime.AddDays(-60);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO JournalEntries (Id,UserId,Text,SearchText,CreatedUtc,IsDateOnly,ForSession) VALUES ({legacy},{a},{originalText},'registro',{date},0,1)");
            await db.Database.MigrateAsync();
        }
        check((await store.GetEntryAsync(a, legacy))?.Text == originalText, "Compartir: la migración aditiva conserva los registros existentes");
        await Reject(() => shares.PrepareAsync(empty, "Vacío"), "no crea un recorrido ficticio para cuentas vacías");
        var foreign = await store.AddEntryAsync(b, "PRIVADO_B_NO_COMPARTIR", []);
        var foreignNote = await store.SaveNoteAsync(b, null, "NOTA_B_NO_COMPARTIR");
        await store.SaveProfileAsync(a, new("NOMBRE_INTERNO_NO_PUBLICAR", "CONTEXTO_A_PARA_COMPARTIR", false));
        var note = await store.SaveNoteAsync(a, null, "NOTA_A_PARA_COMPARTIR");
        await store.SetNoteForSessionAsync(a, note.Id, true);
        await store.SaveReflectionAsync(a, legacy, new("Una posibilidad", "LECTURA_A_GUARDADA", [new("Crear", "Una posibilidad para conversar.", null, null)], ["¿Qué querés retomar?"], ["Llevar el cuaderno a sesión."], null, false, 1, clock.GetUtcNow().UtcDateTime, [new(foreign.Id, foreign.CreatedUtc, "ETIQUETA_B", "CITA_B") ]));
        var document = await store.AddEntryAsync(a, "Un registro con documento.", [new("notas-ficticias.txt", Encoding.UTF8.GetBytes("TEXTO_DOCUMENTO_A"))]);
        await using (var db = await factory.CreateDbContextAsync())
        {
            for (var i = 0; i < 123; i++) {
                var id = Guid.NewGuid(); var date = clock.GetUtcNow().UtcDateTime.AddHours(-i - 1);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO JournalEntries (Id,UserId,Text,SearchText,CreatedUtc,IsDateOnly,ForSession) VALUES ({id},{a},'Registro sintético','registro',{date},0,0)");
            }
            var corpus = await JournalCorpusReader.ReadAsync(db, a);
            var snapshot = new JournalTopicSnapshot(clock.GetUtcNow().UtcDateTime, 125, 125, 1, 0, [], 1, true, null,
                new(1, corpus.FromUtc, corpus.ThroughUtc, clock.GetUtcNow().UtcDateTime.AddDays(-30), 124,
                    [new("panorama", "Mi momento reciente", "Lectura ficticia de prueba.", [new("registro", "Un registro", "Una lectura basada en mis palabras.", "¿Qué te gustaría conversar?", [
                        new(legacy, null, "Fuente propia", originalText), new(foreign.Id, null, "Fuente ajena", "PRIVADO_B_NO_COMPARTIR"),
                        new(null, null, "Nota ajena", "NOTA_B_NO_COMPARTIR", TopicSourceKind.Note, foreignNote.Id)
                    ])])]));
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)); var fingerprint = JournalReportService.Fingerprint(corpus);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO JournalTopicOverviews (UserId,Fingerprint,SnapshotJson) VALUES ({a},{fingerprint},{json})");
        }
        var preview = await shares.PrepareAsync(a, "Para mi sesión");
        check(preview.Snapshot.Entries.Count == 125 && preview.Snapshot.Map.TotalEntries == 125 && !preview.Snapshot.Map.Limited,
            "Compartir: incluye todo el recorrido y supera el límite de 120 entradas sin truncar silenciosamente");
        check(preview.Snapshot.Notes.Count == 1 && preview.Snapshot.PersonalContext == "CONTEXTO_A_PARA_COMPARTIR"
            && preview.Snapshot.Entries.Any(e => e.Documents.Any(d => d.Text?.Contains("TEXTO_DOCUMENTO_A") == true)),
            "Compartir: incluye notas, contexto y textos disponibles de archivos sin publicar sus binarios");
        var serialized = JsonSerializer.Serialize(preview.Snapshot);
        check(!serialized.Contains("PRIVADO_B") && !serialized.Contains("NOTA_B") && !serialized.Contains("ETIQUETA_B")
            && !serialized.Contains("NOMBRE_INTERNO_NO_PUBLICAR") && !serialized.Contains(a) && !serialized.Contains(legacy.ToString(), StringComparison.OrdinalIgnoreCase)
            && !serialized.Contains(legacy.ToString("N"), StringComparison.OrdinalIgnoreCase) && !serialized.Contains("/entrada/") && !serialized.Contains("/adjuntos/"),
            "Compartir: la copia no expone otra cuenta, nombre interno, IDs privados ni rutas autenticadas");
        check(preview.Snapshot.Map.Nodes.All(n => n.Url is null) && preview.Snapshot.Report?.Sections[0].Points[0].Evidence.Count == 1,
            "Compartir: las fuentes del informe se resuelven dentro de la copia y se descartan referencias ajenas");
        check(preview.Snapshot.Map.Nodes.Any(n => n.Kind == "note" && n.Detail == note.Text)
            && preview.Snapshot.Map.Nodes.Where(n => n.SharedEntryId is not null).All(n => preview.Snapshot.Entries.Any(e => e.Id == n.SharedEntryId)),
            "Compartir: notas sin antecedentes y enlaces a originales siguen siendo navegables dentro de la copia");
        await Reject(() => shares.PublishAsync(a, preview, false), "exige confirmación explícita antes de publicar");
        await Reject(() => shares.PublishAsync(b, preview, true), "rechaza una vista previa de otra cuenta");
        await Reject(() => shares.PublishAsync(a, preview, true, 366), "valida el vencimiento");
        var first = await shares.PublishAsync(a, preview, true);
        check(first.Token?.Length == 43 && (await shares.OpenAsync(first.Token))?.Snapshot.Entries.Count == 125,
            "Compartir: crea un enlace aleatorio de 256 bits y una versión pública legible sin sesión");
        await using (var db = await factory.CreateDbContextAsync()) {
            var cipher = await db.Database.SqlQuery<string>($"SELECT ProtectedSnapshot AS Value FROM TherapyShares WHERE Id={first.Id}").SingleAsync();
            var protectedToken = await db.Database.SqlQuery<string>($"SELECT ProtectedToken AS Value FROM TherapyShares WHERE Id={first.Id}").SingleAsync();
            check(!cipher.Contains(originalText) && !protectedToken.Contains(first.Token!), "Compartir: guarda contenido y enlace cifrados, con hash de búsqueda separado");
        }
        check(await shares.OpenAsync("incorrecto") is null && await shares.OpenAsync(first.Token + "\n") is null
            && await shares.GetRevisionAsync(first.Token + "\n") is null, "Compartir: enlaces malformados no revelan una versión");
        check((await shares.ListAsync(b)).Count == 0 && (await shares.ListAsync(a)).Single().Token == first.Token,
            "Compartir: sólo el propietario recupera y administra sus enlaces");
        var added = await store.AddEntryAsync(a, "REGISTRO_NUEVO_POSTERIOR", []);
        check((await shares.OpenAsync(first.Token))!.Snapshot.Entries.Count == 125, "Compartir: las nuevas entradas no se publican automáticamente");
        await Reject(() => shares.PublishAsync(a, preview, true, replaceId: first.Id, expectedRevision: first.Revision), "rechaza publicar una previa que quedó desactualizada");
        var next = await shares.PrepareAsync(a, "Para mi sesión");
        check(next.Snapshot.Report is null && next.Snapshot.PreviousReportOutdated, "Compartir: un informe integral desactualizado no se presenta como lectura de la nueva versión");
        clock.Advance(TimeSpan.FromDays(1));
        var updated = await shares.PublishAsync(a, next, true, -1, replaceId: first.Id, expectedRevision: first.Revision);
        check(updated.Token == first.Token && updated.Revision == 2 && (await shares.OpenAsync(first.Token))!.Snapshot.Entries.Count == 126,
            "Compartir: la actualización aprobada conserva el enlace y reemplaza la versión completa");
        check(updated.ExpiresUtc == first.ExpiresUtc, "Compartir: actualizar puede conservar el vencimiento original sin extenderlo silenciosamente");
        await Reject(() => shares.PublishAsync(a, next, true, replaceId: first.Id, expectedRevision: 1), "detecta actualizaciones simultáneas con una revisión vieja");
        var bPreview = await shares.PrepareAsync(b, "Otra cuenta");
        await Reject(() => shares.PublishAsync(b, bPreview, true, replaceId: first.Id, expectedRevision: 2), "no actualiza enlaces ajenos");
        await Reject(() => shares.RevokeAsync(b, first.Id), "no revoca enlaces ajenos");
        var forged = next with { Snapshot = next.Snapshot with { Entries = [new("fake", "CONTENIDO_INYECTADO", DateTime.UtcNow, "Falso", false, [], null)] } };
        var untouched = await shares.PublishAsync(a, forged, true);
        check(!(await shares.OpenAsync(untouched.Token))!.Snapshot.Entries.Any(e => e.Text == "CONTENIDO_INYECTADO"),
            "Compartir: reconstruye en servidor el material aprobado y no confía en un contenido suministrado por el cliente");
        await store.DeleteNoteAsync(a, note.Id);
        check((await shares.OpenAsync(first.Token))!.Snapshot.Notes.Count == 1, "Compartir: la copia fija conserva lo aprobado hasta actualizar o revocar");
        await shares.RevokeAsync(a, first.Id);
        check(await shares.OpenAsync(first.Token) is null && await shares.GetRevisionAsync(first.Token!) is null,
            "Compartir: la revocación bloquea nuevas aperturas y las comprobaciones de vistas abiertas");
        await using (var db = await factory.CreateDbContextAsync()) {
            check(await db.Database.SqlQuery<string>($"SELECT ProtectedSnapshot AS Value FROM TherapyShares WHERE Id={first.Id}").SingleAsync() == "",
                "Compartir: revocar elimina el contenido de la copia activa");
        }
        next = await shares.PrepareAsync(a, "Otra versión");
        var permanent = await shares.PublishAsync(a, next, true, 0);
        var active = (await shares.ListAsync(a)).Count(l => l.Token is not null);
        for (var i = active; i < TherapyShareService.MaxActiveLinks; i++) await shares.PublishAsync(a, next, true);
        await Reject(() => shares.PublishAsync(a, next, true), "limita a cinco enlaces activos por cuenta");
        clock.Advance(TimeSpan.FromDays(31));
        check(await shares.OpenAsync(untouched.Token) is null && await shares.GetRevisionAsync(untouched.Token!) is null
            && await shares.OpenAsync(permanent.Token) is not null, "Compartir: respeta vencimiento y opción explícita sin vencimiento");
        var user = (await users.FindByIdAsync(a))!;
        var deleted = await users.DeleteAsync(user);
        check(deleted.Succeeded && await shares.OpenAsync(permanent.Token) is null && (await shares.ListAsync(a)).Count == 0,
            "Compartir: borrar la cuenta elimina en cascada todas sus versiones compartidas");

        async Task Reject(Func<Task> action, string label) { var rejected = false; try { await action(); } catch (JournalInputException) { rejected = true; } check(rejected, "Compartir: " + label); }
    }
    private sealed class ShareClock(DateTimeOffset now) : TimeProvider { private DateTimeOffset _now = now; public override DateTimeOffset GetUtcNow() => _now; public void Advance(TimeSpan time) => _now += time; }
}
