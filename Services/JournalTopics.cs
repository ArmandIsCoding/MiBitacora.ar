namespace MiBitacora.Services;

public enum TopicSourceKind { Entry, Document, Note, Profile }

public sealed record TopicSource(string Id, Guid? EntryId, DateTime? CreatedUtc, string Label, string Text,
    TopicSourceKind Kind = TopicSourceKind.Entry, Guid? NoteId = null);
public sealed record TopicEvidence(Guid? EntryId, DateTime? CreatedUtc, string Label, string Excerpt,
    TopicSourceKind Kind = TopicSourceKind.Entry, Guid? NoteId = null);
public sealed record TopicObservation(string TopicId, string NewTitle, string Observation, IReadOnlyList<TopicCitation> Evidence);
public sealed record TopicCitation(string SourceId, string Quote);
public sealed record TopicReading(string Overview, IReadOnlyList<string> Questions);
public sealed record JournalTopic(string Id, string Title, string Overview, IReadOnlyList<string> Questions,
    IReadOnlyList<TopicEvidence> Evidence);
public sealed record JournalTopicSnapshot(DateTime GeneratedUtc, int EntryCount, int TextEntryCount,
    int DocumentCount, int UnreadAttachmentCount, IReadOnlyList<JournalTopic> Topics,
    int NoteCount = 0, bool HasPersonalContext = false, IReadOnlyList<TopicEvidence>? AdditionalContext = null,
    TherapyReport? Report = null);
public sealed record JournalTopicState(JournalTopicSnapshot? Snapshot, bool IsOutdated);

public interface IJournalTopicAnalyzer
{
    bool IsConfigured { get; }
    string ProviderName => "IA local";
    bool UsesExternalProvider => false;
    Task<IReadOnlyList<TopicObservation>> DetectAsync(IReadOnlyList<TopicSource> sources,
        IReadOnlyDictionary<string, string> catalog, CancellationToken cancellationToken, string additionalContext = "");
    Task<TopicReading> ReadAsync(string title, string observations, CancellationToken cancellationToken,
        bool summarizeOnly = false, string additionalContext = "");
}
