using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MiBitacora.Data;
using MiBitacora.Services;

internal static class ThoughtMapChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "mibitacora-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { await VerifyAsync(folder, check); }
        finally { Directory.Delete(folder, true); }
    }

    private static async Task VerifyAsync(string folder, Action<bool, string> check)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(TimeZoneInfo.FindSystemTimeZoneById("Argentina Standard Time"));
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"Data Source={folder}/test.db;Pooling=False;Foreign Keys=True"));
        services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton(new UploadFileStore(Path.Combine(folder, "uploads"), NullLogger<UploadFileStore>.Instance));
        services.AddScoped<IJournalStore, JournalStore>(); services.AddScoped<ThoughtMapService>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var store = scope.ServiceProvider.GetRequiredService<IJournalStore>();
        var maps = scope.ServiceProvider.GetRequiredService<ThoughtMapService>();
        var a = Guid.NewGuid().ToString(); var b = Guid.NewGuid().ToString();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            db.Users.Add(new() { Id = a, UserName = "map-a", SecurityStamp = Guid.NewGuid().ToString() });
            db.Users.Add(new() { Id = b, UserName = "map-b", SecurityStamp = Guid.NewGuid().ToString() });
            await db.SaveChangesAsync();
        }
        var empty = await maps.GetAsync(a);
        check(empty.Nodes.Count == 0 && empty.Links.Count == 0, "Mapa: una cuenta vacía no recibe conexiones ficticias");
        var foreign = await store.AddEntryAsync(b, "PRIVADO_DE_OTRA_CUENTA", []);
        var foreignNote = await store.SaveNoteAsync(b, null, "NOTITA_AJENA");
        var notebook = await store.SaveNotebookAsync(a, null, "Ideas creativas");
        var first = await store.AddEntryAsync(a, "Abrí el cuaderno para dibujar.", []);
        await store.SaveReflectionAsync(a, first.Id, Reply("Creatividad", "¿Qué querés crear?"));
        var reply = await store.AddEntryAsync(a, "Quiero volver a tocar la guitarra.", [], replyToEntryId: first.Id);
        var note = await store.SaveNoteAsync(a, null, "Mis palabras actuales en una notita.");
        var other = await store.AddEntryAsync(a, "Me anoté en un taller.", [], notebookId: notebook.Id);
        await store.SaveReflectionAsync(a, other.Id, Reply("  CREATIVÍDAD  ", "¿Qué te entusiasma?", [
            new(first.Id, first.CreatedUtc, "rótulo viejo", "CITA_ANTIGUA_NO_USAR"),
            new(first.Id, first.CreatedUtc, "documento", "", TopicSourceKind.Document),
            new(foreign.Id, foreign.CreatedUtc, "ETIQUETA_AJENA", "EXTRACTO_AJENO"),
            new(null, note.CreatedUtc, "notita", "CITA_VIEJA_NOTITA", TopicSourceKind.Note, note.Id),
            new(null, foreignNote.CreatedUtc, "NOTA_AJENA", "CITA_AJENA", TopicSourceKind.Note, foreignNote.Id),
            new(null, null, "perfil", "PERFIL_VIEJO", TopicSourceKind.Profile)
        ]));
        var map = await maps.GetAsync(a);
        var serialized = JsonSerializer.Serialize(map);
        check(!serialized.Contains("AJEN") && !serialized.Contains("OTRA_CUENTA") && !serialized.Contains("CITA_"),
            "Mapa: resuelve antecedentes contra originales de la cuenta y no expone IDs, etiquetas ni citas ajenas/viejas");
        check(map.Nodes.Count(n => n.Kind == "theme") == 1 && map.Links.Count(l => l.Kind == "theme") == 2,
            "Mapa: conecta títulos equivalentes por mayúsculas, espacios y acentos sin inventar equivalencia semántica");
        check(map.Links.Any(l => l.Kind == "context" && l.Source == E(first.Id) && l.Target == E(other.Id))
            && map.Links.Any(l => l.Kind == "reply" && l.Target == E(reply.Id)),
            "Mapa: conserva dirección y distingue antecedentes, preguntas y respuestas");
        check(map.Nodes.Any(n => n.Kind == "note" && n.Detail == note.Text) && map.Nodes.All(n => n.Id != "profile"),
            "Mapa: muestra notitas actuales y omite contexto personal borrado");
        check(map.Nodes.Where(n => n.Kind == "question").All(n => n.Url?.StartsWith("/entrada/", StringComparison.Ordinal) == true),
            "Mapa: las preguntas abren su original sin responder otra pregunta ni generar IA");
        await store.SaveReflectionAsync(a, first.Id, Reply("Creatividad", "¿Preferís otra actividad?"));
        map = await maps.GetAsync(a);
        check(map.Nodes.Any(n => n.Kind == "question" && n.Detail == "¿Qué querés crear?")
            && map.Nodes.Any(n => n.Detail == "¿Preferís otra actividad?"),
            "Mapa: regenerar una devolución conserva la pregunta histórica que abrió la rama");
        var ownDefault = await maps.GetAsync(a, notebook: "default");
        check(ownDefault.TotalEntries == 2 && ownDefault.Nodes.All(n => n.Id != E(other.Id)),
            "Mapa: el filtro de diario predeterminado excluye entradas de otros diarios");
        var focused = await maps.GetAsync(b, first.Id);
        check(focused.Focus is null && focused.Nodes.All(n => n.Id != E(first.Id)), "Mapa: un enlace con entrada ajena no cruza cuentas");
        var filtered = await maps.GetAsync(a, notebook: notebook.Id!.Value.ToString());
        check(filtered.TotalEntries == 1 && filtered.ShownEntries == 1 && filtered.Nodes.Any(n => n.Id == E(first.Id)),
            "Mapa: un diario conserva sus antecedentes originales de otros diarios de la misma cuenta");
        await store.DeleteNoteAsync(a, note.Id); await store.DeleteEntryAsync(a, first.Id);
        map = await maps.GetAsync(a);
        check(map.Nodes.All(n => n.Id != E(first.Id) && n.Detail != note.Text)
            && map.Links.All(l => map.Nodes.Any(n => n.Id == l.Source) && map.Nodes.Any(n => n.Id == l.Target)),
            "Mapa: borrar originales elimina sus nodos y evita flechas colgantes");

        var oldest = await store.AddEntryAsync(a, "PRIMERA_ETAPA", []);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var date = new DateTime(2020, 1, 1, 2, 30, 0, DateTimeKind.Utc);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE JournalEntries SET CreatedUtc={date} WHERE Id={oldest.Id}");
            for (var i = 0; i < ThoughtMapService.EntryLimit + 2; i++)
            {
                var id = Guid.NewGuid(); var created = DateTime.UtcNow.AddMinutes(i);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO JournalEntries (Id,UserId,Text,SearchText,CreatedUtc,IsDateOnly,ForSession) VALUES ({id},{a},'Paso de prueba','paso',{created},0,0)");
            }
        }
        map = await maps.GetAsync(a);
        check(map.Limited && map.ShownEntries == ThoughtMapService.EntryLimit && map.Nodes.All(n => n.Id != E(oldest.Id)),
            "Mapa: limita la carga del recorrido y comunica que hay más historia");
        focused = await maps.GetAsync(a, oldest.Id);
        check(focused.Focus == E(oldest.Id) && focused.Nodes.Any(n => n.Id == E(oldest.Id)) && focused.ShownEntries == ThoughtMapService.EntryLimit,
            "Mapa: se puede abrir una entrada antigua sin superar el límite de carga");
        var dates = await maps.GetAsync(a, from: new(2019, 12, 31), until: new(2019, 12, 31));
        check(dates.TotalEntries == 1 && dates.Nodes.Single(n => n.Kind == "entry").Id == E(oldest.Id),
            "Mapa: los filtros de fechas respetan el día local argentino, incluso junto a medianoche UTC");
        await using (var db = await factory.CreateDbContextAsync())
        {
            var corrupt = "{\"themes\":null,\"openQuestions\":null}";
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE JournalEntries SET ReflectionJson={corrupt} WHERE Id={oldest.Id}");
        }
        dates = await maps.GetAsync(a, from: new(2019, 12, 31), until: new(2019, 12, 31));
        check(dates.Nodes.Count == 1 && dates.Nodes[0].Detail == "PRIMERA_ETAPA",
            "Mapa: una devolución antigua ilegible no impide abrir el original");
        var demo = ThoughtMapDemo.Create();
        check(demo.Nodes.All(n => n.Url is null) && demo.Links.All(l => demo.Nodes.Any(n => n.Id == l.Source) && demo.Nodes.Any(n => n.Id == l.Target)),
            "Mapa: la muestra pública es ficticia, autocontenida y no enlaza entradas privadas");
    }
    private static string E(Guid id) => "entry-" + id.ToString("N");
    private static JournalReflection Reply(string theme, string question, IReadOnlyList<TopicEvidence>? sources = null) =>
        new("Una mirada", "Un momento para pensar.", [new(theme, "Una posibilidad.", null, null)], [question], [], null, false, 1, DateTime.UtcNow, sources);
}
