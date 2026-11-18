namespace MiBitacora.Services;

public sealed class LocalAttachmentAnalysis(IJournalStore store, LocalAttachmentProcessor processor, IAudioTranscriber transcriber)
{
    private const string VideoTranscript = "[Transcripción automática del audio del video · hablantes no confirmados]";
    public bool Enabled => processor.Enabled;
    public static bool WasPrepared(JournalAttachment attachment) => attachment.ExtractionStatus.StartsWith("PDF ·", StringComparison.Ordinal)
        || attachment.ExtractionStatus.StartsWith("Video de ", StringComparison.Ordinal);

    public async Task<IReadOnlyList<JournalMediaFile>> PrepareAsync(string userId, JournalEntry entry,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!Enabled) return [];
        var media = new List<JournalMediaFile>();
        foreach (var attachment in entry.Attachments.Where(a => AttachmentPresentation.Kind(a) is AttachmentViewKind.Pdf or AttachmentViewKind.Video))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Preparando la lectura de {attachment.FileName}…");
            var file = await store.GetAttachmentAsync(userId, attachment.Id, ct)
                ?? throw new JournalInputException("El archivo ya no está disponible.");
            var cachedTranscript = attachment.ExtractedText?.StartsWith(VideoTranscript, StringComparison.Ordinal) == true;
            PreparedLocalAttachment prepared;
            await using (file.Content) prepared = await processor.PrepareAsync(file, !cachedTranscript, ct);
            var text = prepared.Text;
            if (AttachmentPresentation.Kind(attachment) == AttachmentViewKind.Video)
            {
                text = cachedTranscript ? attachment.ExtractedText : null;
                if (prepared.Audio is { } audio)
                {
                    progress?.Report($"Transcribiendo el audio de {attachment.FileName}…");
                    await using var stream = new MemoryStream(audio, writable: false);
                    var transcript = await transcriber.TranscribeAsync(new("video-audio.wav", "audio/wav", stream), ct);
                    text = VideoTranscript + "\n" + transcript;
                }
            }
            await store.SaveLocalExtractionAsync(userId, attachment.Id, text, prepared.Coverage, ct);
            media.AddRange(prepared.Images.Select(image => new JournalMediaFile(attachment.Id, attachment.FileName,
                "image/jpeg", image.Content, image.SourcePart)));
        }
        return media;
    }
}
