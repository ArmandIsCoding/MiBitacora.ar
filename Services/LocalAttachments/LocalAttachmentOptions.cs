namespace MiBitacora.Services;

public sealed class LocalAttachmentOptions
{
    // Prepared for a later rollout. Never changes the account's AI provider.
    public bool Enabled { get; set; }
    public int PdfTextPages { get; set; } = 80;
    public int PdfVisualPages { get; set; } = 2;
    public int VideoFrames { get; set; } = 2;
    public int PreparationTimeoutSeconds { get; set; } = 60;
    public bool IsValid() => PdfTextPages is >= 1 and <= 200 && PdfVisualPages is >= 1 and <= 4
        && VideoFrames is >= 1 and <= 4 && PreparationTimeoutSeconds is >= 10 and <= 120;
}

public sealed record PreparedLocalVisual(byte[] Content, string SourcePart);
public sealed record PreparedLocalAttachment(string? Text, string Coverage,
    IReadOnlyList<PreparedLocalVisual> Images, byte[]? Audio = null);
