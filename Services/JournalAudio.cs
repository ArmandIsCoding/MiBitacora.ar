using System.Text;

namespace MiBitacora.Services;

public static class JournalAudio
{
    public static bool IsAudio(JournalAttachment file) => AttachmentPresentation.Kind(file) == AttachmentViewKind.Audio;
    public static bool HasTranscript(JournalAttachment file) => IsAudio(file) && !string.IsNullOrWhiteSpace(file.ExtractedText);
    public static string Label(JournalAttachment file) => file.IsVoiceMessage
        ? "Mensaje de voz · transcripción automática"
        : "Audio adjunto · transcripción automática; hablante no confirmado";
    public static string ConversationText(JournalEntry entry)
    {
        var text = new StringBuilder(entry.Text);
        foreach (var audio in entry.Attachments.Where(HasTranscript))
            text.AppendLine().AppendLine($"[{Label(audio)}: {audio.FileName}]").AppendLine(audio.ExtractedText);
        return text.ToString().Trim();
    }
}
