using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MiBitacora.Data;

namespace MiBitacora.Services;

// Per-account job ownership; model access also shares LocalInferenceGate with individual reflections.
public sealed class JournalTopicJobs
{
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);
    internal bool Start(string userId) => _active.TryAdd(userId, 0);
    internal void Finish(string userId) => _active.TryRemove(userId, out _);
}

public sealed class JournalTopicService(IDbContextFactory<ApplicationDbContext> factory,
    IJournalTopicAnalyzer analyzer, JournalTopicJobs jobs)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int SourceLength = 2400;
    private const int BatchLength = 6500;

    public async Task<JournalTopicState> GetAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = await db.JournalTopicOverviews.AsNoTracking().SingleOrDefaultAsync(r => r.UserId == userId, ct);
        if (row is null) return new(null, false);
        var input = await ReadInputAsync(db, userId, ct);
        return new(JsonSerializer.Deserialize<JournalTopicSnapshot>(row.SnapshotJson, Json), row.Fingerprint != input.Fingerprint);
    }

    public async Task<JournalTopicSnapshot> GenerateAsync(string userId, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!analyzer.IsConfigured) throw new JournalInputException("Activá la IA local para generar la devolución por temas.");
        if (!jobs.Start(userId)) throw new JournalInputException("Ya se está generando una devolución para tu cuenta. Esperá a que termine.");
        try
        {
            Input input;
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                input = await ReadInputAsync(db, userId, ct);
            }
            if (input.Sources.Count == 0)
                throw new JournalInputException("Todavía no hay texto para analizar. Escribí una entrada o adjuntá un documento con texto legible.");

            var topics = new List<WorkingTopic>();
            var batches = Batches(input.Sources, s => s.Text.Length, BatchLength, 4).ToList();
            for (var i = 0; i < batches.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Detectando temas · parte {i + 1} de {batches.Count}");
                var catalog = topics.ToDictionary(t => t.Id, t => t.Title);
                // Reject oversized context explicitly rather than silently ignoring older themes/text.
                if (JsonSerializer.Serialize(catalog, Json).Length > 6500)
                    throw new JournalInputException("Se detectaron demasiados temas para el contexto del modelo local. La devolución anterior se conserva.");
                var detected = await analyzer.DetectAsync(batches[i], catalog, ct);
                foreach (var item in detected)
                {
                    var topic = item.TopicId == "nuevo"
                        ? topics.FirstOrDefault(t => string.Equals(t.Title, item.NewTitle.Trim(), StringComparison.OrdinalIgnoreCase))
                        : topics.Single(t => t.Id == item.TopicId);
                    if (topic is null)
                    {
                        topic = new($"t{topics.Count + 1}", item.NewTitle.Trim());
                        topics.Add(topic);
                    }
                    var sources = item.Evidence.Select(e => batches[i].Single(s => s.Id == e.SourceId)).ToList();
                    topic.Observations.Add($"{sources.Min(s => s.CreatedUtc):yyyy-MM-dd} a {sources.Max(s => s.CreatedUtc):yyyy-MM-dd}: {item.Observation}");
                    foreach (var evidence in item.Evidence)
                    {
                        var source = batches[i].Single(s => s.Id == evidence.SourceId);
                        topic.Evidence.Add(new(source.EntryId, source.CreatedUtc, source.Label, evidence.Quote));
                    }
                }
            }

            var results = new List<JournalTopic>();
            foreach (var topic in topics)
            {
                progress?.Report($"Preparando devoluciones · tema {results.Count + 1} de {topics.Count}");
                var observations = topic.Observations;
                // Hierarchical summaries include every observation, even for very long-lived topics.
                while (string.Join("\n", observations).Length > BatchLength)
                {
                    var summaries = new List<string>();
                    foreach (var batch in Batches(observations, s => s.Length + 1, BatchLength))
                        summaries.Add((await analyzer.ReadAsync(topic.Title, string.Join("\n", batch), ct, summarizeOnly: true)).Overview);
                    observations = summaries;
                }
                var reading = await analyzer.ReadAsync(topic.Title, string.Join("\n", observations), ct);
                results.Add(new(topic.Id, topic.Title, reading.Overview, reading.Questions,
                    topic.Evidence.Distinct().OrderBy(e => e.CreatedUtc).ToArray()));
            }
            ct.ThrowIfCancellationRequested();
            var snapshot = new JournalTopicSnapshot(DateTime.UtcNow, input.EntryCount, input.TextEntryCount,
                input.DocumentCount, input.UnreadAttachmentCount, results);
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                if ((await ReadInputAsync(db, userId, ct)).Fingerprint != input.Fingerprint)
                    throw new JournalInputException("Tu cuaderno cambió durante el análisis. Volvé a generar la devolución para incluir esos cambios.");
                var row = await db.JournalTopicOverviews.SingleOrDefaultAsync(r => r.UserId == userId, ct);
                if (row is null) { row = new() { UserId = userId }; db.JournalTopicOverviews.Add(row); }
                row.Fingerprint = input.Fingerprint;
                row.SnapshotJson = JsonSerializer.Serialize(snapshot, Json);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            return snapshot;
        }
        finally { jobs.Finish(userId); }
    }

    private static async Task<Input> ReadInputAsync(ApplicationDbContext db, string userId, CancellationToken ct)
    {
        var entries = await db.JournalEntries.AsNoTracking().Where(e => e.UserId == userId)
            .OrderBy(e => e.CreatedUtc).ThenBy(e => e.Id)
            .Select(e => new { e.Id, e.Text, e.CreatedUtc, e.IsDateOnly,
                Files = e.Attachments.OrderBy(a => a.Position).Select(a => new { a.Id, a.FileName, a.ExtractedText }).ToList() })
            .ToListAsync(ct);
        var sources = new List<TopicSource>();
        var documents = 0;
        var unread = 0;
        var textEntries = 0;
        foreach (var entry in entries)
        {
            var hasText = !string.IsNullOrWhiteSpace(entry.Text);
            Add(entry.Text, "Entrada");
            foreach (var file in entry.Files)
            {
                if (string.IsNullOrWhiteSpace(file.ExtractedText)) { unread++; continue; }
                documents++;
                hasText = true;
                Add(file.ExtractedText, $"Documento: {file.FileName}");
            }
            if (hasText) textEntries++;
            void Add(string text, string label)
            {
                // No clipping: split every original text into bounded, contiguous parts.
                for (var start = 0; start < text.Length;)
                {
                    var end = Math.Min(start + SourceLength, text.Length);
                    if (end < text.Length)
                    {
                        var boundary = text.LastIndexOfAny([' ', '\n'], end - 1, end - start);
                        if (boundary > start + SourceLength / 2) end = boundary + 1;
                        if (char.IsHighSurrogate(text[end - 1])) end--;
                    }
                    var chunk = text[start..end];
                    if (!string.IsNullOrWhiteSpace(chunk)) sources.Add(new($"s{sources.Count + 1}", entry.Id, entry.CreatedUtc, label, chunk));
                    start = end;
                }
            }
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entries, Json))));
        return new(sources, fingerprint, entries.Count, textEntries, documents, unread);
    }

    private static IEnumerable<List<T>> Batches<T>(IEnumerable<T> source, Func<T, int> size, int budget, int count = int.MaxValue)
    {
        var batch = new List<T>();
        var length = 0;
        foreach (var item in source)
        {
            if (batch.Count > 0 && (length + size(item) > budget || batch.Count == count))
            { yield return batch; batch = []; length = 0; }
            batch.Add(item);
            length += size(item);
        }
        if (batch.Count > 0) yield return batch;
    }

    private sealed record Input(List<TopicSource> Sources, string Fingerprint, int EntryCount, int TextEntryCount,
        int DocumentCount, int UnreadAttachmentCount);
    private sealed class WorkingTopic(string id, string title)
    {
        internal string Id { get; } = id;
        internal string Title { get; } = title;
        internal List<string> Observations { get; } = [];
        internal List<TopicEvidence> Evidence { get; } = [];
    }
}
