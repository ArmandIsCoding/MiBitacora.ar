using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MiBitacora.Data;

namespace MiBitacora.Services;

public sealed class JournalReportService(IDbContextFactory<ApplicationDbContext> factory,
    JournalMemoryService memory, IJournalReportAnalyzer analyzer, JournalTopicJobs jobs, AccountAiRouter? routing = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Fingerprint(JournalCorpus corpus) => "report-v1:" + corpus.Fingerprint;

    public async Task<JournalTopicSnapshot> GenerateAsync(string userId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        using var aiScope = routing is null ? null : routing.Use(await routing.ResolveAsync(userId, ct));
        if (!analyzer.IsConfigured) throw new JournalInputException("Activá la IA para preparar el informe.");
        if (!jobs.Start(userId)) throw new JournalInputException("Ya hay una devolución en preparación para tu cuenta.");
        try
        {
            JournalCorpus corpus;
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                corpus = await JournalCorpusReader.ReadAsync(db, userId, ct: ct);
            }
            if (corpus.Sources.Count == 0) throw new JournalInputException("Todavía no hay texto para preparar un informe. Podés empezar con una entrada o una notita.");
            var now = DateTime.UtcNow;
            var cards = await memory.PrepareAsync(userId, corpus, progress, ct);
            var plans = new List<ReportSectionPlan>();
            var batches = JournalCorpusReader.Batches(cards, c => c.Digest.Summary.Length + c.Digest.Concepts.Length + 120,
                analyzer.UsesExternalProvider ? 65000 : 4500, 180).ToList();
            for (var i = 0; i < batches.Count; i++)
            {
                progress?.Report($"Leyendo el recorrido completo · parte {i + 1} de {batches.Count}");
                plans.AddRange(await analyzer.PlanReportAsync(batches[i], now, ct));
            }
            var sections = new List<TherapyReportSection>();
            var recentSince = now.AddDays(-30);
            var recentCount = corpus.Sources.Where(s => s.EntryId.HasValue && s.CreatedUtc >= recentSince).Select(s => s.EntryId).Distinct().Count();
            var scope = $"Fecha de preparación: {now:yyyy-MM-dd}. Período registrado: {corpus.FromUtc:yyyy-MM-dd} a {corpus.ThroughUtc:yyyy-MM-dd}. " +
                $"Entradas: {corpus.EntryCount}; con texto: {corpus.TextEntryCount}; documentos: {corpus.DocumentCount}; notitas: {corpus.NoteCount}. " +
                $"Hay {recentCount} entradas con texto desde {recentSince:yyyy-MM-dd} (últimos 30 días). El estado de hoy no se puede deducir solo del pasado. " +
                "No se analizaron las imágenes ni las respuestas previas de la IA para este informe.";
            foreach (var definition in JournalReportSections.All)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Redactando el informe · {sections.Count + 1} de {JournalReportSections.All.Count}: {definition.Title}");
                var ids = plans.Where(p => p.Id == definition.Id).SelectMany(p => p.SourceIds).ToHashSet();
                var candidates = cards.Where(c => ids.Contains(c.Source.Id)).ToArray();
                var limit = analyzer.UsesExternalProvider ? 14 : 4;
                IReadOnlyList<MemoryCard> selected = candidates;
                if (candidates.Length > limit)
                {
                    selected = await memory.SelectAsync("Selección para informe. " + scope + "\n" + definition.Purpose, candidates, limit, ct);
                }
                var editorialContext = JsonSerializer.Serialize(sections.Select(s => new
                {
                    s.Id, s.Title,
                    Points = s.Points.Select(p => new { p.Title,
                        Text = p.Text[..Math.Min(analyzer.UsesExternalProvider ? 180 : 0, p.Text.Length)],
                        Question = p.Question[..Math.Min(analyzer.UsesExternalProvider ? 280 : 80, p.Question.Length)] })
                }), Json);
                sections.Add(await analyzer.WriteReportSectionAsync(definition, selected.Select(c => c.Source).ToArray(),
                    scope + "\nAPARTADOS YA REDACTADOS (borrador editorial de IA, nunca evidencia):\n" + editorialContext, ct));
            }
            var snapshot = new JournalTopicSnapshot(DateTime.UtcNow, corpus.EntryCount, corpus.TextEntryCount, corpus.DocumentCount,
                corpus.UnreadAttachmentCount, [], corpus.NoteCount, corpus.HasPersonalContext, null,
                new(1, corpus.FromUtc, corpus.ThroughUtc, recentSince, recentCount, sections));
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                if ((await JournalCorpusReader.ReadAsync(db, userId, ct: ct)).Fingerprint != corpus.Fingerprint)
                    throw new JournalInputException("Tu cuaderno cambió durante la preparación. El informe anterior se conserva; volvé a actualizar.");
                var row = await db.JournalTopicOverviews.SingleOrDefaultAsync(r => r.UserId == userId, ct);
                if (row is null) { row = new() { UserId = userId }; db.JournalTopicOverviews.Add(row); }
                row.Fingerprint = Fingerprint(corpus);
                row.SnapshotJson = JsonSerializer.Serialize(snapshot, Json);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            return snapshot;
        }
        finally { jobs.Finish(userId); }
    }
}
