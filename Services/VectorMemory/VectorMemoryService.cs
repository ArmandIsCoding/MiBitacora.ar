using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MiBitacora.Data;

namespace MiBitacora.Services;

public sealed record VectorIndexResult(bool Complete, int Embedded, long Revision);

public sealed class VectorMemoryService(IDbContextFactory<ApplicationDbContext> factory,
    IJournalEmbeddingClient embeddings, IOptions<EmbeddingOptions> settings, ILogger<VectorMemoryService> logger,
    LocalInferenceGate? generations = null)
{
    private static readonly Regex Words = new(@"[\p{L}\p{N}]{2,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly HashSet<string> StopWords = new(("de la el los las un una unos unas y o a en con por para que " +
        "es son del al lo me mi mis te tu tus se su sus yo si no como pero porque cuando donde muy mas más " +
        "esta está este esto estas estos estoy tengo quiero quisiera hoy ayer algo eso esa ese también tambien " +
        "antes despues después hasta desde sobre hace hay fue ser estar todo todos toda todas mismo misma " +
        "sí sé vale bueno claro pregunta anterior ia hecho mensaje propuesta contexto").Split(' '));

    // No model calls here. A cold request can make the lexical catalog current without waiting for the backlog.
    public async Task<long> SynchronizeCatalogAsync(string userId, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var state = await db.JournalVectorStates.AsNoTracking().SingleOrDefaultAsync(s => s.UserId == userId, ct)
                ?? throw Changed();
            if (state.CatalogRevision == state.Revision) return state.Revision;
            var corpus = await JournalCorpusReader.ReadAsync(db, userId, ct: ct);
            var sources = corpus.Sources.SelectMany(JournalVectors.Split).ToDictionary(s => s.Id);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var live = await db.JournalVectorStates.SingleOrDefaultAsync(s => s.UserId == userId, ct) ?? throw Changed();
            if (live.Revision != state.Revision) continue;
            if (live.CatalogRevision == live.Revision) return live.Revision;
            // Comparing the catalog never needs to load all vector blobs into the change tracker.
            var rows = await db.JournalVectorChunks.AsNoTracking().Where(c => c.UserId == userId)
                .Select(c => new { c.Id, c.SourceId, c.SourceHash }).ToDictionaryAsync(c => c.SourceId, ct);
            foreach (var source in sources.Values)
            {
                var hash = JournalCorpusReader.SourceHash(source);
                if (rows.TryGetValue(source.Id, out var existing) && existing.SourceHash == hash) continue;
                var row = new JournalVectorChunkRow { UserId = userId, SourceId = source.Id };
                if (existing is null) db.JournalVectorChunks.Add(row);
                else { row.Id = existing.Id; db.Entry(row).State = EntityState.Modified; }
                row.SourceHash = hash; row.EntryId = source.EntryId; row.NoteId = source.NoteId;
                row.CreatedUtc = source.CreatedUtc; row.Kind = (int)source.Kind;
                row.Label = source.Label; row.Text = source.Text; row.ModelKey = ""; row.Embedding = null;
            }
            var removed = rows.Values.Where(r => !sources.ContainsKey(r.SourceId)).Select(r => r.Id).ToArray();
            if (removed.Length > 0) await db.JournalVectorChunks.Where(c => c.UserId == userId && removed.Contains(c.Id)).ExecuteDeleteAsync(ct);
            live.CatalogRevision = live.Revision;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return live.Revision;
        }
        throw Changed();
    }

    public async Task<VectorIndexResult> IndexPendingAsync(string userId, EmbeddingModel model,
        int maxBatches = 4, CancellationToken ct = default)
    {
        var revision = await SynchronizeCatalogAsync(userId, ct);
        var embedded = 0;
        for (var batch = 0; batch < maxBatches; batch++)
        {
            // Background work yields between batches while Gemma is serving a conversation or report.
            if (generations?.IsBusy == true) break;
            List<JournalVectorChunkRow> pending;
            await using (var db = await factory.CreateDbContextAsync(ct))
                pending = await db.JournalVectorChunks.AsNoTracking().Where(c => c.UserId == userId
                    && (c.Embedding == null || c.ModelKey != model.Key)).OrderBy(c => c.Id)
                    .Take(settings.Value.BatchSize).ToListAsync(ct);
            if (pending.Count == 0) break;
            // Never hold a database transaction during inference.
            var vectors = await embeddings.EmbedAsync(model, pending.Select(c => JournalVectors.Document(c.ToSource())).ToArray(), false, ct);
            if (vectors.Count != pending.Count) throw new EmbeddingUnavailableException("El lote de memoria quedó incompleto.");
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                if (!await db.JournalVectorStates.AnyAsync(s => s.UserId == userId && s.Revision == revision, ct)) throw Changed();
                for (var i = 0; i < pending.Count; i++)
                {
                    var original = pending[i];
                    var packed = JournalVectors.Pack(JournalVectors.Normalize(vectors[i], model.Dimensions));
                    embedded += await db.JournalVectorChunks.Where(c => c.UserId == userId && c.Id == original.Id && c.SourceHash == original.SourceHash)
                        .ExecuteUpdateAsync(u => u.SetProperty(c => c.ModelKey, model.Key).SetProperty(c => c.Embedding, packed), ct);
                }
                await transaction.CommitAsync(ct);
            }
        }
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var state = await db.JournalVectorStates.SingleOrDefaultAsync(s => s.UserId == userId, ct) ?? throw Changed();
            if (state.Revision != revision) throw Changed();
            var complete = !await db.JournalVectorChunks.AnyAsync(c => c.UserId == userId && (c.Embedding == null || c.ModelKey != model.Key), ct);
            state.Failures = 0;
            state.NextAttemptTicks = complete ? 0 : DateTime.UtcNow.AddSeconds(1).Ticks;
            if (complete) { state.IndexedRevision = revision; state.ModelKey = model.Key; }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(complete, embedded, revision);
        }
    }

    public async Task<JournalContext> EnrichAsync(string userId, JournalContext context, bool external,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var timer = Stopwatch.StartNew();
        // Do not trust a caller-supplied context to authorize a query/cache write.
        await using (var db = await factory.CreateDbContextAsync(ct))
            if (!await db.JournalEntries.AnyAsync(e => e.UserId == userId && e.Id == context.Current.Id, ct)) throw Changed();
        var revision = await SynchronizeCatalogAsync(userId, ct);
        progress?.Report("Buscando antecedentes en tu cuaderno…");
        if (context.MemoryRevision.HasValue && context.MemoryRevision.Value != revision) throw Changed();
        var recent = context.History.Where(e => e.CreatedUtc < context.Current.CreatedUtc && e.NotebookId == context.Current.NotebookId)
            .OrderByDescending(e => e.CreatedUtc).Take(external ? 6 : 2).ToArray();
        var queryText = BuildQuery(context, recent);
        var recentIds = recent.Select(e => e.Id).ToArray();
        var scores = new Dictionary<long, double>();
        var vectorCount = 0;
        var eligibleCount = 0;
        var semanticAvailable = false;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var cutoff = context.Current.CreatedUtc;
            var allowProfile = cutoff.Date >= DateTime.UtcNow.Date;
            var eligible = db.JournalVectorChunks.AsNoTracking().Where(c => c.UserId == userId
                && ((!c.CreatedUtc.HasValue && allowProfile && c.Kind == (int)TopicSourceKind.Profile)
                    || (c.CreatedUtc.HasValue && (c.EntryId != null ? c.CreatedUtc < cutoff : c.CreatedUtc <= cutoff)))
                && (!c.EntryId.HasValue || !recentIds.Contains(c.EntryId.Value)));
            eligibleCount = await eligible.CountAsync(ct);
            if (eligibleCount > 0)
            {
                try
                {
                    var model = await embeddings.GetModelAsync(ct);
                    vectorCount = await eligible.CountAsync(c => c.Embedding != null && c.ModelKey == model.Key, ct);
                    // A completely cold index gains nothing from embedding the query yet.
                    if (vectorCount > 0)
                    {
                        var query = await QueryVectorAsync(db, userId, context.Current.Id, revision, queryText, model, ct);
                        var vectors = await eligible.Where(c => c.Embedding != null && c.ModelKey == model.Key)
                            .Select(c => new { c.Id, c.Embedding }).ToListAsync(ct);
                        var ranked = vectors.Select(c => (c.Id, Score: JournalVectors.Similarity(c.Embedding!, query)))
                            .Where(c => c.Score >= 0.25).OrderByDescending(c => c.Score).ThenBy(c => c.Id).Take(32).ToArray();
                        for (var i = 0; i < ranked.Length; i++) scores[ranked[i].Id] = 1d / (60 + i + 1);
                        semanticAvailable = true;
                    }
                }
                catch (EmbeddingUnavailableException)
                {
                    // Retrieval remains useful with words and recent turns; no cloud or generative fallback.
                    logger.LogWarning("Memoria semántica temporalmente no disponible; se usará búsqueda por palabras y conversación reciente.");
                }
                // Exact names/topics in the new message take priority over incidental words in the prior turn.
                var currentText = JournalVectors.Utf8Prefix(JournalAudio.ConversationText(context.Current), 1500);
                var lexical = await LexicalAsync(db, userId, cutoff, allowProfile, recentIds,
                    SearchTerms(currentText).Length == 0 ? queryText : currentText, ct);
                for (var i = 0; i < lexical.Count; i++)
                    scores[lexical[i]] = scores.GetValueOrDefault(lexical[i]) + 1d / (60 + i + 1);
            }
            var ids = scores.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Select(p => p.Key).ToArray();
            var originals = await eligible.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
            var selected = new List<TopicSource>();
            var diversity = new Dictionary<string, int>();
            foreach (var id in ids)
            {
                if (!originals.TryGetValue(id, out var row)) continue;
                var group = row.EntryId?.ToString() ?? row.NoteId?.ToString() ?? "profile";
                if (diversity.GetValueOrDefault(group) >= (row.Kind == (int)TopicSourceKind.Profile ? 1 : 2)) continue;
                diversity[group] = diversity.GetValueOrDefault(group) + 1;
                selected.Add(row.ToSource());
                if (selected.Count == (external ? 8 : 3)) break;
            }
            if (!await db.JournalVectorStates.AnyAsync(s => s.UserId == userId && s.Revision == revision, ct)) throw Changed();
            if (eligibleCount > vectorCount || (!semanticAvailable && eligibleCount > 0))
                progress?.Report("La memoria semántica sigue preparándose; usamos coincidencias de texto y la conversación reciente.");
            logger.LogInformation("Antecedentes recuperados en {ElapsedMs} ms: {Selected} fragmentos; {Indexed}/{Total} vectores disponibles; búsqueda semántica {Semantic}.",
                timer.ElapsedMilliseconds, selected.Count, vectorCount, eligibleCount, semanticAvailable);
            return context with
            {
                History = recent.OrderBy(e => e.CreatedUtc).ToArray(), MemorySources = selected,
                IndexedSourceCount = vectorCount, MemoryRevision = revision,
                LimitedMemory = eligibleCount > vectorCount || (!semanticAvailable && eligibleCount > 0),
                Profile = allowProfile ? context.Profile : context.Profile with { PersonalContext = "" },
                Notes = context.Notes.Where(n => n.CreatedUtc <= cutoff).ToArray()
            };
        }
    }

    private async Task<float[]> QueryVectorAsync(ApplicationDbContext db, string userId, Guid entryId, long revision,
        string text, EmbeddingModel model, CancellationToken ct)
    {
        var input = JournalVectors.Query(text);
        var hash = JournalVectors.Hash(model.Key + "\n" + input);
        var cached = await db.JournalVectorQueries.AsNoTracking().SingleOrDefaultAsync(q => q.UserId == userId && q.EntryId == entryId, ct);
        if (cached is not null && cached.QueryHash == hash && cached.ModelKey == model.Key)
        {
            try { return JournalVectors.Unpack(cached.Embedding, model.Dimensions); }
            catch (EmbeddingUnavailableException) { /* Repair a corrupt or old vector by recalculating just this query. */ }
        }
        var result = await embeddings.EmbedAsync(model, [input], true, ct);
        if (result.Count != 1) throw new EmbeddingUnavailableException("No pudimos calcular la consulta de memoria.");
        var vector = JournalVectors.Normalize(result[0], model.Dimensions);
        var packed = JournalVectors.Pack(vector);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await db.JournalVectorStates.AnyAsync(s => s.UserId == userId && s.Revision == revision, ct)) throw Changed();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO JournalVectorQueries (UserId, EntryId, QueryHash, ModelKey, Embedding)
                VALUES ({userId}, {entryId}, {hash}, {model.Key}, {packed})
                ON CONFLICT(UserId, EntryId) DO UPDATE SET QueryHash=excluded.QueryHash, ModelKey=excluded.ModelKey, Embedding=excluded.Embedding
            """, ct);
        await transaction.CommitAsync(ct);
        return vector;
    }

    private static string BuildQuery(JournalContext context, IReadOnlyList<JournalEntry> recent)
    {
        var current = JournalAudio.ConversationText(context.Current);
        var text = JournalVectors.Utf8Prefix(current, 850);
        if (text.Length < current.Length) text += " … " + JournalVectors.Utf8Prefix(current[Math.Max(text.Length, current.Length - 220)..], 300);
        if (recent.FirstOrDefault() is { } prior)
        {
            // A short answer such as "sí, eso" needs the preceding question, never as factual evidence.
            text += "\nPregunta anterior de la IA (no es un hecho): " + JournalVectors.Utf8Prefix(
                context.Current.ReplyToQuestion ?? string.Join(" ", prior.Reflection?.OpenQuestions ?? []), 180);
            text += "\nMensaje anterior: " + JournalVectors.Utf8Prefix(JournalAudio.ConversationText(prior), 300);
            text += "\nPropuesta anterior de la IA (no es un hecho): " + JournalVectors.Utf8Prefix(prior.Reflection?.Overview ?? "", 220);
        }
        return JournalVectors.Utf8Prefix(text, 1500);
    }

    private static async Task<IReadOnlyList<long>> LexicalAsync(ApplicationDbContext db, string userId, DateTime cutoff,
        bool allowProfile, Guid[] recentIds, string text, CancellationToken ct)
    {
        var terms = SearchTerms(text).Select(t => "\"" + t + "\"").ToArray();
        if (terms.Length == 0) return [];
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT c.Id FROM JournalVectorText JOIN JournalVectorChunks c ON c.Id = JournalVectorText.rowid
            WHERE JournalVectorText MATCH $query AND c.UserId = $user
                AND ((c.CreatedUtc IS NULL AND $profile = 1 AND c.Kind = 3)
                    OR (c.CreatedUtc IS NOT NULL AND ((c.EntryId IS NOT NULL AND c.CreatedUtc < $cutoff)
                        OR (c.EntryId IS NULL AND c.CreatedUtc <= $cutoff))))
            """;
        command.Parameters.Add(new SqliteParameter("$query", string.Join(" OR ", terms)));
        command.Parameters.Add(new SqliteParameter("$user", userId));
        command.Parameters.Add(new SqliteParameter("$profile", allowProfile ? 1 : 0));
        command.Parameters.Add(new SqliteParameter("$cutoff", cutoff));
        for (var i = 0; i < recentIds.Length; i++)
        {
            command.CommandText += $" AND (c.EntryId IS NULL OR c.EntryId != $recent{i})";
            command.Parameters.Add(new SqliteParameter($"$recent{i}", recentIds[i]));
        }
        command.CommandText += " ORDER BY bm25(JournalVectorText), c.Id LIMIT 32";
        var result = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetInt64(0));
        return result;
    }

    private static string[] SearchTerms(string text) => Words.Matches(text.Normalize(NormalizationForm.FormC).ToLowerInvariant()).Select(m => m.Value)
        .Where(t => !StopWords.Contains(t)).Distinct().Take(24).ToArray();

    private static JournalInputException Changed() => new("Tu cuaderno cambió mientras buscábamos antecedentes. Volvé a pedir la respuesta.");
}
