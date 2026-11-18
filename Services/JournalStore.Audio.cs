using Microsoft.EntityFrameworkCore;

namespace MiBitacora.Services;

public sealed partial class JournalStore
{
    public async Task SaveLocalExtractionAsync(string userId, Guid attachmentId, string? text, string coverage, CancellationToken cancellationToken = default)
    {
        text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (text?.Length > 120_000 || coverage.Length > 600 || string.IsNullOrWhiteSpace(coverage))
            throw new JournalInputException("La lectura del adjunto está incompleta o es demasiado extensa.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var file = await db.JournalAttachments.SingleOrDefaultAsync(a => a.Id == attachmentId && a.Entry.UserId == userId, cancellationToken);
        if (file is null) throw EntryUnavailable();
        var kind = AttachmentPresentation.Kind(ToAttachment(file));
        if (kind is not (AttachmentViewKind.Pdf or AttachmentViewKind.Video))
            throw new JournalInputException("Este archivo no es un PDF ni un video.");
        // Historical contexts carry excerpts. Never replace a full, completed
        // video transcript with the excerpt used by a follow-up question.
        if (kind == AttachmentViewKind.Video && !string.IsNullOrWhiteSpace(file.ExtractedText)) text = file.ExtractedText;
        if (file.ExtractedText == text && file.ExtractionStatus == coverage) return;
        file.ExtractedText = text;
        file.ExtractionStatus = coverage;
        file.SearchText = NormalizeSearch($"{file.FileName}\n{text}");
        await db.SaveChangesAsync(cancellationToken);
    }

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
