using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MiBitacora.Data;

namespace MiBitacora.Services;

public sealed class TherapyShareBuilder(ThoughtMapService maps, TimeZoneInfo timeZone)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int MaxSnapshotBytes = 24 * 1024 * 1024;

    internal async Task<PreparedTherapyShare> BuildAsync(ApplicationDbContext db, string userId, string title, DateTime now, CancellationToken ct)
    {
        title = title.Trim();
        if (title.Length is < 1 or > 100) throw new JournalInputException("Escribí un título de hasta 100 caracteres para esta versión.");
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct)) throw new JournalInputException("Tu cuenta ya no está disponible.");
        var characters = await db.JournalEntries.Where(e => e.UserId == userId).Select(e => (long)e.Text.Length
            + (e.ReflectionJson == null ? 0 : e.ReflectionJson.Length)
            + e.Attachments.Sum(a => (long)(a.ExtractedText == null ? 0 : a.ExtractedText.Length))).SumAsync(ct);
        if (characters > MaxSnapshotBytes) throw new JournalInputException("El recorrido supera el tamaño disponible para una versión compartida. Contactanos desde Ayuda para preparar una copia completa.");
        var rows = await db.JournalEntries.AsNoTracking().Where(e => e.UserId == userId)
            .OrderBy(e => e.CreatedUtc).ThenBy(e => e.Id).Include(e => e.Notebook).Include(e => e.Attachments).ToArrayAsync(ct);
        var notes = await db.JournalNotes.AsNoTracking().Where(n => n.UserId == userId).OrderBy(n => n.CreatedUtc).ThenBy(n => n.Id).ToArrayAsync(ct);
        var context = await db.JournalProfiles.AsNoTracking().Where(p => p.UserId == userId).Select(p => p.PersonalContext).SingleOrDefaultAsync(ct) ?? "";
        if (rows.Length == 0 && notes.Length == 0 && context.Length == 0) throw new JournalInputException("Todavía no hay registros para compartir.");
        var saved = await db.JournalTopicOverviews.AsNoTracking().SingleOrDefaultAsync(t => t.UserId == userId, ct);
        var sourceJson = JsonSerializer.Serialize(new {
            title, entries = rows.Select(e => new { e.Id, e.Text, e.CreatedUtc, e.ForSession, e.ReflectionJson, e.ReplyToEntryId, e.ReplyToQuestion,
                Notebook = e.Notebook?.Name, Files = e.Attachments.OrderBy(a => a.Position).Select(a => new { a.Id, a.FileName, a.ExtractedText, a.ExtractionStatus }) }),
            notes = notes.Select(n => new { n.Id, n.Text, n.CreatedUtc, n.ForSession }), context, report = saved?.SnapshotJson, fingerprint = saved?.Fingerprint
        }, Json);
        if (Encoding.UTF8.GetByteCount(sourceJson) > MaxSnapshotBytes) throw new JournalInputException("El recorrido supera el tamaño disponible para una versión compartida. Contactanos desde Ayuda para preparar una copia completa.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceJson)));
        var map = await maps.ReadAsync(db, userId, complete: true, ct: ct);
        var originals = map.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        foreach (var note in notes) originals.TryAdd(NoteKey(note.Id), new(NoteKey(note.Id), "note", Short(note.Text), note.Text, note.CreatedUtc));
        if (!string.IsNullOrWhiteSpace(context)) originals.TryAdd("profile", new("profile", "note", "Mi contexto personal", context));
        // Public IDs belong only to this snapshot. Account, entry, note and attachment IDs never leave it.
        var ids = originals.Keys.Select((id, i) => (id, publicId: "p" + i.ToString("x"))).ToDictionary(p => p.id, p => p.publicId);
        var entryIds = rows.ToDictionary(e => e.Id, e => ids[ThoughtMapService.EntryId(e.Id)]);
        string? EntryFor(ThoughtNode node)
        {
            if (node.Kind == "entry") return ids[node.Id];
            if (node.Url?.StartsWith("/entrada/", StringComparison.Ordinal) == true && node.Url.Length >= 45
                && Guid.TryParse(node.Url.AsSpan(9, 36), out var id) && entryIds.TryGetValue(id, out var publicId)) return publicId;
            return null;
        }
        var publicMap = new ThoughtMapData(originals.Values.Select(n => n with { Id = ids[n.Id], Url = null, SharedEntryId = EntryFor(n) }).ToArray(),
            map.Links.Select(l => l with { Source = ids[l.Source], Target = ids[l.Target] }).ToArray(), rows.Length, rows.Length, false);
        var entries = rows.Select(row => new SharedEntry(entryIds[row.Id], row.Text, row.CreatedUtc, row.Notebook?.Name ?? "Mi diario", row.ForSession,
            row.Attachments.OrderBy(a => a.Position).Select(a => new SharedDocument(a.FileName, a.ExtractedText, a.ExtractionStatus)).ToArray(), Reflection(row.ReflectionJson))).ToArray();
        var sharedNotes = notes.Select(n => new SharedNote(ids[NoteKey(n.Id)], n.Text, n.CreatedUtc, n.ForSession)).ToArray();
        SharedReport? report = null;
        JournalTopicSnapshot? stored = null;
        try { if (saved is not null) stored = JsonSerializer.Deserialize<JournalTopicSnapshot>(saved.SnapshotJson, Json); } catch (JsonException) { }
        var outdated = false;
        if (stored?.Report is { } reading)
        {
            var corpus = await JournalCorpusReader.ReadAsync(db, userId, ct: ct);
            outdated = saved!.Fingerprint != JournalReportService.Fingerprint(corpus);
            if (!outdated)
            {
                SharedEvidence? Evidence(TopicEvidence e)
                {
                    string? key = null; string text = "";
                    if (e.Kind == TopicSourceKind.Profile && ids.ContainsKey("profile")) { key = "profile"; text = context; }
                    else if (e.NoteId is { } nid && notes.FirstOrDefault(n => n.Id == nid) is { } note) { key = NoteKey(nid); text = note.Text; }
                    else if (e.EntryId is { } eid && rows.FirstOrDefault(r => r.Id == eid) is { } entry)
                    { key = ThoughtMapService.EntryId(eid); text = entry.Text + "\n" + string.Join('\n', entry.Attachments.Select(a => a.ExtractedText)); }
                    if (key is null || string.IsNullOrWhiteSpace(e.Excerpt) || !text.Contains(e.Excerpt, StringComparison.Ordinal)) return null;
                    return new(ids[key], originals[key].Label, e.Excerpt, e.CreatedUtc);
                }
                report = new(stored.GeneratedUtc, reading.Sections.Select((s, i) => new SharedReportSection("section-" + i, s.Title, s.Introduction,
                    s.Points.Select(p => new SharedReportPoint(p.Kind, p.Title, p.Text, p.Question,
                        p.Evidence.Select(Evidence).OfType<SharedEvidence>().ToArray())).ToArray())).ToArray());
            }
        }
        var themes = publicMap.Nodes.Where(n => n.Kind == "theme").Select(n => new SharedThemeSummary(n.Id, n.Label,
            publicMap.Links.Count(l => l.Target == n.Id && l.Kind == "theme"))).OrderByDescending(t => t.Entries).ThenBy(t => t.Title).ToArray();
        var activity = entries.GroupBy(e => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(e.Date, DateTimeKind.Utc), timeZone).ToString("yyyy-MM"))
            .OrderBy(g => g.Key).Select(g => new SharedActivity(g.Key, g.Count())).ToArray();
        var snapshot = new SharedTherapySnapshot(1, title, now, publicMap, entries, sharedNotes, context, report, outdated, activity, themes);
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(snapshot, Json)) > MaxSnapshotBytes)
            throw new JournalInputException("La versión completa supera el tamaño disponible. Contactanos desde Ayuda para preparar una copia completa.");
        return new(userId, fingerprint, snapshot);
    }

    private static string NoteKey(Guid id) => "note-" + id.ToString("N");
    private static string Short(string text) => text.Length <= 66 ? text : text[..66] + "…";
    private static SharedReflection? Reflection(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try {
            var r = JsonSerializer.Deserialize<JournalReflection>(json, Json);
            return r is not { IsDemo: false, Themes: not null, OpenQuestions: not null } ? null : new(r.Title, r.Overview, r.GeneratedUtc,
                r.Themes.Select(t => new SharedTheme(t.Title, t.Detail, t.Quote)).ToArray(), r.OpenQuestions, r.SessionTopics ?? [], r.NextStep, r.LimitedMemory);
        } catch (JsonException) { return null; }
    }
}
