using Microsoft.EntityFrameworkCore;

namespace MiBitacora.Services;

public sealed partial class JournalStore
{
    public async Task SaveAudioTranscriptAsync(string userId, Guid attachmentId, string transcript, CancellationToken cancellationToken = default)
    {
        var text = transcript.Trim();
        if (text.Length is 0 or > 120_000)
            throw new JournalInputException("La transcripción está vacía o es demasiado extensa. El audio original se conserva.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var file = await db.JournalAttachments.SingleOrDefaultAsync(a => a.Id == attachmentId && a.Entry.UserId == userId, cancellationToken);
        if (file is null) throw EntryUnavailable();
        if (!JournalAudio.IsAudio(ToAttachment(file))) throw new JournalInputException("El archivo no es un audio.");
        if (!string.IsNullOrWhiteSpace(file.ExtractedText)) return; // Reuse a completed transcript on every retry/regeneration.
        file.ExtractedText = text;
        file.ExtractionStatus = "Transcripción automática disponible";
        file.SearchText = NormalizeSearch($"{file.FileName}\n{text}");
        await db.SaveChangesAsync(cancellationToken);
    }
}
