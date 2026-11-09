namespace MiBitacora.Data;

internal sealed class JournalProfileRow
{
    public string UserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PersonalContext { get; set; } = "";
    public bool GeminiConsent { get; set; }
}

internal sealed class JournalEntryRow
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Text { get; set; } = "";
    public string SearchText { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public bool IsDateOnly { get; set; }
    public string? ReflectionJson { get; set; }
    public bool ForSession { get; set; }
    public List<JournalAttachmentRow> Attachments { get; set; } = [];
}

internal sealed class JournalNoteRow
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public bool ForSession { get; set; }
}

internal sealed class JournalAttachmentRow
{
    public Guid Id { get; set; }
    public Guid EntryId { get; set; }
    public JournalEntryRow Entry { get; set; } = null!;
    public int Position { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public string ExtractionStatus { get; set; } = "";
    public string? ExtractedText { get; set; }
    public string SearchText { get; set; } = "";
}
