using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MiBitacora.Data;

namespace MiBitacora.Services;

// Originals only: generated answers never become evidence or autobiographical memory.
public sealed record JournalCorpus(IReadOnlyList<TopicSource> Sources, string Fingerprint,
    int EntryCount, int TextEntryCount, int DocumentCount, int UnreadAttachmentCount,
    int NoteCount, bool HasPersonalContext, DateTime? FromUtc, DateTime? ThroughUtc);

public static class JournalCorpusReader
{
    public static async Task<JournalCorpus> ReadAsync(ApplicationDbContext db, string userId,
        DateTime? beforeUtc = null, CancellationToken ct = default)
    {
        var entries = await db.JournalEntries.AsNoTracking()
            .Where(e => e.UserId == userId && (!beforeUtc.HasValue || e.CreatedUtc < beforeUtc.Value))
            .OrderBy(e => e.CreatedUtc).ThenBy(e => e.Id)
            .Select(e => new { e.Id, e.Text, e.CreatedUtc, e.IsDateOnly,
                Files = e.Attachments.OrderBy(a => a.Position).Select(a => new { a.Id, a.FileName, a.ContentType, a.ExtractedText, a.IsVoiceMessage }).ToList() })
            .ToListAsync(ct);
        var notes = await db.JournalNotes.AsNoTracking()
            .Where(n => n.UserId == userId && (!beforeUtc.HasValue || n.CreatedUtc <= beforeUtc.Value))
            .OrderBy(n => n.CreatedUtc).ThenBy(n => n.Id).Select(n => new { n.Id, n.Text, n.CreatedUtc }).ToListAsync(ct);
        var profile = await db.JournalProfiles.AsNoTracking().Where(p => p.UserId == userId)
            .Select(p => p.PersonalContext).SingleOrDefaultAsync(ct) ?? "";
        // Undated present-day profile is not evidence about a historical entry.
        if (beforeUtc.HasValue && beforeUtc.Value.Date < DateTime.UtcNow.Date) profile = "";
        var sources = new List<TopicSource>();
        var docs = 0; var unread = 0; var textEntries = 0;
        foreach (var e in entries)
        {
            var hasText = !string.IsNullOrWhiteSpace(e.Text);
            Add("e" + e.Id, e.Text, "Entrada", TopicSourceKind.Entry, e.Id, e.CreatedUtc);
            foreach (var file in e.Files)
            {
                if (string.IsNullOrWhiteSpace(file.ExtractedText)) { unread++; continue; }
                docs++; hasText = true;
                var attachment = new JournalAttachment(file.Id, e.Id, file.FileName, file.ContentType, 0, "", file.ExtractedText, file.IsVoiceMessage);
                Add("a" + file.Id, file.ExtractedText,
                    (JournalAudio.IsAudio(attachment) ? JournalAudio.Label(attachment) + ": " : "Documento: ") + file.FileName,
                    file.IsVoiceMessage ? TopicSourceKind.Voice : TopicSourceKind.Document, e.Id, e.CreatedUtc);
            }
            if (hasText) textEntries++;
        }
        foreach (var n in notes) Add("n" + n.Id, n.Text, "Notita", TopicSourceKind.Note, null, n.CreatedUtc, n.Id);
        Add("perfil", profile, "Contexto personal del perfil", TopicSourceKind.Profile, null, null);
        var fingerprint = Hash(JsonSerializer.Serialize(new { version = 1, entries, notes, profile }));
        return new(sources, fingerprint, entries.Count, textEntries, docs, unread, notes.Count,
            !string.IsNullOrWhiteSpace(profile), entries.FirstOrDefault()?.CreatedUtc, entries.LastOrDefault()?.CreatedUtc);

        void Add(string key, string text, string label, TopicSourceKind kind, Guid? entryId, DateTime? date, Guid? noteId = null)
        {
            for (var start = 0; start < text.Length;)
            {
                var end = Math.Min(start + 2400, text.Length);
                if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end--;
                var part = text[start..end];
                if (!string.IsNullOrWhiteSpace(part)) sources.Add(new(key + ":" + start, entryId, date, label, part, kind, noteId));
                start = end;
            }
        }
    }

    public static string SourceHash(TopicSource source) => Hash(JsonSerializer.Serialize(new { version = 1, source }));
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static IEnumerable<List<T>> Batches<T>(IEnumerable<T> source, Func<T, int> size, int budget, int count = 100)
    {
        var batch = new List<T>(); var length = 0;
        foreach (var item in source)
        {
            if (batch.Count > 0 && (length + size(item) > budget || batch.Count == count))
            { yield return batch; batch = []; length = 0; }
            batch.Add(item); length += size(item);
        }
        if (batch.Count > 0) yield return batch;
    }
}
