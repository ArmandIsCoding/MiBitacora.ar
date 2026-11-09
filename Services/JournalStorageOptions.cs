namespace MiBitacora.Services;

public sealed class JournalStorageOptions
{
    public long MaxAccountAttachmentBytes { get; set; } = 80 * 1024 * 1024;
    public int MaxAccountFiles { get; set; } = 250;
}
