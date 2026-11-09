namespace MiBitacora.Services;

public sealed record TopicSource(string Id, Guid EntryId, DateTime CreatedUtc, string Label, string Text);
public sealed record TopicEvidence(Guid EntryId, DateTime CreatedUtc, string Label, string Excerpt);
public sealed record TopicObservation(string TopicId, string NewTitle, string Observation, IReadOnlyList<TopicCitation> Evidence);
public sealed record TopicCitation(string SourceId, string Quote);
public sealed record TopicReading(string Overview, IReadOnlyList<string> Questions);
public sealed record JournalTopic(string Id, string Title, string Overview, IReadOnlyList<string> Questions,
    IReadOnlyList<TopicEvidence> Evidence);
public sealed record JournalTopicSnapshot(DateTime GeneratedUtc, int EntryCount, int TextEntryCount,
    int DocumentCount, int UnreadAttachmentCount, IReadOnlyList<JournalTopic> Topics);
public sealed record JournalTopicState(JournalTopicSnapshot? Snapshot, bool IsOutdated);

public interface IJournalTopicAnalyzer
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<TopicObservation>> DetectAsync(IReadOnlyList<TopicSource> sources,
        IReadOnlyDictionary<string, string> catalog, CancellationToken cancellationToken);
    Task<TopicReading> ReadAsync(string title, string observations, CancellationToken cancellationToken, bool summarizeOnly = false);
}
