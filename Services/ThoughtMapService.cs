using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MiBitacora.Data;

namespace MiBitacora.Services;

public sealed record ThoughtNode(string Id, string Kind, string Label, string Detail,
    DateTime? Date = null, string? Notebook = null, string? Url = null);
public sealed record ThoughtLink(string Source, string Target, string Kind, string Label);
public sealed record ThoughtMapData(IReadOnlyList<ThoughtNode> Nodes, IReadOnlyList<ThoughtLink> Links,
    int TotalEntries, int ShownEntries, bool Limited, string? Focus = null);

/// <summary>A view of saved relationships, never a new AI inference or an entitlement.</summary>
public sealed class ThoughtMapService(IDbContextFactory<ApplicationDbContext> factory, TimeZoneInfo timeZone)
{
    public const int EntryLimit = 120;
    public const int ReferenceLimit = 80;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ThoughtMapData> GetAsync(string userId, Guid? focus = null,
        string? notebook = null, DateOnly? from = null, DateOnly? until = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Every query, including references from stored AI output, is scoped to the account.
        var owned = db.JournalEntries.AsNoTracking().Where(e => e.UserId == userId);
        var query = owned;
        if (notebook == "default") query = query.Where(e => e.NotebookId == null);
        else if (Guid.TryParse(notebook, out var notebookId)) query = query.Where(e => e.NotebookId == notebookId);
        if (from is { } start) { var date = TimeZoneInfo.ConvertTimeToUtc(start.ToDateTime(TimeOnly.MinValue), timeZone); query = query.Where(e => e.CreatedUtc >= date); }
        if (until is { } end) { var date = TimeZoneInfo.ConvertTimeToUtc(end.ToDateTime(TimeOnly.MaxValue), timeZone); query = query.Where(e => e.CreatedUtc <= date); }
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(e => e.CreatedUtc).ThenBy(e => e.Id).Take(EntryLimit)
            .Include(e => e.Notebook).ToListAsync(ct);
        // A link from an old reflection must still open that entry's part of the map.
        if (focus is { } focusId && rows.All(e => e.Id != focusId))
        {
            var focused = await query.Include(e => e.Notebook).SingleOrDefaultAsync(e => e.Id == focusId, ct);
            if (focused is not null) { if (rows.Count == EntryLimit) rows.RemoveAt(rows.Count - 1); rows.Add(focused); }
        }
        var selectedCount = rows.Count;
        var reflections = rows.ToDictionary(e => e.Id, e => ReadReflection(e.ReflectionJson));
        var referenceIds = rows.SelectMany(e => (reflections[e.Id]?.ContextSources ?? [])
                .Where(s => s.EntryId.HasValue).Select(s => s.EntryId!.Value)
                .Concat(e.ReplyToEntryId is { } parent ? [parent] : []))
            .Where(id => rows.All(e => e.Id != id)).Distinct().ToArray();
        // Bring back actual originals (not the old quoted snippets) and omit deleted/foreign sources.
        var references = await owned.Where(e => referenceIds.Contains(e.Id)).OrderByDescending(e => e.CreatedUtc)
            .ThenBy(e => e.Id).Take(ReferenceLimit).Include(e => e.Notebook).ToListAsync(ct);
        var limited = total > selectedCount || references.Count < referenceIds.Length;
        rows.AddRange(references);
        foreach (var row in references) reflections[row.Id] = ReadReflection(row.ReflectionJson);
        var allIds = rows.Select(e => e.Id).ToHashSet();
        var noteIds = reflections.Values.SelectMany(r => r?.ContextSources ?? [])
            .Where(s => s.NoteId.HasValue).Select(s => s.NoteId!.Value).Distinct().ToArray();
        var notes = await db.JournalNotes.AsNoTracking().Where(n => n.UserId == userId && noteIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, ct);
        var profile = await db.JournalProfiles.AsNoTracking().Where(p => p.UserId == userId)
            .Select(p => p.PersonalContext).SingleOrDefaultAsync(ct);
        var nodes = new Dictionary<string, ThoughtNode>(StringComparer.Ordinal);
        var links = new Dictionary<string, ThoughtLink>(StringComparer.Ordinal);
        void Link(string source, string target, string kind, string label)
        {
            if (source != target) links.TryAdd($"{source}|{target}|{kind}", new(source, target, kind, label));
        }
        foreach (var row in rows)
        {
            var id = EntryId(row.Id);
            var reflection = reflections[row.Id];
            var text = string.IsNullOrWhiteSpace(row.Text) ? "Entrada con archivos adjuntos" : row.Text;
            nodes[id] = new(id, "entry", Clip(text, 66), Clip(text, JournalStore.MaxTextLength), row.CreatedUtc,
                row.Notebook?.Name ?? "Mi diario", $"/entrada/{row.Id:D}?origen=mapa");
            // Demo reflections aren't a personal analysis and must not become inferred themes.
            if (reflection is null || reflection.IsDemo) continue;
            foreach (var theme in reflection.Themes.Take(8).Where(t => !string.IsNullOrWhiteSpace(t.Title)))
            {
                var themeId = "theme-" + Key(theme.Title);
                nodes.TryAdd(themeId, new(themeId, "theme", Clip(theme.Title, 100),
                    "Un tema propuesto por Brisa en tus devoluciones. Explorá las entradas conectadas para ver qué significa en cada momento."));
                Link(id, themeId, "theme", "Brisa propuso este tema");
            }
            foreach (var question in reflection.OpenQuestions.Take(4).Where(q => !string.IsNullOrWhiteSpace(q)))
            {
                var qid = QuestionId(row.Id, question);
                nodes[qid] = new(qid, "question", Clip(question, 120), Clip(question, 1200), row.CreatedUtc,
                    row.Notebook?.Name ?? "Mi diario", $"/entrada/{row.Id:D}?origen=mapa");
                Link(id, qid, "question", "Brisa dejó esta pregunta");
            }
        }
        foreach (var row in rows)
        {
            var id = EntryId(row.Id);
            if (row.ReplyToEntryId is { } parent && allIds.Contains(parent))
            {
                var source = EntryId(parent);
                if (!string.IsNullOrWhiteSpace(row.ReplyToQuestion))
                {
                    // Keep the question actually answered, even if the reflection was regenerated.
                    source = QuestionId(parent, row.ReplyToQuestion);
                    nodes.TryAdd(source, new(source, "question", Clip(row.ReplyToQuestion, 120),
                        Clip(row.ReplyToQuestion, 1200), Url: $"/entrada/{parent:D}?origen=mapa"));
                    Link(EntryId(parent), source, "question", "Pregunta de esta conversación");
                }
                Link(source, id, "reply", "Respondiste y abriste una rama");
            }
            if (reflections[row.Id] is not { IsDemo: false } reflection) continue;
            foreach (var evidence in reflection.ContextSources ?? [])
            {
                if (evidence.Kind == TopicSourceKind.Profile)
                {
                    if (string.IsNullOrWhiteSpace(profile)) continue;
                    nodes.TryAdd("profile", new("profile", "note", "Tu contexto personal", Clip(profile, 3000), Url: "/Account/Manage"));
                    Link("profile", id, "context", "Brisa consultó tu contexto personal");
                }
                else if (evidence.NoteId is { } nid && notes.TryGetValue(nid, out var note))
                {
                    var source = "note-" + nid.ToString("N");
                    nodes.TryAdd(source, new(source, "note", Clip(note.Text, 66), note.Text, note.CreatedUtc, Url: "/bitacora?seccion=notas"));
                    Link(source, id, "context", "Brisa consultó esta notita");
                }
                else if (evidence.EntryId is { } eid && allIds.Contains(eid))
                    Link(EntryId(eid), id, "context", evidence.Kind is TopicSourceKind.Document or TopicSourceKind.Voice
                        ? "Brisa consultó un archivo de esta entrada" : "Brisa consultó este antecedente");
            }
        }
        return new(nodes.Values.ToArray(), links.Values.ToArray(), total, selectedCount, limited,
            focus is { } f && nodes.ContainsKey(EntryId(f)) ? EntryId(f) : null);
    }

    private static JournalReflection? ReadReflection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var reflection = JsonSerializer.Deserialize<JournalReflection>(value, Json);
            return reflection is { Themes: not null, OpenQuestions: not null } ? reflection : null;
        }
        catch (JsonException) { return null; } // The original is still available when an old response is unreadable.
    }
    internal static string EntryId(Guid id) => "entry-" + id.ToString("N");
    private static string QuestionId(Guid entry, string text) => "question-" + entry.ToString("N") + "-" + Key(text);
    private static string Key(string text)
    {
        // Whitespace/case/accents only: never invent semantic equivalence between different topics.
        var normalized = new string(text.Normalize(NormalizationForm.FormD).Where(c =>
            CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        normalized = string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..20].ToLowerInvariant();
    }
    private static string Clip(string value, int length) => value.Length <= length ? value.Trim() : value[..length].TrimEnd() + "…";
}
