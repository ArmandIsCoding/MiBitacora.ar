using System.Collections.Concurrent;

namespace MiBitacora.Services;

public sealed class JournalConversationJobs
{
    private readonly ConcurrentDictionary<(string User, Guid Entry), byte> _running = new();
    internal bool Start(string user, Guid entry) => _running.TryAdd((user, entry), 0);
    internal void Finish(string user, Guid entry) => _running.TryRemove((user, entry), out _);
}

public sealed class JournalConversationService(IJournalStore store, IAudioTranscriber transcriber,
    JournalMemoryService memory, IJournalReflector reflector, JournalConversationJobs jobs, AccountAiRouter? routing = null)
{
    public bool IsConfigured => reflector.IsConfigured;
    public async Task<JournalEntry> GenerateAsync(string userId, Guid entryId, bool useAi = true,
        IProgress<string>? progress = null, CancellationToken ct = default, IProgress<BrisaActivity>? activity = null)
    {
        using var aiScope = routing is null ? null : routing.Use(await routing.ResolveAsync(userId, ct));
        if (!jobs.Start(userId, entryId)) throw new JournalReflectionException("Ya se está preparando una respuesta para esta entrada. Esperá a que termine.");
        try
        {
            activity?.Report(BrisaActivity.Reading);
            progress?.Report("Brisa está leyendo lo que le contaste…");
            var entry = await store.GetEntryAsync(userId, entryId, ct) ?? throw new JournalInputException("La entrada ya no está disponible.");
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
            }
            var context = await store.GetContextAsync(userId, entryId, useAi, ct);
            if (useAi) context = await memory.EnrichAsync(userId, context, progress, ct);
            activity?.Report(BrisaActivity.Thinking);
            progress?.Report("Brisa está pensando en lo que le contaste…");
            var response = (await reflector.ReflectAsync(context, useAi, ct)) with { LimitedMemory = useAi && context.LimitedMemory };
            ct.ThrowIfCancellationRequested();
            // Only replace after a complete, validated response. Failures keep the previous response.
            await store.SaveReflectionAsync(userId, entryId, response, ct, context.MemoryRevision);
            return await store.GetEntryAsync(userId, entryId, ct) ?? throw new JournalInputException("La entrada ya no está disponible.");
        }
        finally { jobs.Finish(userId, entryId); }
    }
}
