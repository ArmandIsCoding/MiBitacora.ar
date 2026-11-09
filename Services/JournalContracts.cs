namespace MiBitacora.Services;

public sealed record JournalProfile(string DisplayName, string PersonalContext, bool GeminiConsent);

public sealed record JournalEntry(
    Guid Id,
    string Text,
    DateTime CreatedUtc,
    JournalReflection? Reflection,
    bool ForSession,
    IReadOnlyList<JournalAttachment> Attachments);

public sealed record JournalNote(Guid Id, string Text, DateTime CreatedUtc, bool ForSession);

public sealed record PendingAttachment(string FileName, byte[] Content);

public sealed record JournalAttachment(
    Guid Id,
    Guid EntryId,
    string FileName,
    string ContentType,
    long Size,
    string ExtractionStatus,
    string? ExtractedText);

public sealed record AttachmentContent(string FileName, string ContentType, Stream Content);

public sealed record JournalMediaFile(Guid AttachmentId, string FileName, string ContentType, byte[] Content);

public sealed record JournalContext(
    JournalProfile Profile,
    JournalEntry Current,
    IReadOnlyList<JournalEntry> History,
    IReadOnlyList<JournalNote> Notes,
    IReadOnlyList<JournalAttachment> Attachments,
    IReadOnlyList<JournalMediaFile> Media);

public sealed record ReflectionTheme(string Title, string Detail, string? Quote, string? Source);

public sealed record JournalReflection(
    string Title,
    string Overview,
    IReadOnlyList<ReflectionTheme> Themes,
    IReadOnlyList<string> OpenQuestions,
    IReadOnlyList<string> SessionTopics,
    string? NextStep,
    bool IsDemo);

public interface IJournalReflector
{
    bool IsConfigured { get; }

    Task<JournalReflection> ReflectAsync(
        JournalContext context,
        bool useAi,
        CancellationToken cancellationToken = default);
}

public interface IJournalStore
{
    Task<JournalProfile> GetProfileAsync(string userId, CancellationToken cancellationToken = default);
    Task SaveProfileAsync(string userId, JournalProfile profile, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JournalEntry>> GetEntriesAsync(string userId, int take = 40, DateTime? beforeUtc = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<DateOnly, int>> GetEntryDaysAsync(string userId, TimeZoneInfo timeZone, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JournalEntry>> GetEntriesInRangeAsync(string userId, DateTime fromUtc, DateTime untilUtc, CancellationToken cancellationToken = default);
    Task<JournalEntry> AddEntryAsync(string userId, string text, IReadOnlyList<PendingAttachment> files, CancellationToken cancellationToken = default);
    Task SaveReflectionAsync(string userId, Guid entryId, JournalReflection reflection, CancellationToken cancellationToken = default);
    Task SetEntryForSessionAsync(string userId, Guid entryId, bool selected, CancellationToken cancellationToken = default);
    Task DeleteEntryAsync(string userId, Guid entryId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JournalNote>> GetNotesAsync(string userId, CancellationToken cancellationToken = default);
    Task<JournalNote> SaveNoteAsync(string userId, Guid? noteId, string text, CancellationToken cancellationToken = default);
    Task SetNoteForSessionAsync(string userId, Guid noteId, bool selected, CancellationToken cancellationToken = default);
    Task DeleteNoteAsync(string userId, Guid noteId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JournalEntry>> GetSessionEntriesAsync(string userId, CancellationToken cancellationToken = default);
    Task<JournalContext> GetContextAsync(string userId, Guid entryId, bool includeMedia, CancellationToken cancellationToken = default);
    Task<AttachmentContent?> GetAttachmentAsync(string userId, Guid attachmentId, CancellationToken cancellationToken = default);
}


public sealed class JournalInputException(string message) : Exception(message);
