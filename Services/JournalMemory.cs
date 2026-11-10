using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MiBitacora.Data;

namespace MiBitacora.Services;

public sealed record MemoryDigest(string SourceId, string Summary, string Concepts, string Quote);
public sealed record MemoryCard(TopicSource Source, MemoryDigest Digest);
public interface IJournalMemoryAnalyzer
{
    bool UsesExternalProvider { get; }
    Task<IReadOnlyList<MemoryDigest>> IndexAsync(IReadOnlyList<TopicSource> sources, CancellationToken ct);
    Task<IReadOnlyList<string>> RecallAsync(string conversation, IReadOnlyList<MemoryCard> memories, int limit, CancellationToken ct);
}
public sealed class JournalMemoryJobs
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    internal SemaphoreSlim For(string userId) => _gates.GetOrAdd(userId, _ => new(1, 1));
}

public sealed class JournalMemoryService(IDbContextFactory<ApplicationDbContext> factory,
    IJournalMemoryAnalyzer analyzer, JournalMemoryJobs jobs)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<MemoryCard>> PrepareAsync(string userId, JournalCorpus corpus,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var gate = jobs.For(userId);
        await gate.WaitAsync(ct);
        try
        {
            Dictionary<string, JournalMemoryRow> cached;
            await using (var db = await factory.CreateDbContextAsync(ct))
                cached = await db.JournalMemories.AsNoTracking().Where(m => m.UserId == userId).ToDictionaryAsync(m => m.SourceId, ct);
            var ready = new Dictionary<string, MemoryDigest>();
            var missing = new List<TopicSource>();
            foreach (var source in corpus.Sources)
            {
                if (cached.TryGetValue(source.Id, out var row) && row.SourceHash == JournalCorpusReader.SourceHash(source))
                {
                    var digest = JsonSerializer.Deserialize<MemoryDigest>(row.DigestJson, Json);
                    if (digest is not null) { ready[source.Id] = digest; continue; }
                }
                missing.Add(source);
            }
            var batches = JournalCorpusReader.Batches(missing, s => s.Text.Length + 150, analyzer.UsesExternalProvider ? 14000 : 5000, 8).ToList();
            for (var i = 0; i < batches.Count; i++)
            {
                progress?.Report($"Organizando la memoria del cuaderno · {i + 1} de {batches.Count}");
                var digests = await analyzer.IndexAsync(batches[i], ct);
                if (digests.Count != batches[i].Count || digests.Select(d => d.SourceId).Distinct().Count() != batches[i].Count)
                    throw new JournalReflectionException("La memoria quedó incompleta. Tus entradas se conservan; podés reintentar.");
                await using var db = await factory.CreateDbContextAsync(ct);
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                // Check live originals inside the write transaction; a deleted/edited source cannot be resurrected.
                var live = (await JournalCorpusReader.ReadAsync(db, userId, ct: ct)).Sources.ToDictionary(s => s.Id);
                foreach (var source in batches[i])
                {
                    if (!live.TryGetValue(source.Id, out var actual) || JournalCorpusReader.SourceHash(source) != JournalCorpusReader.SourceHash(actual))
                        throw new JournalInputException("Tu cuaderno cambió mientras preparábamos la memoria. Volvé a intentarlo.");
                    var digest = digests.Single(d => d.SourceId == source.Id);
                    var row = await db.JournalMemories.SingleOrDefaultAsync(m => m.UserId == userId && m.SourceId == source.Id, ct);
                    if (row is null) { row = new() { UserId = userId, SourceId = source.Id, EntryId = source.EntryId, NoteId = source.NoteId }; db.JournalMemories.Add(row); }
                    row.SourceHash = JournalCorpusReader.SourceHash(source);
                    row.DigestJson = JsonSerializer.Serialize(digest, Json);
                    ready[source.Id] = digest;
                }
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            return corpus.Sources.Select(s => new MemoryCard(s, ready[s.Id])).ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task<JournalContext> EnrichAsync(string userId, JournalContext context,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        JournalCorpus corpus;
        await using (var db = await factory.CreateDbContextAsync(ct))
            corpus = await JournalCorpusReader.ReadAsync(db, userId, context.Current.CreatedUtc, ct);
        var cards = await PrepareAsync(userId, corpus, progress, ct);
        progress?.Report("Conectando lo que contás con tus antecedentes…");
        var recent = context.History.Where(e => e.NotebookId == context.Current.NotebookId)
            .OrderByDescending(e => e.CreatedUtc).Take(analyzer.UsesExternalProvider ? 6 : 2).ToArray();
        var conversation = JsonSerializer.Serialize(new
        {
            diario = context.Current.NotebookName,
            actual = Excerpt(context.Current.Text, analyzer.UsesExternalProvider ? 12000 : 1000),
            recientes = recent.Take(analyzer.UsesExternalProvider ? 3 : 1).Select(e => new { e.CreatedUtc,
                texto = Excerpt(e.Text, analyzer.UsesExternalProvider ? 1800 : 650),
                propuestaAnteriorDeLaIA = Excerpt(e.Reflection?.Overview ?? "", analyzer.UsesExternalProvider ? 1000 : 550), preguntaAnteriorDeLaIA = e.Reflection?.OpenQuestions })
        }, Json);
        var recentIds = recent.Select(e => e.Id).ToHashSet();
        var candidates = cards.Where(c => !c.Source.EntryId.HasValue || !recentIds.Contains(c.Source.EntryId.Value)).ToArray();
        var limit = analyzer.UsesExternalProvider ? 8 : 3;
        var selected = await SelectAsync(conversation, candidates, limit, ct);
        // Recheck the cutoff corpus after model calls. Changed sources must not leak into a new answer.
        await using (var db = await factory.CreateDbContextAsync(ct))
            if ((await JournalCorpusReader.ReadAsync(db, userId, context.Current.CreatedUtc, ct)).Fingerprint != corpus.Fingerprint)
                throw new JournalInputException("Tu cuaderno cambió mientras buscábamos antecedentes. Volvé a pedir la respuesta.");
        return context with { History = recent.OrderBy(e => e.CreatedUtc).ToArray(),
            MemorySources = selected.Select(c => c.Source).ToArray(), IndexedSourceCount = cards.Count };
    }

    public async Task<IReadOnlyList<MemoryCard>> SelectAsync(string query, IReadOnlyList<MemoryCard> candidates, int limit, CancellationToken ct)
    {
        while (candidates.Count > 0)
        {
            var selected = new List<MemoryCard>();
            foreach (var batch in JournalCorpusReader.Batches(candidates, c => c.Digest.Summary.Length + c.Digest.Concepts.Length + 120,
                analyzer.UsesExternalProvider ? 65000 : 4000, 180))
            {
                var ids = await analyzer.RecallAsync(query, batch, limit, ct);
                selected.AddRange(ids.Select(id => batch.Single(c => c.Source.Id == id)));
            }
            if (selected.Count <= limit) return selected;
            if (selected.Count >= candidates.Count) throw new JournalReflectionException("No se pudo acotar la búsqueda de antecedentes. Podés reintentar.");
            candidates = selected;
        }
        return [];
    }
    private static string Excerpt(string value, int limit) => value.Length <= limit ? value : value[..(char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit)];
}
