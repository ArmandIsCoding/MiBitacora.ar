using System.Collections.Concurrent;

namespace MiBitacora.Services;

public sealed class JournalConversationJobs
{
    private readonly ConcurrentDictionary<(string User, Guid Entry), byte> _running = new();
    internal bool Start(string user, Guid entry) => _running.TryAdd((user, entry), 0);
    internal void Finish(string user, Guid entry) => _running.TryRemove((user, entry), out _);
}

public sealed class JournalConversationService(IJournalStore store, IAudioTranscriber transcriber,
    JournalMemoryService memory, IJournalReflector reflector, JournalConversationJobs jobs, AccountAiRouter? routing = null,
    AiUsageService? usage = null, LocalAttachmentAnalysis? localAttachments = null)
{
    public bool IsConfigured => reflector.IsConfigured;
    public async Task<JournalEntry> GenerateAsync(string userId, Guid entryId, bool useAi = true,
        IProgress<string>? progress = null, CancellationToken ct = default, IProgress<BrisaActivity>? activity = null)
    {
        using var aiScope = routing is null ? null : routing.Use(await routing.ResolveAsync(userId, ct));
        if (!jobs.Start(userId, entryId)) throw new JournalReflectionException("Ya se está preparando una respuesta para esta entrada. Esperá a que termine.");
        try
        {
            await using var quota = useAi && reflector.UsesExternalProvider && usage is not null
                ? await usage.ReserveAsync(userId, "reply", 1, ct) : null;
            activity?.Report(BrisaActivity.Reading);
            progress?.Report("Brisa está leyendo lo que le contaste…");
            var entry = await store.GetEntryAsync(userId, entryId, ct) ?? throw new JournalInputException("La entrada ya no está disponible.");
            IReadOnlyList<JournalMediaFile> localMedia = [];
            if (useAi)
            {
                var audios = entry.Attachments.Where(a => JournalAudio.IsAudio(a) && !JournalAudio.HasTranscript(a)).ToArray();
                for (var i = 0; i < audios.Length; i++)
                {
                    progress?.Report($"Transcribiendo audio · {i + 1} de {audios.Length}…");
                    var audio = await store.GetAttachmentPreviewAsync(userId, audios[i].Id, ct)
                        ?? throw new JournalReflectionException("No pudimos leer el audio. El original se conserva; revisá su formato.");
                    await using (audio.Content)
                    {
                        var transcript = await transcriber.TranscribeAsync(audio, ct);
                        await store.SaveAudioTranscriptAsync(userId, audios[i].Id, transcript, ct);
                    }
                }
                if (!reflector.UsesExternalProvider && localAttachments?.Enabled == true)
                    localMedia = await localAttachments.PrepareAsync(userId, entry, progress, ct);
            }
            var context = await store.GetContextAsync(userId, entryId, useAi, ct);
            if (useAi && !reflector.UsesExternalProvider && localAttachments?.Enabled == true)
            {
                // Follow-up questions can revisit a prepared PDF/video without uploading it again.
                var currentIds = entry.Attachments.Select(a => a.Id).ToHashSet();
                var priorFiles = context.Attachments.Where(a => !currentIds.Contains(a.Id) && LocalAttachmentAnalysis.WasPrepared(a)).Take(2).ToArray();
                var restored = new List<JournalMediaFile>(localMedia);
                foreach (var prior in priorFiles)
                {
                    try { restored.AddRange(await localAttachments.PrepareAsync(userId, entry with { Attachments = [prior] }, progress, ct)); }
                    catch (JournalInputException) { /* A missing historical file must not block the new message. */ }
                }
                if (priorFiles.Length > 0) context = await store.GetContextAsync(userId, entryId, useAi, ct);
                localMedia = restored;
            }
            if (localMedia.Count > 0) context = context with { Media = context.Media.Concat(localMedia).ToArray() };
            if (useAi) context = await memory.EnrichAsync(userId, context, progress, ct);
            activity?.Report(BrisaActivity.Thinking);
            progress?.Report("Brisa está pensando en lo que le contaste…");
            var response = (await reflector.ReflectAsync(context, useAi, ct)) with { LimitedMemory = useAi && context.LimitedMemory };
            ct.ThrowIfCancellationRequested();
            // Only replace after a complete, validated response. Failures keep the previous response.
            await store.SaveReflectionAsync(userId, entryId, response, ct, context.MemoryRevision);
            if (quota is not null) await quota.CompleteAsync(ct);
            return await store.GetEntryAsync(userId, entryId, ct) ?? throw new JournalInputException("La entrada ya no está disponible.");
        }
        finally { jobs.Finish(userId, entryId); }
    }
}
