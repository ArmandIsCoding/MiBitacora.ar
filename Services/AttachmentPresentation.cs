namespace MiBitacora.Services;

public enum AttachmentViewKind { Image, Audio, Video, Pdf, Text, File }

public static class AttachmentPresentation
{
    public static AttachmentViewKind Kind(JournalAttachment file)
    {
        if (HeicImageService.HasPreview(file.ContentType)) return AttachmentViewKind.Image;
        if (file.ContentType == "application/pdf") return AttachmentViewKind.Pdf;
        if (IsInlineAudio(file.ContentType)) return AttachmentViewKind.Audio;
        if (IsInlineVideo(file.ContentType)) return AttachmentViewKind.Video;
        // Older uploads may have generic metadata. The delivery endpoint checks the bytes
        // before serving a media MIME type; extensions only select a tentative player.
        if (file.ContentType == "application/octet-stream")
        {
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (extension is ".mp3" or ".m4a" or ".aac" or ".wav" or ".ogg" or ".oga" or ".opus" or ".flac" or ".weba")
                return AttachmentViewKind.Audio;
            if (extension is ".mp4" or ".m4v" or ".mov" or ".webm" or ".ogv" or ".3gp") return AttachmentViewKind.Video;
        }
        return string.IsNullOrWhiteSpace(file.ExtractedText) ? AttachmentViewKind.File : AttachmentViewKind.Text;
    }

    public static bool IsInlineAudio(string type) => type is "audio/mpeg" or "audio/mp4" or "audio/aac"
        or "audio/wav" or "audio/ogg" or "audio/flac" or "audio/webm";
    public static bool IsInlineVideo(string type) => type is "video/mp4" or "video/quicktime" or "video/webm" or "video/ogg" or "video/3gpp";
    public static bool IsInlineType(string type) => type is "image/jpeg" or "image/png" or "image/webp" or "image/gif" or "image/avif" or "application/pdf"
        || IsInlineAudio(type) || IsInlineVideo(type);
    public static string Icon(JournalAttachment file) => Kind(file) switch
    {
        AttachmentViewKind.Image => "image",
        AttachmentViewKind.Audio => "audio",
        AttachmentViewKind.Video => "play",
        _ => "file"
    };
    public static string Action(JournalAttachment file) => Kind(file) switch
    {
        AttachmentViewKind.Image => "Ver foto",
        AttachmentViewKind.Audio => "Escuchar audio",
        AttachmentViewKind.Video => "Ver video",
        AttachmentViewKind.Pdf => "Ver PDF",
        AttachmentViewKind.Text => "Leer documento",
        _ => "Ver archivo"
    };
}
